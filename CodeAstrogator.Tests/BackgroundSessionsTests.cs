using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>Outputs as measured against CLI 2.1.294 (2026-10-28, `--bg --resume … --remote-control`).</summary>
    public class BackgroundSessionsTests
    {
        private const string StartOutput =
            "Starting background service\u2026\n" +
            "backgrounded \u00b7 8a6d7f9e (idle \u2014 send a prompt to start)\n" +
            "  claude agents             list sessions\n" +
            "  claude attach 8a6d7f9e    open in this terminal\n" +
            "  claude logs 8a6d7f9e      show recent output\n" +
            "  claude stop 8a6d7f9e      stop this session\n";

        [Fact]
        public void StartOutput_YieldsTheShortId() =>
            Assert.Equal("8a6d7f9e", ClaudeBackgroundSessions.ParseStartedId(StartOutput));

        [Fact]
        public void StartError_YieldsNoId() =>
            Assert.Null(ClaudeBackgroundSessions.ParseStartedId("Error: Workspace not trusted. Please run `claude` first"));

        [Fact]
        public void Logs_YieldTheRemoteControlLink_OnceActive()
        {
            const string connecting = "\u001b[2J\u001b[H ~\\work \u00b7 /rc connecting\u2026\u001b[K\n";
            const string active = connecting +
                "  /remote-control is active \u00b7 Continue here, on your phone, or at https://claude.ai/code/session_01AstrogatorDemoSession42\u001b[K\n";

            Assert.Null(ClaudeBackgroundSessions.ExtractRemoteUrl(connecting));
            Assert.Equal("https://claude.ai/code/session_01AstrogatorDemoSession42", ClaudeBackgroundSessions.ExtractRemoteUrl(active));
        }

        [Fact]
        public void Agents_RunningAndStopped()
        {
            const string json = @"[
  { ""pid"": 22860, ""cwd"": ""C:\\Users\\Jan\\CodeAstrogator"", ""kind"": ""interactive"", ""startedAt"": 1793183455200,
    ""sessionId"": ""7be1754c-d7a0-4414-a524-de3a157a9479"", ""name"": ""codeastrogator-a0"", ""status"": ""busy"" },
  { ""pid"": 10432, ""id"": ""8a6d7f9e"", ""cwd"": ""C:\\work"", ""kind"": ""background"", ""startedAt"": 1793183878706,
    ""sessionId"": ""8a6d7f9e-c2b0-4e06-9d7a-5ee80fc262f1"", ""name"": ""8a6d7f9e"", ""status"": ""idle"", ""state"": ""blocked"" }
]";
            var list = ClaudeBackgroundSessions.ParseAgents(json);
            var bg = list.Single(s => s.Id == "8a6d7f9e");
            Assert.Equal("background", bg.Kind);
            Assert.Equal("8a6d7f9e-c2b0-4e06-9d7a-5ee80fc262f1", bg.SessionId);
            Assert.Equal("idle", bg.Status);
            Assert.False(ClaudeBackgroundSessions.HasEnded(bg)); // "blocked" = waiting for input, still running

            const string stopped = @"[ { ""id"": ""8a6d7f9e"", ""cwd"": ""C:\\work"", ""kind"": ""background"",
    ""startedAt"": 1793183877252, ""sessionId"": ""8a6d7f9e-c2b0-4e06-9d7a-5ee80fc262f1"", ""state"": ""stopped"" } ]";
            Assert.True(ClaudeBackgroundSessions.HasEnded(ClaudeBackgroundSessions.ParseAgents(stopped).Single()));
            Assert.True(ClaudeBackgroundSessions.HasEnded(null)); // no longer listed

            // the host died with the PC (shutdown while the session ran) — CLI 2.1.312
            const string done = @"[ { ""id"": ""8a6d7f9e"", ""cwd"": ""C:\\work"", ""kind"": ""background"",
    ""startedAt"": 1793183877252, ""sessionId"": ""8a6d7f9e-c2b0-4e06-9d7a-5ee80fc262f1"", ""name"": ""demo"", ""state"": ""done"" } ]";
            Assert.True(ClaudeBackgroundSessions.HasEnded(ClaudeBackgroundSessions.ParseAgents(done).Single()));
        }

        [Fact]
        public void Agents_GarbageIsEmpty() =>
            Assert.Empty(ClaudeBackgroundSessions.ParseAgents("error: unknown command"));
    }
}
