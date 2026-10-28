using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ShellTaskProcessTests
    {
        // Command lines as measured against CLI 2.1.287 (2026-10-28), children of claude.exe.
        private static readonly ChildProcessInfo Conhost = new ChildProcessInfo
        {
            ProcessId = 1, Name = "conhost.exe", CommandLine = @"\??\C:\Windows\system32\conhost.exe 0x4",
        };

        private static readonly ChildProcessInfo McpServer = new ChildProcessInfo
        {
            ProcessId = 2, Name = "cmd.exe", CommandLine = "C:\\Windows\\system32\\cmd.exe /d /s /c \"npx ^\"chrome-devtools-mcp@1.5.0^\"\"",
        };

        private static readonly ChildProcessInfo PowerShellTask = new ChildProcessInfo
        {
            ProcessId = 3, Name = "cmd.exe",
            CommandLine = "C:\\Windows\\System32\\cmd.exe /d /s /c \"\"C:\\Windows\\System32\\chcp.com\" 65001 >nul & "
                + "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                + "\"$__claudeCodeScript = $env:CLAUDE_CODE_SHELL_LAUNCHER_SCRIPT; Invoke-Expression -Command $__claudeCodeScript\" > "
                + "\"C:\\Users\\Jan\\AppData\\Local\\Temp\\claude\\C--Users-Jan-CodeAstrogator\\7be1754c\\tasks\\bnzzmop8l.output\" 2>&1\"",
        };

        private static ChildProcessInfo Bash(int pid, string escapedEval) => new ChildProcessInfo
        {
            ProcessId = pid, Name = "bash.exe",
            CommandLine = "\"C:\\Program Files\\Git\\bin\\bash.exe\" -c \"source /c/Users/Jan/.claude/shell-snapshots/snapshot-bash-1.sh "
                + "2>/dev/null || true && shopt -u extglob 2>/dev/null || true && " + escapedEval
                + " < /dev/null && pwd -P >| /c/Users/Jan/AppData/Local/Temp/claude-d3fa-cwd\"",
        };

        [Fact]
        public void PowerShell_IsFoundByTheTaskFileOnTheWrapperCommandLine()
        {
            var pid = ShellTaskProcess.Find(new[] { Conhost, McpServer, PowerShellTask }, "bnzzmop8l", "PowerShell", "Get-Date");
            Assert.Equal(3, pid);
        }

        [Fact]
        public void PowerShell_UnknownTask_IsNotGuessed()
        {
            var pid = ShellTaskProcess.Find(new[] { Conhost, McpServer, PowerShellTask }, "other", "PowerShell", "Get-Date");
            Assert.Null(pid);
        }

        [Fact]
        public void Bash_IsMatchedByItsQuotedCommand()
        {
            // measured: echo start; sleep 40; echo "finished normally"
            var a = Bash(10, "eval 'echo start; sleep 40; echo \\\"finished normally\\\"'");
            var b = Bash(11, "eval 'npm test'");
            var pid = ShellTaskProcess.Find(new[] { Conhost, McpServer, a, b }, "b24io55nt", "Bash",
                "echo start; sleep 40; echo \"finished normally\"");
            Assert.Equal(10, pid);
        }

        [Fact]
        public void Bash_SingleQuotesInTheCommand_AreMatched()
        {
            var cmd = "powershell -NoProfile -Command '$p = 1; \"x\"'";
            var escaped = "eval 'powershell -NoProfile -Command '\\\"'\\\"'$p = 1; \\\"x\\\"'\\\"'\\\"''";
            Assert.Equal(escaped, ShellTaskProcess.EscapeForCommandLine(ShellTaskProcess.BashEval(cmd)));

            var pid = ShellTaskProcess.Find(new[] { Bash(20, escaped), Bash(21, "eval 'ls'") }, "t1", "Bash", cmd);
            Assert.Equal(20, pid);
        }

        [Fact]
        public void Bash_NoMatch_FallsBackToTheOnlyRunningShell()
        {
            var pid = ShellTaskProcess.Find(new[] { Conhost, McpServer, Bash(30, "eval 'rewritten'") }, "t1", "Bash", "original");
            Assert.Equal(30, pid);
        }

        [Fact]
        public void Bash_NoMatch_SeveralShells_IsNotGuessed()
        {
            var pid = ShellTaskProcess.Find(new[] { Bash(30, "eval 'a'"), Bash(31, "eval 'b'") }, "t1", "Bash", "c");
            Assert.Null(pid);
        }

        [Fact]
        public void Bash_SameCommandTwice_IsNotGuessed()
        {
            var pid = ShellTaskProcess.Find(new[] { Bash(30, "eval 'sleep 5'"), Bash(31, "eval 'sleep 5'") }, "t1", "Bash", "sleep 5");
            Assert.Null(pid);
        }

        [Fact]
        public void NoShellChildren_ReturnsNull()
        {
            Assert.Null(ShellTaskProcess.Find(new[] { Conhost, McpServer }, "t1", "Bash", "ls"));
        }
    }
}
