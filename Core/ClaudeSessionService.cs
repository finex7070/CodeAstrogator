using System;
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
    /// </summary>
    public sealed class ClaudeSessionService
    {
        private readonly IClaudeProcessHost _processHost;
        private CancellationTokenSource? _turnCts;
        private int _busy; // 0 = idle, 1 = turn running

        // A turn that was handed back at its `result` while its process keeps running to deliver the
        // prompt suggestion (see RunTurnAsync). Killed when the next turn starts or the session changes.
        private readonly object _drainLock = new object();
        private CancellationTokenSource? _drainCts;
        private Task? _drainDone;

        // The result-event error text of the current turn (e.g. "API Error: …",
        // "Credit balance is too low"). The CLI reports many failures here on stdout
        // while stderr stays empty, so we fold it into the turn's error message.
        private string? _lastResultError;

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
            CancelDrain();
            SessionId = null;
            TotalTokens = 0;
        }

        /// <summary>Attaches to an existing CLI session (history → --resume).</summary>
        public void AttachSession(string sessionId)
        {
            CancelDrain();
            SessionId = sessionId;
            TotalTokens = 0;
        }

        /// <summary>Kills the process of an already-completed turn that is still waiting for its
        /// prompt suggestion (nothing else of that process is forwarded any more).</summary>
        private Task? CancelDrain()
        {
            CancellationTokenSource? cts;
            Task? done;
            lock (_drainLock)
            {
                cts = _drainCts;
                done = _drainDone;
            }
            // outside the lock: cancelling may run the turn's continuation (and its cleanup) inline
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { } // the process exited on its own in the meantime
            return done;
        }

        public void StopTurn()
        {
            try { _turnCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Runs one prompt. Throws <see cref="InvalidOperationException"/> when a turn
        /// is already running or the executable cannot be resolved.
        /// </summary>
        public async Task RunTurnAsync(string prompt, string? executableOverride, string? workingDirectory,
            System.Collections.Generic.IReadOnlyList<string>? imagePaths = null)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new InvalidOperationException("A turn is already running.");

            // Set once this turn was handed back early (at its result) — from then on the busy flag and
            // TurnCompleted belong to whoever runs next, and only the prompt suggestion is forwarded.
            var completedEarly = false;
            var processDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // The previous turn's process may still be generating its suggestion — end it first,
                // so two processes never write the same session.
                var draining = CancelDrain();
                if (draining != null)
                    await Task.WhenAny(draining, Task.Delay(5000)).ConfigureAwait(false);

                var exe = ClaudeExecutableLocator.Locate(executableOverride)
                          ?? throw new InvalidOperationException(
                              "Claude Code CLI not found. Install it (npm i -g @anthropic-ai/claude-code) " +
                              "or set the path via the gear menu → Advanced options.");

                // Which value means "ask about everything" depends on the installed CLI — probe it
                // (cached per executable, hidden process) BEFORE mapping the mode below.
                var capabilities = await ClaudeCliCapabilities.GetAsync(exe).ConfigureAwait(false);
                AskPermissionModeArg = capabilities.AskPermissionModeArg;
                var suggestions = Settings.PromptSuggestions && capabilities.SupportsPromptSuggestions;

                // Pin the mode for this turn: the process below keeps it for its whole lifetime,
                // even if the UI changes Settings.PermissionMode mid-turn (see LaunchedPermissionMode).
                LaunchedPermissionMode = Settings.PermissionMode;
                EditsRouteThroughHook = Settings.ReviewEditsAtTurnEnd && Settings.PermissionMode == "acceptEdits";
                _lastResultError = null;

                ClaudeTurnExit exit;
                var retriedWithoutResume = false;
                while (true)
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

                    var parser = new NdjsonParser();
                    var backgroundTasks = 0;
                    var cts = new CancellationTokenSource();
                    _turnCts = cts;
                    try
                    {
                        exit = await _processHost.RunTurnAsync(request, line =>
                        {
                            foreach (var ev in parser.ParseLine(line))
                            {
                                if (completedEarly)
                                {
                                    if (ev is PromptSuggestionEvent)
                                        EventReceived?.Invoke(ev);
                                    continue;
                                }

                                Bookkeep(ev);
                                if (ev is BackgroundTasksChangedEvent bg)
                                    backgroundTasks = bg.Count;
                                EventReceived?.Invoke(ev);

                                // With suggestions on, the process lives on for ~4-11 s after the result
                                // to generate one. Hand the turn back right here instead of at exit, or the
                                // UI would sit on "working" that long. Not while background tasks run: the
                                // CLI then answers their completion with a follow-up turn in this process
                                // (2.1.287), which must still be shown — exit stays the turn end there.
                                if (suggestions && ev is TurnResultEvent r && string.IsNullOrEmpty(r.ParentToolUseId)
                                    && !r.IsError && backgroundTasks == 0)
                                {
                                    completedEarly = true;
                                    lock (_drainLock)
                                    {
                                        _drainCts = cts;
                                        _drainDone = processDone.Task;
                                    }
                                    Interlocked.CompareExchange(ref _turnCts, null, cts);
                                    TurnCompleted?.Invoke(new ClaudeTurnExit { ExitCode = 0 }, null);
                                    Volatile.Write(ref _busy, 0);
                                }
                            }
                        }, cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _turnCts, null, cts);
                        lock (_drainLock)
                        {
                            if (_drainCts == cts)
                            {
                                _drainCts = null;
                                _drainDone = null;
                            }
                        }
                        cts.Dispose();
                    }

                    if (completedEarly)
                        return;

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

                string? error = null;
                if (!exit.WasCancelled && exit.ExitCode != 0)
                {
                    // Prefer the CLI's own error text (result event / stderr) over the bare exit code.
                    var detail = !string.IsNullOrWhiteSpace(_lastResultError)
                        ? _lastResultError!.Trim()
                        : exit.StdErrTail?.Trim();
                    error = string.IsNullOrWhiteSpace(detail)
                        ? $"claude exited with code {exit.ExitCode} (no error output)"
                        : $"claude exited with code {exit.ExitCode}:\n{detail}";
                }

                TurnCompleted?.Invoke(exit, error);
            }
            finally
            {
                if (!completedEarly)
                    Volatile.Write(ref _busy, 0);
                processDone.TrySetResult(true);
            }
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
