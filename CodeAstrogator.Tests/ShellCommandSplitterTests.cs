using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ShellCommandSplitterTests
    {
        [Fact]
        public void Splits_On_Single_Ampersand()
        {
            Assert.Equal(
                new[] { "npm run build", "npm test" },
                ShellCommandSplitter.Split("npm run build & npm test").ToArray());
        }

        [Fact]
        public void Splits_On_AndOr_And_Semicolon()
        {
            Assert.Equal(
                new[] { "a", "b", "c", "d" },
                ShellCommandSplitter.Split("a && b || c ; d").ToArray());
        }

        [Fact]
        public void Keeps_Redirect_And_Pipe_As_One_Command()
        {
            // 2>&1 must NOT split (no whitespace around &); pipes belong to one pipeline
            Assert.Equal(
                new[] { "node x 2>&1 | grep y" },
                ShellCommandSplitter.Split("node x 2>&1 | grep y").ToArray());
        }

        [Fact]
        public void Ignores_Operators_Inside_Quotes()
        {
            Assert.Equal(
                new[] { "echo 'a & b'", "ls" },
                ShellCommandSplitter.Split("echo 'a & b' & ls").ToArray());
        }

        [Fact]
        public void RealWorld_Build_Command_Splits_Into_Two()
        {
            const string cmd =
                "node --check WebUI\\app.js & \"C:\\Program Files\\MSBuild.exe\" sln /t:Build 2>&1 | Select-String error";
            var parts = ShellCommandSplitter.Split(cmd);
            Assert.Equal(2, parts.Count);
            Assert.Equal("node --check WebUI\\app.js", parts[0]);
            Assert.StartsWith("\"C:\\Program Files\\MSBuild.exe\"", parts[1]);
            Assert.Contains("2>&1", parts[1]); // redirect preserved, not split
        }

        [Fact]
        public void Empty_Returns_Empty()
        {
            Assert.Empty(ShellCommandSplitter.Split(""));
            Assert.Empty(ShellCommandSplitter.Split("   "));
        }

        // ── ExtractCommands (auto-approve pattern source) ────────────────────────

        [Fact]
        public void ExtractCommands_DropsHereStringAssignment_AndBareVariablePipe()
        {
            // The exact shape that previously suggested the "$lorem = @'…" assignment.
            const string cmd =
                "$lorem = @'\nLorem ipsum dolor sit amet,\nconsectetur adipiscing elit.\n'@\n"
                + "$lorem | Out-File -FilePath \"lorem.txt\" -Encoding utf8\n"
                + "Get-Item \"lorem.txt\" | Select-Object FullName, Length";
            Assert.Equal(
                new[] { "Out-File -FilePath \"lorem.txt\" -Encoding utf8", "Get-Item \"lorem.txt\"", "Select-Object FullName, Length" },
                ShellCommandSplitter.ExtractCommands(cmd).ToArray());
        }

        [Fact]
        public void ExtractCommands_DropsAssignments_JoinedByAmpersand()
        {
            Assert.Equal(
                new[] { "npm test" },
                ShellCommandSplitter.ExtractCommands("$dir = 'build' & npm test").ToArray());
        }

        [Fact]
        public void ExtractCommands_PlainCommand_ReturnsItself()
        {
            Assert.Equal(new[] { "git status" }, ShellCommandSplitter.ExtractCommands("git status").ToArray());
        }

        [Fact]
        public void ExtractCommands_IgnoresSeparatorsInsideHereString()
        {
            // newlines/operators inside @'…'@ must not split; the whole assignment is dropped
            Assert.Empty(ShellCommandSplitter.ExtractCommands("$x = @'\na && b | c ; d\n'@"));
        }

        [Fact]
        public void ExtractCommands_BackslashEscapedQuote_KeepsSeparatorInsideString()
        {
            // bash: the \" is an escaped quote, so the ; and rm stay INSIDE the string and must
            // not be split off as a separate (dangerous) suggested command.
            Assert.Equal(
                new[] { "echo \"a\\\" ; rm -rf /\"" },
                ShellCommandSplitter.ExtractCommands("echo \"a\\\" ; rm -rf /\"").ToArray());
        }

        [Fact]
        public void ExtractCommands_PowerShellBacktick_EscapesSeparator()
        {
            // PowerShell: a backtick escapes the following ; so it is one command, not two.
            Assert.Equal(
                new[] { "echo `; ls" },
                ShellCommandSplitter.ExtractCommands("echo `; ls").ToArray());
        }

        [Fact]
        public void ExtractCommands_DoesNotSplitInsideScriptBlock()
        {
            // The ; lives inside { … }; the whole foreach is one statement.
            Assert.Equal(
                new[] { "foreach ($i in 1..3) { Write-Host $i; npm test }" },
                ShellCommandSplitter.ExtractCommands("foreach ($i in 1..3) { Write-Host $i; npm test }").ToArray());
        }

        [Fact]
        public void ExtractCommands_DoesNotSplitInsideSubexpression()
        {
            // The ; lives inside $( … ); the whole call is one command.
            Assert.Equal(
                new[] { "Write-Host $(git rev-parse HEAD; echo done)" },
                ShellCommandSplitter.ExtractCommands("Write-Host $(git rev-parse HEAD; echo done)").ToArray());
        }

        [Fact]
        public void ExtractCommands_DoubledQuote_KeepsSeparatorInsideString()
        {
            // PowerShell: "" is an escaped literal quote, so the ; stays inside the string.
            Assert.Equal(
                new[] { "Write-Host \"a ; \"\"q\"\" b\"" },
                ShellCommandSplitter.ExtractCommands("Write-Host \"a ; \"\"q\"\" b\"").ToArray());
        }

        [Fact]
        public void Wildcardize_ReplacesQuotedArgumentsWithBareWildcard()
        {
            // Quotes are dropped (not kept as "*") so the pattern is quote-agnostic.
            Assert.Equal("Out-File -FilePath * -Encoding utf8",
                ShellCommandSplitter.Wildcardize("Out-File -FilePath \"lorem.txt\" -Encoding utf8"));
            Assert.Equal("git commit -m *", ShellCommandSplitter.Wildcardize("git commit -m \"fix the bug\""));
            Assert.Equal("git commit -m *", ShellCommandSplitter.Wildcardize("git commit -m 'fix the bug'"));
        }

        [Fact]
        public void Wildcardize_CollapsesAdjacentWildcards()
        {
            Assert.Equal("echo *", ShellCommandSplitter.Wildcardize("echo \"a\" \"b\""));        // adjacent → "* *" collapses to "*"
            Assert.Equal("cp * -t *", ShellCommandSplitter.Wildcardize("cp \"a\" -t \"dir\"")); // non-adjacent → both kept
        }

        // ── MatchesGlob ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("git status", "*")]                       // bare * matches anything
        [InlineData("git status", "git *")]                   // trailing wildcard
        [InlineData("smiley.py", "*.py")]                      // leading wildcard
        [InlineData("npm run build", "npm * build")]          // middle wildcard
        [InlineData("GIT STATUS", "git status")]              // case-insensitive
        [InlineData("git\nlog", "git*log")]                   // * spans newlines (Singleline)
        public void MatchesGlob_Matches(string value, string pattern) =>
            Assert.True(ShellCommandSplitter.MatchesGlob(value, pattern));

        [Theory]
        [InlineData("git push", "git status")]
        [InlineData("rm -rf /", "git *")]
        [InlineData("git commit --amend", "git commit -m *")] // -m required, no match
        public void MatchesGlob_DoesNotMatch(string value, string pattern) =>
            Assert.False(ShellCommandSplitter.MatchesGlob(value, pattern));

        [Fact]
        public void MatchesGlob_WildcardizedPattern_IsQuoteAgnostic()
        {
            // The exact regression: a wildcardized pattern must match the command regardless of the
            // quote style (or absence of quotes) the next invocation happens to use.
            var pattern = ShellCommandSplitter.Wildcardize("git commit -m \"first msg\""); // → git commit -m *
            Assert.True(ShellCommandSplitter.MatchesGlob("git commit -m \"another msg\"", pattern));
            Assert.True(ShellCommandSplitter.MatchesGlob("git commit -m 'another msg'", pattern));
            Assert.True(ShellCommandSplitter.MatchesGlob("git commit -m hello", pattern));
        }

        // ── IsCommandCovered ─────────────────────────────────────────────────────

        [Fact]
        public void IsCommandCovered_RequiresEverySubCommand()
        {
            var patterns = new[] { "npm run build" };
            Assert.True(ShellCommandSplitter.IsCommandCovered("npm run build", patterns));
            // second &&-stage not covered → the whole chain is NOT approved
            Assert.False(ShellCommandSplitter.IsCommandCovered("npm run build && rm -rf dist", patterns));
        }

        [Fact]
        public void IsCommandCovered_WildcardPatternCoversChain()
        {
            Assert.True(ShellCommandSplitter.IsCommandCovered("npm run build && npm test", new[] { "npm *" }));
            Assert.True(ShellCommandSplitter.IsCommandCovered("anything; here too", new[] { "*" }));
        }

        [Fact]
        public void IsCommandCovered_BlankPatterns_NeverMatch()
        {
            Assert.False(ShellCommandSplitter.IsCommandCovered("git status", new[] { "", "   " }));
            Assert.False(ShellCommandSplitter.IsCommandCovered("git status", new string[0]));
        }
    }
}
