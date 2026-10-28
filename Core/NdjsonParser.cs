using System;
using System.Collections.Generic;
using System.Linq;
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

        // A system/task_notification opened this run: the next zero-turn result belongs to the queued
        // notification, not to the user's prompt (see NotificationTurnResultEvent).
        private bool _notificationPending;

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
                case "prompt_suggestion":
                {
                    var suggestion = obj.Value<string>("suggestion");
                    if (!string.IsNullOrWhiteSpace(suggestion))
                    {
                        events.Add(new PromptSuggestionEvent
                        {
                            Suggestion = suggestion!.Trim(),
                            SessionId = obj.Value<string>("session_id"),
                        });
                    }
                    break;
                }
                case "control_response":
                    ParseControlResponse(obj, events);
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

        private void ParseSystem(JObject obj, List<ClaudeEvent> events)
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
            else if (subtype == "task_started")
            {
                events.Add(new TaskStartedEvent
                {
                    TaskId = obj.Value<string>("task_id") ?? "",
                    ToolUseId = obj.Value<string>("tool_use_id") ?? "",
                    TaskType = obj.Value<string>("task_type"),
                    IsBackgrounded = obj.Value<bool?>("is_backgrounded") ?? false,
                    SessionId = obj.Value<string>("session_id"),
                });
            }
            else if (subtype == "background_tasks_changed")
            {
                var tasks = new List<BackgroundTaskInfo>();
                if (obj["tasks"] is JArray arr)
                {
                    foreach (var t in arr.OfType<JObject>())
                    {
                        tasks.Add(new BackgroundTaskInfo
                        {
                            TaskId = t.Value<string>("task_id") ?? "",
                            TaskType = t.Value<string>("task_type"),
                            Description = t.Value<string>("description"),
                        });
                    }
                }
                events.Add(new BackgroundTasksChangedEvent { Count = (obj["tasks"] as JArray)?.Count ?? 0, Tasks = tasks });
            }
            else if (subtype == "task_notification")
            {
                // Also fires after every ordinary foreground command; only a following zero-turn
                // result is diverted (see ParseResult), so arming the flag here is safe.
                _notificationPending = true;
                events.Add(new TaskNotificationEvent
                {
                    TaskId = obj.Value<string>("task_id") ?? "",
                    ToolUseId = obj.Value<string>("tool_use_id"),
                    Status = obj.Value<string>("status"),
                });
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
            // --replay-user-messages: one of our stdin messages, echoed when the CLI takes it up
            if (obj.Value<bool?>("isReplay") == true)
            {
                events.Add(new UserMessageReplayedEvent());
                return;
            }
            if (obj["message"]?["content"] is not JArray content)
                return;

            var results = content.OfType<JObject>().Where(b => b.Value<string>("type") == "tool_result").ToList();
            // A subagent's hand-back carries a structured tool_use_result next to the framed text (one
            // tool_result per message) — show its clean report and stats instead of the model-facing frame.
            var agent = results.Count == 1 ? ParseSubagent(obj["tool_use_result"] as JObject) : null;
            foreach (var b in results)
            {
                // (a backgrounded agent's launch ack has no report yet → keep the CLI's text)
                var summary = agent is { report.Length: > 0 } a ? a.report : ExtractToolResultText(b["content"]);
                events.Add(new ToolResultEvent
                {
                    ToolUseId = b.Value<string>("tool_use_id") ?? "",
                    IsError = b.Value<bool?>("is_error") ?? false,
                    Summary = Truncate(summary),
                    Agent = agent?.stats,
                });
            }
        }

        /// <summary>Reads an Agent tool's <c>tool_use_result</c> (recognised by its <c>agentId</c>): the
        /// report text plus duration/tokens/tool uses. Null for every other tool.</summary>
        private static (string report, SubagentStats stats)? ParseSubagent(JObject? r)
        {
            if (r == null || string.IsNullOrEmpty(r.Value<string>("agentId")))
                return null;
            var report = string.Join("\n", (r["content"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(c => c.Value<string>("type") == "text")
                .Select(c => c.Value<string>("text") ?? ""));
            return (report, new SubagentStats
            {
                Status = r.Value<string>("status"),
                DurationMs = r.Value<long?>("totalDurationMs") ?? 0,
                TotalTokens = r.Value<long?>("totalTokens") ?? 0,
                ToolUses = r.Value<int?>("totalToolUseCount") ?? 0,
                Model = r.Value<string>("resolvedModel"),
            });
        }

        private void ParseResult(JObject obj, List<ClaudeEvent> events)
        {
            var numTurns = obj.Value<int?>("num_turns") ?? 0;

            // The queued background-task notification is answered first, as a zero-turn "turn" of its
            // own; the user's prompt follows with a fresh system/init. Only the notification's result is
            // diverted — a zero-turn result WITHOUT a preceding notification is a real local turn
            // (/help, /usage …) and still ends the turn.
            if (_notificationPending && numTurns == 0 && string.IsNullOrEmpty(obj.Value<string>("parent_tool_use_id")))
            {
                _notificationPending = false;
                events.Add(new NotificationTurnResultEvent
                {
                    SessionId = obj.Value<string>("session_id") ?? "",
                    DurationMs = obj.Value<long?>("duration_ms") ?? 0,
                    CostUsd = obj.Value<double?>("total_cost_usd") ?? 0,
                });
                return;
            }
            _notificationPending = false;

            var usage = obj["usage"] as JObject;
            long input = usage?.Value<long?>("input_tokens") ?? 0;
            input += usage?.Value<long?>("cache_read_input_tokens") ?? 0;
            input += usage?.Value<long?>("cache_creation_input_tokens") ?? 0;

            events.Add(new TurnResultEvent
            {
                SessionId = obj.Value<string>("session_id") ?? "",
                NumTurns = numTurns,
                IsError = obj.Value<bool?>("is_error") ?? false,
                CostUsd = obj.Value<double?>("total_cost_usd") ?? 0,
                DurationMs = obj.Value<long?>("duration_ms") ?? 0,
                InputTokens = input,
                OutputTokens = usage?.Value<long?>("output_tokens") ?? 0,
                ContextInputTokens = _lastContextInput,
                ContextOutputTokens = _lastContextOutput,
                ContextWindow = MainContextWindow(obj["modelUsage"] as JObject),
                ResultText = obj.Value<string>("result"),
                ParentToolUseId = obj.Value<string>("parent_tool_use_id"),
            });
        }

        /// <summary><c>modelUsage</c> has one entry per model used in the turn (subagents and the
        /// suggestion model may add more); the main loop is the one that read the most input.</summary>
        private static long MainContextWindow(JObject? modelUsage)
        {
            long window = 0, mostInput = -1;
            foreach (var p in modelUsage?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (!(p.Value is JObject m))
                    continue;
                var size = m.Value<long?>("contextWindow") ?? 0;
                if (size <= 0)
                    continue;
                var input = (m.Value<long?>("inputTokens") ?? 0)
                            + (m.Value<long?>("cacheReadInputTokens") ?? 0)
                            + (m.Value<long?>("cacheCreationInputTokens") ?? 0);
                if (input > mostInput)
                {
                    mostInput = input;
                    window = size;
                }
            }
            return window;
        }

        /// <summary>Answer to one of our <c>get_context_usage</c> requests (other control responses and
        /// error answers — an older CLI without the subtype — are dropped).</summary>
        private static void ParseControlResponse(JObject obj, List<ClaudeEvent> events)
        {
            var response = obj["response"] as JObject;
            var requestId = response?.Value<string>("request_id") ?? "";
            if (response?.Value<string>("subtype") != "success"
                || !requestId.StartsWith(StreamJsonInput.ContextUsageRequestPrefix, StringComparison.Ordinal)
                || !(response["response"] is JObject r))
                return;

            var ev = new ContextUsageEvent
            {
                TotalTokens = r.Value<long?>("totalTokens") ?? 0,
                MaxTokens = r.Value<long?>("maxTokens") ?? 0,
                Percentage = r.Value<int?>("percentage") ?? 0,
                AutoCompactThreshold = r.Value<long?>("autoCompactThreshold") ?? 0,
                AutoCompactEnabled = r.Value<bool?>("isAutoCompactEnabled") ?? false,
                Model = r.Value<string>("model"),
            };
            foreach (var c in (r["categories"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                ev.Categories.Add(new ContextCategory
                {
                    Name = c.Value<string>("name") ?? "",
                    Tokens = c.Value<long?>("tokens") ?? 0,
                    Kind = c.Value<string>("kind") ?? "used",
                });
            }
            foreach (var f in (r["memoryFiles"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = f.Value<string>("path") ?? "";
                ev.MemoryFiles.Add(new ContextItem
                {
                    Name = System.IO.Path.GetFileName(path),
                    Detail = (f.Value<string>("type") is string type && type.Length > 0 ? type + " · " : "") + path,
                    Tokens = f.Value<long?>("tokens") ?? 0,
                });
            }
            // per tool there can be hundreds of entries — the panel shows one line per server
            foreach (var server in ((r["mcpTools"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                         .GroupBy(t => t.Value<string>("serverName") ?? ""))
            {
                ev.McpServers.Add(new ContextItem
                {
                    Name = server.Key,
                    Count = server.Count(),
                    Tokens = server.Where(t => t.Value<bool?>("isLoaded") ?? true).Sum(t => t.Value<long?>("tokens") ?? 0),
                });
            }
            foreach (var a in (r["agents"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                ev.Agents.Add(new ContextItem
                {
                    Name = a.Value<string>("agentType") ?? "",
                    Detail = a.Value<string>("source"),
                    Tokens = a.Value<long?>("tokens") ?? 0,
                });
            }
            foreach (var s in (r["skills"]?["skillFrontmatter"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                ev.Skills.Add(new ContextItem
                {
                    Name = s.Value<string>("name") ?? "",
                    Detail = s.Value<string>("pluginName") ?? s.Value<string>("source"),
                    Tokens = s.Value<long?>("tokens") ?? 0,
                });
            }
            events.Add(ev);
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
