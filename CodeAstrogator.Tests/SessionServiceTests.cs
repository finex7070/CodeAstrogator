using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>Feeds recorded NDJSON fixture lines through the session service.</summary>
    public class SessionServiceTests
    {
        private sealed class FixtureProcessHost : IClaudeProcessHost
        {
            private readonly string[] _lines;
            public ClaudeTurnRequest? LastRequest;

            public FixtureProcessHost(string fixtureName)
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", fixtureName);
                _lines = File.ReadAllLines(path);
            }

            public Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
            {
                LastRequest = request;
                foreach (var line in _lines)
                    onStdoutLine(line);
                return Task.FromResult(new ClaudeTurnExit { ExitCode = 0 });
            }
        }

        private static string FakeExecutable()
        {
            // Locator only checks existence; any real file works for tests.
            var path = Path.Combine(Path.GetTempPath(), "claude-fake.cmd");
            File.WriteAllText(path, "@echo off");
            return path;
        }

        [Fact]
        public async Task RunTurn_CapturesSessionId_AndAccumulatesTokens()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;

            await session.RunTurnAsync("hi", FakeExecutable(), workingDirectory: null);

            Assert.Equal("sess-123", session.SessionId);
            Assert.Equal(12 + 100 + 900 + 45, session.TotalTokens);
            Assert.Contains(events, e => e is TurnResultEvent);
            Assert.False(session.IsBusy);
        }

        [Fact]
        public async Task SecondTurn_PassesSessionIdAsResume()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            var exe = FakeExecutable();

            await session.RunTurnAsync("first", exe, null);
            await session.RunTurnAsync("second", exe, null);

            Assert.Equal("sess-123", host.LastRequest!.SessionId);
        }

        [Fact]
        public async Task EffortSetting_IsForwardedToTheCli()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            session.Settings.Effort = "max";

            await session.RunTurnAsync("hi", FakeExecutable(), null);

            Assert.Equal("max", host.LastRequest!.Effort);
        }

        [Fact]
        public async Task LaunchedPermissionMode_PinsTheModeAtTurnStart()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            session.Settings.PermissionMode = "plan";

            await session.RunTurnAsync("plan it", FakeExecutable(), null);
            Assert.Equal("plan", session.LaunchedPermissionMode);

            // mid-turn UI switch (e.g. plan approval) changes the live setting, but the running
            // turn keeps the mode it launched with until the NEXT turn starts.
            session.Settings.PermissionMode = "acceptEdits";
            Assert.Equal("plan", session.LaunchedPermissionMode);

            await session.RunTurnAsync("do it", FakeExecutable(), null);
            Assert.Equal("acceptEdits", session.LaunchedPermissionMode);
        }

        [Fact]
        public async Task ResetSession_ClearsResumeAndTokens()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            await session.RunTurnAsync("first", FakeExecutable(), null);

            session.ResetSession();

            Assert.Null(session.SessionId);
            Assert.Equal(0, session.TotalTokens);
        }

        [Fact]
        public async Task LocalOnlyTurn_DoesNotAdoptSessionId()
        {
            // /help & friends: result has a session_id but num_turns 0 — resuming
            // that id fails with "No conversation found" (the bug from image_1).
            var host = new FixtureProcessHost("turn-local-slash.ndjson");
            var session = new ClaudeSessionService(host);

            await session.RunTurnAsync("/help", FakeExecutable(), null);
            Assert.Null(session.SessionId);

            await session.RunTurnAsync("hallo", FakeExecutable(), null);
            Assert.Null(host.LastRequest!.SessionId); // no --resume after a local-only turn
        }

        [Fact]
        public async Task StaleResumeId_RetriesOnceWithoutResume()
        {
            var host = new StaleResumeProcessHost();
            var session = new ClaudeSessionService(host);
            session.AttachSession("dead-session");
            string? error = null;
            session.TurnCompleted += (_, e) => error = e;

            await session.RunTurnAsync("hi", FakeExecutable(), null);

            Assert.Equal(2, host.Calls);
            Assert.Null(host.SecondRequestSessionId); // retry ran fresh
            Assert.Null(error);
        }

        private sealed class StaleResumeProcessHost : IClaudeProcessHost
        {
            public int Calls;
            public string? SecondRequestSessionId;

            public Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
            {
                Calls++;
                if (Calls == 1)
                {
                    return Task.FromResult(new ClaudeTurnExit
                    {
                        ExitCode = 1,
                        StdErrTail = "No conversation found with session ID: dead-session",
                    });
                }
                SecondRequestSessionId = request.SessionId;
                onStdoutLine("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"session_id\":\"fresh-1\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
                return Task.FromResult(new ClaudeTurnExit { ExitCode = 0 });
            }
        }

        [Fact]
        public async Task TurnCompleted_ReportsCliError()
        {
            var host = new ErrorProcessHost();
            var session = new ClaudeSessionService(host);
            string? reported = null;
            session.TurnCompleted += (_, error) => reported = error;

            await session.RunTurnAsync("hi", FakeExecutable(), null);

            Assert.NotNull(reported);
            Assert.Contains("exit", reported, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("boom", reported);
        }

        private sealed class ErrorProcessHost : IClaudeProcessHost
        {
            public Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
                => Task.FromResult(new ClaudeTurnExit { ExitCode = 1, StdErrTail = "boom" });
        }

        [Fact]
        public async Task MissingExecutable_Throws()
        {
            var session = new ClaudeSessionService(new FixtureProcessHost("turn-success.ndjson"));
            // Force an override path that does not exist so PATH resolution is skipped.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("hi", Path.Combine(Path.GetTempPath(), "no-such-claude.exe"), null));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public async Task UltracodeToggle_InjectsKeywordIntoPrompt()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);
            session.Settings.Ultracode = true;

            await session.RunTurnAsync("refactor the parser", FakeExecutable(), null);

            Assert.Equal("refactor the parser\n\nultracode", host.LastRequest!.Prompt);
        }

        [Fact]
        public async Task ImagePaths_AreForwardedToTheRequest()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);

            await session.RunTurnAsync("look", FakeExecutable(), null, new[] { @"C:\p\a.png", @"C:\p\b.jpg" });

            Assert.Equal(new[] { @"C:\p\a.png", @"C:\p\b.jpg" }, host.LastRequest!.ImagePaths);
        }

        [Fact]
        public async Task UltracodeOff_LeavesPromptUntouched()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);

            await session.RunTurnAsync("refactor the parser", FakeExecutable(), null);

            Assert.Equal("refactor the parser", host.LastRequest!.Prompt);
        }

        [Fact]
        public void Ultracode_DoesNotDuplicateExistingKeyword()
        {
            var session = new ClaudeSessionService(new ErrorProcessHost());
            session.Settings.Ultracode = true;

            Assert.Equal("do it with Ultracode please",
                session.DecoratePrompt("do it with Ultracode please"));
        }

        // ── prompt suggestions: the turn is handed back at its result, the process lingers ──────

        /// <summary>A fake executable whose (pinned) capabilities include --prompt-suggestions; its own
        /// path so the static capability cache does not leak into the other tests.</summary>
        private static string SuggestingExecutable()
        {
            var path = Path.Combine(Path.GetTempPath(), "claude-fake-suggest.cmd");
            File.WriteAllText(path, "@echo off");
            ClaudeCliCapabilities.SetForTesting(path, new CliCapabilities
            {
                Known = true,
                SupportsManualPermissionMode = true,
                SupportsPromptSuggestions = true,
            });
            return path;
        }

        /// <summary>Feeds a fixture and records the session state seen at every line.</summary>
        private sealed class ObservingProcessHost : IClaudeProcessHost
        {
            private readonly string[] _lines;
            public ClaudeTurnRequest? LastRequest;
            public readonly List<(string type, bool busy)> Seen = new List<(string, bool)>();
            public ClaudeSessionService? Session;

            public ObservingProcessHost(string fixtureName)
            {
                _lines = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", fixtureName));
            }

            public Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
            {
                LastRequest = request;
                foreach (var line in _lines)
                {
                    onStdoutLine(line);
                    var type = Newtonsoft.Json.Linq.JObject.Parse(line).Value<string>("type") ?? "";
                    Seen.Add((type, Session!.IsBusy));
                }
                return Task.FromResult(new ClaudeTurnExit { ExitCode = 0 });
            }
        }

        [Fact]
        public async Task PromptSuggestions_FlagPassed_TurnEndsAtResult_SuggestionStillForwarded()
        {
            var host = new ObservingProcessHost("turn-prompt-suggestion.ndjson");
            var session = new ClaudeSessionService(host);
            host.Session = session;
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;
            var completions = 0;
            session.TurnCompleted += (_, error) => { completions++; Assert.Null(error); };

            await session.RunTurnAsync("make calc.py", SuggestingExecutable(), null);

            Assert.Contains("--prompt-suggestions", host.LastRequest!.ExtraArgs);
            // busy right up to the result line, idle from it on — the suggestion comes after
            Assert.True(host.Seen.First(s => s.type == "stream_event").busy);
            Assert.False(host.Seen.Single(s => s.type == "result").busy);
            Assert.Equal(1, completions); // not a second time at process exit
            Assert.Equal("python calc.py", events.OfType<PromptSuggestionEvent>().Single().Suggestion);
            Assert.Equal("sess-sug", session.SessionId);
        }

        [Fact]
        public async Task PromptSuggestionsOff_FlagOmitted_TurnEndsAtExit()
        {
            var host = new ObservingProcessHost("turn-prompt-suggestion.ndjson");
            var session = new ClaudeSessionService(host);
            host.Session = session;
            session.Settings.PromptSuggestions = false;

            await session.RunTurnAsync("make calc.py", SuggestingExecutable(), null);

            Assert.DoesNotContain("--prompt-suggestions", host.LastRequest!.ExtraArgs);
            Assert.True(host.Seen.Single(s => s.type == "result").busy);
            Assert.False(session.IsBusy);
        }

        [Fact]
        public async Task CliWithoutTheFlag_NeverGetsIt()
        {
            var host = new FixtureProcessHost("turn-success.ndjson");
            var session = new ClaudeSessionService(host);

            await session.RunTurnAsync("hi", FakeExecutable(), null); // probe fails → unknown capabilities

            Assert.DoesNotContain("--prompt-suggestions", host.LastRequest!.ExtraArgs);
        }

        [Fact]
        public async Task BackgroundTaskRunning_TurnStaysOpenForTheFollowUp()
        {
            // CLI 2.1.287: a running background task keeps the process alive past the first result and
            // its completion triggers a follow-up turn in the same process — that must still be shown,
            // so the turn is only handed back at the follow-up's result (no task running any more).
            var host = new ObservingProcessHost("turn-background-task.ndjson");
            var session = new ClaudeSessionService(host);
            host.Session = session;
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;
            var completions = 0;
            session.TurnCompleted += (_, __) => completions++;

            await session.RunTurnAsync("start it", SuggestingExecutable(), null);

            var results = host.Seen.Where(s => s.type == "result").ToList();
            Assert.True(results[0].busy);
            Assert.False(results[1].busy);
            Assert.Equal(2, events.OfType<TurnResultEvent>().Count());
            Assert.Single(events.OfType<PromptSuggestionEvent>());
            Assert.Equal(1, completions);
        }

        /// <summary>Delivers a result, then keeps "generating the suggestion" until cancelled.</summary>
        private sealed class LingeringProcessHost : IClaudeProcessHost
        {
            public int Calls;
            public bool FirstWasCancelled;

            public async Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
            {
                var call = Interlocked.Increment(ref Calls);
                onStdoutLine("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"session_id\":\"s-" + call + "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
                if (call > 1)
                    return new ClaudeTurnExit { ExitCode = 0 };
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    FirstWasCancelled = true;
                    onStdoutLine("{\"type\":\"prompt_suggestion\",\"suggestion\":\"too late\",\"session_id\":\"s-1\"}");
                    onStdoutLine("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"session_id\":\"s-stale\",\"usage\":{}}");
                }
                return new ClaudeTurnExit { ExitCode = -1, WasCancelled = true };
            }
        }

        [Fact]
        public async Task NextTurn_EndsTheLingeringProcessFirst()
        {
            var host = new LingeringProcessHost();
            var session = new ClaudeSessionService(host);
            var exe = SuggestingExecutable();
            var completed = new TaskCompletionSource<bool>();
            var completions = 0;
            var results = new List<string>();
            session.TurnCompleted += (_, __) => { completions++; completed.TrySetResult(true); };
            session.EventReceived += e => { if (e is TurnResultEvent r) results.Add(r.SessionId); };

            var first = session.RunTurnAsync("one", exe, null);
            await completed.Task; // handed back at the result, process still running
            Assert.False(first.IsCompleted);
            Assert.False(session.IsBusy);

            await session.RunTurnAsync("two", exe, null);
            await first;

            Assert.True(host.FirstWasCancelled);
            Assert.Equal(2, completions);
            // nothing but the suggestion leaves a handed-back process — its stale result is dropped
            Assert.Equal(new[] { "s-1", "s-2" }, results);
            Assert.Equal("s-2", session.SessionId);
        }

        [Theory]
        [InlineData("ask", false, null)]
        [InlineData("acceptEdits", false, "acceptEdits")]
        [InlineData("plan", false, "plan")]
        [InlineData("bypass", false, "bypassPermissions")]
        [InlineData("ask", true, "plan")] // plan-mode toggle wins
        public void PermissionModeMapping(string uiMode, bool planMode, string? expected)
        {
            var session = new ClaudeSessionService(new ErrorProcessHost());
            session.Settings.PermissionMode = uiMode;
            session.Settings.PlanMode = planMode;
            Assert.Equal(expected, session.MapPermissionMode());
        }
    }
}
