using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Domain events produced by <see cref="NdjsonParser"/> from the CLI's
    /// stream-json output (one NDJSON line can yield zero or more events).
    /// These are CLI-agnostic; the bridge layer maps them onto the
    /// host→web message contract (assistant.start/delta/end, tool.use, …).
    /// </summary>
    public abstract class ClaudeEvent
    {
    }

    /// <summary>system/init — first event of a turn; carries the session id.</summary>
    public sealed class SessionInitEvent : ClaudeEvent
    {
        public string SessionId { get; set; } = "";
        public string? Model { get; set; }
        public string? Cwd { get; set; }

        /// <summary>Slash commands the CLI actually supports in this (headless)
        /// environment — bare names without the leading slash. Interactive-TUI-only
        /// commands (/help, /remote-control, …) are not in this list.</summary>
        public IReadOnlyList<string> SlashCommands { get; set; } = new string[0];
    }

    /// <summary>A new assistant message starts streaming (maps to assistant.start).</summary>
    public sealed class AssistantStartEvent : ClaudeEvent
    {
        public string MessageId { get; set; } = "";
    }

    /// <summary>Incremental text (text_delta) for the current assistant message.</summary>
    public sealed class AssistantDeltaEvent : ClaudeEvent
    {
        public string MessageId { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>The current assistant message finished streaming (maps to assistant.end).</summary>
    public sealed class AssistantEndEvent : ClaudeEvent
    {
        public string MessageId { get; set; } = "";
    }

    /// <summary>An extended-thinking block starts streaming (maps to thinking.start).</summary>
    public sealed class ThinkingStartEvent : ClaudeEvent
    {
        public string BlockId { get; set; } = "";
    }

    /// <summary>Incremental thinking text (thinking_delta). The print-mode CLI redacts
    /// the text (empty) and only reports an estimated token count.</summary>
    public sealed class ThinkingDeltaEvent : ClaudeEvent
    {
        public string BlockId { get; set; } = "";
        public string Text { get; set; } = "";
        public long EstimatedTokens { get; set; }
    }

    /// <summary>The thinking block finished (maps to thinking.end).</summary>
    public sealed class ThinkingEndEvent : ClaudeEvent
    {
        public string BlockId { get; set; } = "";
    }

    /// <summary>system/compact_boundary — the CLI compacted the conversation context.
    /// compact_metadata carries the context size before/after (0 if absent).</summary>
    public sealed class CompactBoundaryEvent : ClaudeEvent
    {
        public long PreTokens { get; set; }
        public long PostTokens { get; set; }
    }

    /// <summary>system/status — transient CLI activity (e.g. "compacting", which can
    /// take a long time); cleared by the CLI with status null.</summary>
    public sealed class StatusEvent : ClaudeEvent
    {
        public string? Status { get; set; }
    }

    /// <summary>
    /// <c>system/task_started</c> — the CLI started running a shell command (Bash / PowerShell tool,
    /// <c>task_type: "local_bash"</c>). While it runs, the CLI writes the command's output live to
    /// <c>%TEMP%\claude\&lt;munged cwd&gt;\&lt;session&gt;\tasks\&lt;task_id&gt;.output</c> (see
    /// <see cref="TaskOutputWatcher"/>); the stream itself stays silent until the tool_result.
    /// </summary>
    public sealed class TaskStartedEvent : ClaudeEvent
    {
        public string TaskId { get; set; } = "";
        /// <summary>The tool_use this task runs for — i.e. the tool card to stream into.</summary>
        public string ToolUseId { get; set; } = "";
        public string? TaskType { get; set; }
        public bool IsBackgrounded { get; set; }
        public string? SessionId { get; set; }
    }

    /// <summary>
    /// <c>system/task_notification</c> — a task reached a final state (<c>completed</c>,
    /// <c>stopped</c>, …). Fires after every foreground command as well as for background tasks
    /// (including the queued one that opens a later run, see <see cref="NotificationTurnResultEvent"/>).
    /// </summary>
    public sealed class TaskNotificationEvent : ClaudeEvent
    {
        public string TaskId { get; set; } = "";
        public string? ToolUseId { get; set; }
        public string? Status { get; set; }
    }

    /// <summary>Claude invoked a tool (maps to tool.use).</summary>
    public sealed class ToolUseEvent : ClaudeEvent
    {
        public string ToolUseId { get; set; } = "";
        public string Name { get; set; } = "";
        public JObject Input { get; set; } = new JObject();
    }

    /// <summary>Result of a tool invocation came back (maps to tool.result).</summary>
    public sealed class ToolResultEvent : ClaudeEvent
    {
        public string ToolUseId { get; set; } = "";
        public bool IsError { get; set; }
        public string Summary { get; set; } = "";

        /// <summary>Set when this is a subagent's (Agent/Task tool) hand-back — read from the message's
        /// structured <c>tool_use_result</c>; <see cref="Summary"/> is then the subagent's clean report.</summary>
        public SubagentStats? Agent { get; set; }
    }

    /// <summary>
    /// What a finished subagent reports (CLI 2.1.287, <c>tool_use_result</c> of the Agent tool): it no
    /// longer emits a <c>result</c> event of its own (so there is no <c>parent_tool_use_id</c> result and no
    /// cost for it). The tool_result TEXT wraps the report in a harness frame ("[Subagent hand-back] The text
    /// below is the final report …", indented report, <c>agentId: …</c>, <c>&lt;usage&gt;…&lt;/usage&gt;</c>),
    /// which is meant for the model, not for display.
    /// </summary>
    public sealed class SubagentStats
    {
        public string? Status { get; set; }
        public long DurationMs { get; set; }
        /// <summary><c>totalTokens</c> — everything the subagent processed, its context included.</summary>
        public long TotalTokens { get; set; }
        public int ToolUses { get; set; }
        public string? Model { get; set; }
    }

    /// <summary>result — end of turn with cost/usage (maps to turn.result).</summary>
    public sealed class TurnResultEvent : ClaudeEvent
    {
        public string SessionId { get; set; } = "";

        /// <summary>0 for local-only turns (e.g. /help) — those create NO resumable
        /// conversation; resuming their session_id fails with "No conversation found".</summary>
        public int NumTurns { get; set; }

        public bool IsError { get; set; }

        /// <summary><c>total_cost_usd</c> — NOT this turn's cost: the CLI keeps it cumulative for the
        /// whole session, across turns in one process and across <c>--resume</c> (measured 2.1.287).
        /// API list-price basis (<c>modelUsage.*.costBasis: "list"</c>).</summary>
        public double CostUsd { get; set; }
        public long DurationMs { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }

        /// <summary>Input/output of the LAST assistant message in the turn — i.e. the real
        /// context-window occupancy after the turn. Distinct from <see cref="InputTokens"/>/
        /// <see cref="OutputTokens"/>, which the CLI aggregates over every API round-trip in the
        /// turn (each re-reads the cached context, so the aggregate vastly over-counts context).</summary>
        public long ContextInputTokens { get; set; }
        public long ContextOutputTokens { get; set; }

        /// <summary>Size of the main model's context window (<c>modelUsage.*.contextWindow</c>, e.g.
        /// 1000000 for <c>claude-opus-5-5[1m]</c>); 0 when the result carries none (local turns). With
        /// several models in the turn (subagents) the one that read the most input is the main loop.</summary>
        public long ContextWindow { get; set; }
        public string? ResultText { get; set; }

        /// <summary>Set when this result belongs to a subagent (Task tool) rather than the main
        /// turn — the CLI tags subagent results with <c>parent_tool_use_id</c>. Such results are
        /// rendered as a subordinate agent footer, not as a (duplicate-looking) turn end.</summary>
        public string? ParentToolUseId { get; set; }
    }

    /// <summary>
    /// A <c>result</c> the CLI emits for a <b>queued background-task notification</b>, not for the
    /// user's prompt. When a <c>run_in_background</c> task from an earlier turn reported back, CLI
    /// 2.1.280 opens the next <c>-p</c> run with <c>system/task_notification</c> → <c>system/init</c>
    /// → <c>result</c> (<c>num_turns: 0</c>, empty text, ~0 ms, the unchanged session cost) and only
    /// then starts the real turn with a second <c>system/init</c>. Treating that first result as the
    /// turn end drew a "0s · $x" footer the moment a prompt was sent and ran the end-of-turn
    /// bookkeeping (changed-files review, usage refresh) before the turn had even begun — so it gets
    /// its own event, which the bridge and session service ignore.
    /// <para>Note: <c>task_notification</c> also fires after every ordinary foreground command
    /// mid-turn. That is harmless here — only a result with <c>num_turns: 0</c> is diverted, and a
    /// turn that ran a command always reports at least one turn.</para>
    /// </summary>
    public sealed class NotificationTurnResultEvent : ClaudeEvent
    {
        public string SessionId { get; set; } = "";
        public long DurationMs { get; set; }

        /// <summary>The session's cost total before the user's prompt runs (unchanged by this zero-turn
        /// result) — a baseline for the turn cost (see <see cref="TurnResultEvent.CostUsd"/>).</summary>
        public double CostUsd { get; set; }
    }

    /// <summary>
    /// <c>prompt_suggestion</c> — the CLI's prediction of the user's next prompt (2-12 words), emitted
    /// after the turn's <c>result</c> when launched with <c>--prompt-suggestions true</c>. Only surfaced
    /// with <c>--input-format stream-json</c>; arrives ~4-11 s after the result, after which the
    /// process exits (measured against CLI 2.1.287). The CLI stays silent when no next step is obvious.
    /// </summary>
    public sealed class PromptSuggestionEvent : ClaudeEvent
    {
        public string Suggestion { get; set; } = "";
        public string? SessionId { get; set; }
    }

    /// <summary>
    /// The CLI's context breakdown — its <c>control_response</c> to our <c>get_context_usage</c> request
    /// (see <see cref="StreamJsonInput.BuildContextUsageRequest"/>), sent after each turn's result.
    /// Undocumented; measured against CLI 2.1.294.
    /// </summary>
    public sealed class ContextUsageEvent : ClaudeEvent
    {
        /// <summary>Tokens the CLI counts as occupied (everything except free space and deferred tools).</summary>
        public long TotalTokens { get; set; }
        public long MaxTokens { get; set; }
        public int Percentage { get; set; }
        public long AutoCompactThreshold { get; set; }
        public bool AutoCompactEnabled { get; set; }
        public string? Model { get; set; }

        /// <summary>In the CLI's order: System prompt, System tools, MCP tools, …, Free space.</summary>
        public List<ContextCategory> Categories { get; } = new List<ContextCategory>();

        public List<ContextItem> MemoryFiles { get; } = new List<ContextItem>();

        /// <summary>One entry per MCP server: <see cref="ContextItem.Count"/> = its tools,
        /// <see cref="ContextItem.Tokens"/> = what its loaded (non-deferred) tools take up.</summary>
        public List<ContextItem> McpServers { get; } = new List<ContextItem>();

        public List<ContextItem> Agents { get; } = new List<ContextItem>();
        public List<ContextItem> Skills { get; } = new List<ContextItem>();
    }

    public sealed class ContextCategory
    {
        public string Name { get; set; } = "";
        public long Tokens { get; set; }

        /// <summary><c>used</c> | <c>deferred</c> (tool definitions not loaded into the context) |
        /// <c>buffer</c> (autocompact reserve) | <c>free</c>.</summary>
        public string Kind { get; set; } = "used";
    }

    public sealed class ContextItem
    {
        public string Name { get; set; } = "";

        /// <summary>Secondary text: memory-file type + path, agent/skill source.</summary>
        public string? Detail { get; set; }
        public long Tokens { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// <c>system/background_tasks_changed</c> — the set of running background tasks changed. While any
    /// is running, the CLI keeps the process alive past the turn's <c>result</c> and answers the task's
    /// completion with a follow-up turn of its own (CLI 2.1.287).
    /// </summary>
    public sealed class BackgroundTasksChangedEvent : ClaudeEvent
    {
        public int Count { get; set; }

        /// <summary>The running tasks (<c>tasks[]</c>: <c>task_id</c>, <c>description</c>).</summary>
        public System.Collections.Generic.IReadOnlyList<BackgroundTaskInfo> Tasks { get; set; } =
            System.Array.Empty<BackgroundTaskInfo>();

        /// <summary>Not from the CLI: raised by the session service when the process that kept the
        /// tasks alive is gone (stopped, replaced, session changed) — the tasks ended with it.</summary>
        public bool ProcessEnded { get; set; }
    }

    public sealed class BackgroundTaskInfo
    {
        public string TaskId { get; set; } = "";
        public string? TaskType { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Not from the CLI: the session service raises it when a process that was kept alive for its
    /// background tasks starts a turn on its own — the CLI answering a finished task with a follow-up
    /// turn while no prompt of ours was pending. The UI goes back to "working" for it.
    /// </summary>
    public sealed class FollowUpTurnStartedEvent : ClaudeEvent
    {
    }

    /// <summary>
    /// A <c>user</c> event with <c>isReplay: true</c> (<c>--replay-user-messages</c>): the CLI has taken up
    /// one of the messages we wrote to stdin. Measured 2.1.287: a message written while a background
    /// task's follow-up turn is about to start is queued behind it (follow-up <c>init</c> → <c>result</c>,
    /// then <c>init</c> → this replay → answer → <c>result</c>) — so a result before the replay is not
    /// the end of our prompt's turn. Session-service internal; not forwarded.
    /// </summary>
    public sealed class UserMessageReplayedEvent : ClaudeEvent
    {
    }

    /// <summary>The CLI is retrying an API call (informational).</summary>
    public sealed class ApiRetryEvent : ClaudeEvent
    {
        public string? Message { get; set; }
    }

    /// <summary>A fatal error reported on the stream.</summary>
    public sealed class StreamErrorEvent : ClaudeEvent
    {
        public string Message { get; set; } = "";
    }

    /// <summary>An NDJSON line whose type the parser does not understand (kept for diagnostics).</summary>
    public sealed class UnknownEvent : ClaudeEvent
    {
        public string RawType { get; set; } = "";
        public string RawLine { get; set; } = "";
    }
}
