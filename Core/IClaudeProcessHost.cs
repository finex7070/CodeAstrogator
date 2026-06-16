using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Describes one turn to run against the Claude Code CLI.
    /// </summary>
    public sealed class ClaudeTurnRequest
    {
        public string Prompt { get; set; } = "";

        /// <summary>Resolved path to the claude executable (claude.exe / claude.cmd).</summary>
        public string ExecutablePath { get; set; } = "";

        /// <summary>Session to resume (null/empty for the first turn).</summary>
        public string? SessionId { get; set; }

        /// <summary>Model id/alias passed as --model (null = CLI default).</summary>
        public string? Model { get; set; }

        /// <summary>Effort level passed as --effort: low|medium|high|xhigh|max (null = CLI default).</summary>
        public string? Effort { get; set; }

        /// <summary>Working directory of the child process = open solution/folder.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>CLI permission mode (default | acceptEdits | plan | bypassPermissions).</summary>
        public string? PermissionMode { get; set; }

        /// <summary>Extra arguments, e.g. --mcp-config / --permission-prompt-tool once the bridge is wired.</summary>
        public IList<string> ExtraArgs { get; } = new List<string>();

        /// <summary>Environment overrides for the child process (e.g. ANTHROPIC_API_KEY).</summary>
        public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>();
    }

    public sealed class ClaudeTurnExit
    {
        public int ExitCode { get; set; }
        public bool WasCancelled { get; set; }
        public string StdErrTail { get; set; } = "";
    }

    /// <summary>
    /// Abstraction over the CLI child process (Teil A §A3). v1 runs one
    /// <c>claude -p … --output-format stream-json</c> process per turn; the interface
    /// allows switching to a persistent bidirectional process later without UI changes.
    /// </summary>
    public interface IClaudeProcessHost
    {
        /// <summary>
        /// Runs one turn. <paramref name="onStdoutLine"/> is invoked (on a background
        /// thread) for every NDJSON line. Cancelling <paramref name="ct"/> kills the process.
        /// </summary>
        Task<ClaudeTurnExit> RunTurnAsync(
            ClaudeTurnRequest request,
            System.Action<string> onStdoutLine,
            CancellationToken ct);
    }
}
