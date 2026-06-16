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
        public void Wildcardize_ReplacesQuotedArgumentValues()
        {
            Assert.Equal("Out-File -FilePath \"*\" -Encoding utf8",
                ShellCommandSplitter.Wildcardize("Out-File -FilePath \"lorem.txt\" -Encoding utf8"));
            Assert.Equal("git commit -m \"*\"", ShellCommandSplitter.Wildcardize("git commit -m \"fix the bug\""));
        }
    }
}
