using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Stateful parser for the NDJSON stream emitted by
    /// <c>claude -p --output-format stream-json --verbose --include-partial-messages</c>.
    ///
    /// One instance per turn. Text streaming is taken from <c>stream_event</c> lines
    /// (message_start / content_block_delta / message_stop); complete <c>assistant</c>
    /// messages are used only to extract tool_use blocks (their text would duplicate
    /// the deltas). <c>user</c> messages carry tool_result blocks.
    /// </summary>
    public sealed class NdjsonParser
    {
        private string? _currentMessageId;
        private bool _assistantStarted;
        private readonly Dictionary<int, string> _blockTypes = new Dictionary<int, string>();

        // Context-window size after the turn = usage of the LAST assistant message (one API call),
        // NOT the result-event usage (which aggregates cache reads across every round-trip).
        private long _lastContextInput;
        private long _lastContextOutput;

        /// <summary>Maximum length of a tool-result summary forwarded to the UI
        /// (the UI collapses long output behind a "Show more" toggle).</summary>
        public int MaxSummaryLength { get; set; } = 10000;

        /// <summary>Parses a single NDJSON line into zero or more domain events.</summary>
        public IReadOnlyList<ClaudeEvent> ParseLine(string line)
        {
            var events = new List<ClaudeEvent>();
            if (string.IsNullOrWhiteSpace(line))
                return events;

            JObject obj;
            try
            {
                obj = JObject.Parse(line);
            }
            catch (Exception)
            {
                // Non-JSON noise on stdout (should not happen with stream-json) — ignore.
                return events;
            }

            var type = obj.Value<string>("type") ?? "";
            switch (type)
            {
                case "system":
                    ParseSystem(obj, events);
                    break;
                case "stream_event":
                    ParseStreamEvent(obj, events);
                    break;
                case "assistant":
                    ParseAssistantMessage(obj, events);
                    break;
                case "user":
                    ParseUserMessage(obj, events);
                    break;
                case "result":
                    ParseResult(obj, events);
                    break;
                case "error":
                    events.Add(new StreamErrorEvent
                    {
                        Message = obj.Value<string>("message")
                                  ?? obj["error"]?.Value<string>("message")
                                  ?? line,
                    });
                    break;
                default:
                    events.Add(new UnknownEvent { RawType = type, RawLine = line });
                    break;
            }

            return events;
        }

        private static void ParseSystem(JObject obj, List<ClaudeEvent> events)
        {
            var subtype = obj.Value<string>("subtype") ?? "";
            if (subtype == "init")
            {
                var slashCommands = new List<string>();
                if (obj["slash_commands"] is JArray slashArr)
                {
                    foreach (var item in slashArr)
                    {
                        var name = item.Value<string>();
                        if (!string.IsNullOrEmpty(name))
                            slashCommands.Add(name!);
                    }
                }

                events.Add(new SessionInitEvent
                {
                    SessionId = obj.Value<string>("session_id") ?? "",
                    Model = obj.Value<string>("model"),
                    Cwd = obj.Value<string>("cwd"),
                    SlashCommands = slashCommands,
                });
            }
            else if (subtype == "api_retry" || subtype == "retry")
            {
                events.Add(new ApiRetryEvent { Message = obj.Value<string>("message") });
            }
            else if (subtype == "compact_boundary")
            {
                var meta = obj["compact_metadata"] as JObject;
                events.Add(new CompactBoundaryEvent
                {
                    PreTokens = meta?.Value<long?>("pre_tokens") ?? 0,
                    PostTokens = meta?.Value<long?>("post_tokens") ?? 0,
                });
            }
            else if (subtype == "status")
            {
                events.Add(new StatusEvent { Status = obj.Value<string>("status") });
            }
        }

        private void ParseStreamEvent(JObject obj, List<ClaudeEvent> events)
        {
            if (obj["event"] is not JObject ev)
                return;

            var evType = ev.Value<string>("type") ?? "";
            switch (evType)
            {
                case "message_start":
                    _currentMessageId = ev["message"]?.Value<string>("id") ?? Guid.NewGuid().ToString("n");
                    _assistantStarted = false;
                    _blockTypes.Clear();
                    break;

                case "content_block_start":
                {
                    var blockType = ev["content_block"]?.Value<string>("type") ?? "";
                    var index = ev.Value<int?>("index") ?? 0;
                    _blockTypes[index] = blockType;

                    if (blockType == "text" && !_assistantStarted && _currentMessageId != null)
                    {
                        _assistantStarted = true;
                        events.Add(new AssistantStartEvent { MessageId = _currentMessageId });
                    }
                    else if (blockType == "thinking" && _currentMessageId != null)
                    {
                        events.Add(new ThinkingStartEvent { BlockId = ThinkingBlockId(index) });
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = ev["delta"] as JObject;
                    var deltaType = delta?.Value<string>("type");
                    var index = ev.Value<int?>("index") ?? 0;

                    if (deltaType == "text_delta" && _currentMessageId != null)
                    {
                        if (!_assistantStarted)
                        {
                            _assistantStarted = true;
                            events.Add(new AssistantStartEvent { MessageId = _currentMessageId });
                        }
                        events.Add(new AssistantDeltaEvent
                        {
                            MessageId = _currentMessageId,
                            Text = delta!.Value<string>("text") ?? "",
                        });
                    }
                    else if (deltaType == "thinking_delta" && _currentMessageId != null)
                    {
                        events.Add(new ThinkingDeltaEvent
                        {
                            BlockId = ThinkingBlockId(index),
                            Text = delta!.Value<string>("thinking") ?? "",
                            EstimatedTokens = delta.Value<long?>("estimated_tokens") ?? 0,
                        });
                    }
                    // signature_delta / input_json_delta: not surfaced (decision #16)
                    break;
                }

                case "content_block_stop":
                {
                    var index = ev.Value<int?>("index") ?? 0;
                    if (_blockTypes.TryGetValue(index, out var t) && t == "thinking" && _currentMessageId != null)
                        events.Add(new ThinkingEndEvent { BlockId = ThinkingBlockId(index) });
                    break;
                }

                case "message_stop":
                    if (_assistantStarted && _currentMessageId != null)
                    {
                        events.Add(new AssistantEndEvent { MessageId = _currentMessageId });
                        _assistantStarted = false;
                    }
                    break;
            }
        }

        private string ThinkingBlockId(int index) => (_currentMessageId ?? "msg") + ":t" + index;

        private void ParseAssistantMessage(JObject obj, List<ClaudeEvent> events)
        {
            // Each assistant message carries the usage of that single API call — the last one's
            // input(+cache)+output is the real context size after the turn (see ParseResult).
            if (obj["message"]?["usage"] is JObject usage)
            {
                _lastContextInput = (usage.Value<long?>("input_tokens") ?? 0)
                    + (usage.Value<long?>("cache_read_input_tokens") ?? 0)
                    + (usage.Value<long?>("cache_creation_input_tokens") ?? 0);
                _lastContextOutput = usage.Value<long?>("output_tokens") ?? 0;
            }

            // Complete assistant message: only tool_use blocks are extracted here;
            // text already arrived via stream_event deltas.
            if (obj["message"]?["content"] is not JArray content)
                return;

            foreach (var block in content)
            {
                if (block is JObject b && b.Value<string>("type") == "tool_use")
                {
                    events.Add(new ToolUseEvent
                    {
                        ToolUseId = b.Value<string>("id") ?? "",
                        Name = b.Value<string>("name") ?? "",
                        Input = b["input"] as JObject ?? new JObject(),
                    });
                }
            }
        }

        private void ParseUserMessage(JObject obj, List<ClaudeEvent> events)
        {
            if (obj["message"]?["content"] is not JArray content)
                return;

            foreach (var block in content)
            {
                if (block is JObject b && b.Value<string>("type") == "tool_result")
                {
                    events.Add(new ToolResultEvent
                    {
                        ToolUseId = b.Value<string>("tool_use_id") ?? "",
                        IsError = b.Value<bool?>("is_error") ?? false,
                        Summary = Truncate(ExtractToolResultText(b["content"])),
                    });
                }
            }
        }

        private void ParseResult(JObject obj, List<ClaudeEvent> events)
        {
            var usage = obj["usage"] as JObject;
            long input = usage?.Value<long?>("input_tokens") ?? 0;
            input += usage?.Value<long?>("cache_read_input_tokens") ?? 0;
            input += usage?.Value<long?>("cache_creation_input_tokens") ?? 0;

            events.Add(new TurnResultEvent
            {
                SessionId = obj.Value<string>("session_id") ?? "",
                NumTurns = obj.Value<int?>("num_turns") ?? 0,
                IsError = obj.Value<bool?>("is_error") ?? false,
                CostUsd = obj.Value<double?>("total_cost_usd") ?? 0,
                DurationMs = obj.Value<long?>("duration_ms") ?? 0,
                InputTokens = input,
                OutputTokens = usage?.Value<long?>("output_tokens") ?? 0,
                ContextInputTokens = _lastContextInput,
                ContextOutputTokens = _lastContextOutput,
                ResultText = obj.Value<string>("result"),
            });
        }

        /// <summary>tool_result content can be a plain string or an array of content blocks.</summary>
        private static string ExtractToolResultText(JToken? content)
        {
            switch (content)
            {
                case null:
                    return "";
                case JValue v:
                    return v.Value<string>() ?? "";
                case JArray arr:
                {
                    var parts = new List<string>();
                    foreach (var item in arr)
                    {
                        if (item is JObject o && o.Value<string>("type") == "text")
                            parts.Add(o.Value<string>("text") ?? "");
                    }
                    return string.Join("\n", parts);
                }
                default:
                    return content.ToString();
            }
        }

        private string Truncate(string text)
        {
            if (text.Length <= MaxSummaryLength)
                return text;
            return text.Substring(0, MaxSummaryLength) + "\n… (truncated)";
        }
    }
}
