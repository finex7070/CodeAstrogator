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
    }

    /// <summary>result — end of turn with cost/usage (maps to turn.result).</summary>
    public sealed class TurnResultEvent : ClaudeEvent
    {
        public string SessionId { get; set; } = "";

        /// <summary>0 for local-only turns (e.g. /help) — those create NO resumable
        /// conversation; resuming their session_id fails with "No conversation found".</summary>
        public int NumTurns { get; set; }

        public bool IsError { get; set; }
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
        public string? ResultText { get; set; }
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
