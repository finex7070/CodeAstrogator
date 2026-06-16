using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class RemoteControlOutputParserTests
    {
        [Fact]
        public void Push_ParsesUrlAndCapacity_AndSuppressesTuiRedraws()
        {
            var parser = new RemoteControlOutputParser();

            Assert.Null(parser.Push("·|· Connecting · CodeAstrogator · main"));
            Assert.Null(parser.Push("[1A[J·✔︎· Ready · CodeAstrogator · main"));
            // capacity before the URL is known: remembered, but no state yet
            Assert.Null(parser.Push("    Capacity: 0/32 · New sessions will be created in the current directory"));

            var ready = parser.Push(
                "Code anywhere with the Claude mobile app or https://claude.ai/code?environment=env_01TTYK");
            Assert.NotNull(ready);
            Assert.Equal("ready", ready!.State);
            Assert.Equal("https://claude.ai/code?environment=env_01TTYK", ready.Url);
            Assert.Equal(0, ready.ActiveSessions);

            // TUI redraw repeats the same lines — no new state
            Assert.Null(parser.Push(
                "Code anywhere with the Claude mobile app or https://claude.ai/code?environment=env_01TTYK"));
            Assert.Null(parser.Push("    Capacity: 0/32 · New sessions will be created in the current directory"));

            // a phone connects
            var connected = parser.Push("    Capacity: 1/32 · New sessions will be created in the current directory");
            Assert.NotNull(connected);
            Assert.Equal("ready", connected!.State);
            Assert.Equal(1, connected.ActiveSessions);
            Assert.Equal("https://claude.ai/code?environment=env_01TTYK", connected.Url);
        }
    }

    public class CliSessionReaderTests
    {
        [Fact]
        public void MungePath_ReplacesNonAlphanumericWithDashes()
        {
            Assert.Equal("C--Users-Jan-CodeAstrogator", CliSessionReader.MungePath(@"C:\Users\Jan\CodeAstrogator"));
            Assert.Equal("C--Users-Jan-CodeAstrogator", CliSessionReader.MungePath(@"C:\Users\Jan\CodeAstrogator\"));
            Assert.Equal("c--repo-my-app", CliSessionReader.MungePath("c:/repo/my.app"));
        }

        [Fact]
        public void ImportTranscript_BuildsTranscriptMessages()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "remote-session.jsonl");
            var session = CliSessionReader.ImportTranscript(path);

            Assert.NotNull(session);
            Assert.Equal("remote-session", session!.SessionId);
            Assert.Equal("Fix the login bug", session.Title);

            var roles = session.Messages.Select(m => m.Value<string>("role")).ToArray();
            Assert.Equal(new[] { "user", "assistant", "tool", "tool", "assistant" }, roles);

            // split text blocks of the same assistant message are merged
            Assert.Equal("Let me look. Reading the file now.", session.Messages[1].Value<string>("text"));
            Assert.Equal("Done.", session.Messages[4].Value<string>("text"));

            // tool status patched from the matching tool_result
            Assert.Equal("Read", session.Messages[2].Value<string>("toolName"));
            Assert.Equal("ok", session.Messages[2].Value<string>("status"));
            Assert.Equal("Bash", session.Messages[3].Value<string>("toolName"));
            Assert.Equal("error", session.Messages[3].Value<string>("status"));

            // context size from the last assistant usage (input + cache + output)
            Assert.Equal(1200 + 24000 + 800 + 540, session.ContextTokens);
        }

        [Fact]
        public void ImportTranscript_WithoutUserMessages_ReturnsNull()
        {
            var path = Path.Combine(Path.GetTempPath(), "ccp-empty-" + Guid.NewGuid().ToString("n") + ".jsonl");
            File.WriteAllLines(path, new[]
            {
                "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"s\"}",
                "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"<local-command-caveat>x</local-command-caveat>\"},\"uuid\":\"u0\"}",
            });
            try
            {
                Assert.Null(CliSessionReader.ImportTranscript(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void FindSessionsSince_FiltersByMtime_NewestFirst()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ccp-sessions-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            try
            {
                var old = Path.Combine(dir, "old.jsonl");
                var newer = Path.Combine(dir, "newer.jsonl");
                var newest = Path.Combine(dir, "newest.jsonl");
                File.WriteAllText(old, "{}");
                File.WriteAllText(newer, "{}");
                File.WriteAllText(newest, "{}");
                var now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(old, now.AddHours(-2));
                File.SetLastWriteTimeUtc(newer, now.AddMinutes(-5));
                File.SetLastWriteTimeUtc(newest, now.AddMinutes(-1));

                var found = CliSessionReader.FindSessionsSince(dir, now.AddMinutes(-30));
                Assert.Equal(new[] { newest, newer }, found);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
