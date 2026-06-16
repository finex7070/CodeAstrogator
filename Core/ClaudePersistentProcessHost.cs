using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Persistent bidirectional CLI host (Teil A §A3, roadmap #2): keeps ONE long-lived
    /// <c>claude -p --input-format stream-json --output-format stream-json</c> process and
    /// feeds one user turn per <see cref="RunTurnAsync"/> over stdin, reading until the
    /// turn's <c>result</c> line. The process survives across turns (no per-turn spawn =
    /// lower latency) and is restarted transparently when a startup-fixed parameter
    /// changes (model/effort/permission/cwd) or the conversation switches (--resume).
    ///
    /// Stop interrupts in place via a control_request (the process stays alive); if the CLI
    /// does not end the turn within a short window, the process is killed as a fallback.
    ///
    /// Same <see cref="IClaudeProcessHost"/> contract as the per-turn host, so the session
    /// service and UI are unchanged. Verified against CLI 2.1.162.
    /// </summary>
    public sealed class ClaudePersistentProcessHost : IClaudeProcessHost, IDisposable
    {
        /// <summary>How long to wait for the CLI to end an interrupted turn before killing the process.</summary>
        private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(4);

        /// <summary>Field separator for the startup-flag signature (cannot occur in a flag value).</summary>
        private const char SigSep = (char)0x1F;

        private readonly object _gate = new object();
        private Process? _proc;
        private StreamWriter? _stdin;      // UTF-8 writer over the process stdin (net472 has no StandardInputEncoding)
        private string? _flagSig;          // startup flags of the running process (excl. resume/prompt)
        private string? _startResumeId;    // the --resume id passed at startup (null = fresh)
        private volatile string? _liveSessionId; // session id the running process is actually on
        private readonly StderrTail _stderr = new StderrTail(40);
        private volatile TurnState? _currentTurn;
        private bool _disposed;

        public async Task<ClaudeTurnExit> RunTurnAsync(
            ClaudeTurnRequest request,
            Action<string> onStdoutLine,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(request.ExecutablePath))
                throw new InvalidOperationException("Claude executable path is not set.");

            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ClaudePersistentProcessHost));
                EnsureProcess(request);
            }

            var turn = new TurnState(onStdoutLine);
            _currentTurn = turn;

            // Send the user turn (stream-json). Interrupt/kill writes also go under _gate.
            var userLine = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = request.Prompt },
                    },
                },
            }.ToString(Newtonsoft.Json.Formatting.None);

            try
            {
                lock (_gate)
                    WriteLine(userLine);
            }
            catch (Exception ex)
            {
                _currentTurn = null;
                return new ClaudeTurnExit { ExitCode = -1, StdErrTail = "Failed to write to claude stdin: " + ex.Message };
            }

            Timer? killTimer = null;
            using (ct.Register(() =>
            {
                turn.Cancelled = true;
                Interrupt(); // ask the CLI to abort the turn (process stays alive)
                killTimer = new Timer(_ =>
                {
                    // Fallback: the CLI did not end the turn — kill the process.
                    if (!turn.Done.Task.IsCompleted)
                        KillProcess();
                }, null, InterruptGrace, Timeout.InfiniteTimeSpan);
            }))
            {
                try
                {
                    // VSTHRD003: this TCS is ours (completed by HandleLine/OnProcessExited), not foreign work.
#pragma warning disable VSTHRD003
                    return await turn.Done.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                finally
                {
                    killTimer?.Dispose();
                    _currentTurn = null;
                }
            }
        }

        // ── process lifecycle ───────────────────────────────────────────────────

        /// <summary>Returns a process compatible with the request, (re)starting it if needed. Caller holds _gate.</summary>
        private Process EnsureProcess(ClaudeTurnRequest request)
        {
            if (IsCompatible(request))
                return _proc!;

            StopProcess();

            var desiredResume = NullIfEmpty(request.SessionId);
            var psi = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                Arguments = BuildArguments(request, desiredResume),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(request.WorkingDirectory))
                psi.WorkingDirectory = request.WorkingDirectory;
            foreach (var kv in request.Environment)
                psi.EnvironmentVariables[kv.Key] = kv.Value;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _stderr.Add(e.Data); };
            proc.Exited += (_, __) => OnProcessExited(proc);

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // net472 has no ProcessStartInfo.StandardInputEncoding → wrap stdin in a UTF-8 writer.
            _stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };
            _proc = proc;
            _flagSig = FlagSig(request);
            _startResumeId = desiredResume;
            _liveSessionId = null;
            return proc;
        }

        /// <summary>True when the running process can serve this turn without a restart. Caller holds _gate.</summary>
        private bool IsCompatible(ClaudeTurnRequest request)
        {
            if (_proc == null || _proc.HasExited)
                return false;
            if (_flagSig != FlagSig(request))
                return false;

            var desired = NullIfEmpty(request.SessionId);
            if (desired == null)
                // A "fresh conversation" request only reuses a process that has not yet
                // established a session (otherwise session.new must start a new one).
                return _liveSessionId == null && _startResumeId == null;

            // Continuing/resuming a specific conversation: the process must already be on it,
            // either because it created it (live id) or was started resuming it.
            return desired == _liveSessionId || desired == _startResumeId;
        }

        private void OnProcessExited(Process proc)
        {
            // Complete any in-flight turn with the real exit code so the session service can
            // surface the error (and run its "No conversation found" retry).
            var turn = _currentTurn;
            if (turn != null)
            {
                int code;
                try { code = proc.ExitCode; } catch { code = -1; }
                turn.Done.TrySetResult(new ClaudeTurnExit
                {
                    ExitCode = code,
                    WasCancelled = turn.Cancelled,
                    StdErrTail = _stderr.ToString(),
                });
            }
            lock (_gate)
            {
                if (ReferenceEquals(_proc, proc))
                {
                    _proc = null;
                    _liveSessionId = null;
                    _startResumeId = null;
                    _flagSig = null;
                }
            }
        }

        private void HandleLine(string line)
        {
            var turn = _currentTurn;
            if (turn != null)
                turn.OnStdout(line); // forward verbatim to the session service's parser

            // Sniff the line for session id + turn boundary (cheap; the parser does the rest).
            string? type = null, sid = null;
            try
            {
                var obj = JObject.Parse(line);
                type = obj.Value<string>("type");
                sid = obj.Value<string>("session_id");
            }
            catch
            {
                return; // non-JSON noise
            }

            if (!string.IsNullOrEmpty(sid))
                _liveSessionId = sid;

            if (type == "result" && turn != null)
            {
                turn.Done.TrySetResult(new ClaudeTurnExit
                {
                    ExitCode = 0, // the process is still alive; a turn ran to completion
                    WasCancelled = turn.Cancelled,
                    StdErrTail = _stderr.ToString(),
                });
            }
        }

        private void Interrupt()
        {
            var req = new JObject
            {
                ["type"] = "control_request",
                ["request_id"] = "int_" + Guid.NewGuid().ToString("n"),
                ["request"] = new JObject { ["subtype"] = "interrupt" },
            }.ToString(Newtonsoft.Json.Formatting.None);
            try
            {
                lock (_gate)
                {
                    if (_proc != null && !_proc.HasExited)
                        WriteLine(req);
                }
            }
            catch
            {
                // best-effort; the kill-fallback timer covers a failed interrupt
            }
        }

        private void KillProcess()
        {
            lock (_gate)
                StopProcess();
        }

        /// <summary>Kills and clears the running process. Caller holds _gate.</summary>
        private void StopProcess()
        {
            var proc = _proc;
            try { _stdin?.Dispose(); } catch { } // closes stdin (EOF) before the kill
            _stdin = null;
            if (proc == null)
                return;
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch
            {
                // already gone
            }
            try { proc.Dispose(); } catch { }
            _proc = null;
            _liveSessionId = null;
            _startResumeId = null;
            _flagSig = null;
        }

        /// <summary>Writes one newline-terminated JSON line to stdin. Caller holds _gate.</summary>
        private void WriteLine(string line)
        {
            if (_stdin == null)
                throw new InvalidOperationException("claude stdin is not open.");
            _stdin.Write(line);
            _stdin.Write('\n');
            _stdin.Flush();
        }

        // ── argument / signature helpers ─────────────────────────────────────────

        internal static string BuildArguments(ClaudeTurnRequest request, string? resumeId)
        {
            var args = new List<string>
            {
                "-p",
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
            };

            if (!string.IsNullOrEmpty(resumeId))
            {
                args.Add("--resume");
                args.Add(ClaudeCliProcessHost.Quote(resumeId!));
            }
            if (!string.IsNullOrEmpty(request.Model))
            {
                args.Add("--model");
                args.Add(ClaudeCliProcessHost.Quote(request.Model!));
            }
            if (!string.IsNullOrEmpty(request.Effort))
            {
                args.Add("--effort");
                args.Add(request.Effort!);
            }
            if (!string.IsNullOrEmpty(request.PermissionMode) && request.PermissionMode != "default")
            {
                args.Add("--permission-mode");
                args.Add(request.PermissionMode!);
            }
            foreach (var extra in request.ExtraArgs)
                args.Add(extra);

            return string.Join(" ", args);
        }

        /// <summary>Signature of all startup-fixed flags except the resume target (which is tracked separately).</summary>
        private static string FlagSig(ClaudeTurnRequest request)
        {
            var sb = new StringBuilder();
            sb.Append(request.ExecutablePath).Append(SigSep);
            sb.Append(request.Model).Append(SigSep);
            sb.Append(request.Effort).Append(SigSep);
            sb.Append(request.PermissionMode).Append(SigSep);
            sb.Append(request.WorkingDirectory).Append(SigSep);
            sb.Append(string.Join(" ", request.ExtraArgs)).Append(SigSep);
            foreach (var kv in SortedEnv(request.Environment))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(SigSep);
            return sb.ToString();
        }

        private static IEnumerable<KeyValuePair<string, string>> SortedEnv(IDictionary<string, string> env)
        {
            var keys = new List<string>(env.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys)
                yield return new KeyValuePair<string, string>(k, env[k]);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                StopProcess();
            }
        }

        /// <summary>State of the single in-flight turn.</summary>
        private sealed class TurnState
        {
            public readonly Action<string> OnStdout;
            public readonly TaskCompletionSource<ClaudeTurnExit> Done =
                new TaskCompletionSource<ClaudeTurnExit>(TaskCreationOptions.RunContinuationsAsynchronously);
            public volatile bool Cancelled;

            public TurnState(Action<string> onStdout) => OnStdout = onStdout;
        }

        /// <summary>Keeps the last N lines of stderr for diagnostics (thread-safe).</summary>
        private sealed class StderrTail
        {
            private readonly object _lock = new object();
            private readonly Queue<string> _lines = new Queue<string>();
            private readonly int _max;

            public StderrTail(int max) => _max = max;

            public void Add(string line)
            {
                lock (_lock)
                {
                    _lines.Enqueue(line);
                    while (_lines.Count > _max)
                        _lines.Dequeue();
                }
            }

            public override string ToString()
            {
                lock (_lock)
                    return string.Join("\n", _lines);
            }
        }
    }
}
