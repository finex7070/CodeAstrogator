using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Runs <c>claude -p</c> with stream-json output, one process per turn (Teil A §A3).
    /// The prompt is written to stdin (not argv) so multiline prompts survive the
    /// Windows command line and the npm .cmd shim unscathed.
    /// </summary>
    public sealed class ClaudeCliProcessHost : IClaudeProcessHost
    {
        public async Task<ClaudeTurnExit> RunTurnAsync(
            ClaudeTurnRequest request,
            Action<string> onStdoutLine,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(request.ExecutablePath))
                throw new InvalidOperationException("Claude executable path is not set.");

            var psi = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                Arguments = BuildArguments(request),
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

            var stderrTail = new ConcurrentTail(40);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) =>
            {
                try { exited.TrySetResult(process.ExitCode); }
                catch { exited.TrySetResult(-1); }
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    onStdoutLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderrTail.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // -p without a prompt argument reads the prompt from stdin until EOF.
            await process.StandardInput.WriteAsync(request.Prompt).ConfigureAwait(false);
            process.StandardInput.Close();

            var cancelled = false;
            using (ct.Register(() =>
            {
                cancelled = true;
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch
                {
                    // process already gone
                }
            }))
            {
                var exitCode = await exited.Task.ConfigureAwait(false);
                // Drain async output handlers.
                try { process.WaitForExit(); } catch { }

                return new ClaudeTurnExit
                {
                    ExitCode = exitCode,
                    WasCancelled = cancelled,
                    StdErrTail = stderrTail.ToString(),
                };
            }
        }

        internal static string BuildArguments(ClaudeTurnRequest request)
        {
            var args = new List<string>
            {
                "-p", // prompt arrives via stdin
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
            };

            if (!string.IsNullOrEmpty(request.SessionId))
            {
                args.Add("--resume");
                args.Add(Quote(request.SessionId!));
            }

            if (!string.IsNullOrEmpty(request.Model))
            {
                args.Add("--model");
                args.Add(Quote(request.Model!));
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

        /// <summary>
        /// Quotes an argument for the Windows command line (CommandLineToArgvW rules:
        /// backslashes before a quote must be doubled, embedded quotes escaped).
        /// </summary>
        internal static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) < 0)
                return value;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append('"');
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Keeps the last N lines of stderr for diagnostics.</summary>
        private sealed class ConcurrentTail
        {
            private readonly object _lock = new object();
            private readonly Queue<string> _lines = new Queue<string>();
            private readonly int _max;

            public ConcurrentTail(int max) => _max = max;

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
