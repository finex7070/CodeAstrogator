using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ProcessHostTests
    {
        [Fact]
        public void BuildArguments_MinimalTurn_PromptStaysOffTheCommandLine()
        {
            var args = ClaudeCliProcessHost.BuildArguments(new ClaudeTurnRequest
            {
                Prompt = "hello multi\nline prompt",
                ExecutablePath = "claude.exe",
            });

            // prompt is piped via stdin, never via argv
            Assert.Equal("-p --output-format stream-json --verbose --include-partial-messages", args);
        }

        [Fact]
        public void BuildArguments_WithResumeModelAndPermissionMode()
        {
            var args = ClaudeCliProcessHost.BuildArguments(new ClaudeTurnRequest
            {
                Prompt = "do it",
                SessionId = "sess-1",
                Model = "opus",
                PermissionMode = "acceptEdits",
            });

            Assert.Contains("--resume sess-1", args);
            Assert.Contains("--model opus", args);
            Assert.Contains("--permission-mode acceptEdits", args);
            Assert.DoesNotContain("do it", args);
        }

        [Fact]
        public void BuildArguments_WithEffort()
        {
            var args = ClaudeCliProcessHost.BuildArguments(new ClaudeTurnRequest
            {
                Prompt = "x",
                Effort = "xhigh",
            });

            Assert.Contains("--effort xhigh", args);
        }

        [Fact]
        public void BuildArguments_DefaultPermissionMode_OmitsFlag()
        {
            var args = ClaudeCliProcessHost.BuildArguments(new ClaudeTurnRequest
            {
                Prompt = "x",
                PermissionMode = "default",
            });

            Assert.DoesNotContain("--permission-mode", args);
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("two words", "\"two words\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData("", "\"\"")]
        public void Quote_FollowsWindowsArgvRules(string input, string expected)
        {
            Assert.Equal(expected, ClaudeCliProcessHost.Quote(input));
        }

        [Fact]
        public void Quote_TrailingBackslashBeforeClosingQuote_IsDoubled()
        {
            Assert.Equal("\"C:\\path with space\\\\\"", ClaudeCliProcessHost.Quote("C:\\path with space\\"));
        }

    }
}
