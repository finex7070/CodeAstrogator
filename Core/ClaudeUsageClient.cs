using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>Session (5h) / weekly (7d) plan utilization as shown by the CLI's /usage.</summary>
    public sealed class UsageSnapshot
    {
        public int SessionPct { get; set; }
        public int WeeklyPct { get; set; }
        public DateTimeOffset? SessionResetsAt { get; set; }
        public DateTimeOffset? WeeklyResetsAt { get; set; }
    }

    /// <summary>
    /// Fetches plan-limit utilization. Primary source is the OAuth usage endpoint
    /// (<c>GET api.anthropic.com/api/oauth/usage</c>, authorized with the subscription access token
    /// from <c>~/.claude/.credentials.json</c>) — it returns the real session/weekly percentages and
    /// exact reset times as structured JSON. This replaced the earlier <c>claude -p /usage</c> text
    /// scraping after the CLI's <c>/usage</c> redesign stopped printing the limit percentages in
    /// headless mode (it now serves an attribution breakdown; a headless pseudo-console can't recover
    /// them because the native binary won't render its TUI into one — see docs/NOTES.md). The
    /// <c>-p /usage</c> text parse remains as a fallback. Only meaningful in subscription mode —
    /// API-key sessions have no OAuth token / plan limits (meters stay 0).
    /// </summary>
    public static class ClaudeUsageClient
    {
        public static string DefaultClaudeJsonPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        private const string OAuthUsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private static readonly HttpClient Http = new HttpClient();
        // After a 429 the endpoint stays throttled for a while; back off so the once-a-minute
        // refresh doesn't keep hammering it (and prolonging the throttle).
        private static DateTime _oauthBackoffUntilUtc = DateTime.MinValue;
        // Short cache so bursts (per-minute timer + every turn-end + window-open) don't each hit the
        // network — the limits barely move in 30s, and it keeps us well under any endpoint throttle.
        private static UsageSnapshot? _oauthCache;
        private static DateTime _oauthCacheAtUtc = DateTime.MinValue;
        private static readonly TimeSpan OAuthCacheTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Fetches the current utilization; null on any failure (no token, API-key mode, offline,
        /// throttled, …). Tries the structured OAuth endpoint first, then the <c>-p /usage</c> text.
        /// </summary>
        public static async Task<UsageSnapshot?> FetchAsync(
            string? executableOverride = null,
            string? workingDirectory = null,
            CancellationToken ct = default)
        {
            try
            {
                // Primary: structured OAuth usage endpoint (real % + ISO reset times).
                var viaOAuth = await FetchFromOAuthEndpointAsync(ct).ConfigureAwait(false);
                if (viaOAuth != null)
                    return viaOAuth;

                // Fallback: `claude -p /usage` text (has percentages only while the CLI serves the
                // old report format; the redesigned format has none → null → meters hold last value).
                var exe = ClaudeExecutableLocator.Locate(executableOverride);
                if (string.IsNullOrEmpty(exe))
                    return null;

                var output = await RunUsageCommandAsync(exe!, workingDirectory, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(output))
                    return null;

                return ParseUsageResult(output!, DateTime.Now);
            }
            catch
            {
                return null; // usage display is best-effort; never break the chat over it
            }
        }

        /// <summary>Path to the CLI's OAuth credentials file (<c>~/.claude/.credentials.json</c>).</summary>
        internal static string CredentialsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        /// <summary>
        /// Queries the OAuth usage endpoint with the subscription access token. Returns null when
        /// there's no OAuth token (API-key mode), while backing off after a 429, or on any error.
        /// </summary>
        internal static async Task<UsageSnapshot?> FetchFromOAuthEndpointAsync(CancellationToken ct)
        {
            if (_oauthCache != null && DateTime.UtcNow - _oauthCacheAtUtc < OAuthCacheTtl)
                return _oauthCache; // fresh enough — serve from cache, no network

            if (DateTime.UtcNow < _oauthBackoffUntilUtc)
                return null; // still cooling down after a 429

            var token = ReadOAuthAccessToken();
            if (string.IsNullOrEmpty(token))
                return null; // API-key mode / not signed in → no plan limits to show

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, OAuthUsageUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                req.Headers.TryAddWithoutValidation("User-Agent", "CodeAstrogator");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);

                if ((int)resp.StatusCode == 429)
                {
                    _oauthBackoffUntilUtc = DateTime.UtcNow.AddMinutes(5);
                    return null;
                }
                if (!resp.IsSuccessStatusCode)
                    return null;

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var snapshot = ParseOAuthUsage(JObject.Parse(body));
                if (snapshot != null)
                {
                    _oauthCache = snapshot;
                    _oauthCacheAtUtc = DateTime.UtcNow;
                }
                return snapshot;
            }
            catch
            {
                return null; // offline / token expired / parse error → fall back
            }
        }

        /// <summary>Reads the subscription OAuth access token from <c>~/.claude/.credentials.json</c>
        /// (<c>claudeAiOauth.accessToken</c>); null if absent (API-key mode / not signed in).</summary>
        internal static string? ReadOAuthAccessToken()
        {
            try
            {
                var path = CredentialsPath;
                if (!File.Exists(path))
                    return null;
                var oauth = JObject.Parse(File.ReadAllText(path))["claudeAiOauth"] as JObject;
                var token = oauth?.Value<string>("accessToken");
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Maps the OAuth usage JSON to a snapshot: <c>five_hour</c> → session,
        /// <c>seven_day</c> → weekly (utilization 0–100, <c>resets_at</c> as ISO-8601). Returns null
        /// if neither window is present.</summary>
        internal static UsageSnapshot? ParseOAuthUsage(JObject root)
        {
            if (root == null)
                return null;

            UsageSnapshot? snapshot = null;

            if (root["five_hour"] is JObject fh && TryUtilization(fh, out var sPct))
            {
                snapshot ??= new UsageSnapshot();
                snapshot.SessionPct = sPct;
                snapshot.SessionResetsAt = ParseIso(fh.Value<string>("resets_at"));
            }
            if (root["seven_day"] is JObject sd && TryUtilization(sd, out var wPct))
            {
                snapshot ??= new UsageSnapshot();
                snapshot.WeeklyPct = wPct;
                snapshot.WeeklyResetsAt = ParseIso(sd.Value<string>("resets_at"));
            }
            return snapshot;
        }

        private static bool TryUtilization(JObject window, out int pct)
        {
            pct = 0;
            var util = window["utilization"];
            if (util == null || util.Type == JTokenType.Null)
                return false;
            try
            {
                pct = Math.Max(0, Math.Min(100, (int)Math.Round(util.Value<double>())));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DateTimeOffset? ParseIso(string? s) =>
            !string.IsNullOrEmpty(s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                ? dto
                : (DateTimeOffset?)null;

        /// <summary>
        /// Runs <c>claude -p /usage --output-format json</c> and returns its raw stdout.
        /// stdin is closed immediately (the slash command takes no prompt) so the CLI
        /// does not wait on its "no stdin received" grace period.
        /// </summary>
        private static async Task<string?> RunUsageCommandAsync(
            string exe, string? workingDirectory, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-p /usage --output-format json",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) =>
            {
                try { exited.TrySetResult(process.ExitCode); }
                catch { exited.TrySetResult(-1); }
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    lock (stdout) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, __) => { /* ignored — best-effort */ };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeoutCts.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch { /* already gone */ }
            }))
            {
                await exited.Task.ConfigureAwait(false);
                try { process.WaitForExit(); } catch { }
                if (timeoutCts.IsCancellationRequested)
                    return null; // killed by timeout/caller — output is unreliable
                lock (stdout) return stdout.ToString();
            }
        }

        /// <summary>
        /// Parses the CLI output. With <c>--output-format json</c> the /usage report is
        /// wrapped as <c>{"result": "…"}</c>; we unwrap it (tolerating SessionStart-hook
        /// preamble lines) and fall back to treating the whole output as the report text.
        /// </summary>
        internal static UsageSnapshot? ParseUsageResult(string output, DateTime now)
        {
            string? text = null;
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.Length == 0 || trimmed[0] != '{')
                        continue;
                    try
                    {
                        var result = JObject.Parse(trimmed).Value<string>("result");
                        if (!string.IsNullOrEmpty(result))
                            text = result; // keep the last result line
                    }
                    catch
                    {
                        // not the JSON envelope — ignore this line
                    }
                }
            }

            return ParseUsageText(text ?? output, now);
        }

        /// <summary>
        /// Parses the plain-text /usage report, e.g.:
        /// <code>
        /// Current session: 8% used · resets Jun 10, 1pm (Europe/Berlin)
        /// Current week (all models): 1% used · resets Jun 10, 9pm (Europe/Berlin)
        /// Current week (Sonnet only): 0% used
        /// </code>
        /// Returns null if neither the session nor the weekly line is present.
        /// </summary>
        internal static UsageSnapshot? ParseUsageText(string text, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            UsageSnapshot? snapshot = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();

                var session = Regex.Match(line, @"^Current session:\s*(\d+)%", RegexOptions.IgnoreCase);
                if (session.Success)
                {
                    snapshot ??= new UsageSnapshot();
                    snapshot.SessionPct = ClampPct(session.Groups[1].Value);
                    snapshot.SessionResetsAt = ParseResetTime(line, now);
                    continue;
                }

                // Match the "all models" weekly (or a plain "Current week:"), never the
                // per-model rows like "Current week (Sonnet only):".
                var weekly = Regex.Match(line, @"^Current week(?:\s*\(all models\))?:\s*(\d+)%", RegexOptions.IgnoreCase);
                if (weekly.Success)
                {
                    snapshot ??= new UsageSnapshot();
                    snapshot.WeeklyPct = ClampPct(weekly.Groups[1].Value);
                    snapshot.WeeklyResetsAt = ParseResetTime(line, now);
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Parses a "resets Jun 10, 1pm (…)" suffix into a local DateTimeOffset. The CLI
        /// prints the reset in the user's local time (the "(Europe/Berlin)" tail is just a
        /// label), so we read it as wall-clock-local. No year is printed; we assume the
        /// current year and roll to next year if the date already lies in the past.
        /// </summary>
        internal static DateTimeOffset? ParseResetTime(string line, DateTime now)
        {
            var m = Regex.Match(
                line,
                @"resets\s+([A-Za-z]{3,})\s+(\d{1,2}),\s*(\d{1,2})(?::(\d{2}))?\s*([ap]m)",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                return null;

            if (!TryParseMonth(m.Groups[1].Value, out var month))
                return null;
            if (!int.TryParse(m.Groups[2].Value, out var day))
                return null;
            if (!int.TryParse(m.Groups[3].Value, out var hour12))
                return null;
            var minute = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;

            var hour = hour12 % 12;
            if (m.Groups[5].Value.Equals("pm", StringComparison.OrdinalIgnoreCase))
                hour += 12;

            try
            {
                var year = now.Year;
                var candidate = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                if (candidate < now.AddDays(-2)) // printed date is past → next year's window
                    candidate = new DateTime(year + 1, month, day, hour, minute, 0, DateTimeKind.Local);
                return new DateTimeOffset(candidate);
            }
            catch
            {
                return null; // e.g. Feb 30 in a malformed report
            }
        }

        private static bool TryParseMonth(string token, out int month)
        {
            month = 0;
            // "MMM" matches both 3-letter ("Jun") and full ("June") month names.
            if (DateTime.TryParseExact(token.Substring(0, Math.Min(3, token.Length)), "MMM",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                month = d.Month;
                return true;
            }
            return false;
        }

        /// <summary>Plan badge label from ~/.claude.json (oauthAccount.organizationType).</summary>
        public static string? ReadPlanLabel(string? claudeJsonPath = null)
        {
            try
            {
                var path = claudeJsonPath ?? DefaultClaudeJsonPath;
                if (!File.Exists(path))
                    return null;
                var obj = JObject.Parse(File.ReadAllText(path));
                var account = obj["oauthAccount"] as JObject;
                if (account == null)
                    return null;

                var orgType = account.Value<string>("organizationType") ?? "";
                var label = MapPlanLabel(orgType, account.Value<string>("userRateLimitTier") ?? "");
                return label;
            }
            catch
            {
                return null;
            }
        }

        internal static string? MapPlanLabel(string organizationType, string rateLimitTier)
        {
            switch (organizationType)
            {
                case "claude_team":
                    return "Team Plan";
                case "claude_enterprise":
                    return "Enterprise";
                case "claude_pro":
                    return "Pro Plan";
                case "claude_max":
                    return "Max Plan";
            }
            if (rateLimitTier.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Max Plan";
            if (rateLimitTier.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Pro Plan";
            return null;
        }

        private static int ClampPct(string value) =>
            int.TryParse(value, out var n) ? Math.Max(0, Math.Min(100, n)) : 0;
    }
}
