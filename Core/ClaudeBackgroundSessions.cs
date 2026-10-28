using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>One entry of <c>claude agents --json [--all]</c>.</summary>
    public sealed class BackgroundSessionInfo
    {
        /// <summary>Short id of a background session (<c>--bg</c>); null for interactive ones.</summary>
        public string? Id { get; set; }
        public string? SessionId { get; set; }
        /// <summary><c>background</c> | <c>interactive</c>.</summary>
        public string? Kind { get; set; }
        /// <summary><c>busy</c> | <c>idle</c> (absent once stopped).</summary>
        public string? Status { get; set; }
        /// <summary>Background sessions: e.g. <c>blocked</c> (waiting for input) or <c>stopped</c>.</summary>
        public string? State { get; set; }
        public int? Pid { get; set; }
        public string? Cwd { get; set; }
        public string? Name { get; set; }
        /// <summary>Start time, Unix ms.</summary>
        public long? StartedAt { get; set; }
    }

    /// <summary>
    /// Background sessions of the CLI (2.1.29x): <c>claude --bg [--resume id] --remote-control [name]</c>
    /// starts an interactive session hosted by the CLI's own daemon (<c>claude daemon run</c> →
    /// <c>--bg-pty-host</c> pseudo terminal) and returns at once, printing a short id. <c>agents --json</c>
    /// lists sessions without a TTY, <c>logs &lt;id&gt;</c> prints the session's terminal (incl. the Remote
    /// Control link), <c>stop</c> ends it (conversation kept), <c>rm</c> drops the registry entry (the
    /// transcript stays), <c>attach</c> opens it in a terminal. Measured against CLI 2.1.294 (2026-10-28) —
    /// undocumented output formats, re-verify on a CLI update. Purely the official binary; no network.
    /// </summary>
    public static class ClaudeBackgroundSessions
    {
        /// <summary>Output and exit code of one short CLI call.</summary>
        public sealed class CliRun
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = "";
            public bool TimedOut { get; set; }
        }

        private static readonly Regex AnsiEscape = new Regex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*(\x07|\x1b\\)", RegexOptions.Compiled);
        private static readonly Regex StartedId = new Regex(@"backgrounded\W+([0-9a-f]{6,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StopHint = new Regex(@"claude\s+stop\s+([0-9a-f]{6,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RemoteUrl = new Regex(@"https://claude\.ai/code/session_[A-Za-z0-9_-]+", RegexOptions.Compiled);

        /// <summary>Strips terminal escape sequences (cursor moves, colours, clears).</summary>
        public static string StripAnsi(string text) => AnsiEscape.Replace(text ?? "", "");

        /// <summary>The short id <c>claude --bg</c> prints ("backgrounded · 8a6d7f9e (idle …)", then hints
        /// like "claude stop 8a6d7f9e"). Null when the output has none (the start failed).</summary>
        public static string? ParseStartedId(string output)
        {
            var text = StripAnsi(output);
            var m = StartedId.Match(text);
            if (m.Success)
                return m.Groups[1].Value;
            m = StopHint.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Parses <c>agents --json</c>; empty on anything unexpected.</summary>
        public static IReadOnlyList<BackgroundSessionInfo> ParseAgents(string json)
        {
            var list = new List<BackgroundSessionInfo>();
            try
            {
                var start = json.IndexOf('[');
                if (start < 0)
                    return list;
                foreach (var t in JArray.Parse(json.Substring(start)))
                {
                    if (t is not JObject o)
                        continue;
                    list.Add(new BackgroundSessionInfo
                    {
                        Id = o.Value<string>("id"),
                        SessionId = o.Value<string>("sessionId"),
                        Kind = o.Value<string>("kind"),
                        Status = o.Value<string>("status"),
                        State = o.Value<string>("state"),
                        Pid = o.Value<int?>("pid"),
                        Cwd = o.Value<string>("cwd"),
                        Name = o.Value<string>("name"),
                        StartedAt = o.Value<long?>("startedAt"),
                    });
                }
            }
            catch
            {
                // not JSON (older CLI, error text) → nothing known
            }
            return list;
        }

        /// <summary>The Remote Control link from <c>logs</c> ("/remote-control is active · … at
        /// https://claude.ai/code/session_…"); the last one wins. Null while it is still connecting.</summary>
        public static string? ExtractRemoteUrl(string logs)
        {
            string? last = null;
            foreach (Match m in RemoteUrl.Matches(StripAnsi(logs)))
                last = m.Value;
            return last;
        }

        /// <summary>True when a background session is gone: no longer listed, or listed as stopped/exited
        /// (a stopped entry carries no pid).</summary>
        public static bool HasEnded(BackgroundSessionInfo? session)
        {
            if (session == null)
                return true;
            var state = session.State ?? "";
            if (state.Equals("stopped", StringComparison.OrdinalIgnoreCase)
                || state.Equals("exited", StringComparison.OrdinalIgnoreCase)
                || state.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || state.Equals("error", StringComparison.OrdinalIgnoreCase))
                return true;
            return session.Pid == null && string.IsNullOrEmpty(session.Status);
        }

        /// <summary>Runs one short CLI call hidden and returns its output. Waits for the process, not for
        /// EOF on its pipes (a daemon it spawns might keep inherited handles open).</summary>
        public static async Task<CliRun> RunAsync(string exe, string arguments, string? workingDirectory,
            int timeoutMs, CancellationToken ct)
        {
            var output = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(true);
            DataReceivedEventHandler collect = (_, e) =>
            {
                if (e.Data == null)
                    return;
                lock (output)
                    output.AppendLine(e.Data);
            };
            process.OutputDataReceived += collect;
            process.ErrorDataReceived += collect;

            var run = new CliRun();
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); } catch { }

                using (ct.Register(() => exited.TrySetResult(false)))
                {
                    var done = await Task.WhenAny(exited.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (done != exited.Task || !process.HasExited)
                    {
                        run.TimedOut = true;
                        try { if (!process.HasExited) process.Kill(); } catch { }
                    }
                }
                await Task.Delay(150).ConfigureAwait(false); // let the async readers deliver the tail
                try { run.ExitCode = process.HasExited ? process.ExitCode : -1; } catch { }
            }
            catch (Exception ex)
            {
                lock (output)
                    output.AppendLine(ex.Message);
            }
            lock (output)
                run.Output = output.ToString();
            return run;
        }
    }
}
