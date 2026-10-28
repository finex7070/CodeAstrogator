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

        /// <summary>Images sent as base64 blocks with the prompt (see <see cref="CliImageAttachments"/>).</summary>
        public IList<string> ImagePaths { get; } = new List<string>();

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

        /// <summary>Called with the CLI's process id right after it started (the per-command stop
        /// button looks for the command's shell among this process's children).</summary>
        public System.Action<int>? ProcessStarted { get; set; }

        /// <summary>Called once the first user message is written, with the still-open stdin. The
        /// session service closes it at the turn's result — or keeps it open while background tasks
        /// run, so the next prompt can go into the same process (see <see cref="IClaudeTurnInput"/>).</summary>
        public System.Action<IClaudeTurnInput>? InputOpened { get; set; }
    }

    /// <summary>
    /// The stdin of a running CLI process (stream-json input). Normally closed at the turn's
    /// <c>result</c> (the CLI then finishes up and exits); kept open only while background tasks
    /// run, so a further user message can be written into the same process. Thread-safe.
    /// </summary>
    public interface IClaudeTurnInput
    {
        /// <summary>Writes one NDJSON line. False when stdin is closed or the process is gone.</summary>
        bool TrySend(string line);

        /// <summary>Closes stdin (EOF). Idempotent.</summary>
        void Close();
    }

    public sealed class ClaudeTurnExit
    {
        public int ExitCode { get; set; }
        public bool WasCancelled { get; set; }
        public string StdErrTail { get; set; } = "";
    }

    /// <summary>
    /// Abstraction over the CLI child process (Teil A §A3): one
    /// <c>claude -p … --output-format stream-json</c> process per turn. The interface keeps the
    /// session service testable (the tests inject stub hosts); the bidirectional long-lived host
    /// that used to sit behind it was removed in 0.8.0 — see docs/NOTES.md.
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
