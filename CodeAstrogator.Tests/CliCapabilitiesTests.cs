using System;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>
    /// Covers the <c>--permission-mode</c> value selection. The regression behind these tests:
    /// omitting the flag used to mean "ask about everything", but on CLI 2.1.2xx+ it resolves to
    /// <c>auto</c>, where the model decides whether to consult <c>--permission-prompt-tool</c> —
    /// so "Review edits at end of turn" captured no baseline and silently produced no review.
    /// </summary>
    public class CliCapabilitiesTests
    {
        /// <summary>Never actually launched here — MapPermissionMode is pure.</summary>
        private sealed class StubProcessHost : IClaudeProcessHost
        {
            public Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
                => Task.FromResult(new ClaudeTurnExit { ExitCode = 1, StdErrTail = "not used" });
        }

        // Verbatim shape of `claude --help` on 2.1.263 (wrapped exactly as the CLI prints it).
        private const string Help2_1_263 = @"Usage: claude [options] [command] [prompt]

Options:
  -d, --debug [filter]                  Enable debug mode
  --permission-mode <mode>              Permission mode to use for the session
                                        (choices: ""acceptEdits"", ""auto"",
                                        ""bypassPermissions"", ""manual"",
                                        ""dontAsk"", ""plan"")
  -c, --continue                        Continue the most recent conversation
";

        // Legacy shape (<= 2.1.178): no `manual`, but a `default` value.
        private const string HelpLegacy = @"Options:
  --permission-mode <mode>              Permission mode to use for the session
                                        (choices: ""default"", ""acceptEdits"",
                                        ""plan"", ""bypassPermissions"")
";

        [Fact]
        public void ParseHelp_CurrentCli_OffersManual()
        {
            var caps = ClaudeCliCapabilities.ParseHelp(Help2_1_263);

            Assert.True(caps.Known);
            Assert.True(caps.SupportsManualPermissionMode);
            Assert.False(caps.SupportsDefaultPermissionMode);
            Assert.Equal("manual", caps.AskPermissionModeArg);
        }

        [Fact]
        public void ParseHelp_LegacyCli_HasNoManual_SoTheFlagIsOmitted()
        {
            var caps = ClaudeCliCapabilities.ParseHelp(HelpLegacy);

            Assert.True(caps.Known);
            Assert.False(caps.SupportsManualPermissionMode);
            Assert.True(caps.SupportsDefaultPermissionMode);
            Assert.Null(caps.AskPermissionModeArg); // omission still means "ask" there
        }

        [Theory]
        [InlineData("")]
        [InlineData("Usage: claude [options]\n  -d, --debug  Enable debug mode\n")]
        [InlineData("  --permission-mode <mode>  Permission mode to use for the session\n")]
        public void ParseHelp_Unreadable_ReportsUnknown(string help)
        {
            var caps = ClaudeCliCapabilities.ParseHelp(help);

            Assert.False(caps.Known);
            Assert.Null(caps.AskPermissionModeArg); // → callers keep the legacy behaviour
        }

        [Fact]
        public void ReviewEditsAtTurnEnd_PassesTheAskModeExplicitly()
        {
            var session = new ClaudeSessionService(new StubProcessHost());
            session.Settings.PermissionMode = "acceptEdits";
            session.Settings.ReviewEditsAtTurnEnd = true;
            session.AskPermissionModeArg = "manual"; // what the probe found on this CLI

            // NOT "acceptEdits" (the CLI would apply edits itself, no baseline) and NOT null
            // (that is `auto`, where the model may skip the hook entirely).
            Assert.Equal("manual", session.MapPermissionMode());
        }

        [Fact]
        public void ReviewEditsAtTurnEnd_OnLegacyCli_FallsBackToOmittingTheFlag()
        {
            var session = new ClaudeSessionService(new StubProcessHost());
            session.Settings.PermissionMode = "acceptEdits";
            session.Settings.ReviewEditsAtTurnEnd = true;
            session.AskPermissionModeArg = null; // legacy CLI: no `manual` value exists

            Assert.Null(session.MapPermissionMode());
        }

        [Fact]
        public void AskMode_AlsoPassesTheAskModeExplicitly()
        {
            var session = new ClaudeSessionService(new StubProcessHost());
            session.Settings.PermissionMode = "ask";
            session.AskPermissionModeArg = "manual";

            // Same bug hits plain "Ask before edits": on `auto` the CLI may apply an edit
            // without ever prompting.
            Assert.Equal("manual", session.MapPermissionMode());
        }

        [Fact]
        public void PlanMode_AndAcceptEdits_AreUnaffectedByTheProbe()
        {
            var plan = new ClaudeSessionService(new StubProcessHost());
            plan.Settings.PermissionMode = "ask";
            plan.Settings.PlanMode = true;
            plan.AskPermissionModeArg = "manual";
            Assert.Equal("plan", plan.MapPermissionMode());

            var accept = new ClaudeSessionService(new StubProcessHost());
            accept.Settings.PermissionMode = "acceptEdits";
            accept.Settings.ReviewEditsAtTurnEnd = false;
            accept.AskPermissionModeArg = "manual";
            Assert.Equal("acceptEdits", accept.MapPermissionMode());
        }
    }
}
