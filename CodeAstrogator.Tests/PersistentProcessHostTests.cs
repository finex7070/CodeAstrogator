using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class PersistentProcessHostTests
    {
        [Fact]
        public void BuildArguments_Minimal_IsBidirectionalStreamJson_NoResume()
        {
            var args = ClaudePersistentProcessHost.BuildArguments(
                new ClaudeTurnRequest { Prompt = "hello", ExecutablePath = "claude.exe" },
                resumeId: null);

            Assert.Equal(
                "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages",
                args);
            // the prompt is sent as a stream-json user message, never on argv
            Assert.DoesNotContain("hello", args);
        }

        [Fact]
        public void BuildArguments_WithResumeIsExplicit_NotTakenFromRequestSessionId()
        {
            // The persistent host tracks the live session itself; the resume target is passed
            // explicitly so reusing the running process does NOT re-add --resume every turn.
            var args = ClaudePersistentProcessHost.BuildArguments(
                new ClaudeTurnRequest { Prompt = "x", SessionId = "sess-live" },
                resumeId: "sess-target");

            Assert.Contains("--resume sess-target", args);
            Assert.DoesNotContain("sess-live", args);
        }

        [Fact]
        public void BuildArguments_NullResume_OmitsResumeFlag()
        {
            var args = ClaudePersistentProcessHost.BuildArguments(
                new ClaudeTurnRequest { Prompt = "x", SessionId = "ignored-when-resume-null" },
                resumeId: null);

            Assert.DoesNotContain("--resume", args);
        }

        [Fact]
        public void BuildArguments_ModelEffortPermission_AreForwarded()
        {
            var args = ClaudePersistentProcessHost.BuildArguments(
                new ClaudeTurnRequest { Prompt = "x", Model = "opus", Effort = "max", PermissionMode = "acceptEdits" },
                resumeId: null);

            Assert.Contains("--model opus", args);
            Assert.Contains("--effort max", args);
            Assert.Contains("--permission-mode acceptEdits", args);
        }

        [Fact]
        public void BuildArguments_DefaultPermissionMode_OmitsFlag()
        {
            var args = ClaudePersistentProcessHost.BuildArguments(
                new ClaudeTurnRequest { Prompt = "x", PermissionMode = "default" },
                resumeId: null);

            Assert.DoesNotContain("--permission-mode", args);
        }
    }
}
