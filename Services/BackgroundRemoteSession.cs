using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Remote Control as a CLI <b>background session</b> (CLI 2.1.29x+, see <see cref="ClaudeBackgroundSessions"/>):
    /// <c>claude --bg [--resume &lt;id&gt;] --remote-control &lt;name&gt;</c> — the CLI's daemon hosts the interactive
    /// session in a pseudo terminal, so no console window opens. A monitor polls <c>claude logs</c> until the
    /// Remote Control link shows up (→ link + QR in the panel) and <c>claude agents --json</c> for busy/idle and
    /// the end of the session. "End" = <c>claude stop</c> (conversation kept) + <c>claude rm</c> (registry entry
    /// only; the transcript stays — verified). The session outlives VS on purpose (you may still be working on
    /// the phone); the bridge re-adopts it on the next start (<see cref="Adopt"/>).
    /// </summary>
    internal sealed class BackgroundRemoteSession : IRemoteSession
    {
        private const int StartTimeoutMs = 60000;
        private const int CallTimeoutMs = 30000;

        private string? _exe;
        private string? _cwd;
        private string? _id;
        private CancellationTokenSource? _monitor;
        private volatile bool _active;
        private int _ended;

        public bool IsActive => _active;
        public DateTime StartedUtc { get; private set; }
        public string? LastError { get; private set; }
        public string? Url { get; private set; }
        public string? SessionId { get; private set; }
        public string? Activity { get; private set; }
        public bool CanOpenTerminal => _id != null;

        /// <summary>The CLI's short id of the background session.</summary>
        public string? BackgroundId => _id;

        public event Action? Ended;
        public event Action? Changed;

        public async Task<bool> StartAsync(string claudeExe, string? resumeSessionId, string? workingDirectory, string? name, CancellationToken ct)
        {
            StartedUtc = DateTime.UtcNow;
            _ended = 0;
            LastError = null;
            Url = null;
            Activity = null;
            _exe = claudeExe;
            _cwd = workingDirectory;
            SessionId = string.IsNullOrEmpty(resumeSessionId) ? null : resumeSessionId;

            var args = "--bg";
            if (!string.IsNullOrEmpty(resumeSessionId))
                args += " --resume " + ClaudeCliProcessHost.Quote(resumeSessionId!);
            args += " --remote-control";
            if (!string.IsNullOrWhiteSpace(name))
                args += " " + ClaudeCliProcessHost.Quote(name!.Trim());

            var run = await ClaudeBackgroundSessions.RunAsync(claudeExe, args, workingDirectory, StartTimeoutMs, ct).ConfigureAwait(false);
            var id = ClaudeBackgroundSessions.ParseStartedId(run.Output);
            if (id == null)
            {
                var text = ClaudeBackgroundSessions.StripAnsi(run.Output).Trim();
                LastError = run.TimedOut ? "the CLI did not answer in time"
                    : text.Length == 0 ? "no output from the CLI"
                    : text.Length > 300 ? text.Substring(text.Length - 300) : text;
                return false;
            }

            _id = id;
            _active = true;
            StartMonitor();
            return true;
        }

        /// <summary>Takes over a background session that is still running (e.g. started before VS was
        /// restarted).</summary>
        public void Adopt(string claudeExe, string? workingDirectory, BackgroundSessionInfo session)
        {
            _exe = claudeExe;
            _cwd = workingDirectory;
            _id = session.Id;
            SessionId = session.SessionId;
            Activity = session.Status;
            StartedUtc = session.StartedAt is long ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTime.UtcNow.AddHours(-1);
            _ended = 0;
            _active = true;
            StartMonitor();
        }

        private void StartMonitor()
        {
            _monitor?.Cancel();
            var cts = new CancellationTokenSource();
            _monitor = cts;
            _ = Task.Run(() => MonitorAsync(cts.Token));
        }

        private async Task MonitorAsync(CancellationToken ct)
        {
            var goneCount = 0;
            while (!ct.IsCancellationRequested && _active)
            {
                try
                {
                    if (Url == null)
                    {
                        var logs = await ClaudeBackgroundSessions.RunAsync(_exe!, "logs " + _id, _cwd, CallTimeoutMs, ct).ConfigureAwait(false);
                        var url = ClaudeBackgroundSessions.ExtractRemoteUrl(logs.Output);
                        if (url != null)
                            Url = url;
                    }

                    var agents = await ClaudeBackgroundSessions.RunAsync(_exe!, "agents --json --all", _cwd, CallTimeoutMs, ct).ConfigureAwait(false);
                    if (!agents.TimedOut && agents.Output.IndexOf('[') >= 0) // else: unknown, try again
                    {
                        var me = ClaudeBackgroundSessions.ParseAgents(agents.Output).FirstOrDefault(s => s.Id == _id);
                        // two observations in a row, so a hiccup of the registry can't end the session
                        goneCount = ClaudeBackgroundSessions.HasEnded(me) ? goneCount + 1 : 0;
                        if (goneCount >= 2)
                        {
                            FireEnded();
                            return;
                        }
                        if (me != null)
                        {
                            Activity = me.Status;
                            if (SessionId == null && !string.IsNullOrEmpty(me.SessionId))
                                SessionId = me.SessionId;
                        }
                    }

                    // every poll is reported — the bridge also mirrors the transcript from it
                    try { Changed?.Invoke(); } catch { /* listener errors are not ours */ }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // a failed poll is retried on the next tick
                }

                try { await Task.Delay(Url == null ? 2000 : 3000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Stops the session (<c>claude stop</c>, conversation kept) and removes its registry
        /// entry (<c>claude rm</c>, the transcript stays). Idempotent.</summary>
        public async Task EndAsync(CancellationToken ct)
        {
            _monitor?.Cancel();
            var id = _id;
            if (id != null && _exe != null)
            {
                await ClaudeBackgroundSessions.RunAsync(_exe, "stop " + id, _cwd, CallTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                await ClaudeBackgroundSessions.RunAsync(_exe, "rm " + id, _cwd, CallTimeoutMs, CancellationToken.None).ConfigureAwait(false);
            }
            FireEnded();
        }

        /// <summary>Opens the same session in a PowerShell console (<c>claude attach &lt;id&gt;</c>) — closing
        /// that window leaves the session running.</summary>
        public void OpenTerminal()
        {
            if (_id == null || _exe == null)
                return;
            try
            {
                var psCommand = "& '" + _exe.Replace("'", "''") + "' attach " + _id;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -Command \"" + psCommand + "\"",
                    UseShellExecute = true,
                    WorkingDirectory = _cwd ?? "",
                })?.Dispose();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private void FireEnded()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
                return;
            _active = false;
            _monitor?.Cancel();
            try { Ended?.Invoke(); } catch { /* listener errors are not ours */ }
        }

        public void Dispose()
        {
            // Tool window / VS closing: the session keeps running (you may still be on the phone); only the
            // polling stops. The next start re-adopts it.
            _monitor?.Cancel();
        }
    }
}
