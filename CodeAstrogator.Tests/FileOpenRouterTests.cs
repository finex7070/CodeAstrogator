using System;
using System.IO;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class FileOpenRouterTests
    {
        // Every extension that is not VS-owned is asked about its shell association; in the tests the
        // probe is injected so the result never depends on what this machine has registered.
        private static readonly Func<string, bool> Associated = _ => true;
        private static readonly Func<string, bool> NotAssociated = _ => false;

        [Theory]
        [InlineData(@"C:\repo\Core\NdjsonParser.cs")]
        [InlineData(@"C:\repo\CodeAstrogator.csproj")]
        [InlineData(@"C:\repo\CodeAstrogator.slnx")]
        [InlineData(@"C:\repo\docs\NOTES.md")]
        [InlineData(@"C:\repo\WebUI\app.js")]
        [InlineData(@"C:\repo\WebUI\app.css")]
        [InlineData(@"C:\repo\appsettings.json")]
        [InlineData(@"C:\repo\Directory.Build.props")]
        [InlineData(@"C:\repo\.editorconfig")]
        [InlineData(@"C:\repo\Dockerfile")]
        [InlineData(@"C:\repo\Makefile")]
        public void VsOwnedTypes_OpenInVisualStudio(string path)
        {
            // even with a registered default program (e.g. an editor claiming .json) VS wins
            Assert.Equal(FileOpenTarget.VisualStudio, FileOpenRouter.Decide(path, Associated));
        }

        [Theory]
        [InlineData(@"C:\repo\docs\screenshot.png")]
        [InlineData(@"C:\repo\docs\photo.JPG")]
        [InlineData(@"C:\repo\docs\anim.gif")]
        [InlineData(@"C:\repo\docs\spec.pdf")]
        [InlineData(@"C:\repo\docs\report.docx")]
        [InlineData(@"C:\repo\docs\sheet.xlsx")]
        [InlineData(@"C:\repo\bin\bundle.zip")]
        [InlineData(@"C:\repo\media\clip.mp4")]
        public void AssociatedNonVsTypes_OpenWithDefaultProgram(string path)
        {
            Assert.Equal(FileOpenTarget.DefaultProgram, FileOpenRouter.Decide(path, Associated));
        }

        [Theory]
        [InlineData(@"C:\repo\bin\tool.exe")]
        [InlineData(@"C:\repo\bin\lib.dll")]
        [InlineData(@"C:\downloads\setup.msi")]
        [InlineData(@"C:\downloads\app.msix")]
        [InlineData(@"C:\tmp\script.vbs")]
        [InlineData(@"C:\tmp\payload.jar")]
        [InlineData(@"C:\tmp\tweak.reg")]
        [InlineData(@"C:\tmp\shortcut.lnk")]
        public void RunnableTypes_AreOnlyRevealedInExplorer(string path)
        {
            // "open with the default program" would EXECUTE these — a click in the transcript must not
            // run what a turn just produced, so they are revealed instead, even though they are associated
            Assert.Equal(FileOpenTarget.Explorer, FileOpenRouter.Decide(path, Associated));
        }

        [Theory]
        [InlineData(@"C:\repo\build.bat")]
        [InlineData(@"C:\repo\deploy.ps1")]
        [InlineData(@"C:\repo\WebUI\app.js")]
        public void ScriptsVsEditsAsText_StillOpenInVisualStudio(string path)
        {
            // safe: VS shows them as text rather than running them
            Assert.Equal(FileOpenTarget.VisualStudio, FileOpenRouter.Decide(path, Associated));
        }

        [Fact]
        public void UnassociatedUnknownType_FallsBackToVisualStudio()
        {
            // nothing registered for .qqq → the IDE at least shows the bytes instead of Windows
            // popping its "How do you want to open this file?" chooser
            Assert.Equal(FileOpenTarget.VisualStudio, FileOpenRouter.Decide(@"C:\repo\data.qqq", NotAssociated));
        }

        [Fact]
        public void ExtensionlessFile_OpensInVisualStudio()
        {
            Assert.Equal(FileOpenTarget.VisualStudio, FileOpenRouter.Decide(@"C:\repo\LICENSE", Associated));
        }

        [Fact]
        public void Directory_GoesToExplorer()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(FileOpenTarget.Explorer, FileOpenRouter.Decide(dir, Associated));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyPath_GoesToExplorer(string? path)
        {
            Assert.Equal(FileOpenTarget.Explorer, FileOpenRouter.Decide(path, Associated));
        }

        [Theory]
        [InlineData(@"C:\repo\a.PNG", "png")]
        [InlineData(@"C:\repo\a.tar.gz", "gz")]
        [InlineData(@"C:\repo\LICENSE", "")]
        public void ExtensionOf_NormalizesToLowercaseWithoutDot(string path, string expected)
        {
            Assert.Equal(expected, FileOpenRouter.ExtensionOf(path));
        }

        [Fact]
        public void HasDefaultProgram_EmptyExtension_IsFalse()
        {
            Assert.False(FileOpenRouter.HasDefaultProgram(""));
            Assert.False(FileOpenRouter.HasDefaultProgram(null));
        }

        [Fact]
        public void HasDefaultProgram_ExeIsAlwaysAssociated()
        {
            // sanity check that the shell lookup itself works on the test machine: ".exe" runs itself
            Assert.True(FileOpenRouter.HasDefaultProgram(".exe"));
        }

        [Fact]
        public void HasDefaultProgram_PackagedImageHandler_IsRecognized()
        {
            // Regression: with Photos (a packaged/UWP app) as the default handler, ASSOCSTR_EXECUTABLE
            // and ASSOCSTR_COMMAND are BOTH empty (ERROR_NO_ASSOCIATION) and only the friendly app name
            // is reported — the probe used to answer "no association", so images opened in VS after all.
            Assert.True(FileOpenRouter.HasDefaultProgram(".png"));
            Assert.True(FileOpenRouter.HasDefaultProgram("png")); // with and without the dot
        }

        [Fact]
        public void HasDefaultProgram_OpenWithPickerCountsAsNone()
        {
            // an extension nobody registered resolves to OpenWith.exe — not a real default program
            Assert.False(FileOpenRouter.HasDefaultProgram(".zzq9x"));
        }
    }
}
