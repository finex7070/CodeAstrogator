using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    /// Fetches plan-limit utilization by running the CLI's own <c>/usage</c> slash
    /// command headlessly (<c>claude -p /usage --output-format json</c>) and parsing
    /// the human-readable report it prints. This runs locally — no API turn, no cost
    /// (<c>num_turns: 0</c>) — and works for whatever auth the CLI is configured with,
    /// so we no longer scrape the OAuth token or hit the undocumented usage endpoint.
    /// Only works in subscription mode — API-key sessions print no limits (meters stay 0).
    /// </summary>
    public static class ClaudeUsageClient
    {
        public static string DefaultClaudeJsonPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        /// <summary>
        /// Fetches the current utilization via <c>claude -p /usage</c>; null on any
        /// failure (CLI not found, API-key mode, offline, timeout …).
        /// </summary>
        public static async Task<UsageSnapshot?> FetchAsync(
            string? executableOverride = null,
            string? workingDirectory = null,
            CancellationToken ct = default)
        {
            try
            {
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
