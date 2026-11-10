using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>One management call of the CLI (<c>claude plugin …</c>, <c>claude mcp …</c>).</summary>
    public sealed class CliCommand
    {
        public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();
        /// <summary>Project/local scopes act on the working directory — the workspace.</summary>
        public string? WorkingDirectory { get; set; }
        /// <summary>Written to stdin, then stdin is closed (e.g. <c>configure --values-stdin</c>).</summary>
        public string? Stdin { get; set; }
        public int TimeoutMs { get; set; } = 60_000;
    }

    /// <summary>What a <see cref="CliCommand"/> printed. stdout and stderr stay apart: with <c>--json</c> the
    /// result is the last stdout line, while a failure's human message goes to stderr after it.</summary>
    public sealed class CliCommandResult
    {
        public int ExitCode { get; set; } = -1;
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public bool TimedOut { get; set; }
        /// <summary>The process could not be started (no CLI, bad path).</summary>
        public string? StartError { get; set; }
    }

    /// <summary>
    /// Runs management commands of the official CLI as hidden processes (no console window, stdin closed
    /// unless input is given, killed on timeout/cancel). The plugin, skill and MCP panels go through this —
    /// never through files the CLI owns. Replaceable for tests via the delegate the clients take.
    /// </summary>
    public static class CliCommandRunner
    {
        public static async Task<CliCommandResult> RunAsync(string exe, CliCommand command, CancellationToken ct)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", command.Args.Select(ClaudeCliProcessHost.Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(command.WorkingDirectory))
                psi.WorkingDirectory = command.WorkingDirectory;

            var result = new CliCommandResult();
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(true);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    if (command.Stdin != null)
                    {
                        var input = new System.IO.StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
                        await input.WriteAsync(command.Stdin).ConfigureAwait(false);
                        await input.FlushAsync().ConfigureAwait(false);
                        input.Close();
                    }
                    else
                    {
                        process.StandardInput.Close();
                    }
                }
                catch { /* the process may have exited already */ }

                using (ct.Register(() => exited.TrySetResult(false)))
                {
                    // waits for the process, not for EOF: a server it starts may keep inherited handles open
                    var done = await Task.WhenAny(exited.Task, Task.Delay(command.TimeoutMs)).ConfigureAwait(false);
                    if (done != exited.Task || !process.HasExited)
                    {
                        result.TimedOut = true;
                        try { if (!process.HasExited) ProcessTree.KillTree(process.Id); } catch { }
                    }
                }
                await Task.Delay(150).ConfigureAwait(false); // let the async readers deliver the tail
                try { result.ExitCode = process.HasExited ? process.ExitCode : -1; } catch { }
            }
            catch (Exception ex)
            {
                result.StartError = ex.Message;
            }
            lock (stdout)
                result.StdOut = stdout.ToString();
            lock (stderr)
                result.StdErr = stderr.ToString();
            return result;
        }
    }
}
