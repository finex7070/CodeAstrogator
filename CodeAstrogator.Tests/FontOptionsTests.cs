using CodeAstrogator.Options;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class FontOptionsTests
    {
        [Theory]
        [InlineData("roboto", "roboto")]
        [InlineData("cascadia", "cascadia")]
        [InlineData("times", "times")]
        [InlineData("", "default")]
        [InlineData(null, "default")]
        [InlineData("comic", "default")]                    // unknown built-in
        [InlineData("custom:Fira.ttf", "custom:Fira.ttf")]   // a loaded font
        [InlineData("custom:fira.TTF", "custom:fira.TTF")]   // file names compare case-insensitively
        [InlineData("custom:Gone.ttf", "default")]          // removed in the settings window
        public void NormalizeFont_KeepsKnownIds(string? value, string expected) =>
            Assert.Equal(expected, AstrogatorOptions.NormalizeFont(value, new[] { "Fira.ttf" }));

        [Theory]
        [InlineData(0, 100)]     // unset → 1×
        [InlineData(-3, 100)]
        [InlineData(100, 100)]
        [InlineData(110, 110)]
        [InlineData(25, 70)]     // below the smallest → 0.7×
        [InlineData(118, 120)]   // nearest offered factor
        [InlineData(200, 150)]   // above the largest → 1.5×
        public void ClampFontScale_SnapsToTheOfferedFactors(int percent, int expected) =>
            Assert.Equal(expected, AstrogatorOptions.ClampFontScale(percent));
    }
}
