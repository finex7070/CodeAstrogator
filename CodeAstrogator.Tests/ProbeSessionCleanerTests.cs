using System;
using System.IO;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ProbeSessionCleanerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "probe-clean-" + Guid.NewGuid().ToString("n"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        // A `claude -p /usage` session file as CLI 2.1.312 writes it (shortened).
        private static readonly string[] UsageProbe =
        {
            @"{""type"":""queue-operation"",""operation"":""enqueue"",""timestamp"":""2026-10-29T07:30:30.267Z"",""sessionId"":""s1"",""content"":""/usage""}",
            @"{""type"":""queue-operation"",""operation"":""dequeue"",""timestamp"":""2026-10-29T07:30:30.270Z"",""sessionId"":""s1""}",
            @"{""parentUuid"":null,""isSidechain"":false,""type"":""user"",""message"":{""role"":""user"",""content"":""<local-command-caveat>The command below was run directly.</local-command-caveat>""}}",
            @"{""parentUuid"":""a"",""isSidechain"":false,""type"":""user"",""message"":{""role"":""user"",""content"":""<command-name>/usage</command-name>\n<command-args></command-args>""}}",
            @"{""parentUuid"":""b"",""isSidechain"":false,""type"":""system"",""subtype"":""local_command"",""content"":""<local-command-stdout>Current session: 5% used</local-command-stdout>""}",
            @"{""type"":""last-prompt"",""leafUuid"":""c"",""sessionId"":""s1""}",
            @"{""type"":""cost-state"",""sessionId"":""s1"",""totalCostUSD"":0}",
        };

        [Fact]
        public void UsageAndModelProbes_AreRecognized()
        {
            Assert.True(ProbeSessionCleaner.IsProbeTranscript(UsageProbe));

            var model = (string[])UsageProbe.Clone();
            model[0] = model[0].Replace("/usage", "/model");
            Assert.True(ProbeSessionCleaner.IsProbeTranscript(model));
        }

        [Fact]
        public void RealConversations_AreKept()
        {
            // someone typed /usage and then asked something
            var typed = new[]
            {
                UsageProbe[0], UsageProbe[2],
                @"{""type"":""user"",""message"":{""role"":""user"",""content"":""How do I add a column?""}}",
            };
            Assert.False(ProbeSessionCleaner.IsProbeTranscript(typed));

            var answered = new[]
            {
                UsageProbe[0],
                @"{""type"":""assistant"",""message"":{""role"":""assistant"",""content"":[{""type"":""text"",""text"":""Hi""}]}}",
            };
            Assert.False(ProbeSessionCleaner.IsProbeTranscript(answered));

            var otherCommand = (string[])UsageProbe.Clone();
            otherCommand[0] = otherCommand[0].Replace("/usage", "Fix the build");
            Assert.False(ProbeSessionCleaner.IsProbeTranscript(otherCommand));

            Assert.False(ProbeSessionCleaner.IsProbeTranscript(new[] { "not json" }));
            Assert.False(ProbeSessionCleaner.IsProbeTranscript(Array.Empty<string>()));
        }

        [Fact]
        public void Clean_DeletesOnlyOldProbeFiles()
        {
            var project = Directory.CreateDirectory(Path.Combine(_root, "C--src-SampleShop")).FullName;
            var old = DateTime.UtcNow.AddHours(-1);

            var probe = Path.Combine(project, "probe.jsonl");
            File.WriteAllLines(probe, UsageProbe);
            File.SetLastWriteTimeUtc(probe, old);

            var fresh = Path.Combine(project, "running.jsonl"); // a probe that may still be writing
            File.WriteAllLines(fresh, UsageProbe);

            var chat = Path.Combine(project, "chat.jsonl");
            File.WriteAllLines(chat, new[]
            {
                @"{""type"":""user"",""message"":{""role"":""user"",""content"":""Refactor OrderService""}}",
            });
            File.SetLastWriteTimeUtc(chat, old);

            Assert.Equal(1, ProbeSessionCleaner.Clean(_root));
            Assert.False(File.Exists(probe));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(chat));
        }
    }
}
