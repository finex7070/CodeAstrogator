using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class NdjsonParserTests
    {
        private static List<ClaudeEvent> ParseFixture(string name)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);
            var parser = new NdjsonParser();
            return File.ReadAllLines(path)
                .SelectMany(parser.ParseLine)
                .ToList();
        }

        [Fact]
        public void FullTurn_ProducesExpectedEventSequence()
        {
            var events = ParseFixture("turn-success.ndjson");

            var kinds = events.Select(e => e.GetType().Name).ToArray();
            Assert.Equal(new[]
            {
                nameof(SessionInitEvent),
                nameof(AssistantStartEvent),
                nameof(AssistantDeltaEvent),
                nameof(AssistantDeltaEvent),
                nameof(AssistantEndEvent),
                nameof(ToolUseEvent),
                nameof(ToolResultEvent),
                nameof(TurnResultEvent),
            }, kinds);
        }

        [Fact]
        public void Init_CarriesSessionIdAndModel()
        {
            var init = ParseFixture("turn-success.ndjson").OfType<SessionInitEvent>().Single();
            Assert.Equal("sess-123", init.SessionId);
            Assert.Equal("claude-opus-4-8", init.Model);
            Assert.Equal("C:\\repo", init.Cwd);
        }

        [Fact]
        public void Init_CarriesSlashCommands()
        {
            var parser = new NdjsonParser();
            var events = parser.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"slash_commands\":[\"clear\",\"compact\",\"usage\"]}");
            var init = Assert.IsType<SessionInitEvent>(Assert.Single(events));
            Assert.Equal(new[] { "clear", "compact", "usage" }, init.SlashCommands);
        }

        [Fact]
        public void Init_WithoutSlashCommands_YieldsEmptyList()
        {
            var init = ParseFixture("turn-success.ndjson").OfType<SessionInitEvent>().Single();
            Assert.Empty(init.SlashCommands);
        }

        [Fact]
        public void CompactBoundary_CarriesPreAndPostTokens()
        {
            var parser = new NdjsonParser();
            var events = parser.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":{\"trigger\":\"manual\",\"pre_tokens\":39480,\"post_tokens\":2934}}");
            var ev = Assert.IsType<CompactBoundaryEvent>(Assert.Single(events));
            Assert.Equal(39480, ev.PreTokens);
            Assert.Equal(2934, ev.PostTokens);
        }

        [Fact]
        public void SystemStatus_YieldsStatusEvent()
        {
            var parser = new NdjsonParser();
            var events = parser.ParseLine("{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"compacting\"}");
            var ev = Assert.IsType<StatusEvent>(Assert.Single(events));
            Assert.Equal("compacting", ev.Status);
        }

        [Fact]
        public void Deltas_ComeFromStreamEvents_NotFromCompleteAssistantMessage()
        {
            var events = ParseFixture("turn-success.ndjson");
            var text = string.Concat(events.OfType<AssistantDeltaEvent>().Select(d => d.Text));
            // The complete "assistant" line repeats the text; it must not be duplicated.
            Assert.Equal("Hello world.", text);
        }

        [Fact]
        public void ToolUse_ExtractedFromCompleteAssistantMessage()
        {
            var tool = ParseFixture("turn-success.ndjson").OfType<ToolUseEvent>().Single();
            Assert.Equal("toolu_01", tool.ToolUseId);
            Assert.Equal("Read", tool.Name);
            Assert.Equal("C:\\repo\\Program.cs", tool.Input.Value<string>("file_path"));
        }

        [Fact]
        public void ToolResult_CarriesSummaryText()
        {
            var result = ParseFixture("turn-success.ndjson").OfType<ToolResultEvent>().Single();
            Assert.Equal("toolu_01", result.ToolUseId);
            Assert.False(result.IsError);
            Assert.Contains("class Program", result.Summary);
        }

        [Fact]
        public void Result_AggregatesUsageIncludingCacheTokens()
        {
            var result = ParseFixture("turn-success.ndjson").OfType<TurnResultEvent>().Single();
            Assert.Equal("sess-123", result.SessionId);
            Assert.False(result.IsError);
            Assert.Equal(0.0123, result.CostUsd, 6);
            Assert.Equal(4321, result.DurationMs);
            Assert.Equal(12 + 100 + 900, result.InputTokens);
            Assert.Equal(45, result.OutputTokens);
        }

        [Fact]
        public void ContextTokens_ComeFromLastAssistantMessage_NotAggregatedResult()
        {
            // A multi-round-trip turn: each assistant message re-reads the cached context, so the
            // result-event usage AGGREGATES cache reads (over-counts the real context window). The
            // real context size is the LAST assistant message's own usage.
            var parser = new NdjsonParser();
            parser.ParseLine("{\"type\":\"assistant\",\"message\":{\"content\":[],\"usage\":" +
                "{\"input_tokens\":2,\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":40000,\"output_tokens\":50}}}");
            parser.ParseLine("{\"type\":\"assistant\",\"message\":{\"content\":[],\"usage\":" +
                "{\"input_tokens\":2,\"cache_creation_input_tokens\":120,\"cache_read_input_tokens\":42000,\"output_tokens\":80}}}");
            var events = parser.ParseLine("{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\"," +
                "\"num_turns\":2,\"usage\":{\"input_tokens\":4,\"cache_creation_input_tokens\":220," +
                "\"cache_read_input_tokens\":82000,\"output_tokens\":130}}");

            var result = events.OfType<TurnResultEvent>().Single();
            // aggregate (cost/round-trips) — re-counts cache reads
            Assert.Equal(4 + 220 + 82000, result.InputTokens);
            Assert.Equal(130, result.OutputTokens);
            // context = last message only
            Assert.Equal(2 + 120 + 42000, result.ContextInputTokens);
            Assert.Equal(80, result.ContextOutputTokens);
        }

        [Fact]
        public void DeltaWithoutContentBlockStart_StillEmitsAssistantStart()
        {
            var parser = new NdjsonParser();
            parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}");
            var events = parser.ParseLine(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}}");

            Assert.Collection(events,
                e => Assert.IsType<AssistantStartEvent>(e),
                e => Assert.Equal("hi", Assert.IsType<AssistantDeltaEvent>(e).Text));
        }

        [Fact]
        public void ToolResult_ContentBlocks_AreJoined()
        {
            var parser = new NdjsonParser();
            var events = parser.ParseLine(
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"is_error\":true," +
                "\"content\":[{\"type\":\"text\",\"text\":\"line1\"},{\"type\":\"text\",\"text\":\"line2\"}]}]}}");

            var result = Assert.IsType<ToolResultEvent>(Assert.Single(events));
            Assert.True(result.IsError);
            Assert.Equal("line1\nline2", result.Summary);
        }

        [Fact]
        public void LongToolResult_IsTruncated()
        {
            var parser = new NdjsonParser { MaxSummaryLength = 10 };
            var events = parser.ParseLine(
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"0123456789ABCDEF\"}]}}");

            var result = Assert.IsType<ToolResultEvent>(Assert.Single(events));
            Assert.StartsWith("0123456789", result.Summary);
            Assert.EndsWith("(truncated)", result.Summary);
        }

        [Fact]
        public void ThinkingBlock_ProducesStartDeltaEnd()
        {
            var parser = new NdjsonParser();
            var events = new List<ClaudeEvent>();
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}"));
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}}"));
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm \",\"estimated_tokens\":50}}}"));
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":0}}"));
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}"));
            events.AddRange(parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"answer\"}}}"));

            Assert.Collection(events,
                e => Assert.Equal("m1:t0", Assert.IsType<ThinkingStartEvent>(e).BlockId),
                e =>
                {
                    var d = Assert.IsType<ThinkingDeltaEvent>(e);
                    Assert.Equal("hmm ", d.Text);
                    Assert.Equal(50, d.EstimatedTokens);
                },
                e => Assert.Equal("m1:t0", Assert.IsType<ThinkingEndEvent>(e).BlockId),
                e => Assert.IsType<AssistantStartEvent>(e),
                e => Assert.Equal("answer", Assert.IsType<AssistantDeltaEvent>(e).Text));
        }

        [Fact]
        public void TextBlockStop_DoesNotEmitThinkingEnd()
        {
            var parser = new NdjsonParser();
            parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"m1\"}}}");
            parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}");
            var events = parser.ParseLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":0}}");

            Assert.Empty(events);
        }

        [Fact]
        public void CompactBoundary_IsSurfaced()
        {
            var events = new NdjsonParser().ParseLine("{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s1\"}");
            Assert.IsType<CompactBoundaryEvent>(Assert.Single(events));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        public void GarbageLines_AreIgnored(string line)
        {
            Assert.Empty(new NdjsonParser().ParseLine(line));
        }

        [Fact]
        public void UnknownType_IsSurfacedAsUnknownEvent()
        {
            var events = new NdjsonParser().ParseLine("{\"type\":\"somethingNew\",\"x\":1}");
            var unknown = Assert.IsType<UnknownEvent>(Assert.Single(events));
            Assert.Equal("somethingNew", unknown.RawType);
        }

        [Fact]
        public void ErrorLine_BecomesStreamError()
        {
            var events = new NdjsonParser().ParseLine("{\"type\":\"error\",\"message\":\"boom\"}");
            Assert.Equal("boom", Assert.IsType<StreamErrorEvent>(Assert.Single(events)).Message);
        }
    }
}
