using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>The guards of typing a prompt into a remote background session (`claude attach` in a hidden pseudo
    /// console). Screen samples are the stripped terminal output measured against CLI 2.1.319.</summary>
    public class RemotePromptSenderTests
    {
        private const string Rule = "────────────────────────────────────────────────────────";

        private static readonly string IdleScreen =
            " ▐▛███▛█ Claude Code v2.1.319 \n ✔ Update installed · Restart to update \n" + Rule + " ca-spike ─\n"
            + "❯ Try \"how does <filepath> work?\" \n" + Rule + "\n ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents \n";

        private static readonly string PermissionDialog =
            IdleScreen + " Bash command \n npm run build \n Do you want to proceed? \n❯ 1. Yes \n 2. Yes, and don't ask again \n 3. No \n"
            + " Esc to cancel \n";

        private static readonly string QuestionDialog =
            IdleScreen + " Which framework? \n❯ 1. xUnit \n 2. NUnit \n Enter to select · ↑/↓ to navigate · Esc to cancel \n";

        [Fact]
        public void IdleInputBox_IsReady_AndThePlaceholderIsNotText()
        {
            Assert.True(RemotePromptSender.IsInputReady(IdleScreen));
            Assert.Equal("", RemotePromptSender.InputBoxText(IdleScreen));
        }

        [Fact]
        public void OpenDialogs_AreNeverTypedInto()
        {
            Assert.False(RemotePromptSender.IsInputReady(PermissionDialog));
            Assert.False(RemotePromptSender.IsInputReady(QuestionDialog));
            Assert.False(RemotePromptSender.IsInputReady(""));
            Assert.False(RemotePromptSender.IsInputReady(" Attaching… ")); // not drawn yet
        }

        [Fact]
        public void LeftoverInput_IsRead()
        {
            var screen = IdleScreen + Rule + "\n❯ second line äöü \"quoted\" \n" + Rule + "\n ⏵⏵ auto mode on (shift+tab to cycle) \n";
            Assert.True(RemotePromptSender.IsInputReady(screen));
            Assert.Equal("second line äöü \"quoted\"", RemotePromptSender.InputBoxText(screen));
        }

        [Fact]
        public void PasteIsSeen_ByItsFirstWords_OrThePastedTextPlaceholder()
        {
            Assert.True(RemotePromptSender.PasteVisible("❯ alpha line \n beta line \n" + Rule, "alpha line\nbeta line"));
            Assert.True(RemotePromptSender.PasteVisible("❯ [Pasted text #1 +40 lines] \n" + Rule, "x\n" + new string('y', 5000)));
            Assert.False(RemotePromptSender.PasteVisible("❯ 1. Yes \n", "Add a unit test for the cart totals"));
        }

        [Fact]
        public void Sanitize_KeepsLineFeedsOnly_DropsControls_AndDefusesModeCharacters()
        {
            // \r inside a bracketed paste submits the line (measured) → \n only
            Assert.Equal("one\ntwo\nthree", RemotePromptSender.SanitizePrompt("one\r\ntwo\rthree"));
            // an ESC would end the paste early ("\e[201~") and could drive the TUI; C1 CSI (U+009B) too
            Assert.Equal("a[201~b[2Jc", RemotePromptSender.SanitizePrompt("a\u001b[201~b\u009b[2Jc"));
            Assert.Equal("tab here", RemotePromptSender.SanitizePrompt("tab\there"));
            // "!" = bash mode, "#" = memory mode at the start of the box
            Assert.Equal(" !rm -rf build", RemotePromptSender.SanitizePrompt("!rm -rf build"));
            Assert.Equal(" # note", RemotePromptSender.SanitizePrompt("# note"));
            Assert.Equal("", RemotePromptSender.SanitizePrompt("  \r\n "));
        }

        [Fact]
        public void StripAnsi_TurnsTheTerminalStreamIntoText()
        {
            var raw = "\u001b[?1049h\u001b[2J\u001b[H\u001b]0;Claude Code\u0007\u001b[38;2;215;119;87m❯\u001b[39m hello\r\n";
            var text = RemotePromptSender.StripAnsi(raw);
            Assert.DoesNotContain("\u001b", text);
            Assert.Equal("❯ hello", System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim());
        }
    }
}
