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

        /// <summary>How long (ms) the CLI may wait on a permission/AskUserQuestion prompt before it
        /// times out — applied via the MCP_TOOL_TIMEOUT env var. Driven by the settings window;
        /// defaults to <see cref="McpPermissionBridge.ToolTimeoutMs"/>.</summary>
        public int McpToolTimeoutMs { get; set; } = McpPermissionBridge.ToolTimeoutMs;
    }

    /// <summary>
    /// Turn orchestration (Teil A §A6/§A7): starts one CLI process per turn,
    /// feeds NDJSON lines through the parser and re-raises domain events.
    /// UI-free; the bridge layer maps events onto the WebView message contract.
    /// </summary>
    public sealed class ClaudeSessionService
    {
        private IClaudeProcessHost _processHost;
        private CancellationTokenSource? _turnCts;
        private int _busy; // 0 = idle, 1 = turn running

        public ClaudeSessionService(IClaudeProcessHost processHost)
        {
            _processHost = processHost;
        }

        /// <summary>
        /// Swaps the process-host implementation (per-turn ↔ persistent). Only valid while
        /// idle; the caller owns disposing the previous host. Throws if a turn is running.
        /// </summary>
        public void SetProcessHost(IClaudeProcessHost host)
        {
            if (IsBusy)
                throw new InvalidOperationException("Cannot swap the process host while a turn is running.");
            _processHost = host ?? throw new ArgumentNullException(nameof(host));
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

        /// <summary>Raised when a turn ends; string carries an error description or null.</summary>
        public event Action<ClaudeTurnExit, string?>? TurnCompleted;

        /// <summary>Forgets the session id so the next turn starts a fresh conversation.</summary>
        public void ResetSession()
        {
            SessionId = null;
            TotalTokens = 0;
        }

        /// <summary>Attaches to an existing CLI session (history → --resume).</summary>
        public void AttachSession(string sessionId)
        {
            SessionId = sessionId;
            TotalTokens = 0;
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
        public async Task RunTurnAsync(string prompt, string? executableOverride, string? workingDirectory)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new InvalidOperationException("A turn is already running.");

            try
            {
                var exe = ClaudeExecutableLocator.Locate(executableOverride)
                          ?? throw new InvalidOperationException(
                              "Claude Code CLI not found. Install it (npm i -g @anthropic-ai/claude-code) " +
                              "or set the path via the gear menu → Advanced options.");

                // Pin the mode for this turn: the process below keeps it for its whole lifetime,
                // even if the UI changes Settings.PermissionMode mid-turn (see LaunchedPermissionMode).
                LaunchedPermissionMode = Settings.PermissionMode;

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
                        // prompt / AskUserQuestion would "time out" long before a human answers. Raise
                        // it via env vars (the config's `timeout` field alone isn't honoured for tool
                        // calls on HTTP MCP servers). MCP_TOOL_TIMEOUT = per-tool-call wait.
                        var timeoutMs = (Settings.McpToolTimeoutMs > 0
                            ? Settings.McpToolTimeoutMs
                            : McpPermissionBridge.ToolTimeoutMs).ToString();
                        request.Environment["MCP_TOOL_TIMEOUT"] = timeoutMs;
                        request.Environment["MCP_TIMEOUT"] = timeoutMs; // server-startup grace too
                    }

                    var parser = new NdjsonParser();
                    _turnCts = new CancellationTokenSource();
                    try
                    {
                        exit = await _processHost.RunTurnAsync(request, line =>
                        {
                            foreach (var ev in parser.ParseLine(line))
                            {
                                Bookkeep(ev);
                                EventReceived?.Invoke(ev);
                            }
                        }, _turnCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _turnCts.Dispose();
                        _turnCts = null;
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

                string? error = null;
                if (!exit.WasCancelled && exit.ExitCode != 0)
                {
                    error = $"claude exited with code {exit.ExitCode}";
                    if (!string.IsNullOrWhiteSpace(exit.StdErrTail))
                        error += ":\n" + exit.StdErrTail;
                }

                TurnCompleted?.Invoke(exit, error);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
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
            return Settings.PermissionMode switch
            {
                "acceptEdits" => "acceptEdits",
                "plan" => "plan",
                "bypass" => "bypassPermissions",
                _ => null, // "ask" → CLI default
            };
        }
    }
}
