using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ChangelogReaderTests
    {
        private const string Text = "# Changelog\r\n\r\nAll notable changes.\r\n\r\n---\r\n\r\n"
            + "## [1.3.0] – 2026-11-10\r\n\r\n### Added\r\n- **Panel.** Plugins and skills.\r\n  - nested\r\n\r\n"
            + "## [1.2.1] – 2026-11-04\r\n\r\n### Fixed\r\n- Stop stays put.\r\n\r\n"
            + "## [1.2.0] - 2026-11-04\r\n\r\n### Added\r\n- Notifications.\r\n\r\n"
            + "## [1.1.0] – 2026-10-29\r\n\r\n### Added\r\n- Fonts.\r\n";

        [Fact]
        public void Parse_SectionsWithDateAndBody()
        {
            var e = ChangelogReader.Parse(Text);
            Assert.Equal(new[] { "1.3.0", "1.2.1", "1.2.0", "1.1.0" }, e.Select(x => x.Version));
            Assert.Equal("2026-11-10", e[0].Date);
            Assert.Equal("2026-11-04", e[2].Date); // plain hyphen works too
            Assert.StartsWith("### Added", e[0].Markdown);
            Assert.Contains("  - nested", e[0].Markdown);
            Assert.DoesNotContain("1.2.1", e[0].Markdown);
        }

        [Fact]
        public void Since_EverythingAfterTheLastSeenVersion_NewestFirst()
        {
            var e = ChangelogReader.Parse(Text);
            Assert.Equal(new[] { "1.3.0", "1.2.1" }, ChangelogReader.Since(e, "1.2.0", "1.3.0").Select(x => x.Version));
            Assert.Empty(ChangelogReader.Since(e, "1.3.0", "1.3.0"));
            // no last-seen version (upgraded from a release before this feature) → just the current one
            Assert.Equal(new[] { "1.3.0" }, ChangelogReader.Since(e, null, "1.3.0").Select(x => x.Version));
            // never newer than what is installed, and capped
            Assert.Equal(new[] { "1.2.1", "1.2.0" }, ChangelogReader.Since(e, "1.1.0", "1.2.1").Select(x => x.Version));
            Assert.Single(ChangelogReader.Since(e, "1.0.0", "1.3.0", max: 1));
        }

        [Fact]
        public void IsNewer()
        {
            Assert.True(ChangelogReader.IsNewer("1.3.0", "1.2.1"));
            Assert.True(ChangelogReader.IsNewer("1.3.0", null));
            Assert.False(ChangelogReader.IsNewer("1.3.0", "1.3.0"));
            Assert.False(ChangelogReader.IsNewer("1.2.9", "1.10.0")); // numeric, not text
            Assert.False(ChangelogReader.IsNewer(null, "1.0.0"));
        }
    }
}
