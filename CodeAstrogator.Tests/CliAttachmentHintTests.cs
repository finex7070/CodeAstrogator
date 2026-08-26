using System;
using System.IO;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class CliAttachmentHintTests : IDisposable
    {
        private readonly string _dir;

        public CliAttachmentHintTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ca-hint-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string WriteFile(string name, long bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        [Theory]
        [InlineData(@"C:\p\shot.png", true)]
        [InlineData(@"C:\p\shot.JPG", true)]
        [InlineData(@"C:\p\anim.gif", true)]
        [InlineData(@"C:\p\Program.cs", false)]
        [InlineData(@"C:\p\notes.md", false)]
        [InlineData(@"C:\p\vector.svg", false)] // text/XML — the CLI reads it as text, not as an image block
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsImagePath_ClassifiesByExtension(string? path, bool expected)
        {
            Assert.Equal(expected, CliAttachmentHint.IsImagePath(path));
        }

        [Fact]
        public void IsOversizedImage_AtTheLimit_IsFalse()
        {
            var path = WriteFile("at-limit.png", CliAttachmentHint.InlineImageLimitBytes);
            Assert.False(CliAttachmentHint.IsOversizedImage(path));
        }

        [Fact]
        public void IsOversizedImage_OneByteOver_IsTrue()
        {
            var path = WriteFile("over.png", CliAttachmentHint.InlineImageLimitBytes + 1);
            Assert.True(CliAttachmentHint.IsOversizedImage(path));
        }

        [Fact]
        public void IsOversizedImage_LargeNonImage_IsFalse()
        {
            // a big .cs file is fine — the limit only drops image content
            var path = WriteFile("big.cs", CliAttachmentHint.InlineImageLimitBytes * 2);
            Assert.False(CliAttachmentHint.IsOversizedImage(path));
        }

        [Fact]
        public void IsOversizedImage_MissingFile_IsFalse()
        {
            Assert.False(CliAttachmentHint.IsOversizedImage(Path.Combine(_dir, "gone.png")));
        }

        [Fact]
        public void BuildReadHint_NoOversizedImages_IsNull()
        {
            Assert.Null(CliAttachmentHint.BuildReadHint(new string[0]));
            Assert.Null(CliAttachmentHint.BuildReadHint(null));
            Assert.Null(CliAttachmentHint.BuildReadHint(new[] { "", "   " }));
        }

        [Fact]
        public void BuildReadHint_SingleImage_IsSingularAndListsThePath()
        {
            var hint = CliAttachmentHint.BuildReadHint(new[] { @"C:\p\shot.png" });
            Assert.NotNull(hint);
            Assert.Contains("image file is larger", hint!);
            Assert.Contains("Open it with the Read tool", hint);
            Assert.Contains(@"C:\p\shot.png", hint);
        }

        [Fact]
        public void BuildReadHint_SeveralImages_IsPluralAndDeduplicates()
        {
            var hint = CliAttachmentHint.BuildReadHint(new[]
            {
                @"C:\p\a.png", @"C:\p\b.png", @"C:\P\A.PNG", // same file, different casing
            });
            Assert.NotNull(hint);
            Assert.Contains("image files are larger", hint!);
            Assert.Contains("Open them with the Read tool", hint);
            var lines = hint!.Split('\n');
            Assert.Equal(3, lines.Length); // the note + two paths
        }
    }
}
