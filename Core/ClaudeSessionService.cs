using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>Mutable per-session settings driven by the UI (Model·Mode popover).</summary>
    public sealed class SessionSettings
    {
        public string? Model { get; set; }
        public string Effort { get; set; } = "medium";
        public bool PlanMode { get; set; }

        /// <summary>When on, the "ultracode" keyword is injected into each prompt
        /// (opts the CLI into multi-agent workflow orchestration for that turn).</summary>
        public bool Ultracode { get; set; }

        /// <summary>UI permission mode: ask | acceptEdits | plan | bypass.</summary>
        public string PermissionMode { get; set; } = "ask";

        /// <summary>When on (and <see cref="PermissionMode"/> is <c>acceptEdits</c>), edits are routed
        /// through the permission hook and auto-approved there so a pre-edit baseline can be captured;
        /// the changed files are reviewed at the end of the turn. See <see cref="AstrogatorOptions"/>.</summary>
        public bool ReviewEditsAtTurnEnd { get; set; }

        /// <summary>How long (ms) the CLI may wait on a permission/AskUserQuestion prompt before it
        /// times out — carried in the MCP config's <c>timeout</c> field (the CLI prefers it over the
        /// MCP_TOOL_TIMEOUT env var). Driven by the settings window; defaults to
        /// <see cref="McpPermissionBridge.DefaultToolTimeoutMs"/>.</summary>
        public int McpToolTimeoutMs { get; set; } = McpPermissionBridge.DefaultToolTimeoutMs;

        /// <summary>Ask the CLI for a predicted next prompt after each turn (<c>--prompt-suggestions</c>,
        /// only passed when the installed CLI knows the flag). Costs one extra, mostly cached API call
        /// per turn.</summary>
        public bool PromptSuggestions { get; set; } = true;
    }

    /// <summary>
    /// Turn orchestration (Teil A §A6/§A7): starts one CLI process per turn,
    /// feeds NDJSON lines through the parser and re-raises domain events.
    /// UI-free; the bridge layer maps events onto the WebView message contract.
    /// <para>
    /// Process lifetime (see <see cref="CliProcess"/>): the prompt goes in as one stream-json message
    /// and stdin is closed at the turn's <c>result</c> — the CLI then finishes up (prompt suggestion) and
    /// exits. Exception: while <b>background tasks</b> run, the turn is handed back at its result but
    /// process and stdin stay open, so the tasks keep running, the next prompt is written into the same
    /// process, and the CLI's own follow-up turn (when a task finishes) is still shown.
    /// </para>
    /// </summary>
    public sealed class ClaudeSessionService
    {
        private readonly IClaudeProcessHost _processHost;
        private int _busy; // 0 = idle, 1 = turn running

        // The CLI process of the current or last turn, while it is still alive. Guarded by _procLock.
        private readonly object _procLock = new object();
        private CliProcess? _proc;

        // The result-event error text of the current turn (e.g. "API Error: …",
        // "Credit balance is too low"). The CLI reports many failures here on stdout
        // while stderr stays empty, so we fold it into the turn's error message.
        private string? _lastResultError;

        private int _contextRequestSeq;

        private enum ProcState
        {
            /// <summary>A turn runs in it: every event is forwarded; its result may end the turn.</summary>
            Turn,
            /// <summary>Turn handed back, background tasks keep it alive, stdin open: events are still
            /// forwarded (task updates, a follow-up turn), the next prompt may be written into it.</summary>
            Idle,
            /// <summary>Turn handed back, stdin closed: only the prompt suggestion is forwarded.</summary>
            Draining,
            /// <summary>Being killed (next turn / session change): nothing is forwarded any more.</summary>
            Ending,
        }

        /// <summary>One running <c>claude -p</c> process.</summary>
        private sealed class CliProcess
        {
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public readonly NdjsonParser Parser = new NdjsonParser();
            public readonly TaskCompletionSource<bool> Exited =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public ProcState State = ProcState.Turn;
            public IClaudeTurnInput? Input;
            public int Pid;
            public string LaunchKey = "";
            public bool Suggestions;
            public int BackgroundTasks;
            /// <summary>False once a follow-up turn is on its way (a task finished) — a prompt must not
            /// be queued behind it; such a process is replaced instead.</summary>
            public bool Reusable = true;
            /// <summary>The running turn is the CLI's own follow-up — nobody awaits it, so the process
            /// exit has to complete it.</summary>
            public bool FollowUp;
            /// <summary>Launched with <c>--replay-user-messages</c>: the CLI acknowledges each stdin message
            /// when it takes it up, so a prompt may even be written while a follow-up is on its way.</summary>
            public bool Replay;
            /// <summary>Prompts written into this process that the CLI has not taken up yet. A result
            /// arriving meanwhile belongs to the follow-up turn queued before them, not to our prompt.</summary>
            public int AwaitingReplay;
            /// <summary>Completes when the running turn ends: null = handed back at its result
            /// (TurnCompleted already raised), otherwise the exit of the process.</summary>
            public TaskCompletionSource<ClaudeTurnExit?>? TurnDone;
        }

        public ClaudeSessionService(IClaudeProcessHost processHost)
        {
            _processHost = processHost;
        }

        /// <summary>Session id from system/init of the first turn; passed as --resume afterwards.</summary>
        public string? SessionId { get; private set; }

        public SessionSettings Settings { get; } = new SessionSettings();

        /// <summary>The UI permission mode the CURRENTLY RUNNING turn was launched with. The live
        /// <see cref="SessionSettings.PermissionMode"/> can change mid-turn (UI popover / plan
        /// approval), but the running CLI process keeps the mode it started with — so anything that
        /// must match the running process's behaviour (e.g. pre-rendering auto-approved edit cards)
        /// reads this, not the live setting. Null until the first turn launches.</summary>
        public string? LaunchedPermissionMode { get; private set; }

        /// <summary>Pinned per-turn: true when the "Review edits at end of turn" feature routes edits
        /// through the permission hook (user mode acceptEdits + the toggle). The CLI is then launched in
        /// its default (ask) mode so Edit/Write/MultiEdit reach the hook (auto-approved there, baseline
        /// captured). The bridge reads this to (a) run the baseline-capture/auto-approve branch and
        /// (b) suppress the tool.use pre-decided green card (which assumes CLI-side auto-accept).
        /// <see cref="LaunchedPermissionMode"/> stays the user selection (acceptEdits) so command
        /// semantics (Auto-accept commands) are unchanged.</summary>
        public bool EditsRouteThroughHook { get; private set; }

        /// <summary>
        /// Optional MCP permission bridge (Teil A §A5). When available, its --mcp-config +
        /// --permission-prompt-tool flags are injected per turn so the CLI routes
        /// genehmigungspflichtige Tool-Calls through it — except in bypass mode.
        /// </summary>
        public IPermissionBridge? PermissionBridge { get; set; }

        /// <summary>Accumulated token usage of this session (sum over turns).</summary>
        public long TotalTokens { get; private set; }

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        /// <summary>Process id of the CLI running the current turn or keeping background tasks alive;
        /// null otherwise (also once a handed-back process only produces its prompt suggestion).</summary>
        public int? ActiveProcessId
        {
            get
            {
                lock (_procLock)
                {
                    var p = _proc;
                    return p != null && p.Pid > 0 && (p.State == ProcState.Turn || p.State == ProcState.Idle)
                        ? p.Pid
                        : (int?)null;
                }
            }
        }

        /// <summary>Raised (on a background thread) for every parsed domain event.</summary>
        public event Action<ClaudeEvent>? EventReceived;

        /// <summary>Value to pass for "ask about every tool", resolved from the installed CLI's
        /// <c>--help</c> before the first turn (see <see cref="ClaudeCliCapabilities"/>): <c>manual</c>
        /// on 2.1.2xx+, null (omit the flag) on older builds that have no such value. Omission is NOT
        /// interchangeable with it on new CLIs — it resolves to <c>auto</c>, where the model decides
        /// whether to consult the permission hook at all.</summary>
        internal string? AskPermissionModeArg { get; set; }

        /// <summary>Raised when a turn ends; string carries an error description or null.</summary>
        public event Action<ClaudeTurnExit, string?>? TurnCompleted;

        /// <summary>Forgets the session id so the next turn starts a fresh conversation.</summary>
        public void ResetSession()
        {
            _ = EndProcessAsync();
            SessionId = null;
            TotalTokens = 0;
        }

        /// <summary>Attaches to an existing CLI session (history → --resume).</summary>
        public void AttachSession(string sessionId)
        {
            _ = EndProcessAsync();
            SessionId = sessionId;
            TotalTokens = 0;
        }

        /// <summary>Stops the running turn (kills its process — and with it any background task).</summary>
        public void StopTurn()
        {
            CliProcess? p;
            lock (_procLock)
                p = _proc != null && _proc.State == ProcState.Turn ? _proc : null;
            Cancel(p); // the exit completes the turn (WasCancelled)
        }

        /// <summary>Kills a process that only lives on for its background tasks (no turn running) —
        /// every background task ends with it. The conversation itself is unaffected.</summary>
        public void StopBackgroundTasks()
        {
            CliProcess? p;
            lock (_procLock)
                p = _proc != null && _proc.State == ProcState.Idle ? _proc : null;
            Cancel(p);
        }

        /// <summary>Kills whatever process is left (tool window closing).</summary>
        public void Shutdown() => _ = EndProcessAsync();

        private static void Cancel(CliProcess? p)
        {
            try { p?.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Ends the lingering process (suggestion pending / background tasks) and waits ≤ 5 s
        /// for it to exit, so two processes never write the same session.</summary>
        private async Task EndProcessAsync()
        {
            CliProcess? p;
            lock (_procLock)
            {
                p = _proc;
                _proc = null;
                if (p != null && p.State != ProcState.Turn)
                    p.State = ProcState.Ending;
            }
            if (p == null)
                return;
            Cancel(p);
            await Task.WhenAny(p.Exited.Task, Task.Delay(5000)).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs one prompt. Throws <see cref="InvalidOperationException"/> when a turn
        /// is already running or the executable cannot be resolved. Returns when the turn is over
        /// (handed back at its result, or the process exited).
        /// </summary>
        public async Task RunTurnAsync(string prompt, string? executableOverride, string? workingDirectory,
            IReadOnlyList<string>? imagePaths = null)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new InvalidOperationException("A turn is already running.");

            // Set once the turn was handed back at its result — from then on the busy flag belongs to
            // whoever runs next.
            var handedBack = false;
            try
            {
                var exe = ClaudeExecutableLocator.Locate(executableOverride)
                          ?? throw new InvalidOperationException(
                              "Claude Code CLI not found. Install it (npm i -g @anthropic-ai/claude-code) " +
                              "or set the path via the gear menu → Advanced options.");

                // Which value means "ask about everything" depends on the installed CLI — probe it
                // (cached per executable, hidden process) BEFORE mapping the mode below.
                var capabilities = await ClaudeCliCapabilities.GetAsync(exe).ConfigureAwait(false);
                AskPermissionModeArg = capabilities.AskPermissionModeArg;
                var suggestions = Settings.PromptSuggestions && capabilities.SupportsPromptSuggestions;
                var replay = capabilities.SupportsReplayUserMessages;

                // Pin the mode for this turn: the process below keeps it for its whole lifetime,
                // even if the UI changes Settings.PermissionMode mid-turn (see LaunchedPermissionMode).
                LaunchedPermissionMode = Settings.PermissionMode;
                EditsRouteThroughHook = Settings.ReviewEditsAtTurnEnd && Settings.PermissionMode == "acceptEdits";
                _lastResultError = null;

                ClaudeTurnExit? exit;
                var retriedWithoutResume = false;
                while (true)
                {
                    var request = BuildRequest(prompt, exe, workingDirectory, imagePaths, suggestions, replay);
                    var launchKey = BuildLaunchKey(request);

                    // Background tasks keep the last process alive with its stdin open: if it was
                    // launched with the same settings, the prompt goes into it — the tasks keep running.
                    var turnDone = TryContinueInLiveProcess(request, launchKey);
                    if (turnDone == null)
                    {
                        // Anything still lingering (suggestion pending, background tasks under other
                        // settings) ends first, so two processes never write the same session.
                        await EndProcessAsync().ConfigureAwait(false);
                        turnDone = StartProcess(request, launchKey, suggestions, replay);
                    }

                    exit = await turnDone.ConfigureAwait(false);
                    if (exit == null)
                    {
                        handedBack = true; // TurnCompleted already raised at the result
                        return;
                    }

                    // Stale --resume id (e.g. CLI history pruned): drop the session
                    // and retry once as a fresh conversation instead of failing.
                    if (!exit.WasCancelled && exit.ExitCode != 0 && !retriedWithoutResume
                        && !string.IsNullOrEmpty(request.SessionId)
                        && exit.StdErrTail.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        retriedWithoutResume = true;
                        SessionId = null;
                        continue;
                    }
                    break;
                }

                TurnCompleted?.Invoke(exit!, ComposeError(exit!));
            }
            finally
            {
                if (!handedBack)
                    Volatile.Write(ref _busy, 0);
            }
        }

        private ClaudeTurnRequest BuildRequest(string prompt, string exe, string? workingDirectory,
            IReadOnlyList<string>? imagePaths, bool suggestions, bool replay)
        {
            var request = new ClaudeTurnRequest
            {
                Prompt = DecoratePrompt(prompt),
                ExecutablePath = exe,
                SessionId = SessionId,
                Model = Settings.Model,
                Effort = Settings.Effort,
                WorkingDirectory = workingDirectory,
                PermissionMode = MapPermissionMode(),
            };
            if (imagePaths != null)
            {
                foreach (var image in imagePaths)
                    request.ImagePaths.Add(image);
            }

            // Route tool permissions through the in-process MCP bridge (Teil A §A5),
            // except in bypass mode (the CLI ignores the prompt tool there anyway).
            if (PermissionBridge?.IsAvailable == true
                && !string.IsNullOrEmpty(PermissionBridge.McpConfigPath)
                && Settings.PermissionMode != "bypass")
            {
                request.ExtraArgs.Add("--mcp-config");
                request.ExtraArgs.Add(ClaudeCliProcessHost.Quote(PermissionBridge.McpConfigPath!));
                request.ExtraArgs.Add("--permission-prompt-tool");
                request.ExtraArgs.Add(McpPermissionBridge.PermissionPromptToolRef);
                // The CLI's default MCP tool-call timeout is short (≈ a minute) — a permission
                // prompt / AskUserQuestion would "time out" long before a human answers. The
                // effective wait is the config's `timeout` field (set on the bridge from the
                // same setting; the CLI prefers it over the env var — verified 2.1.178). We
                // still set MCP_TOOL_TIMEOUT as a fallback in case a future CLI flips that.
                var timeoutMs = (Settings.McpToolTimeoutMs > 0
                    ? Settings.McpToolTimeoutMs
                    : McpPermissionBridge.DefaultToolTimeoutMs).ToString();
                request.Environment["MCP_TOOL_TIMEOUT"] = timeoutMs;
                request.Environment["MCP_TIMEOUT"] = timeoutMs; // server-startup grace too
            }

            if (suggestions)
            {
                request.ExtraArgs.Add("--prompt-suggestions");
                request.ExtraArgs.Add("true");
            }
            if (replay)
                request.ExtraArgs.Add("--replay-user-messages");
            return request;
        }

        /// <summary>What a process was launched with (executable, folder, flags) — a prompt only goes
        /// into a live process when this still matches; the session id is left out, the live process
        /// already carries it.</summary>
        internal static string BuildLaunchKey(ClaudeTurnRequest request)
        {
            var sessionId = request.SessionId;
            request.SessionId = null;
            try
            {
                return request.ExecutablePath + "\n" + (request.WorkingDirectory ?? "") + "\n"
                    + ClaudeCliProcessHost.BuildArguments(request);
            }
            finally
            {
                request.SessionId = sessionId;
            }
        }

        /// <summary>Writes the prompt into the live process when it is idle (kept alive by background
        /// tasks), reusable and launched with the same settings. Returns the turn's completion, or null
        /// when a new process is needed.</summary>
        private Task<ClaudeTurnExit?>? TryContinueInLiveProcess(ClaudeTurnRequest request, string launchKey)
        {
            CliProcess? p;
            TaskCompletionSource<ClaudeTurnExit?> done;
            lock (_procLock)
            {
                p = _proc;
                // A follow-up turn on its way (a task just finished) makes the process non-reusable —
                // unless the CLI acknowledges our messages: it then queues the prompt behind the follow-up
                // and the replay marks where our turn starts, so nothing is lost.
                if (p == null || p.State != ProcState.Idle || (!p.Reusable && !p.Replay) || p.Input == null
                    || p.LaunchKey != launchKey)
                    return null;
                p.State = ProcState.Turn;
                p.FollowUp = false;
                if (p.Replay)
                    p.AwaitingReplay++;
                done = new TaskCompletionSource<ClaudeTurnExit?>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.TurnDone = done;
            }
            // Captured before the write: the turn may be over before TrySend even returns.
            if (p.Input!.TrySend(StreamJsonInput.BuildUserMessage(request.Prompt, request.ImagePaths)))
                return done.Task;

            // The pipe broke — the process is on its way out; the caller replaces it.
            lock (_procLock)
            {
                if (p.State == ProcState.Turn)
                    p.State = ProcState.Idle;
                p.TurnDone = null;
                p.Reusable = false;
                p.Replay = false; // never write into it again
                if (p.AwaitingReplay > 0)
                    p.AwaitingReplay--;
            }
            return null;
        }

        /// <summary>Starts a new CLI process for the turn; returns the turn's completion.</summary>
        private Task<ClaudeTurnExit?> StartProcess(ClaudeTurnRequest request, string launchKey, bool suggestions, bool replay)
        {
            var done = new TaskCompletionSource<ClaudeTurnExit?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var proc = new CliProcess
            {
                LaunchKey = launchKey,
                Suggestions = suggestions,
                Replay = replay,
                TurnDone = done,
            };
            request.ProcessStarted = pid =>
            {
                lock (_procLock)
                    proc.Pid = pid;
            };
            request.InputOpened = input =>
            {
                bool close;
                lock (_procLock)
                {
                    proc.Input = input;
                    // the turn may already be past its result (or the process being killed)
                    close = proc.State == ProcState.Draining || proc.State == ProcState.Ending;
                }
                if (close)
                    input.Close();
            };

            lock (_procLock)
                _proc = proc;
            _ = RunProcessAsync(proc, request);
            return done.Task;
        }

        private async Task RunProcessAsync(CliProcess proc, ClaudeTurnRequest request)
        {
            ClaudeTurnExit exit;
            try
            {
                exit = await _processHost.RunTurnAsync(request, line =>
                {
                    foreach (var ev in proc.Parser.ParseLine(line))
                        OnEvent(proc, ev);
                }, proc.Cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnExit(proc, null, ex);
                return;
            }
            OnExit(proc, exit, null);
        }

        private void OnEvent(CliProcess proc, ClaudeEvent ev)
        {
            bool forward;
            var followUpStarted = false;
            lock (_procLock)
            {
                if (ev is UserMessageReplayedEvent)
                {
                    // the CLI took up one of our stdin messages — internal marker, not forwarded
                    if (proc.AwaitingReplay > 0)
                        proc.AwaitingReplay--;
                    return;
                }
                switch (proc.State)
                {
                    case ProcState.Ending:
                        forward = false;
                        break;
                    case ProcState.Draining:
                        forward = ev is PromptSuggestionEvent || ev is ContextUsageEvent;
                        break;
                    case ProcState.Idle:
                        forward = true;
                        if (ev is TaskNotificationEvent)
                        {
                            proc.Reusable = false; // the CLI answers it with a follow-up turn
                        }
                        else if (ev is SessionInitEvent)
                        {
                            // A task finished and the CLI starts a follow-up turn on its own. Take the
                            // busy flag for it — unless a prompt of ours just got it (that turn then
                            // replaces this process: not reusable any more).
                            proc.Reusable = false;
                            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                            {
                                proc.State = ProcState.Turn;
                                proc.FollowUp = true;
                                proc.TurnDone = new TaskCompletionSource<ClaudeTurnExit?>(TaskCreationOptions.RunContinuationsAsynchronously);
                                _lastResultError = null;
                                followUpStarted = true;
                            }
                        }
                        break;
                    default:
                        forward = true;
                        break;
                }
                if (forward && ev is BackgroundTasksChangedEvent bg)
                    proc.BackgroundTasks = bg.Count;
            }

            if (followUpStarted)
                EventReceived?.Invoke(new FollowUpTurnStartedEvent());
            if (!forward)
                return;

            Bookkeep(ev);
            EventReceived?.Invoke(ev);

            if (ev is TurnResultEvent r && string.IsNullOrEmpty(r.ParentToolUseId))
                OnTurnResult(proc, r);
        }

        /// <summary>The turn's (top-level) result: decide how the turn and its process end.</summary>
        private void OnTurnResult(CliProcess proc, TurnResultEvent result)
        {
            var handBack = false;
            lock (_procLock)
            {
                if (proc.State != ProcState.Turn)
                    return;
                // Our prompt is still queued behind the CLI's follow-up turn: this result is the
                // follow-up's — shown as part of our turn, which goes on (stdin stays open).
                if (proc.AwaitingReplay > 0)
                    return;
                // Ask for the context breakdown while stdin is still open; the CLI answers it locally
                // (no model call) even when stdin is closed right after — see ContextUsageEvent.
                proc.Input?.TrySend(StreamJsonInput.BuildContextUsageRequest(
                    StreamJsonInput.ContextUsageRequestPrefix + Interlocked.Increment(ref _contextRequestSeq)));
                if (proc.BackgroundTasks > 0)
                {
                    // Background tasks keep the process alive anyway. Hand the turn back right here (the
                    // UI is free again) and keep stdin open: the next prompt goes into this process and
                    // the CLI's follow-up turn — when a task finishes — is still shown. Without a stdin
                    // handle (test hosts) there is nothing to write into: wait for the follow-up instead.
                    if (proc.Input != null)
                    {
                        proc.State = ProcState.Idle;
                        proc.Reusable = true;
                        handBack = true;
                    }
                }
                else
                {
                    // EOF: the CLI finishes up (prompt suggestion) and exits.
                    proc.Input?.Close();
                    // With suggestions the process lives on for ~4-11 s to generate one. Hand the turn
                    // back now instead of at exit, or the UI would sit on "working" that long.
                    if (proc.Suggestions && !result.IsError)
                    {
                        proc.State = ProcState.Draining;
                        handBack = true;
                    }
                }
            }

            if (handBack)
                HandBack(proc, result.IsError ? (_lastResultError ?? "The turn ended with an error.") : null);
        }

        private void HandBack(CliProcess proc, string? error)
        {
            TaskCompletionSource<ClaudeTurnExit?>? done;
            lock (_procLock)
            {
                done = proc.TurnDone;
                proc.TurnDone = null;
                proc.FollowUp = false;
            }
            TurnCompleted?.Invoke(new ClaudeTurnExit { ExitCode = 0 }, error);
            Volatile.Write(ref _busy, 0);
            done?.TrySetResult(null);
        }

        private void OnExit(CliProcess proc, ClaudeTurnExit? exit, Exception? failure)
        {
            ProcState state;
            bool followUp, hadTasks;
            TaskCompletionSource<ClaudeTurnExit?>? done;
            lock (_procLock)
            {
                state = proc.State;
                followUp = proc.FollowUp;
                hadTasks = proc.BackgroundTasks > 0;
                done = proc.TurnDone;
                proc.TurnDone = null;
                proc.State = ProcState.Ending;
                proc.Input = null;
                if (_proc == proc)
                    _proc = null;
            }
            proc.Exited.TrySetResult(true);

            // The background tasks died with the process — clear them in the UI.
            if (hadTasks)
                EventReceived?.Invoke(new BackgroundTasksChangedEvent { Count = 0, ProcessEnded = true });

            if (state != ProcState.Turn)
                return;
            exit ??= new ClaudeTurnExit { ExitCode = -1, StdErrTail = failure?.Message ?? "" };
            if (followUp)
            {
                // nobody awaits the CLI's own follow-up turn — end it here
                TurnCompleted?.Invoke(exit, failure != null ? failure.Message : ComposeError(exit));
                Volatile.Write(ref _busy, 0);
            }
            else if (failure != null)
            {
                done?.TrySetException(failure);
            }
            else
            {
                done?.TrySetResult(exit);
            }
        }

        /// <summary>The turn's error text for a process exit, or null when it went fine / was stopped.</summary>
        private string? ComposeError(ClaudeTurnExit exit)
        {
            if (exit.WasCancelled || exit.ExitCode == 0)
                return null;
            // Prefer the CLI's own error text (result event / stderr) over the bare exit code.
            var detail = !string.IsNullOrWhiteSpace(_lastResultError)
                ? _lastResultError!.Trim()
                : exit.StdErrTail?.Trim();
            return string.IsNullOrWhiteSpace(detail)
                ? $"claude exited with code {exit.ExitCode} (no error output)"
                : $"claude exited with code {exit.ExitCode}:\n{detail}";
        }

        private void Bookkeep(ClaudeEvent ev)
        {
            switch (ev)
            {
                case TurnResultEvent result:
                    // Adopt the session id ONLY when the CLI actually created a
                    // conversation (num_turns > 0). Local-only turns like /help report
                    // a session_id that --resume rejects ("No conversation found").
                    if (result.NumTurns > 0 && !string.IsNullOrEmpty(result.SessionId))
                        SessionId = result.SessionId;
                    TotalTokens += result.InputTokens + result.OutputTokens;
                    // Remember the error text so the turn's failure message can carry it
                    // (the CLI puts API/auth/quota failures here, often with empty stderr).
                    if (result.IsError && !string.IsNullOrWhiteSpace(result.ResultText))
                        _lastResultError = result.ResultText;
                    break;
            }
        }

        /// <summary>Applies session toggles that work via prompt keywords (ultracode).</summary>
        internal string DecoratePrompt(string prompt)
        {
            if (Settings.Ultracode
                && prompt.IndexOf("ultracode", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return prompt + "\n\nultracode";
            }
            return prompt;
        }

        /// <summary>Maps the UI permission mode (§3.2 permission.set) onto CLI --permission-mode.</summary>
        internal string? MapPermissionMode()
        {
            if (Settings.PlanMode)
                return "plan";
            // "Review edits at end of turn": launch in the ask/manual mode so edits pass through the
            // permission hook (auto-approved there, with a guaranteed pre-edit baseline) instead of the
            // CLI's own auto-accept, which would apply the write before we could snapshot it.
            //
            // This MUST be the explicit value, not an omitted flag: omitting it means the CLI's default
            // mode, which on 2.1.2xx+ is `auto` — and there the model decides whether to consult the
            // permission hook. Measured against 2.1.263 (2026-09-10, same project, same flags, only the
            // model swapped): Opus 5 applied an Edit without ever calling the hook, Haiku called it. The
            // review then had no baseline and silently produced nothing.
            if (Settings.ReviewEditsAtTurnEnd && Settings.PermissionMode == "acceptEdits")
                return AskPermissionModeArg;
            return Settings.PermissionMode switch
            {
                "acceptEdits" => "acceptEdits",
                "plan" => "plan",
                "bypass" => "bypassPermissions",
                _ => AskPermissionModeArg, // "ask" — same reasoning: must be explicit, else `auto` wins
            };
        }
    }
}
