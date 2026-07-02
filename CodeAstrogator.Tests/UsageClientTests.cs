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

        [Fact]
        public void ParseUsageText_ReadsBothWindows()
        {
            var snapshot = ClaudeUsageClient.ParseUsageText(SampleReport, Now);

            Assert.NotNull(snapshot);
            Assert.Equal(8, snapshot!.SessionPct);
            Assert.Equal(1, snapshot.WeeklyPct); // "all models", not the Sonnet-only 0%
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
            Assert.Equal(100, snapshot!.SessionPct);
        }

        [Fact]
        public void ParseUsageResult_UnwrapsJsonEnvelope()
        {
            var json =
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0," +
                "\"result\":\"Current session: 42% used · resets Jun 10, 1pm (Europe/Berlin)\"}";

            var snapshot = ClaudeUsageClient.ParseUsageResult(json, Now);

            Assert.NotNull(snapshot);
            Assert.Equal(42, snapshot!.SessionPct);
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

        // ── OAuth usage endpoint parsing (the primary source) ──────────────────
        // Real GET /api/oauth/usage response shape (trimmed).
        private const string OAuthJson =
            "{\"five_hour\":{\"utilization\":39.0,\"resets_at\":\"2026-07-02T18:00:00.489592+00:00\"}," +
            "\"seven_day\":{\"utilization\":4.0,\"resets_at\":\"2026-07-08T18:59:59.489613+00:00\"}," +
            "\"seven_day_opus\":null,\"extra_usage\":{\"is_enabled\":false}}";

        [Fact]
        public void ParseOAuthUsage_ReadsUtilizationAndResets()
        {
            var s = ClaudeUsageClient.ParseOAuthUsage(Newtonsoft.Json.Linq.JObject.Parse(OAuthJson));
            Assert.NotNull(s);
            Assert.Equal(39, s!.SessionPct);
            Assert.Equal(4, s.WeeklyPct);
            Assert.Equal(new DateTimeOffset(2026, 7, 2, 18, 0, 0, TimeSpan.Zero), s.SessionResetsAt!.Value.ToUniversalTime());
            Assert.Equal(7, s.WeeklyResetsAt!.Value.ToUniversalTime().Month);
            Assert.Equal(8, s.WeeklyResetsAt.Value.ToUniversalTime().Day);
        }

        [Fact]
        public void ParseOAuthUsage_RoundsAndClamps()
        {
            var json = "{\"five_hour\":{\"utilization\":66.7},\"seven_day\":{\"utilization\":142.0}}";
            var s = ClaudeUsageClient.ParseOAuthUsage(Newtonsoft.Json.Linq.JObject.Parse(json));
            Assert.Equal(67, s!.SessionPct);   // rounded
            Assert.Equal(100, s.WeeklyPct);    // clamped
        }

        [Fact]
        public void ParseOAuthUsage_NullUtilization_IsIgnored()
        {
            var json = "{\"five_hour\":{\"utilization\":null},\"seven_day\":{\"utilization\":5.0}}";
            var s = ClaudeUsageClient.ParseOAuthUsage(Newtonsoft.Json.Linq.JObject.Parse(json));
            Assert.NotNull(s);
            Assert.Equal(0, s!.SessionPct);
            Assert.Equal(5, s.WeeklyPct);
        }

        [Fact]
        public void ParseOAuthUsage_NoWindows_ReturnsNull()
        {
            Assert.Null(ClaudeUsageClient.ParseOAuthUsage(Newtonsoft.Json.Linq.JObject.Parse("{\"extra_usage\":{}}")));
        }
    }
}
