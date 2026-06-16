using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>Diff payload for Edit/Write style tools (drives the inline-diff card).</summary>
    public sealed class PermissionDiff
    {
        public string Path { get; set; } = "";
        public string OldText { get; set; } = "";
        public string NewText { get; set; } = "";
    }

    public sealed class PermissionRequest
    {
        public string RequestId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public JObject Input { get; set; } = new JObject();
        public PermissionDiff? Diff { get; set; }
    }

    /// <summary>Contract of the --permission-prompt-tool MCP tool (Teil A §A5).</summary>
    public sealed class PermissionDecision
    {
        /// <summary>"allow" or "deny".</summary>
        public string Behavior { get; set; } = "deny";

        /// <summary>Optionally edited tool input returned on allow.</summary>
        public JObject? UpdatedInput { get; set; }

        /// <summary>Reason shown to Claude on deny.</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// Permission bridge (Teil A §A5): an in-process localhost MCP server exposing
    /// the tool referenced by <c>--permission-prompt-tool mcp__vsbridge__permission_prompt</c>.
    /// The CLI calls the tool for any non-pre-approved tool use; the implementation
    /// raises the request to the UI (permission.request) and blocks until the user
    /// decides (permission.decision).
    ///
    /// v1: interface only — the UI side of the flow is fully implemented and is
    /// exercised by the mock adapter; this gets wired when the MCP server lands.
    /// </summary>
    public interface IPermissionBridge
    {
        /// <summary>True once the MCP server is listening and the CLI flags should be passed.</summary>
        bool IsAvailable { get; }

        /// <summary>Path to the generated --mcp-config file (null while unavailable).</summary>
        string? McpConfigPath { get; }

        /// <summary>
        /// Raised when the CLI asks for permission; the handler must complete the
        /// returned task with the user's decision (the CLI blocks meanwhile).
        /// </summary>
        Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct);
    }
}
