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
            Assert.False(host.FirstWasCancelled);
            Assert.False(session.IsBusy);

            await session.RunTurnAsync("two", exe, null);
            await first;

            Assert.True(host.FirstWasCancelled);
            Assert.Equal(2, completions);
            // nothing but the suggestion leaves a handed-back process — its stale result is dropped
            Assert.Equal(new[] { "s-1", "s-2" }, results);
            Assert.Equal("s-2", session.SessionId);
        }

        // ── background tasks: the turn is handed back, process + stdin stay open ─────────────────

        private const string InitLine = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-bg\",\"cwd\":\"C:\\\\w\"}";
        private const string TasksRunningLine = "{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[{\"task_id\":\"t1\",\"task_type\":\"local_bash\",\"description\":\"Capture devkit log\"}]}";
        private const string TasksDoneLine = "{\"type\":\"system\",\"subtype\":\"background_tasks_changed\",\"tasks\":[]}";
        private const string TaskCompletedLine = "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"t1\",\"status\":\"completed\"}";
        private const string ResultLine = "{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"session_id\":\"s-bg\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

        /// <summary>A process with a real-ish stdin: the test scripts what the "CLI" prints, both at start
        /// and in answer to each message written into it; it exits when stdin closes or on cancel.</summary>
        private sealed class LiveProcessHost : IClaudeProcessHost
        {
            public int Starts;
            public ClaudeTurnRequest? LastRequest;
            public readonly List<string> Sent = new List<string>();
            public readonly List<string> ControlRequests = new List<string>();
            public Func<string, string[]> OnControl = _ => Array.Empty<string>();
            public bool InputClosed;
            public bool Cancelled;
            public string[] OnStart = Array.Empty<string>();
            public Func<string, string[]> OnMessage = _ => Array.Empty<string>();
            private Action<string>? _emit;
            private TaskCompletionSource<bool>? _exit;

            /// <summary>Prints lines as the running process (e.g. a finished task + follow-up turn).</summary>
            public void Emit(params string[] lines)
            {
                foreach (var l in lines)
                    _emit!(l);
            }

            public async Task<ClaudeTurnExit> RunTurnAsync(ClaudeTurnRequest request, Action<string> onStdoutLine, CancellationToken ct)
            {
                Starts++;
                LastRequest = request;
                _emit = onStdoutLine;
                _exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                InputClosed = false;
                request.InputOpened?.Invoke(new FakeInput(this));
                Emit(OnStart);
                using (ct.Register(() => { Cancelled = true; _exit.TrySetResult(true); }))
                    await _exit.Task;
                return new ClaudeTurnExit { ExitCode = Cancelled ? -1 : 0, WasCancelled = Cancelled };
            }

            private sealed class FakeInput : IClaudeTurnInput
            {
                private readonly LiveProcessHost _host;
                public FakeInput(LiveProcessHost host) => _host = host;

                public bool TrySend(string line)
                {
                    if (_host.InputClosed)
                        return false;
                    if (line.Contains("\"control_request\""))
                    {
                        // the context-usage request at each result — the "CLI" answers it via OnControl
                        _host.ControlRequests.Add(line);
                        _host.Emit(_host.OnControl(line));
                        return true;
                    }
                    _host.Sent.Add(line);
                    _host.Emit(_host.OnMessage(line));
                    return true;
                }

                public void Close()
                {
                    _host.InputClosed = true;
                    _host._exit?.TrySetResult(true); // EOF → the CLI finishes and exits
                }
            }
        }

        [Fact]
        public async Task BackgroundTask_TurnHandedBackAtResult_NextPromptGoesIntoTheSameProcess()
        {
            var host = new LiveProcessHost
            {
                OnStart = new[] { InitLine, TasksRunningLine, ResultLine },
                OnMessage = _ => new[] { InitLine, ResultLine }, // the task still runs
            };
            var session = new ClaudeSessionService(host);
            var exe = FakeExecutable();
            var completions = 0;
            var events = new List<ClaudeEvent>();
            session.TurnCompleted += (_, error) => { completions++; Assert.Null(error); };
            session.EventReceived += events.Add;

            await session.RunTurnAsync("start the capture", exe, null);

            // handed back at the result although the process keeps running for its task
            Assert.False(session.IsBusy);
            Assert.Equal(1, completions);
            Assert.False(host.InputClosed);
            Assert.Equal("Capture devkit log", events.OfType<BackgroundTasksChangedEvent>().Single().Tasks.Single().Description);

            await session.RunTurnAsync("done testing", exe, null);

            Assert.Equal(1, host.Starts); // no second process
            Assert.Contains("done testing", host.Sent.Single());
            Assert.Equal(2, completions);
            Assert.False(session.IsBusy);
            Assert.False(host.InputClosed);
        }

        [Fact]
        public async Task BackgroundTaskFinishes_FollowUpTurnIsShown_ThenTheProcessEnds()
        {
            var host = new LiveProcessHost { OnStart = new[] { InitLine, TasksRunningLine, ResultLine } };
            var session = new ClaudeSessionService(host);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completions = 0;
            var events = new List<ClaudeEvent>();
            session.TurnCompleted += (_, __) => { if (++completions == 2) completed.TrySetResult(true); };
            session.EventReceived += events.Add;

            await session.RunTurnAsync("start the capture", FakeExecutable(), null);
            Assert.False(session.IsBusy);

            // the task finishes: the CLI reports it and answers with a turn of its own
            host.Emit(TaskCompletedLine, TasksDoneLine, InitLine);
            Assert.True(session.IsBusy);
            Assert.Single(events.OfType<FollowUpTurnStartedEvent>());

            host.Emit(ResultLine); // no task left → stdin closes → the process exits
            await completed.Task;

            Assert.True(host.InputClosed);
            Assert.False(session.IsBusy);
            Assert.Null(session.ActiveProcessId);
            Assert.DoesNotContain(events.OfType<BackgroundTasksChangedEvent>(), e => e.ProcessEnded);
        }

        /// <summary>The CLI's answer to a get_context_usage request (trimmed 2.1.294 shape).</summary>
        private static string[] AnswerContextRequest(string requestLine)
        {
            var id = Newtonsoft.Json.Linq.JObject.Parse(requestLine).Value<string>("request_id");
            return new[]
            {
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + id + "\",\"response\":"
                + "{\"totalTokens\":27286,\"maxTokens\":1000000,\"percentage\":3,\"categories\":[{\"name\":\"Messages\",\"tokens\":1361,\"kind\":\"used\"}]}}}",
            };
        }

        [Fact]
        public async Task ContextUsage_IsRequestedAtTheResult_BeforeStdinCloses_AndForwarded()
        {
            var host = new LiveProcessHost
            {
                OnStart = new[] { InitLine, ResultLine },
                OnControl = AnswerContextRequest,
            };
            var session = new ClaudeSessionService(host);
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;

            await session.RunTurnAsync("hi", FakeExecutable(), null);

            var request = Newtonsoft.Json.Linq.JObject.Parse(host.ControlRequests.Single());
            Assert.Equal("get_context_usage", request["request"]!.Value<string>("subtype"));
            Assert.StartsWith(StreamJsonInput.ContextUsageRequestPrefix, request.Value<string>("request_id"));
            Assert.True(host.InputClosed);
            Assert.Equal(27286, events.OfType<ContextUsageEvent>().Single().TotalTokens);
        }

        [Fact]
        public async Task ContextUsage_WithBackgroundTasks_IsRequestedPerTurn_AndStdinStaysOpen()
        {
            var host = new LiveProcessHost
            {
                OnStart = new[] { InitLine, TasksRunningLine, ResultLine },
                OnMessage = _ => new[] { InitLine, ResultLine },
                OnControl = AnswerContextRequest,
            };
            var session = new ClaudeSessionService(host);
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;

            await session.RunTurnAsync("start the capture", FakeExecutable(), null);
            await session.RunTurnAsync("done testing", FakeExecutable(), null);

            Assert.Equal(2, host.ControlRequests.Count);
            Assert.NotEqual(host.ControlRequests[0], host.ControlRequests[1]); // fresh request id each time
            Assert.Equal(2, events.OfType<ContextUsageEvent>().Count());
            Assert.False(host.InputClosed);
        }

        [Fact]
        public async Task BackgroundTask_ChangedSettings_StartNewProcess_TasksEndWithTheOldOne()
        {
            var host = new LiveProcessHost { OnStart = new[] { InitLine, TasksRunningLine, ResultLine } };
            var session = new ClaudeSessionService(host);
            var exe = FakeExecutable();
            var events = new List<ClaudeEvent>();
            session.EventReceived += events.Add;

            await session.RunTurnAsync("start the capture", exe, null);
            session.Settings.Model = "claude-haiku-5-5"; // a process keeps its --model
            host.OnStart = new[] { InitLine, ResultLine };
            var second = session.RunTurnAsync("now with haiku", exe, null);
            await Task.WhenAny(second, Task.Delay(5000));

            Assert.Equal(2, host.Starts);
            Assert.True(host.Cancelled); // the old process (and its task) was ended first
            Assert.Contains(events.OfType<BackgroundTasksChangedEvent>(), e => e.ProcessEnded);
        }

        /// <summary>A fake executable whose capabilities include --replay-user-messages.</summary>
        private static string ReplayingExecutable()
        {
            var path = Path.Combine(Path.GetTempPath(), "claude-fake-replay.cmd");
            File.WriteAllText(path, "@echo off");
            ClaudeCliCapabilities.SetForTesting(path, new CliCapabilities
            {
                Known = true,
                SupportsManualPermissionMode = true,
                SupportsReplayUserMessages = true,
            });
            return path;
        }

        [Fact]
        public async Task PromptInTheRaceWindow_IsQueuedBehindTheFollowUp_NothingLost()
        {
            // Measured 2.1.287: a message written between a task's task_notification and the follow-up's
            // init is queued — follow-up init → result first, then init → replay of our message → answer →
            // result. The follow-up's result must not end our turn, and no new process may be started.
            const string replayLine = "{\"type\":\"user\",\"isReplay\":true,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"race\"}]}}";
            var host = new LiveProcessHost
            {
                OnStart = new[] { InitLine, TasksRunningLine, ResultLine },
                OnMessage = _ => new[] { InitLine, ResultLine, InitLine, replayLine, ResultLine },
            };
            var session = new ClaudeSessionService(host);
            var exe = ReplayingExecutable();
            var results = 0;
            var completions = 0;
            var busyAtResult = new List<bool>();
            session.TurnCompleted += (_, __) => completions++;
            session.EventReceived += e =>
            {
                if (e is TurnResultEvent) { results++; busyAtResult.Add(session.IsBusy); }
                Assert.IsNotType<UserMessageReplayedEvent>(e); // internal marker, never forwarded
            };

            await session.RunTurnAsync("start the capture", exe, null);
            Assert.Contains("--replay-user-messages", host.LastRequest!.ExtraArgs);

            host.Emit(TaskCompletedLine, TasksDoneLine); // the follow-up is on its way, its init not yet out
            await session.RunTurnAsync("race", exe, null);

            Assert.Equal(1, host.Starts);           // written into the same process, not replaced
            Assert.False(host.Cancelled);
            Assert.Equal(3, results);               // first turn, the follow-up, our prompt
            Assert.True(busyAtResult[1]);           // the follow-up's result did not end our turn
            Assert.Equal(2, completions);
            Assert.True(host.InputClosed);          // no task left after our turn → EOF
            Assert.False(session.IsBusy);
        }

        [Fact]
        public async Task StopBackgroundTasks_EndsTheIdleProcess()
        {
            var host = new LiveProcessHost { OnStart = new[] { InitLine, TasksRunningLine, ResultLine } };
            var session = new ClaudeSessionService(host);
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.EventReceived += e => { if (e is BackgroundTasksChangedEvent b && b.ProcessEnded) ended.TrySetResult(true); };

            await session.RunTurnAsync("start the capture", FakeExecutable(), null);
            session.StopTurn(); // no turn running — must not touch the background process
            Assert.False(host.Cancelled);

            session.StopBackgroundTasks();
            await Task.WhenAny(ended.Task, Task.Delay(5000));

            Assert.True(host.Cancelled);
            Assert.True(ended.Task.IsCompleted);
            Assert.False(session.IsBusy);
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
