using System;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class UsageClientTests
    {
        // Real /usage report shape (CLI 2.1.169, subscription mode).
        private const string SampleReport =
            "You are currently using your subscription to power your Claude Code usage\n" +
            "\n" +
            "Current session: 8% used · resets Jun 10, 1pm (Europe/Berlin)\n" +
            "Current week (all models): 1% used · resets Jun 10, 9pm (Europe/Berlin)\n" +
            "Current week (Sonnet only): 0% used";

        private static readonly DateTime Now = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Local);

        // Real "missing percentage block" shape — /usage sporadically drops all "Current …"
        // lines and prints only the (local, approximate) contributing section. Screenshot case.
        private const string NoPercentBlock =
            "You are currently using your subscription to power your Claude Code usage\n" +
            "\n" +
            "What's contributing to your limits usage?\n" +
            "Approximate, based on local sessions on this machine — does not include other devices or claude.ai.\n" +
            "\n" +
            "Last 24h · 43 requests · 7 sessions\n" +
            "  35% of your usage was while 4+ sessions ran in parallel\n" +
            "  Top MCP servers: vsbridge 64%";

        [Fact]
        public void ParseUsageText_ReadsBothWindows()
        {
            var snapshot = ClaudeUsageClient.ParseUsageText(SampleReport, Now);

            Assert.NotNull(snapshot);
            Assert.Equal(8, snapshot!.SessionPct!.Value);
            Assert.Equal(1, snapshot.WeeklyPct!.Value); // "all models", not the Sonnet-only 0%
        }

        [Fact]
        public void ParseUsageText_MissingPercentBlock_ReturnsNull()
        {
            // No "Current …" lines at all → null (a total miss), so Merge keeps last-known-good.
            Assert.Null(ClaudeUsageClient.ParseUsageText(NoPercentBlock, Now));
        }

        [Fact]
        public void ParseUsageText_WeeklyMissing_LeavesWeeklyNull()
        {
            var report =
                "Current session: 65% used · resets Jul 2, 8pm (Europe/Berlin)\n" +
                "Current week (Fable): 2% used · resets Jul 8, 9pm (Europe/Berlin)"; // no "all models" line
            var snapshot = ClaudeUsageClient.ParseUsageText(report, Now);

            Assert.NotNull(snapshot);
            Assert.Equal(65, snapshot!.SessionPct!.Value);
            Assert.Null(snapshot.WeeklyPct); // per-model line is ignored; weekly stays "not present"
        }

        [Fact]
        public void ParseUsageText_ZeroPercent_IsPresentNotNull()
        {
            var snapshot = ClaudeUsageClient.ParseUsageText("Current session: 0% used", Now);

            Assert.NotNull(snapshot);
            Assert.True(snapshot!.SessionPct.HasValue); // genuine 0% must not read as "missing"
            Assert.Equal(0, snapshot.SessionPct!.Value);
        }

        [Fact]
        public void ParseResetTime_ParsesMinutesVariant()
        {
            // The CLI renders the same reset as "9pm" or "8:59pm" between calls.
            var reset = ClaudeUsageClient.ParseResetTime("· resets Jul 8, 8:59pm (Europe/Berlin)", Now);

            Assert.NotNull(reset);
            Assert.Equal(20, reset!.Value.Hour);
            Assert.Equal(59, reset.Value.Minute);
        }

        [Fact]
        public void ParseUsageText_ParsesResetTimesAsLocalWallClock()
        {
            var snapshot = ClaudeUsageClient.ParseUsageText(SampleReport, Now);

            Assert.NotNull(snapshot!.SessionResetsAt);
            Assert.Equal(6, snapshot.SessionResetsAt!.Value.Month);
            Assert.Equal(10, snapshot.SessionResetsAt.Value.Day);
            Assert.Equal(13, snapshot.SessionResetsAt.Value.Hour); // 1pm
            Assert.Equal(21, snapshot.WeeklyResetsAt!.Value.Hour); // 9pm
        }

        [Fact]
        public void ParseResetTime_RollsToNextYearWhenPast()
        {
            var now = new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Local);
            var reset = ClaudeUsageClient.ParseResetTime("· resets Jan 1, 2am (Europe/Berlin)", now);

            Assert.NotNull(reset);
            Assert.Equal(2027, reset!.Value.Year);
        }

        [Fact]
        public void ParseUsageText_ClampsTo100()
        {
            var snapshot = ClaudeUsageClient.ParseUsageText("Current session: 142% used", Now);
            Assert.Equal(100, snapshot!.SessionPct!.Value);
        }

        [Fact]
        public void ParseUsageResult_UnwrapsJsonEnvelope()
        {
            var json =
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0," +
                "\"result\":\"Current session: 42% used · resets Jun 10, 1pm (Europe/Berlin)\"}";

            var snapshot = ClaudeUsageClient.ParseUsageResult(json, Now);

            Assert.NotNull(snapshot);
            Assert.Equal(42, snapshot!.SessionPct!.Value);
        }

        [Fact]
        public void Merge_PartialReport_KeepsPreviousWindow()
        {
            var t0 = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 7, 2, 12, 5, 0, TimeSpan.Zero);
            var previous = ClaudeUsageClient.Merge(null,
                new UsageSnapshot { SessionPct = 65, WeeklyPct = 7 }, t0);

            // A later report that only carries the session window must not zero the weekly meter.
            var merged = ClaudeUsageClient.Merge(previous,
                new UsageSnapshot { SessionPct = 66 }, t1);

            Assert.Equal(66, merged!.SessionPct!.Value);
            Assert.Equal(7, merged.WeeklyPct!.Value);        // kept from the earlier good report
            Assert.Equal(t1, merged.SessionFetchedAt);       // session refreshed
            Assert.Equal(t0, merged.WeeklyFetchedAt);        // weekly stamp unchanged (stale)
        }

        [Fact]
        public void Merge_NullFresh_ReturnsPreviousUnchanged()
        {
            var previous = new UsageSnapshot { SessionPct = 65, WeeklyPct = 7 };
            var merged = ClaudeUsageClient.Merge(previous, null, DateTimeOffset.Now);
            Assert.Same(previous, merged); // total miss / process failure never touches the cache
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a usage report")]
        [InlineData("{\"result\":\"You don't have any usage limits configured.\"}")]
        public void ParseUsageResult_GarbageReturnsNull(string output)
        {
            Assert.Null(ClaudeUsageClient.ParseUsageResult(output, Now));
        }

        [Theory]
        [InlineData("claude_team", "", "Team Plan")]
        [InlineData("claude_enterprise", "", "Enterprise")]
        [InlineData("claude_pro", "", "Pro Plan")]
        [InlineData("claude_max", "", "Max Plan")]
        [InlineData("", "default_claude_max_5x", "Max Plan")]
        [InlineData("", "some_pro_tier", "Pro Plan")]
        [InlineData("", "", null)]
        public void MapPlanLabel_KnownTiers(string orgType, string rateTier, string? expected)
        {
            Assert.Equal(expected, ClaudeUsageClient.MapPlanLabel(orgType, rateTier));
        }
    }
}
