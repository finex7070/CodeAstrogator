using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class CliImageAttachmentsTests : IDisposable
    {
        private readonly string _dir;

        public CliImageAttachmentsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ca-img-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string WriteFile(string name, long bytes)
        {
            var path = Path.Combine(_dir, name);
            using (var fs = new FileStream(path, FileMode.Create))
                fs.SetLength(bytes); // sparse-ish: only the size matters for the selection
            return path;
        }

        [Theory]
        [InlineData(@"C:\p\shot.png", "image/png")]
        [InlineData(@"C:\p\photo.JPG", "image/jpeg")]
        [InlineData(@"C:\p\photo.jpeg", "image/jpeg")]
        [InlineData(@"C:\p\anim.gif", "image/gif")]
        [InlineData(@"C:\p\pic.webp", "image/webp")]
        [InlineData(@"C:\p\old.bmp", "image/bmp")]
        [InlineData(@"C:\p\scan.tiff", null)] // not inline-capable → @path + Read hint
        [InlineData(@"C:\p\vector.svg", null)]
        [InlineData(@"C:\p\Program.cs", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void MediaTypeFor_KnownFormatsOnly(string? path, string? expected)
        {
            Assert.Equal(expected, CliImageAttachments.MediaTypeFor(path));
        }

        [Fact]
        public void SelectInline_KeepsOrder_SkipsNonImagesMissingAndDuplicates()
        {
            var a = WriteFile("a.png", 2_000_000);
            var cs = WriteFile("code.cs", 10);
            var b = WriteFile("b.jpg", 400_000);
            var missing = Path.Combine(_dir, "gone.png");

            var inline = CliImageAttachments.SelectInline(new[] { a, cs, missing, b, a.ToUpperInvariant() });

            Assert.Equal(new[] { a, b }, inline);
        }

        [Fact]
        public void SelectInline_RespectsPerImageAndTotalCaps()
        {
            var tooBig = WriteFile("huge.png", CliImageAttachments.MaxImageBytes + 1);
            var big1 = WriteFile("big1.png", 20L * 1024 * 1024);
            var big2 = WriteFile("big2.png", 20L * 1024 * 1024); // would exceed the total
            var small = WriteFile("small.png", 1024 * 1024);     // still fits after big1

            var inline = CliImageAttachments.SelectInline(new[] { tooBig, big1, big2, small });

            Assert.Equal(new[] { big1, small }, inline);
        }

        [Fact]
        public void BuildUserMessage_AppendsImageBlocksAfterTheText()
        {
            var png = Path.Combine(_dir, "shot.png");
            File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            var obj = JObject.Parse(StreamJsonInput.BuildUserMessage("look", new[] { png }));
            var content = (JArray)obj["message"]!["content"]!;

            Assert.Equal(2, content.Count);
            Assert.Equal("text", content[0]!.Value<string>("type"));
            var image = (JObject)content[1]!;
            Assert.Equal("image", image.Value<string>("type"));
            Assert.Equal("base64", image["source"]!.Value<string>("type"));
            Assert.Equal("image/png", image["source"]!.Value<string>("media_type"));
            Assert.Equal(Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), image["source"]!.Value<string>("data"));
        }

        [Fact]
        public void BuildUserMessage_UnreadableImage_BecomesATextNote()
        {
            var gone = Path.Combine(_dir, "gone.png");

            var obj = JObject.Parse(StreamJsonInput.BuildUserMessage("look", new[] { gone }));
            var content = (JArray)obj["message"]!["content"]!;

            Assert.Equal(2, content.Count);
            Assert.Equal("text", content[1]!.Value<string>("type"));
            Assert.Contains("could not be read", content[1]!.Value<string>("text"));
            Assert.DoesNotContain(content, b => b.Value<string>("type") == "image");
        }
    }
}
