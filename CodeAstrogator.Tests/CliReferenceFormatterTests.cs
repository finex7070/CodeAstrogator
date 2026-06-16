using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>
    /// The CLI's @-parser stops at the first whitespace, so paths with spaces (Windows
    /// profiles like "C:\Users\Jan Huels\…" where pasted screenshots land) must be quoted.
    /// See docs/NOTES.md ("Attachment-Anzeige" / paste handling).
    /// </summary>
    public class CliReferenceFormatterTests
    {
        [Fact]
        public void SpaceFreePath_IsUnquoted()
        {
            Assert.Equal(@"@C:\src\foo.cs", CliReferenceFormatter.FormatFileReference(@"C:\src\foo.cs"));
        }

        [Fact]
        public void PathWithSpace_IsQuoted()
        {
            Assert.Equal(
                "@\"C:\\Users\\Jan Huels\\AppData\\Local\\CodeAstrogator\\pasted\\paste.png\"",
                CliReferenceFormatter.FormatFileReference(@"C:\Users\Jan Huels\AppData\Local\CodeAstrogator\pasted\paste.png"));
        }

        [Fact]
        public void SpaceFreePath_KeepsLineSuffixUnquoted()
        {
            Assert.Equal(@"@C:\src\foo.cs#L10-20", CliReferenceFormatter.FormatFileReference(@"C:\src\foo.cs", "#L10-20"));
        }

        [Fact]
        public void PathWithSpace_KeepsLineSuffixOutsideQuotes()
        {
            Assert.Equal(
                "@\"C:\\my proj\\foo.cs\"#L10-20",
                CliReferenceFormatter.FormatFileReference(@"C:\my proj\foo.cs", "#L10-20"));
        }
    }
}
