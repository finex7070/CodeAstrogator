using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Finds the OS process of one running Bash/PowerShell tool call among the direct children of the
    /// turn's <c>claude.exe</c>, so the card's stop button can kill just that command (the CLI then
    /// reports it as failed with exit code 1 and the turn goes on).
    /// <para>
    /// How the CLI launches commands (measured against CLI 2.1.287, 2026-10-28 — undocumented, re-verify
    /// on a CLI update):
    /// <list type="bullet">
    /// <item><b>PowerShell tool:</b> <c>cmd.exe /d /s /c ""chcp.com" 65001 &gt;nul &amp; "pwsh.exe" … &gt;
    /// "…\tasks\&lt;task_id&gt;.output" 2&gt;&amp;1"</c> — the task id is on the command line.</item>
    /// <item><b>Bash tool:</b> <c>bash.exe -c "source …shell-snapshots… &amp;&amp; eval '&lt;command&gt;' &lt; /dev/null
    /// &amp;&amp; pwd -P &gt;| …"</c> — no task id, but the command itself, single-quoted for bash
    /// (<c>'</c> → <c>'"'"'</c>) and then escaped for the Windows command line.</item>
    /// </list>
    /// Other children (stdio MCP servers, conhost) never match.
    /// </para>
    /// </summary>
    public static class ShellTaskProcess
    {
        /// <summary>Picks the process to kill, or null when it cannot be identified unambiguously.</summary>
        /// <param name="children">Direct children of the turn's CLI process.</param>
        /// <param name="taskId">The command's <c>task_id</c> (from <c>system/task_started</c>).</param>
        /// <param name="toolName"><c>Bash</c> or <c>PowerShell</c>.</param>
        /// <param name="command">The tool input's <c>command</c>.</param>
        public static int? Find(IEnumerable<ChildProcessInfo> children, string? taskId, string? toolName, string? command)
        {
            var list = children.ToList();

            // Exact: the task's output file is on the wrapper's command line (PowerShell tool).
            if (!string.IsNullOrEmpty(taskId))
            {
                foreach (var child in list)
                {
                    if (child.CommandLine.IndexOf(@"\tasks\" + taskId + ".output", StringComparison.OrdinalIgnoreCase) >= 0
                        || child.CommandLine.IndexOf("/tasks/" + taskId + ".output", StringComparison.OrdinalIgnoreCase) >= 0)
                        return child.ProcessId;
                }
            }

            if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase))
                return null;

            var shells = list.Where(IsBashCommandShell).ToList();
            if (!string.IsNullOrEmpty(command))
            {
                var fragment = EscapeForCommandLine(BashEval(command!));
                var matches = shells.Where(c => c.CommandLine.IndexOf(fragment, StringComparison.Ordinal) >= 0).ToList();
                if (matches.Count == 1)
                    return matches[0].ProcessId;
                if (matches.Count > 1)
                    return null; // the same command running twice — don't guess
            }

            // The CLI rewrote the command beyond recognition: only safe when a single shell runs.
            return shells.Count == 1 ? shells[0].ProcessId : (int?)null;
        }

        /// <summary><c>eval '&lt;command&gt;'</c> exactly as the CLI builds it.</summary>
        internal static string BashEval(string command) =>
            "eval '" + command.Replace("'", "'\"'\"'") + "'";

        /// <summary>How a fragment appears inside a quoted Windows command-line argument
        /// (<c>"</c> → <c>\"</c>, backslashes before a quote doubled) — the outer quotes dropped.</summary>
        internal static string EscapeForCommandLine(string fragment)
        {
            var quoted = ClaudeCliProcessHost.Quote(fragment);
            return quoted == fragment ? fragment : quoted.Substring(1, quoted.Length - 2);
        }

        private static bool IsBashCommandShell(ChildProcessInfo child) =>
            (child.Name.Equals("bash.exe", StringComparison.OrdinalIgnoreCase)
             || child.Name.Equals("sh.exe", StringComparison.OrdinalIgnoreCase)
             || child.Name.Equals("bash", StringComparison.OrdinalIgnoreCase))
            && child.CommandLine.IndexOf("eval '", StringComparison.Ordinal) >= 0;
    }
}
