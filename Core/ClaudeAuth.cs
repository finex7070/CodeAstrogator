using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>What <c>claude auth status --json</c> reports (CLI 2.1.319). The email/org are shown in the
    /// panel; nothing here is a secret (the CLI keeps the tokens to itself).</summary>
    public sealed class AuthStatus
    {
        public bool LoggedIn { get; set; }
        /// <summary><c>claude.ai</c> (subscription), <c>none</c>, or what the CLI calls a key / console login.</summary>
        public string AuthMethod { get; set; } = "none";
        /// <summary><c>firstParty</c>, or a cloud provider (Bedrock, Vertex, …) — no login of ours applies then.</summary>
        public string ApiProvider { get; set; } = "firstParty";
        public string? Email { get; set; }
        public string? OrgName { get; set; }
        /// <summary><c>pro</c>, <c>max</c>, <c>team</c>, <c>enterprise</c>, … (absent for API keys).</summary>
        public string? SubscriptionType { get; set; }
        public DateTime? LoginExpiresAtUtc { get; set; }
    }

    /// <summary>
    /// Sign-in of the Claude Code CLI, entirely through the official binary (<c>claude auth status|login|logout</c>)
    /// — the panel never reads or handles a token. See docs/NOTES.md "Sign-in".
    /// </summary>
    public static class ClaudeAuth
    {
        /// <summary>Parses <c>auth status --json</c> (pretty-printed; printed with exit code 1 when signed out).
        /// Null when there is no JSON object in the text.</summary>
        public static AuthStatus? ParseStatus(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var start = text!.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;
            JObject obj;
            try { obj = JObject.Parse(text.Substring(start, end - start + 1)); }
            catch { return null; }
            var status = new AuthStatus
            {
                LoggedIn = obj.Value<bool?>("loggedIn") ?? false,
                AuthMethod = obj.Value<string>("authMethod") ?? "none",
                ApiProvider = obj.Value<string>("apiProvider") ?? "firstParty",
                Email = obj.Value<string>("email"),
                OrgName = obj.Value<string>("orgName"),
                SubscriptionType = obj.Value<string>("subscriptionType"),
            };
            var expires = obj["loginExpiresAt"];
            if (expires != null && expires.Type == JTokenType.Date)
                status.LoginExpiresAtUtc = expires.Value<DateTime>().ToUniversalTime();
            else if (expires != null && expires.Type == JTokenType.String
                     && DateTime.TryParse(expires.Value<string>(), null,
                         System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                         out var parsed))
                status.LoginExpiresAtUtc = parsed;
            return status;
        }

        /// <summary><c>claude auth status --json</c>; null when the CLI could not be run or printed no JSON.</summary>
        public static async Task<AuthStatus?> GetStatusAsync(string exe, CancellationToken ct)
        {
            var run = await CliCommandRunner.RunAsync(exe,
                new CliCommand { Args = new[] { "auth", "status", "--json" }, TimeoutMs = 20_000 }, ct).ConfigureAwait(false);
            return run.StartError != null ? null : ParseStatus(run.StdOut);
        }

        /// <summary><c>claude auth logout</c>. Returns the CLI's message on failure, null on success.</summary>
        public static async Task<string?> LogoutAsync(string exe, CancellationToken ct)
        {
            var run = await CliCommandRunner.RunAsync(exe,
                new CliCommand { Args = new[] { "auth", "logout" }, TimeoutMs = 30_000 }, ct).ConfigureAwait(false);
            if (run.StartError != null)
                return run.StartError;
            if (run.ExitCode == 0)
                return null;
            var message = (run.StdErr.Trim().Length > 0 ? run.StdErr : run.StdOut).Trim();
            return message.Length > 0 ? message : "claude auth logout failed (exit code " + run.ExitCode + ").";
        }

        /// <summary>True for the texts the CLI uses when it is not (or no longer) signed in.</summary>
        public static bool IsAuthFailureText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            return text!.IndexOf("Please run /login", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("OAuth token has expired", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("authentication_failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static readonly Regex ManualUrlPattern = new Regex(@"visit:\s*(https://\S+)", RegexOptions.IgnoreCase);

        /// <summary>The sign-in URL <c>claude auth login</c> prints for "the browser didn't open" — its redirect is
        /// Anthropic's code page, so it works on any device (the page then shows a code to paste back).</summary>
        public static string? ParseManualUrl(string? output)
        {
            if (string.IsNullOrEmpty(output))
                return null;
            var m = ManualUrlPattern.Match(output);
            return m.Success && IsSignInUrl(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }

        /// <summary>The URL the CLI handed to <c>BROWSER</c> (our capture script wrote its arguments, usually quoted).
        /// This one redirects to the CLI's own localhost listener: opened on this PC it completes the sign-in by itself.</summary>
        public static string? ParseCapturedUrl(string? captured)
        {
            if (string.IsNullOrEmpty(captured))
                return null;
            var url = captured!.Trim().Trim('"').Trim();
            var space = url.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' });
            if (space > 0)
                url = url.Substring(0, space);
            return IsSignInUrl(url) ? url : null;
        }

        /// <summary>Only https pages on Anthropic's / Claude's own domains are ever opened or shown.</summary>
        public static bool IsSignInUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "claude.com" || host.EndsWith(".claude.com", StringComparison.Ordinal)
                || host == "claude.ai" || host.EndsWith(".claude.ai", StringComparison.Ordinal)
                || host == "anthropic.com" || host.EndsWith(".anthropic.com", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One running <c>claude auth login</c> (hidden, stdin/stdout redirected). The CLI then (measured 2.1.319)
    /// 1. hands a sign-in URL whose redirect is its own localhost listener to <c>BROWSER</c> — we point that at a tiny
    ///    script that only records the URL, so the panel can open it (a click in the panel → the default browser →
    ///    the sign-in completes by itself);
    /// 2. prints a second URL ("If the browser didn't open, visit: …") whose redirect is Anthropic's code page —
    ///    shown as QR code for another device; the page shows a code, the panel writes it to stdin
    ///    ("Paste code here if prompted >");
    /// 3. exits 0 when signed in, else 1 with "Login failed: …" on stderr.
    /// </summary>
    public sealed class ClaudeLoginProcess : IDisposable
    {
        private readonly string _exe;
        private readonly bool _console;
        private Process? _process;
        private string? _dir;
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly StringBuilder _stderr = new StringBuilder();
        private int _disposed;

        /// <summary>(browserUrl, manualUrl) — either may be null until known; raised when one of them changes.</summary>
        public event Action<string?, string?>? UrlsChanged;
        /// <summary>(exitCode, message): 0 = signed in; otherwise the CLI's "Login failed: …" (or another reason).</summary>
        public event Action<int, string?>? Exited;

        public string? BrowserUrl { get; private set; }
        public string? ManualUrl { get; private set; }
        public bool Cancelled { get; private set; }

        public ClaudeLoginProcess(string exe, bool console)
        {
            _exe = exe;
            _console = console;
        }

        /// <summary>Starts the CLI and watches for the two URLs (off the UI thread). Throws when it cannot start.</summary>
        public void Start()
        {
            _dir = Path.Combine(Path.GetTempPath(), "CodeAstrogator", "login-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_dir);
            var capture = Path.Combine(_dir, "browser-url.txt");
            var script = Path.Combine(_dir, "open-browser.cmd");
            // cmd: `%*` is the URL (quoted — it contains &); written once, no expansion of its %xx escapes
            File.WriteAllText(script, "@echo off\r\n>\"" + capture + "\" echo %*\r\n", Encoding.ASCII);

            var psi = new ProcessStartInfo
            {
                FileName = _exe,
                Arguments = "auth login" + (_console ? " --console" : " --claudeai"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["BROWSER"] = script;
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                lock (_stdout)
                    _stdout.AppendLine(e.Data);
                var manual = ClaudeAuth.ParseManualUrl(e.Data);
                if (manual != null && manual != ManualUrl)
                {
                    ManualUrl = manual;
                    UrlsChanged?.Invoke(BrowserUrl, ManualUrl);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    lock (_stderr) _stderr.AppendLine(e.Data);
            };
            process.Exited += (_, __) => OnExited(process);
            process.Start();
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = WatchCaptureAsync(capture);
        }

        private async Task WatchCaptureAsync(string capture)
        {
            // the CLI calls BROWSER within a second or two; give up after 20 s (older CLI / no BROWSER support →
            // the manual URL alone still works)
            for (var i = 0; i < 200 && Volatile.Read(ref _disposed) == 0; i++)
            {
                try
                {
                    if (File.Exists(capture))
                    {
                        var url = ClaudeAuth.ParseCapturedUrl(File.ReadAllText(capture));
                        if (url != null)
                        {
                            BrowserUrl = url;
                            UrlsChanged?.Invoke(BrowserUrl, ManualUrl);
                            return;
                        }
                    }
                }
                catch { /* being written — next round */ }
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        /// <summary>Writes the code from Anthropic's code page to the CLI ("Paste code here if prompted").</summary>
        public bool SubmitCode(string code)
        {
            var p = _process;
            code = (code ?? "").Trim();
            if (p == null || code.Length == 0)
                return false;
            try
            {
                if (p.HasExited)
                    return false;
                p.StandardInput.WriteLine(code);
                p.StandardInput.Flush();
                return true;
            }
            catch { return false; }
        }

        private void OnExited(Process process)
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            // let the async readers deliver the tail (the failure message)
            _ = Task.Delay(150).ContinueWith(_ =>
            {
                if (Cancelled)
                    return;
                string message;
                lock (_stderr)
                    message = _stderr.ToString().Trim();
                if (message.Length == 0 && code != 0)
                    lock (_stdout)
                        message = LastLine(_stdout.ToString());
                Exited?.Invoke(code, code == 0 ? null : (message.Length > 0 ? message : "Sign-in failed (exit code " + code + ")."));
                Cleanup();
            }, TaskScheduler.Default);
        }

        private static string LastLine(string text)
        {
            var lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";
        }

        /// <summary>Stops the sign-in (the panel's Cancel, a restart with other options, the window closing).</summary>
        public void Cancel()
        {
            Cancelled = true;
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var p = _process;
            try { if (p != null && !p.HasExited) ProcessTree.KillTree(p.Id); } catch { }
            Cleanup();
        }

        private void Cleanup()
        {
            var dir = _dir;
            _dir = null;
            if (dir == null)
                return;
            try { Directory.Delete(dir, recursive: true); } catch { /* the script may still be in use for a moment */ }
        }
    }
}
