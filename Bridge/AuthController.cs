using System;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace CodeAstrogator.Bridge
{
    /// <summary>
    /// Sign-in of the Claude Code CLI from the panel (docs/NOTES.md "Sign-in"): account state (<c>auth.state</c>),
    /// sign in (<c>claude auth login</c> — a link that opens the browser, a QR code for another device, a code field),
    /// sign out (<c>claude auth logout</c>). Everything goes through the official CLI; no token is ever read here.
    /// Shared by the chat tabs (one login per window). Messages: web→host <c>auth.login { console }</c>,
    /// <c>auth.open</c>, <c>auth.code { code }</c>, <c>auth.cancel</c>, <c>auth.logout</c>, <c>auth.refresh</c>;
    /// host→web <c>auth.state</c>, <c>auth.login { state, … }</c>.
    /// </summary>
    internal sealed class AuthController : IDisposable
    {
        private readonly CodeAstrogatorPackage _package;
        private readonly Action<JObject> _post;
        private readonly Func<string?> _busyReason;     // why signing out must wait (a turn / remote session runs)
        private readonly Action _onAccountChanged;      // signed in / out → usage meters, plan label, …
        private ClaudeLoginProcess? _login;             // UI thread
        private int _statusSeq;                         // newest status probe wins
        private bool _disposed;

        public AuthController(CodeAstrogatorPackage package, Action<JObject> post, Func<string?> busyReason,
            Action onAccountChanged)
        {
            _package = package;
            _post = post;
            _busyReason = busyReason;
            _onAccountChanged = onAccountChanged;
        }

        /// <summary>An <c>auth.*</c> message from the page (UI thread). False when it is not one of ours.</summary>
        public bool Handle(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (msg.Value<string>("type"))
            {
                case "auth.login":
                    StartLogin(msg.Value<bool?>("console") ?? false);
                    return true;
                case "auth.open":
                    OpenSignInPage();
                    return true;
                case "auth.code":
                    SubmitCode(msg.Value<string>("code") ?? "");
                    return true;
                case "auth.cancel":
                    CancelLogin();
                    return true;
                case "auth.logout":
                    Logout();
                    return true;
                case "auth.refresh":
                    SendState();
                    return true;
                default:
                    return false;
            }
        }

        // ── account state ──────────────────────────────────────────────────────

        /// <summary>Posts <c>auth.state</c>: first the quick file check (instant), then what <c>claude auth status
        /// --json</c> says (email, organization, plan, expiry). Any thread.</summary>
        public void SendState(string? reason = null)
        {
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            if (exe == null)
            {
                _post(new JObject { ["type"] = "auth.state", ["loggedIn"] = false, ["mode"] = "none", ["noCli"] = true });
                return;
            }
            var seq = Interlocked.Increment(ref _statusSeq);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var status = await ClaudeAuth.GetStatusAsync(exe, _package.DisposalToken).ConfigureAwait(false);
                if (_disposed || seq != Volatile.Read(ref _statusSeq))
                    return;
                if (status == null)
                {
                    // an older CLI without `auth status --json`: the file-based guess
                    var (loggedIn, mode) = ClaudeExecutableLocator.ProbeAuthState();
                    _post(new JObject { ["type"] = "auth.state", ["loggedIn"] = loggedIn, ["mode"] = mode, ["reason"] = reason });
                    return;
                }
                _post(BuildState(status, reason));
            }).Task.Forget();
        }

        internal static JObject BuildState(AuthStatus status, string? reason)
        {
            var thirdParty = !string.Equals(status.ApiProvider, "firstParty", StringComparison.OrdinalIgnoreCase);
            var mode = !status.LoggedIn ? "none"
                : string.Equals(status.AuthMethod, "claude.ai", StringComparison.OrdinalIgnoreCase) ? "oauth"
                : "apiKey";
            return new JObject
            {
                ["type"] = "auth.state",
                ["loggedIn"] = status.LoggedIn || thirdParty,
                ["mode"] = thirdParty ? "provider" : mode,
                ["method"] = status.AuthMethod,
                ["provider"] = status.ApiProvider,
                ["email"] = status.Email,
                ["org"] = status.OrgName,
                ["plan"] = status.SubscriptionType,
                ["expiresAt"] = status.LoginExpiresAtUtc?.ToString("o"),
                // only a CLI login can be signed out of here (not an API key from the environment / a cloud provider)
                ["canLogout"] = status.LoggedIn && !thirdParty && mode == "oauth",
                ["reason"] = reason,
            };
        }

        /// <summary>A turn failed with "Not logged in · Please run /login" (login missing or expired): re-check the
        /// state so the header/gear show it. The tab itself shows the "Sign in again" card. Any thread.</summary>
        public void OnAuthFailed() => SendState("expired");

        // ── sign in ────────────────────────────────────────────────────────────

        /// <summary>Starts the sign-in (the page then shows its dialog). Also <c>/login</c>. UI thread.</summary>
        internal void StartLogin(bool console)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CancelLogin(silent: true); // a restart (e.g. switched to the Console login) replaces the running one
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            if (exe == null)
            {
                PostLogin("error", console, message: "Claude CLI not found — set the executable path via the gear menu → Advanced options.");
                return;
            }
            var login = new ClaudeLoginProcess(exe, console);
            login.UrlsChanged += (browser, manual) => _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_login == login)
                    PostLogin("waiting", console, browser != null, manual);
            }).Task.Forget();
            login.Exited += (code, message) => _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_login != login)
                    return;
                _login = null;
                if (code != 0)
                {
                    PostLogin("error", console, message: CleanMessage(message));
                    return;
                }
                PostLogin("verifying", console);
                await FinishLoginAsync(exe, console);
            }).Task.Forget();
            _login = login;
            PostLogin("starting", console);
            try
            {
                login.Start();
            }
            catch (Exception ex)
            {
                _login = null;
                login.Dispose();
                PostLogin("error", console, message: "Could not start the sign-in: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task FinishLoginAsync(string exe, bool console)
        {
            await TaskScheduler.Default;
            var status = await ClaudeAuth.GetStatusAsync(exe, _package.DisposalToken).ConfigureAwait(false);
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed)
                return;
            if (status != null)
                _post(BuildState(status, "signedIn"));
            var who = status?.Email ?? status?.OrgName;
            PostLogin("done", console, message: who != null ? "Signed in as " + who + "." : "Signed in.");
            _onAccountChanged();
        }

        /// <summary>Opens the sign-in page in the default browser: the URL with the CLI's localhost callback (signs
        /// in by itself), else the code-page URL (the page then shows a code for the panel's code field).</summary>
        private void OpenSignInPage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var url = _login?.BrowserUrl ?? _login?.ManualUrl;
            if (!ClaudeAuth.IsSignInUrl(url))
                return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url!) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                PostLogin("waiting", false, _login?.BrowserUrl != null, _login?.ManualUrl,
                    "Could not open the browser (" + ex.Message + ") — copy the link instead.");
            }
        }

        private void SubmitCode(string code)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var login = _login;
            if (login == null)
                return;
            if (login.SubmitCode(code))
                PostLogin("verifying", false);
        }

        private void CancelLogin(bool silent = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var login = _login;
            _login = null;
            if (login == null)
                return;
            login.Cancel();
            if (!silent)
                PostLogin("cancelled", false);
        }

        private void PostLogin(string state, bool console, bool hasBrowserLink = false, string? manualUrl = null,
            string? message = null)
        {
            var msg = new JObject { ["type"] = "auth.login", ["state"] = state, ["console"] = console };
            if (hasBrowserLink || manualUrl != null)
                msg["hasLink"] = true;
            if (manualUrl != null)
                msg["manualUrl"] = manualUrl; // the QR code + "Copy link" (works on any device)
            if (message != null)
                msg["message"] = message;
            _post(msg);
        }

        /// <summary>"Login failed: That code wasn't accepted. …" → without the prefix (the dialog says it failed).</summary>
        private static string CleanMessage(string? message)
        {
            var text = (message ?? "").Trim();
            const string prefix = "Login failed:";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text.Substring(prefix.Length).Trim();
            return text.Length > 0 ? text : "Sign-in failed.";
        }

        // ── sign out ───────────────────────────────────────────────────────────

        /// <summary>Signs out (refused while a turn or remote session runs). Also <c>/logout</c>. UI thread.</summary>
        internal void Logout()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var busy = _busyReason();
            if (busy != null)
            {
                _post(new JObject { ["type"] = "notify", ["level"] = "warning", ["message"] = busy, ["autoClose"] = 8000 });
                return;
            }
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            if (exe == null)
                return;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var error = await ClaudeAuth.LogoutAsync(exe, _package.DisposalToken).ConfigureAwait(false);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed)
                    return;
                _post(new JObject
                {
                    ["type"] = "notify",
                    ["level"] = error == null ? "info" : "error",
                    ["message"] = error == null ? "Signed out of Claude." : "Sign-out failed: " + error,
                    ["autoClose"] = error == null ? 5000 : 0,
                });
                SendState("signedOut");
                _onAccountChanged();
            }).Task.Forget();
        }

        public void Dispose()
        {
            _disposed = true;
            var login = _login;
            _login = null;
            login?.Cancel();
        }
    }
}
