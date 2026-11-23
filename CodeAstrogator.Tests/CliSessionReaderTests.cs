using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>Importing a CLI session transcript (Remote Control end / live mirror). Line shapes as written by CLI
    /// 2.1.330: a prompt from the panel (stream-json input, entrypoint sdk-cli) has content BLOCKS, one typed in the
    /// terminal / the app a STRING.</summary>
    public class CliSessionReaderTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n") + ".jsonl");

        public void Dispose()
        {
            try { File.Delete(_file); } catch { }
        }

        private const string PanelPrompt =
            "{\"type\":\"user\",\"isSidechain\":false,\"uuid\":\"u1\",\"timestamp\":\"2026-11-23T12:54:02.446Z\",\"entrypoint\":\"sdk-cli\","
            + "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"write a few lorem ipsum sentences into a file\"}]}}";

        private const string AssistantWrite =
            "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-11-23T12:54:10Z\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Write\",\"input\":{\"file_path\":\"lorem.txt\"}}]}}";

        private const string ToolResult =
            "{\"type\":\"user\",\"uuid\":\"r1\",\"timestamp\":\"2026-11-23T12:54:11Z\",\"message\":{\"role\":\"user\","
            + "\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]}}";

        private const string RemotePrompt =
            "{\"type\":\"user\",\"uuid\":\"u2\",\"timestamp\":\"2026-11-23T12:55:00Z\",\"entrypoint\":\"cli\","
            + "\"message\":{\"role\":\"user\",\"content\":\"remove one sentence\"}}";

        [Fact]
        public void PanelPrompts_StoredAsContentBlocks_AreImported()
        {
            File.WriteAllLines(_file, new[] { PanelPrompt, AssistantWrite, ToolResult, RemotePrompt });
            var session = CliSessionReader.ImportTranscript(_file)!;

            var users = session.Messages.Where(m => m.Value<string>("role") == "user").Select(m => m.Value<string>("text")).ToArray();
            Assert.Equal(new[] { "write a few lorem ipsum sentences into a file", "remove one sentence" }, users);
            Assert.Equal("write a few lorem ipsum sentences into a file", session.Title);
            // the tool_result-only user line stays a result, not a bubble
            Assert.Equal(new[] { "user", "tool", "user" }, session.Messages.Select(m => m.Value<string>("role")).ToArray());
        }

        [Fact]
        public void AttachedFilesSection_BecomesAttachmentChips()
        {
            var text = "Fix the totals\n\nAttached files:\n@C:\\src\\SampleShop\\Cart.cs\n@\"C:\\src\\Sample Shop\\Order Service.cs\"#L42-58\n"
                + "C:\\src\\SampleShop\\shot.png (image, included in this message)\n@C:\\src\\SampleShop\\Totals.cs#L7\n\n"
                + "Note: some images were too large to include.";
            var msg = CliSessionReader.BuildUserMessage(text, "u1", "ts");

            Assert.Equal("Fix the totals", msg.Value<string>("text"));
            var chips = msg["attachments"]!.Select(a => a.Value<string>("name") + " | " + a.Value<string>("path")).ToArray();
            Assert.Equal(new[]
            {
                "Cart.cs | C:\\src\\SampleShop\\Cart.cs",
                "Order Service.cs:42-58 | C:\\src\\Sample Shop\\Order Service.cs#L42-58",
                "shot.png | C:\\src\\SampleShop\\shot.png",
                "Totals.cs:7 | C:\\src\\SampleShop\\Totals.cs#L7",
            }, chips);
        }

        [Fact]
        public void TextWithoutAttachedFiles_StaysAsItIs()
        {
            var msg = CliSessionReader.BuildUserMessage("Plain prompt\n\nwith two paragraphs", "u1", "ts");
            Assert.Equal("Plain prompt\n\nwith two paragraphs", msg.Value<string>("text"));
            Assert.Null(msg["attachments"]);
        }
    }
}
