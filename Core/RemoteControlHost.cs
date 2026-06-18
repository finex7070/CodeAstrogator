using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodeAstrogator.Core
{
    /// <summary>Snapshot for the host→web remote.state message.</summary>
    public sealed class RemoteControlState
    {
        /// <summary>starting | ready | stopped | error</summary>
        public string State { get; set; } = "";
        public string? Url { get; set; }
        public int ActiveSessions { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Line-wise parser for the `claude remote-control` stdout. With stdout
    /// redirected the TUI degrades to plain text plus ANSI cursor codes
    /// (verified against CLI 2.1.161):
    ///   ·|· Connecting · &lt;dir&gt; · &lt;branch&gt;
    ///   ·✔︎· Ready · &lt;dir&gt; · &lt;branch&gt;
    ///       Capacity: 0/32 · New sessions will be created …
    ///   Code anywhere with the Claude mobile app or https://claude.ai/code?environment=env_…
    /// Lines repeat on every TUI redraw; <see cref="Push"/> only returns a state
    /// when something actually changed.
    /// </summary>
    public sealed class RemoteControlOutputParser
    {
        private static readonly Regex Ansi = new Regex(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex Url = new Regex(@"https://claude\.ai/\S+", RegexOptions.Compiled);
        private static readonly Regex Capacity = new Regex(@"Capacity:\s*(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

        private string? _url;
        private int _activeSessions;

        public string? CurrentUrl => _url;

        /// <summary>Feeds one raw stdout line; returns a new state or null (no change).</summary>
        public RemoteControlState? Push(string rawLine)
        {
            var line = Ansi.Replace(rawLine ?? "", "");

            var capacity = Capacity.Match(line);
            if (capacity.Success && int.TryParse(capacity.Groups[1].Value, out var active))
            {
                if (_url != null && active != _activeSessions)
                {
                    _activeSessions = active;
                    return Snapshot("ready");
                }
                _activeSessions = active;
                return null;
            }

            var url = Url.Match(line);
            if (url.Success && _url != url.Value)
            {
                _url = url.Value;
                return Snapshot("ready");
            }

            return null;
        }

        private RemoteControlState Snapshot(string state) => new RemoteControlState
        {
            State = state,
            Url = _url,
            ActiveSessions = _activeSessions,
        };
    }

    /// <summary>
    /// Hosts the long-lived `claude remote-control` server process (at most one per
    /// tool window). State changes are raised on background threads — callers
    /// marshal as needed.
    /// </summary>
    public sealed class RemoteControlHost : IDisposable
    {
        private readonly object _lock = new object();
        private Process? _process;
        private RemoteControlOutputParser? _parser;
        private string _stderrTail = "";
        private bool _stopRequested;

        public event Action<RemoteControlState>? StateChanged;

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    try { return _process != null && !_process.HasExited; }
                    catch { return false; }
                }
            }
        }

        /// <summary>UTC start time — lower bound for the session discovery on stop.</summary>
        public DateTime StartedUtc { get; private set; }

        public string? Url
        {
            get { lock (_lock) return _parser?.CurrentUrl; }
        }

        public void Start(string executablePath, string? workingDirectory)
        {
            lock (_lock)
            {
                if (IsRunningUnsafe())
                    return;

                _parser = new RemoteControlOutputParser();
                _stderrTail = "";
                _stopRequested = false;
                StartedUtc = DateTime.UtcNow;

                // `remote-control` refuses to start in an untrusted workspace (unlike headless
                // `claude -p`). Pre-accept the trust dialog for the directory the user opened so a
                // first-ever remote session works without dropping to a terminal. (Best-effort.)
                ClaudeWorkspaceTrust.EnsureTrusted(workingDirectory);

                var psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "remote-control",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                if (!string.IsNullOrEmpty(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                        return;
                    RemoteControlState? state;
                    lock (_lock)
                        state = _parser?.Push(e.Data);
                    if (state != null)
                        StateChanged?.Invoke(state);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                        return;
                    lock (_lock)
                        _stderrTail = (_stderrTail.Length > 2000 ? _stderrTail.Substring(_stderrTail.Length - 2000) : _stderrTail)
                                      + e.Data + "\n";
                };
                process.Exited += (_, __) =>
                {
                    bool expected;
                    string stderr;
                    lock (_lock)
                    {
                        expected = _stopRequested;
                        stderr = _stderrTail.Trim();
                        _process = null;
                    }
                    if (!expected)
                    {
                        StateChanged?.Invoke(new RemoteControlState
                        {
                            State = "error",
                            Message = stderr.Length > 0
                                ? stderr
                                : "Remote control exited unexpectedly. Make sure the CLI is signed in (a subscription is required) and the workspace is trusted (run `claude` once in this directory).",
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
            }

            StateChanged?.Invoke(new RemoteControlState { State = "starting" });
        }

        /// <summary>Kills the whole process tree (the npm .cmd shim spawns a node child
        /// that would otherwise keep the remote session alive).</summary>
        public void Stop()
        {
            Process? process;
            lock (_lock)
            {
                _stopRequested = true;
                process = _process;
                _process = null;
            }
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + process.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    killer?.WaitForExit(5000);
                }
            }
            catch
            {
                try { process.Kill(); } catch { /* already gone */ }
            }
            finally
            {
                process.Dispose();
            }
        }

        private bool IsRunningUnsafe()
        {
            try { return _process != null && !_process.HasExited; }
            catch { return false; }
        }

        public void Dispose() => Stop();
    }
}
