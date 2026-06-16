using CodeAstrogator.Services;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>
    /// Covers the migration/serialization of <c>AutoApprovePatterns</c>: the current format is
    /// a JSON array, but the legacy newline-separated string must still load (so existing user
    /// settings survive an upgrade). See docs/NOTES.md ("Auto-Approve-Patterns").
    /// </summary>
    public class AutoApprovePatternsTests
    {
        [Fact]
        public void Parses_Json_Array()
        {
            Assert.Equal(
                new[] { "npm run build", "git status" },
                AstrogatorSettingsStore.ParsePatterns("[\"npm run build\",\"git status\"]").ToArray());
        }

        [Fact]
        public void Parses_Legacy_Newline_String()
        {
            Assert.Equal(
                new[] { "npm run build", "git status" },
                AstrogatorSettingsStore.ParsePatterns("npm run build\r\ngit status").ToArray());
        }

        [Fact]
        public void Empty_Input_Yields_Empty_List()
        {
            Assert.Empty(AstrogatorSettingsStore.ParsePatterns(""));
            Assert.Empty(AstrogatorSettingsStore.ParsePatterns("   "));
            Assert.Empty(AstrogatorSettingsStore.ParsePatterns(null));
        }

        [Fact]
        public void Malformed_Json_Falls_Back_To_Legacy_Parse()
        {
            // Leading '[' but invalid JSON → treated as a (single-line) legacy string, not dropped.
            Assert.Equal(new[] { "[not valid json" }, AstrogatorSettingsStore.ParsePatterns("[not valid json").ToArray());
        }

        [Fact]
        public void Normalize_Trims_Drops_Blanks_And_Dedupes_CaseInsensitively()
        {
            Assert.Equal(
                new[] { "Git Status", "npm test" },
                AstrogatorSettingsStore.Normalize(new[] { "  Git Status ", "", "git status", "npm test" }).ToArray());
        }
    }
}
