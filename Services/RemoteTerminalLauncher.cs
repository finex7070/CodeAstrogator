using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Opens the current chat's CLI session in an <b>interactive</b> Claude Code session with Remote
    /// Control enabled — <c>claude --resume &lt;id&gt; --remote-control</c> (a top-level flag; the headless
    /// <c>remote-control</c> subcommand can't resume an existing conversation) — in a standalone
    /// <b>PowerShell</b> console the user can type into. The CLI shows the connection link there.
    ///
    /// (The console is trackable, so the session is detected when the window closes — `Process.Exited`
    /// → <see cref="Ended"/> — and "End remote session" can kill it. Windows Terminal would render the
    /// CLI's QR code more nicely, but `wt.exe` hands off and exits immediately, so it can't be tracked
    /// and lost auto-reload — deliberately not used.)
    ///
    /// Raises <see cref="Ended"/> exactly once (on a background thread) when the console closes or
    /// <see cref="EndAsync"/> is called.
    /// </summary>
    internal sealed class RemoteTerminalLauncher : IDisposable
    {
        private Process? _process;
        private volatile bool _active;
        private int _ended; // 0 → not yet; set once via Interlocked

        /// <summary>True between a successful <see cref="StartAsync"/> and <see cref="Ended"/>.</summary>
        public bool IsActive => _active;

        /// <summary>UTC start time — lower bound for the session discovery when the session ends.</summary>
        public DateTime StartedUtc { get; private set; }

        /// <summary>Set when the console couldn't be launched (diagnostics).</summary>
        public string? LastError { get; private set; }

        /// <summary>Raised once when the console closes or <see cref="EndAsync"/> is called.</summary>
        public event Action? Ended;

        /// <summary>
        /// Launches the interactive remote-control session. <paramref name="resumeSessionId"/> is the
        /// current chat's CLI session id (null/empty → start a fresh remote session). Returns false if
        /// the console couldn't be started.
        /// </summary>
        public Task<bool> StartAsync(string claudeExe, string? resumeSessionId, string? workingDirectory, CancellationToken ct)
        {
            StartedUtc = DateTime.UtcNow;
            _ended = 0;
            LastError = null;

            var claudeArgs = string.IsNullOrEmpty(resumeSessionId)
                ? "--remote-control"
                : "--resume " + resumeSessionId + " --remote-control";
            // powershell -NoExit -Command "& '<exe>' <args>" — single-quoted path (spaces ok); -NoExit
            // keeps the window open after the CLI exits so closing it is the clean "done" signal.
            // PowerShell runs claude.exe / .cmd / .bat alike.
            var psCommand = "& '" + claudeExe.Replace("'", "''") + "' " + claudeArgs;
            var psArgs = "-NoExit -Command \"" + psCommand + "\"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = psArgs,
                    UseShellExecute = true, // own console window (no redirection — it's interactive)
                    WorkingDirectory = workingDirectory ?? "",
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (_, __) => FireEnded();
                p.Start();
                _process = p;
                _active = true;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _active = false;
                return Task.FromResult(false);
            }
        }

        /// <summary>Ends the session (closes the console). Idempotent.</summary>
        public Task EndAsync(CancellationToken ct)
        {
            var p = _process;
            try
            {
                if (p != null && !p.HasExited)
                {
                    // Kill the whole tree: PowerShell spawns the node-based CLI child, which a bare
                    // Process.Kill() (no entireProcessTree on net472) would orphan.
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + p.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    killer?.WaitForExit(5000);
                }
            }
            catch
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { /* already gone */ }
            }
            FireEnded();
            return Task.CompletedTask;
        }

        private void FireEnded()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
                return;
            _active = false;
            try { Ended?.Invoke(); } catch { /* listener errors are not ours */ }
        }

        public void Dispose()
        {
            // Tool window / VS closing: leave the console running (it's the user's own window); just
            // release our handle to the Process object.
            try { _process?.Dispose(); } catch { }
        }
    }
}
