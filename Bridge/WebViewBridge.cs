using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using CodeAstrogator.Core.EditReview;
using CodeAstrogator.Options;
using CodeAstrogator.Services;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

#pragma warning disable VSEXTPREVIEW_SETTINGS // settings API is in preview

namespace CodeAstrogator.Bridge
{
    /// <summary>
    /// Host side of the WebView2 message contract (Teil B §3). Receives web→host
    /// messages on the UI thread, drives <see cref="ClaudeSessionService"/> and
    /// marshals its background events back onto the UI thread before posting.
    /// </summary>
    internal sealed class WebViewBridge : IDisposable
    {
        private readonly CoreWebView2 _webView;
        private readonly CodeAstrogatorPackage _package;
        private readonly ClaudeSessionService _session;
        private IClaudeProcessHost _processHost; // per-turn or persistent (UsePersistentCli option)
        private readonly SessionHistoryStore _history;
        private JObject? _streamingAssistant; // transcript accumulator for the active assistant block
        private bool _turnHadAssistantOutput; // gates the result-text fallback (decision #8)
        private bool _sessionStartAnnounced;  // "Session started" note, once per session (decision #9)
        private UsageSnapshot? _lastUsage;    // session/weekly plan utilization (from `claude -p /usage`)
        private string? _planLabel;           // "Team Plan" etc. from ~/.claude.json
        private System.Threading.Timer? _usageTimer; // periodic refresh of the usage meters
        private int _usageRefreshInFlight;    // 0/1 reentrancy guard: never run two /usage fetches at once
        private const int UsageRefreshIntervalMs = 60 * 1000; // every minute (also while a turn runs)
        private JArray? _slashCommands;       // CLI-reported slash commands (system/init)
        private RemoteTerminalLauncher? _remoteTerminal; // interactive `claude --remote-control` session (VS terminal)
        private readonly ActiveDocumentTracker _activeDocs; // active editor tab → auto-reference
        private bool _activeFileSessionEnabled = true; // per-session override (seeded from ActiveFileOnByDefault; AutoAddActiveFile has priority)
        private readonly McpPermissionBridge _permission; // in-process MCP permission server (§A5)
        private readonly EditReviewController _editReview; // inline edit-review adornments (opt-in)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingPermission> _pendingPermissions
            = new System.Collections.Concurrent.ConcurrentDictionary<string, PendingPermission>();
        // recorded (approved) permission messages by tool_use_id, so a later tool.result can
        // upgrade their persisted status to applied/failed. Cleared at turn end.
        private readonly System.Collections.Generic.Dictionary<string, JObject> _recordedPermissions
            = new System.Collections.Generic.Dictionary<string, JObject>();
        // ── "Review edits at end of turn" (opt-in, acceptEdits) ──────────────────
        // All three are guarded by _turnReviewLock (written from the MCP/stream background threads,
        // read/mutated on the UI thread). Baselines accumulate during a turn; the reviews are built at
        // turn end and persist (gating the next prompt) until every file is decided or "Keep all".
        private readonly object _turnReviewLock = new object();
        // path → pre-turn file content ("" = a file created this turn). First-touch capture only.
        private readonly System.Collections.Generic.Dictionary<string, string> _turnEditBaselines
            = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // paths whose pristine content couldn't be snapshotted reliably (read failed / lost the write
        // race) — never reviewed, so a bad read can't become a revert-to-empty target.
        private readonly System.Collections.Generic.HashSet<string> _turnBaselineSkip
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // path → the cumulative review built at turn end (present ⇒ the next prompt is gated).
        private readonly System.Collections.Generic.Dictionary<string, TurnReview> _turnReviews
            = new System.Collections.Generic.Dictionary<string, TurnReview>(StringComparer.OrdinalIgnoreCase);

        private sealed class TurnReview
        {
            public EditReviewSession Session = null!;
            public string Baseline = "";  // pre-turn content (revert target)
            public bool IsNew;            // baseline was empty → file created this turn
        }

        private bool _disposed;
        private bool _uiReady; // the WebUI sent "ready" → safe to Post (else messages are dropped)
        private readonly System.Collections.Generic.List<JObject> _queuedUiMessages
            = new System.Collections.Generic.List<JObject>(); // actions raised before "ready"

        /// <summary>An in-flight permission request awaiting a UI decision.</summary>
        private sealed class PendingPermission
        {
            public TaskCompletionSource<PermissionDecision> Tcs = null!;
            public CancellationTokenRegistration Registration; // disposed when the request settles
            public string RequestId = "";
            public string ToolName = "";
            public JObject Input = new JObject();
            public JObject? Diff;
            public EditReviewSession? Review; // set when this edit is reviewed in the editor (opt-in)
        }

        public WebViewBridge(CoreWebView2 webView, CodeAstrogatorPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _webView = webView;
            _package = package;
            _processHost = CreateProcessHost(package.GetOptions().UsePersistentCli);
            _session = new ClaudeSessionService(_processHost);
            // seed the session from the persisted Model·Mode popover state (sticky across new
            // chats + VS restarts); the popover writes these back via SaveOptions on each change
            var opt = package.GetOptions();
            _session.Settings.Model = string.IsNullOrEmpty(opt.DefaultModel) ? null : opt.DefaultModel;
            _session.Settings.Effort = opt.DefaultEffortString;
            _session.Settings.Ultracode = opt.UltracodeEnabled;
            _session.Settings.PermissionMode = opt.PermissionModeString;
            _session.Settings.ReviewEditsAtTurnEnd = opt.ReviewEditsAtTurnEnd;
            _session.Settings.McpToolTimeoutMs = PromptTimeoutMs(opt);
            _activeFileSessionEnabled = opt.ActiveFileOnByDefault; // initial per-session toggle = option default

            // In-process MCP permission server (Teil A §A5). Best-effort: if it fails to start,
            // IsAvailable stays false and edits fall back to the CLI default (no prompt flags).
            _permission = new McpPermissionBridge
            {
                OnPermissionRequested = HandlePermissionRequestedAsync,
                ToolTimeoutMs = PromptTimeoutMs(opt), // config `timeout` wins over the env var (CLI 2.1.178)
            };
            try { _permission.Start(); } catch { /* leave unavailable */ }
            _session.PermissionBridge = _permission;
            // Inline edit-review (opt-in): opens edited files with a red/green diff + per-hunk
            // Accept/Reject; calls FinalizeEditReview when every hunk in a request is decided.
            _editReview = new EditReviewController(_package, FinalizeEditReview);
            _history = SessionHistoryStore.LoadFrom(
                SessionHistoryStore.GetHistoryPath(package.GetSolutionDirectory()));

            _activeDocs = new ActiveDocumentTracker(package);
            _activeDocs.ActiveDocumentChanged += OnActiveDocumentChanged;

            _webView.WebMessageReceived += OnWebMessageReceived;
            _session.EventReceived += OnSessionEvent;
            _session.TurnCompleted += OnTurnCompleted;
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
            _package.OptionsChanged += OnOptionsChanged;

            // Keep the usage meters fresh with a once-a-minute refresh — including while a turn is
            // running, since the meters are otherwise only updated on window open (ready) and at turn
            // end, so a long turn would show stale limits the whole time. The /usage fetch is its own
            // short-lived `claude -p /usage` process (see RefreshUsage), independent of the turn's
            // process, and a reentrancy guard prevents overlap. First tick after one interval (the
            // ready handler does the on-open fetch). The timer fires on a thread-pool thread;
            // RefreshUsage is thread-agnostic and Post() is guarded against teardown.
            _usageTimer = new System.Threading.Timer(OnUsageTimerTick, null, UsageRefreshIntervalMs, UsageRefreshIntervalMs);
        }

        private void OnUsageTimerTick(object state)
        {
            if (_disposed)
                return;
            RefreshUsage();
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // disposed with the tool window (UI thread)
            if (_disposed)
                return;
            _disposed = true;
            _usageTimer?.Dispose();
            VSColorTheme.ThemeChanged -= OnVsThemeChanged;
            _package.OptionsChanged -= OnOptionsChanged;
            _activeDocs.ActiveDocumentChanged -= OnActiveDocumentChanged;
            _activeDocs.Dispose();
            _session.EventReceived -= OnSessionEvent;
            _session.TurnCompleted -= OnTurnCompleted;
            _webView.WebMessageReceived -= OnWebMessageReceived;
            _session.StopTurn();
            DenyAllPendingPermissions("Tool window closed");
            _editReview.Dispose(); // remove any open edit-review adornments
            _permission.Dispose(); // stops the MCP listener + deletes the config file
            (_processHost as IDisposable)?.Dispose(); // persistent host owns a live process
            _remoteTerminal?.Dispose(); // releases the integrated-terminal proxy (the terminal closes with VS)
            lock (_history)
                _history.Save(); // synchronous — tool window / VS is closing
        }

        private static IClaudeProcessHost CreateProcessHost(bool persistent) =>
            persistent ? new ClaudePersistentProcessHost() : (IClaudeProcessHost)new ClaudeCliProcessHost();

        /// <summary>
        /// Applies the UsePersistentCli option by swapping the process host when it changed.
        /// Only swaps while idle; otherwise the change takes effect on the next tool-window open.
        /// </summary>
        private void ApplyProcessHostOption()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var wantPersistent = _package.GetOptions().UsePersistentCli;
            var isPersistent = _processHost is ClaudePersistentProcessHost;
            if (wantPersistent == isPersistent || _session.IsBusy)
                return;

            var old = _processHost;
            _processHost = CreateProcessHost(wantPersistent);
            _session.SetProcessHost(_processHost);
            (old as IDisposable)?.Dispose();
        }

        // ── web → host ────────────────────────────────────────────────────────

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            JObject msg;
            try
            {
                msg = JObject.Parse(e.WebMessageAsJson);
            }
            catch
            {
                return;
            }

            switch (msg.Value<string>("type"))
            {
                case "ready":
                    SendTheme();
                    SendAuthState();
                    SendInitialSession();
                    SendSlashCommands(); // re-send the CLI-reported list after a WebView reload
                    SendActiveFile();
                    if (_package.SettingsLoadError != null)
                        PostSystemNote("⚠ Settings could not be read — using defaults. " + _package.SettingsLoadError);
                    if (RemoteSessionActive) // WebView reloaded while a remote session runs
                        PostRemoteTerminalState("ready", RemoteRunningMessage());
                    RefreshUsage();
                    FlushQueuedUiMessages(); // deliver actions queued before the UI was ready
                    break;
                case "prompt.send":
                    HandlePromptSend(msg);
                    break;
                case "turn.stop":
                    DenyAllPendingPermissions("Turn stopped"); // let blocked MCP calls return
                    _session.StopTurn();
                    PostSystemNote("Turn stopped"); // decision #11
                    // A cancelled turn emits no TurnResultEvent, so build the changed-files review list
                    // here from whatever baselines were captured before the stop (edits already applied).
                    BuildAndPostTurnReviewList();
                    break;
                case "session.new":
                    HandleSessionNew();
                    break;
                case "session.listRequest":
                    JArray sessions;
                    lock (_history)
                        sessions = _history.ToSessionList();
                    Post(new JObject { ["type"] = "session.list", ["sessions"] = sessions });
                    break;
                case "session.load":
                    HandleSessionLoad(msg.Value<string>("sessionId") ?? "");
                    break;
                case "session.rename":
                    HandleSessionRename(msg.Value<string>("sessionId"), msg.Value<string>("title") ?? "");
                    break;
                case "session.delete":
                    HandleSessionDelete(msg.Value<string>("sessionId"));
                    break;
                case "model.set":
                    _session.Settings.Model = msg.Value<string>("model");
                    _package.GetOptions().DefaultModel = _session.Settings.Model ?? "";
                    _package.SaveOptions(); // sticky across new chats + VS restarts
                    break;
                case "effort.set":
                    var effort = msg.Value<string>("effort") ?? "medium";
                    // CLI 2.1.x accepts exactly these; ignore anything else.
                    if (effort is "low" or "medium" or "high" or "xhigh" or "max")
                    {
                        _session.Settings.Effort = effort;
                        _package.GetOptions().DefaultEffortString = effort;
                        _package.SaveOptions();
                    }
                    break;
                case "mode.set":
                    _session.Settings.PlanMode = msg.Value<bool?>("planMode") ?? false;
                    break;
                case "ultracode.set":
                    _session.Settings.Ultracode = msg.Value<bool?>("enabled") ?? false;
                    _package.GetOptions().UltracodeEnabled = _session.Settings.Ultracode;
                    _package.SaveOptions();
                    break;
                case "permission.set":
                    _session.Settings.PermissionMode = msg.Value<string>("mode") ?? "ask";
                    _package.GetOptions().PermissionModeString = _session.Settings.PermissionMode;
                    _package.SaveOptions();
                    break;
                case "autoAcceptCommands.set":
                    _package.GetOptions().AutoAcceptCommands = msg.Value<bool?>("enabled") ?? false;
                    _package.SaveOptions();
                    break;
                case "reviewEditsInEditor.set":
                    _package.GetOptions().ReviewEditsInEditor = msg.Value<bool?>("enabled") ?? false;
                    _package.SaveOptions();
                    break;
                case "reviewEditsAtTurnEnd.set":
                    _package.GetOptions().ReviewEditsAtTurnEnd = msg.Value<bool?>("enabled") ?? false;
                    _session.Settings.ReviewEditsAtTurnEnd = _package.GetOptions().ReviewEditsAtTurnEnd;
                    _package.SaveOptions();
                    break;
                case "editReview.open":
                    HandleEditReviewOpen(msg.Value<string>("requestId") ?? "");
                    break;
                case "editReview.openTurnFile":
                    HandleOpenTurnFile(msg.Value<string>("path") ?? "");
                    break;
                case "editReview.finishTurnFile":
                    HandleFinishTurnFile(msg.Value<string>("path") ?? "");
                    break;
                case "editReview.keepAll":
                    HandleKeepAllTurnReviews();
                    break;
                case "editReview.discardAll":
                    HandleDiscardAllTurnReviews();
                    break;
                case "consent.set":
                    // first-run consent popup answered (announcements + updates) → persist both
                    // choices and that they were made (so the popup never re-appears).
                    _package.GetOptions().NoticeFetchEnabled = msg.Value<bool?>("noticeEnabled") ?? false;
                    _package.GetOptions().NoticeFetchDecided = true;
                    _package.GetOptions().UpdateCheckEnabled = msg.Value<bool?>("updateEnabled") ?? false;
                    _package.GetOptions().UpdateCheckDecided = true;
                    _package.SaveOptions();
                    break;
                case "theme.setMode":
                    HandleThemeSetMode(msg.Value<string>("mode") ?? "auto");
                    break;
                case "accent.set":
                    HandleAccentSet(msg.Value<string>("color") ?? "");
                    break;
                case "permission.decision":
                    HandlePermissionDecision(msg);
                    break;
                case "permission.approveAlways":
                    HandleApproveAlways(msg.Value<string>("requestId") ?? "", msg["patterns"] as JArray);
                    break;
                case "question.answer":
                    HandleQuestionAnswer(msg);
                    break;
                case "verbosity.set":
                    HandleVerbositySet(msg.Value<string>("level") ?? "normal");
                    break;
                case "options.open":
                    _package.OpenOptions();
                    break;
                case "slash.run":
                    HandleSlashRun(msg.Value<string>("command") ?? "", msg.Value<string>("args"));
                    break;
                case "attach.files":
                case "attach.browse":
                    HandleAttachFiles();
                    break;
                case "attach.context":
                    HandleAttachContext();
                    break;
                case "clipboard.paste":
                    HandleClipboardPaste();
                    break;
                case "remote.start":
                    HandleRemoteStart();
                    break;
                case "remote.stop":
                    HandleRemoteStop();
                    break;
                case "activeFile.setEnabled":
                    HandleActiveFileSetEnabled(msg.Value<bool?>("enabled") ?? true);
                    break;
                case "activeFile.refresh":
                    // composer gained focus — re-read the current editor selection
                    SendActiveFile();
                    break;
                case "files.listRequest":
                    HandleFilesListRequest();
                    break;
                case "editor.insert":
                    HandleEditorInsert(msg.Value<string>("text") ?? "");
                    break;
                case "editor.openFile":
                    HandleOpenFile(msg.Value<string>("path") ?? "");
                    break;
            }
        }

        private void HandlePromptSend(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var text = msg.Value<string>("text") ?? "";
            if (string.IsNullOrWhiteSpace(text) || _session.IsBusy)
                return;
            if (RemoteSessionActive)
                return; // remote control owns the session in the terminal — the UI locks the composer too
            // Authoritative half of the post-turn edit-review gate (the WebUI also disables send): a new
            // prompt can't start while changed files from the last turn are still awaiting review. Guards
            // against a reload, queued sends, or an Enter-race that the client-only check would miss.
            if (HasPendingTurnReview())
            {
                PostSystemNote("Review the changed files above before sending the next prompt.");
                return;
            }

            var typedText = text; // what the user actually typed — shown in the bubble + title

            // Collect file references: explicit attachments + (if enabled) the active
            // editor file. The CLI reads the files itself from the @<path> references.
            // `display` keeps {name, path} so the persisted user message can show chips
            // (matching the live bubble) instead of burying @paths in the text.
            var references = new System.Collections.Generic.List<(string path, string suffix)>();
            var display = new JArray();
            if (msg["attachments"] is JArray attachments)
            {
                foreach (var a in attachments)
                {
                    var path = a is JObject o ? o.Value<string>("path") ?? o.Value<string>("name") : a.Value<string>();
                    if (string.IsNullOrEmpty(path))
                        continue;
                    // A selection chip carries its line range as a `#L<start>[-<end>]` suffix on the
                    // path; split it back out so it stays outside the quotes of a spaced path.
                    var (refPath, refSuffix) = SplitLineSuffix(path!);
                    references.Add((refPath, refSuffix));
                    var name = (a as JObject)?.Value<string>("name");
                    display.Add(new JObject
                    {
                        ["name"] = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(path) : name,
                        ["path"] = path,
                    });
                }
            }
            if (ActiveFileEffective
                && !string.IsNullOrEmpty(_activeDocs.CurrentPath)
                && !references.Any(r => string.Equals(r.path, _activeDocs.CurrentPath, StringComparison.OrdinalIgnoreCase)))
            {
                var sel = GetActiveSelection();
                var lineSuffix = "";
                if (sel != null)
                    lineSuffix = sel.Value.top == sel.Value.bottom
                        ? "#L" + sel.Value.top
                        : "#L" + sel.Value.top + "-" + sel.Value.bottom;
                references.Add((_activeDocs.CurrentPath!, lineSuffix));
                var afName = System.IO.Path.GetFileName(_activeDocs.CurrentPath!);
                if (sel != null)
                    afName += sel.Value.top == sel.Value.bottom
                        ? ":" + sel.Value.top
                        : ":" + sel.Value.top + "-" + sel.Value.bottom;
                display.Add(new JObject { ["name"] = afName, ["path"] = _activeDocs.CurrentPath! + lineSuffix });
            }
            if (references.Count > 0)
            {
                var sb = new System.Text.StringBuilder(text);
                sb.Append("\n\nAttached files:");
                foreach (var r in references)
                    sb.Append('\n').Append(Core.CliReferenceFormatter.FormatFileReference(r.path, r.suffix));
                text = sb.ToString();
            }

            var userMsg = new JObject
            {
                ["role"] = "user",
                ["id"] = Guid.NewGuid().ToString("n"),
                ["text"] = typedText, // clean text; attachments rendered as chips
                ["ts"] = DateTime.UtcNow.ToString("o"),
            };
            if (display.Count > 0)
                userMsg["attachments"] = display;
            RecordMessage(userMsg);
            if (_history.Current.Title == "Untitled")
                _history.Current.Title = typedText.Length > 48 ? typedText.Substring(0, 48) + "…" : typedText;

            PostStatus("working");
            RunPrompt(text);
        }

        private void RunPrompt(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var options = _package.GetOptions();
            var cwd = _package.GetSolutionDirectory();
            if (string.IsNullOrEmpty(_session.Settings.Model) && !string.IsNullOrEmpty(options.DefaultModel))
                _session.Settings.Model = options.DefaultModel;

            _turnHadAssistantOutput = false;
            ResetTurnReviewState(); // fresh turn → drop the previous turn's captured baselines/reviews
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                try
                {
                    await _session.RunTurnAsync(text, options.ClaudeExecutablePath, cwd);
                }
                catch (Exception ex)
                {
                    PostError(ex.Message);
                    PostStatus("error");
                }
            }).Task.Forget();
        }

        // ── permission flow (MCP bridge §A5) ─────────────────────────────────────

        /// <summary>
        /// Called by the MCP bridge (background thread) when the CLI asks for permission.
        /// Posts a permission.request to the UI and returns a task the UI completes via
        /// permission.decision. The CLI blocks on its tools/call meanwhile.
        /// </summary>
        private Task<PermissionDecision> HandlePermissionRequestedAsync(PermissionRequest req, CancellationToken ct)
        {
            // AskUserQuestion routes through the same blocking permission hook — but instead of a
            // diff/approve card it gets an interactive question card; the user's choice is returned
            // as a deny-message (the CLI feeds that back to the model as the tool result).
            var isQuestion = req.ToolName == "AskUserQuestion";

            // Pattern-based auto-approve (Settings → "Auto-approve patterns"): commands/MCP tools
            // matching a saved pattern are allowed silently, no card. (null updatedInput → the MCP
            // bridge echoes the original input, which the CLI requires.)
            if (!isQuestion && IsAutoApprovedByPattern(req.ToolName, req.Input))
                return Task.FromResult(new PermissionDecision { Behavior = "allow" });

            // "Review edits at end of turn" (opt-in, acceptEdits): edits are deliberately routed through
            // this hook (see ClaudeSessionService.MapPermissionMode) so we can snapshot the file BEFORE
            // the CLI writes it — the CLI blocks on our reply, guaranteeing a true pre-edit baseline.
            // Capture it, auto-approve, and show the pre-decided green card; the changed files are reviewed
            // collectively at turn end. MUST come before the "Auto-accept commands" short-circuit below,
            // which would otherwise approve the edit before we snapshot it.
            if (!isQuestion
                && _session.EditsRouteThroughHook
                && EditReviewSession.IsReviewableTool(req.ToolName))
            {
                CaptureTurnBaseline(req.ToolName, req.Input);
                PostAutoApprovedEditCard(req.RequestId, req.ToolName, req.Input);
                return Task.FromResult(new PermissionDecision { Behavior = "allow" });
            }

            // "Auto-accept commands" toggle: in Auto-accept-edits mode, also auto-approve every
            // non-question tool the hook prompts for (Bash/PowerShell/MCP/…) — edits are already
            // auto-accepted by the CLI there. Gated on the mode the running process was LAUNCHED
            // with (not the live setting): only acceptEdits routes commands — not edits — through
            // the hook, so a mid-turn switch can't make us silently approve an edit prompt.
            // AskUserQuestion always stays interactive.
            if (!isQuestion
                && _package.GetOptions().AutoAcceptCommands
                && _session.LaunchedPermissionMode == "acceptEdits")
                return Task.FromResult(new PermissionDecision { Behavior = "allow" });

            // a command/MCP key exists → offer the "Always" button (add a pattern + approve)
            var canApproveAlways = !isQuestion && AutoApproveKey(req.ToolName, req.Input) != null;
            var approveAlwaysSuggestions = canApproveAlways
                ? AutoApproveSuggestions(req.ToolName, req.Input)
                : null;

            var diff0 = isQuestion ? null : BuildPermissionDiff(req.ToolName, req.Input); // file read off the UI thread

            // "Review edits in the editor" (opt-in): for file edits, build the per-hunk review off
            // the UI thread and show a file card (Open in editor / Reject all) instead of the inline
            // diff card. Falls back to the normal card when there's nothing to review.
            EditReviewSession? review = null;
            if (!isQuestion
                && _package.GetOptions().ReviewEditsInEditor
                && EditReviewSession.IsReviewableTool(req.ToolName))
            {
                try
                {
                    var built = EditReviewSession.Build(req.ToolName, req.Input, () => ReadFileSafe(req.Input));
                    if (built.HasHunks)
                        review = built;
                }
                catch { review = null; }
            }

            var pending = new PendingPermission
            {
                Tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously),
                RequestId = req.RequestId,
                ToolName = req.ToolName,
                Input = req.Input,
                Diff = diff0,
                Review = review,
            };
            _pendingPermissions[req.RequestId] = pending;
            // Bridge-lifetime token: only fires on Dispose, but keep it tidy by disposing the
            // registration when the request settles (ResolvePending) to avoid accumulation.
            pending.Registration = ct.Register(() => ResolvePending(req.RequestId, "deny", "Cancelled."));

            var diff = diff0;

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed)
                {
                    ResolvePending(req.RequestId, "deny", "Closing.");
                    return;
                }
                JObject card;
                if (isQuestion)
                {
                    card = new JObject
                    {
                        ["type"] = "question.request",
                        ["requestId"] = req.RequestId,
                        ["questions"] = (req.Input["questions"] as JArray)?.DeepClone() ?? new JArray(),
                    };
                }
                else
                {
                    card = new JObject
                    {
                        ["type"] = "permission.request",
                        ["requestId"] = req.RequestId,
                        ["toolName"] = req.ToolName,
                        ["input"] = req.Input,
                        ["canApproveAlways"] = canApproveAlways, // show the "Always" button
                    };
                    if (approveAlwaysSuggestions != null)
                        card["approveAlwaysSuggestions"] = new JArray(approveAlwaysSuggestions.ToArray());
                    if (review != null)
                    {
                        // file card: the diff is reviewed in the editor (Open in editor / Reject all)
                        card["editInEditor"] = true;
                        card["hunkCount"] = review.Hunks.Count;
                    }
                    if (diff != null)
                        card["diff"] = diff;
                }
                Post(card);
                PostStatus("waiting-permission");
            }).Task.Forget();

            // VSTHRD003: this TCS is ours (settled by ResolvePending), not foreign work.
#pragma warning disable VSTHRD003
            return pending.Tcs.Task;
#pragma warning restore VSTHRD003
        }

        private void HandlePermissionDecision(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var requestId = msg.Value<string>("requestId") ?? "";
            var allow = msg.Value<string>("behavior") == "allow";
            var p = ResolvePending(requestId, allow ? "allow" : "deny",
                allow ? null : msg.Value<string>("message"), allow ? msg["updatedInput"] as JObject : null);
            if (p == null)
                return; // unknown/stale (e.g. mock)
            _editReview.Close(requestId); // a chat Approve-all/Reject-all closes any open editor review
            // Persist the decided card so it survives a reload (transcript.load role "permission").
            RecordPermissionMessage(p, allow ? "approved" : "rejected");
            if (!allow)
                PostSystemNote("Permission denied by user"); // decision #20
            // Approving an ExitPlanMode plan exits plan mode: the next turn must NOT run with
            // --permission-mode plan (Claude couldn't execute the approved plan), so switch to
            // acceptEdits and push the new mode to the UI selector (mode.update, NOT SendSessionInit
            // which would clear the transcript). Deny keeps plan mode — the user keeps planning.
            if (allow && (p.ToolName == "ExitPlanMode" || p.ToolName == "exit_plan_mode"))
                ApplyPlanApprovedMode();
            PostStatusAfterDecision();
        }

        /// <summary>
        /// Restores the status after a permission/question settles mid-turn. Claude can have
        /// several tool calls (and thus several prompts) open at once (parallel tool use); we
        /// must stay "waiting-permission" until the *last* one is answered. Going to "working"
        /// while others are still open makes the UI expire those still-open cards.
        /// </summary>
        private void PostStatusAfterDecision()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_session.IsBusy)
                return;
            PostStatus(_pendingPermissions.IsEmpty ? "working" : "waiting-permission");
        }

        /// <summary>After an approved ExitPlanMode plan: leave plan mode → acceptEdits, persist it,
        /// and update the UI mode selector without resetting the transcript view.</summary>
        private void ApplyPlanApprovedMode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _session.Settings.PlanMode = false;       // legacy flag, kept consistent
            _session.Settings.PermissionMode = "acceptEdits";
            _package.GetOptions().PermissionModeString = "acceptEdits";
            _package.SaveOptions();
            Post(new JObject
            {
                ["type"] = "mode.update",
                ["permissionMode"] = "acceptEdits",
                ["planMode"] = false,
            });
        }

        /// <summary>AskUserQuestion answer: the chosen option(s)/free-text are returned to the CLI
        /// as a deny-message (which the model receives as the tool result), then persisted.</summary>
        private void HandleQuestionAnswer(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var requestId = msg.Value<string>("requestId") ?? "";
            var answers = msg["answers"] as JArray ?? new JArray();
            var p = ResolvePending(requestId, "deny", FormatQuestionAnswers(answers));
            if (p == null)
                return; // unknown/stale (e.g. mock)
            RecordQuestionMessage(p, answers);
            PostStatusAfterDecision();
        }

        /// <summary>Renders the user's answers as the tool-result text the model will read.</summary>
        private static string FormatQuestionAnswers(JArray answers)
        {
            var sb = new System.Text.StringBuilder("The user answered:");
            foreach (var a in answers.OfType<JObject>())
            {
                var header = a.Value<string>("header");
                var question = a.Value<string>("question");
                var label = !string.IsNullOrEmpty(header) ? header! : (question ?? "Answer");
                var parts = new System.Collections.Generic.List<string>();
                if (a["selected"] is JArray sel)
                    foreach (var s in sel)
                    {
                        var v = s.Value<string>();
                        if (!string.IsNullOrEmpty(v)) parts.Add("\"" + v + "\"");
                    }
                var custom = a.Value<string>("custom");
                if (!string.IsNullOrWhiteSpace(custom)) parts.Add("\"" + custom!.Trim() + "\"");
                sb.Append("\n- ").Append(label).Append(": ")
                  .Append(parts.Count > 0 ? string.Join(", ", parts) : "(no selection)");
            }
            return sb.ToString();
        }

        /// <summary>Persists an answered question card (role "question") so it survives a reload,
        /// replacing the redundant tool message of the same tool_use_id.</summary>
        private void RecordQuestionMessage(PendingPermission p, JArray answers)
        {
            var rec = new JObject
            {
                ["role"] = "question",
                ["id"] = p.RequestId,
                ["questions"] = (p.Input["questions"] as JArray)?.DeepClone() ?? new JArray(),
                ["answers"] = answers.DeepClone(),
                ["status"] = "answered",
                ["ts"] = DateTime.UtcNow.ToString("o"),
            };
            lock (_history)
            {
                var msgs = _history.Current.Messages;
                for (int i = msgs.Count - 1; i >= 0; i--)
                    if (msgs[i].Value<string>("role") == "tool" && msgs[i].Value<string>("id") == p.RequestId)
                        msgs.RemoveAt(i);
                msgs.Add(rec);
                _history.Current.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Settles a pending permission once: removes it, disposes its ct registration,
        /// completes the TCS. Returns the removed entry (null if unknown/already settled).</summary>
        private PendingPermission? ResolvePending(string requestId, string behavior, string? message, JObject? updatedInput = null)
        {
            if (!_pendingPermissions.TryRemove(requestId, out var p))
                return null;
            p.Registration.Dispose();
            p.Tcs.TrySetResult(new PermissionDecision { Behavior = behavior, Message = message, UpdatedInput = updatedInput });
            return p;
        }

        /// <summary>Records a decided permission card (role "permission") into the history, replacing
        /// the redundant tool message of the same tool_use_id (the permission card represents it).</summary>
        private void RecordPermissionMessage(PendingPermission p, string status)
        {
            var rec = new JObject
            {
                ["role"] = "permission",
                ["id"] = p.RequestId,
                ["toolName"] = p.ToolName,
                ["input"] = p.Input,
                ["status"] = status,
                ["ts"] = DateTime.UtcNow.ToString("o"),
            };
            if (p.Diff != null)
                rec["diff"] = p.Diff;
            lock (_history)
            {
                var msgs = _history.Current.Messages;
                for (int i = msgs.Count - 1; i >= 0; i--)
                    if (msgs[i].Value<string>("role") == "tool" && msgs[i].Value<string>("id") == p.RequestId)
                        msgs.RemoveAt(i);
                msgs.Add(rec);
                _history.Current.UpdatedAtUtc = DateTime.UtcNow;
                if (status == "approved")
                    _recordedPermissions[p.RequestId] = rec; // upgradeable by tool.result
            }
        }

        private void DenyAllPendingPermissions(string reason)
        {
            foreach (var key in _pendingPermissions.Keys)
            {
                // Settle the host-side task AND tell the UI to close the (now orphaned) card, so a
                // timed-out/abandoned permission or question card doesn't stay open and interactive.
                if (ResolvePending(key, "deny", reason) != null)
                {
                    // Close() is thread-safe — it marshals to the UI thread itself (this can run on a
                    // CLI background thread). The VSTHRD010 analyzer can't see that, hence the suppression.
#pragma warning disable VSTHRD010
                    _editReview.Close(key); // tear down any open editor-review adornments
#pragma warning restore VSTHRD010
                    Post(new JObject { ["type"] = "permission.expire", ["requestId"] = key });
                }
            }
        }

        // ── inline edit review (opt-in: "Review edits in the editor") ────────────

        /// <summary>web→host editReview.open: open the edited file with the inline red/green diff +
        /// per-hunk Accept/Reject adornments. No-op if the request is stale or not an editor review.</summary>
        private void HandleEditReviewOpen(string requestId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(requestId)
                || !_pendingPermissions.TryGetValue(requestId, out var p) || p.Review == null)
                return;
            try { _editReview.Open(requestId, p.Review); }
            catch (Exception ex) { PostError("Could not open the edit for review: " + ex.Message); }
        }

        /// <summary>Called by the EditReviewController once every hunk of a request is decided.
        /// Reconstructs the tool input from the accepted hunks and settles the blocked CLI call:
        /// allow + updatedInput when anything was accepted, otherwise deny. UI thread.</summary>
        private void FinalizeEditReview(string requestId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_pendingPermissions.TryGetValue(requestId, out var pending) || pending.Review == null)
                return;
            var updated = pending.Review.BuildUpdatedInput(); // null = nothing accepted → deny
            var allow = updated != null;
            var p = ResolvePending(requestId, allow ? "allow" : "deny",
                allow ? null : "All edits rejected by user", updated);
            if (p == null)
                return; // already settled (raced with turn end / a chat decision)
            _editReview.Close(requestId);
            // Persist what was ACTUALLY applied (the accepted-hunks reconstruction), so a reload doesn't
            // show the full original diff for a partial accept.
            if (allow && updated != null)
            {
                p.Input = updated;
                p.Diff = BuildPermissionDiff(p.ToolName, updated);
            }
            RecordPermissionMessage(p, allow ? "approved" : "rejected");
            if (!allow)
                PostSystemNote("Edit rejected by user");
            Post(new JObject
            {
                ["type"] = "permission.finalize",
                ["requestId"] = requestId,
                ["status"] = allow ? "approved" : "rejected",
            });
            PostStatusAfterDecision();
        }

        /// <summary>Reads the edited file's current text (background thread; "" if unreadable).
        /// Used to anchor Edit/MultiEdit hunks to real file lines and as the Write old-text.</summary>
        private static string ReadFileSafe(JObject input)
        {
            try
            {
                var path = input.Value<string>("file_path");
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    return System.IO.File.ReadAllText(path);
            }
            catch { }
            return "";
        }

        // ── "Review edits at end of turn" (opt-in, acceptEdits) ──────────────────

        /// <summary>Snapshots a file's pristine (pre-turn) content the FIRST time it is edited this turn.
        /// Runs on the MCP background thread while the CLI is blocked on our reply, so the read is
        /// guaranteed to happen before the write lands. A file we can't snapshot reliably (unreadable, or
        /// an Edit/MultiEdit whose old_string isn't in the read content ⇒ the write already landed) is
        /// recorded as "skip" and never reviewed — so a bad read can't become a revert-to-empty target.</summary>
        private void CaptureTurnBaseline(string toolName, JObject input)
        {
            var path = input.Value<string>("file_path");
            if (string.IsNullOrEmpty(path))
                return;
            lock (_turnReviewLock)
            {
                if (_turnEditBaselines.ContainsKey(path!) || _turnBaselineSkip.Contains(path!))
                    return; // first-touch only
                try
                {
                    if (!System.IO.File.Exists(path))
                    {
                        _turnEditBaselines[path!] = ""; // a file created this turn → empty baseline (isNew)
                        return;
                    }
                    var content = System.IO.File.ReadAllText(path);
                    if (!BaselineMatchesEdit(toolName, input, content))
                    {
                        _turnBaselineSkip.Add(path!); // lost the write race → don't trust this as pristine
                        return;
                    }
                    _turnEditBaselines[path!] = content;
                }
                catch { _turnBaselineSkip.Add(path!); }
            }
        }

        /// <summary>For Edit/MultiEdit the pristine file must still contain the first old_string; if not,
        /// the write already landed and the read is post-edit. Write replaces the whole file → always ok.</summary>
        private static bool BaselineMatchesEdit(string toolName, JObject input, string content)
        {
            string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
            var c = Norm(content);
            if (toolName == "Edit")
            {
                var os = input.Value<string>("old_string") ?? "";
                return os.Length == 0 || c.IndexOf(Norm(os), StringComparison.Ordinal) >= 0;
            }
            if (toolName == "MultiEdit")
            {
                var first = (input["edits"] as JArray)?.FirstOrDefault() as JObject;
                var os = first?.Value<string>("old_string") ?? "";
                return os.Length == 0 || c.IndexOf(Norm(os), StringComparison.Ordinal) >= 0;
            }
            return true; // Write
        }

        /// <summary>Posts the pre-decided green "approved" card for an edit auto-approved by the review-at-
        /// turn-end hook (same shape the acceptEdits/bypass tool.use path uses), and records it so a later
        /// tool.result upgrades it to Applied/Failed. Marshals to the UI thread.</summary>
        private void PostAutoApprovedEditCard(string requestId, string toolName, JObject input)
        {
            var diff = BuildPermissionDiff(toolName, input);
            var inputClone = (JObject)input.DeepClone();
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed)
                    return;
                var card = new JObject
                {
                    ["type"] = "permission.request",
                    ["requestId"] = requestId,
                    ["toolName"] = toolName,
                    ["input"] = inputClone.DeepClone(),
                    ["autoApproved"] = true,
                };
                if (diff != null)
                    card["diff"] = diff;
                Post(card);
                RecordPermissionMessage(
                    new PendingPermission { RequestId = requestId, ToolName = toolName, Input = inputClone, Diff = diff },
                    "approved");
            }).Task.Forget();
        }

        /// <summary>Builds the cumulative per-file review from the captured baselines vs. the current disk
        /// content and posts the changed-files list above the composer. Called at turn end (and on stop).
        /// Idempotent — safe if it runs from both paths.</summary>
        private void BuildAndPostTurnReviewList()
        {
            if (!_session.EditsRouteThroughHook)
                return; // feature off / not acceptEdits this turn

            System.Collections.Generic.Dictionary<string, string> baselines;
            lock (_turnReviewLock)
            {
                if (_turnEditBaselines.Count == 0)
                    return;
                baselines = new System.Collections.Generic.Dictionary<string, string>(_turnEditBaselines, StringComparer.OrdinalIgnoreCase);
            }

            var files = new JArray();
            var built = new System.Collections.Generic.Dictionary<string, TurnReview>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in baselines)
            {
                var path = kv.Key;
                var baseline = kv.Value;
                string current;
                bool exists;
                try
                {
                    exists = System.IO.File.Exists(path);
                    current = exists ? System.IO.File.ReadAllText(path) : "";
                }
                catch { continue; } // unreadable now → skip
                if (!exists) continue; // edited then deleted this turn → nothing to review on disk

                // Model the whole-file change as a Write: old = baseline, new = current disk content.
                var input = new JObject { ["file_path"] = path, ["content"] = current };
                EditReviewSession session;
                try { session = EditReviewSession.Build("Write", input, () => baseline); }
                catch { continue; }
                if (!session.HasHunks) continue; // edited then reverted to identical → drop

                built[path] = new TurnReview { Session = session, Baseline = baseline, IsNew = baseline.Length == 0 };
                files.Add(TurnReviewFileEntry(path, session));
            }

            lock (_turnReviewLock)
            {
                _turnReviews.Clear();
                foreach (var kv in built)
                    _turnReviews[kv.Key] = kv.Value;
            }
            if (files.Count == 0)
                return;
            Post(new JObject { ["type"] = "editReview.turnList", ["files"] = files });

            // Any changed file that's ALREADY open gets its inline review attached immediately, so the
            // green/red diff shows without the user having to click the chip first (files not open are
            // attached on demand when their chip is clicked). AttachIfOpen marshals to the UI thread
            // internally, so this is safe from the session-event background thread (the analyzer can't see
            // the marshaling — hence the suppression; FinalizeTurnFileReview only runs on the UI thread).
#pragma warning disable VSTHRD010
            foreach (var kv in built)
                _editReview.AttachIfOpen(kv.Key, kv.Value.Session, () => FinalizeTurnFileReview(kv.Key), () => PostTurnFileState(kv.Key));
#pragma warning restore VSTHRD010
        }

        /// <summary>Re-posts the pending changed-files list after a WebView reload so the UI (and the gate)
        /// survive the reload. No-op when nothing is pending.</summary>
        private void RepostTurnReviewList()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            JArray files;
            lock (_turnReviewLock)
            {
                if (_turnReviews.Count == 0)
                    return;
                files = new JArray();
                foreach (var kv in _turnReviews)
                    files.Add(TurnReviewFileEntry(kv.Key, kv.Value.Session));
            }
            Post(new JObject { ["type"] = "editReview.turnList", ["files"] = files });
        }

        private bool HasPendingTurnReview()
        {
            lock (_turnReviewLock)
                return _turnReviews.Count > 0;
        }

        /// <summary>Builds a changed-files list entry with the still-open added/removed line counts (a file
        /// leaves the list only when fully reviewed, so every listed file's hunks are still pending).</summary>
        private static JObject TurnReviewFileEntry(string path, EditReviewSession session)
        {
            int added = 0, removed = 0;
            foreach (var h in session.Hunks)
            {
                added += h.AddedLines.Count;
                removed += h.DeletedLines.Count;
            }
            return new JObject
            {
                ["path"] = path,
                ["name"] = System.IO.Path.GetFileName(path),
                ["added"] = added,
                ["removed"] = removed,
                ["allDecided"] = session.AllDecided, // chip's Finish button is enabled only when true
            };
        }

        /// <summary>Clears all captured baselines and pending reviews (new turn / session change).</summary>
        private void ResetTurnReviewState()
        {
            lock (_turnReviewLock)
            {
                _turnEditBaselines.Clear();
                _turnBaselineSkip.Clear();
                _turnReviews.Clear();
            }
        }

        /// <summary>web→host editReview.openTurnFile: open the changed file in the in-editor review with the
        /// pre-turn baseline swapped in (read-only) and per-hunk Accept/Reject. UI thread.</summary>
        private void HandleOpenTurnFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TurnReview? review;
            lock (_turnReviewLock)
                _turnReviews.TryGetValue(path ?? "", out review);
            if (review == null)
                return; // stale (already decided / cleared)
            try { _editReview.OpenForPath(path!, review.Session, () => FinalizeTurnFileReview(path!), () => PostTurnFileState(path!)); }
            catch (Exception ex) { PostError("Could not open the changed file for review: " + ex.Message); }
        }

        /// <summary>web→host editReview.finishTurnFile: the user pressed Finish (editor toolbar or the chat
        /// chip) to complete a file whose every change is decided → commit it and drop it from the list.
        /// Ignored unless every hunk is decided (the chip's button is disabled until then anyway). UI thread.</summary>
        private void HandleFinishTurnFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TurnReview? review;
            lock (_turnReviewLock)
                _turnReviews.TryGetValue(path ?? "", out review);
            if (review == null || !review.Session.AllDecided)
                return;
            FinalizeTurnFileReview(path!);
        }

        /// <summary>Re-posts one file's "all changes decided?" flag so the chat chip can enable/disable its
        /// Finish button. Fired by the adorner on every decide/reset (via the onStateChanged callback).</summary>
        private void PostTurnFileState(string path)
        {
            bool allDecided;
            lock (_turnReviewLock)
            {
                if (!_turnReviews.TryGetValue(path ?? "", out var review))
                    return;
                allDecided = review.Session.AllDecided;
            }
            Post(new JObject { ["type"] = "editReview.turnFileState", ["path"] = path, ["allDecided"] = allDecided });
        }

        /// <summary>Called by the EditReviewController once every hunk of a post-turn file is decided:
        /// writes the accepted/rejected reconstruction to disk (or deletes a fully-rejected new file),
        /// drops the file from the list, and unblocks the composer once the list is empty. UI thread.</summary>
        private void FinalizeTurnFileReview(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TurnReview? review;
            lock (_turnReviewLock)
                _turnReviews.TryGetValue(path ?? "", out review);
            if (review == null)
                return;

            var updated = review.Session.BuildUpdatedInput(); // Write → { content }; null = all rejected
            var finalContent = updated != null ? (updated.Value<string>("content") ?? "") : review.Baseline;
            var deleteFile = review.IsNew && updated == null; // a created file, rejected in full → remove it
            // Open in the editor → commit through the buffer; not open (e.g. finished from the chat chip after
            // the tab was closed) → write the reconstruction straight to disk.
            if (_editReview.IsReviewing(path!))
                _editReview.CommitPath(path!, finalContent, deleteFile);
            else
                RevertOnDisk(path!, finalContent, deleteFile);

            lock (_turnReviewLock)
                _turnReviews.Remove(path!);
            Post(new JObject { ["type"] = "editReview.turnFileDone", ["path"] = path });
        }

        /// <summary>web→host editReview.keepAll: accept every changed file as-is (no disk changes), close any
        /// open review, and clear the list + gate.</summary>
        private void HandleKeepAllTurnReviews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _editReview.CloseAllTurnReviews(); // restore any open review buffers to the applied content
            ResetTurnReviewState();
            Post(new JObject { ["type"] = "editReview.turnListClear" });
        }

        /// <summary>web→host editReview.discardAll: revert every changed file to its pre-turn baseline
        /// (delete files created this turn), then clear the list + gate. Files currently open in a review go
        /// through the controller (buffer + save); the rest are written to disk directly.</summary>
        private void HandleDiscardAllTurnReviews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            System.Collections.Generic.List<(string path, string baseline, bool isNew)> items;
            lock (_turnReviewLock)
                items = _turnReviews.Select(kv => (kv.Key, kv.Value.Baseline, kv.Value.IsNew)).ToList();
            foreach (var (path, baseline, isNew) in items)
            {
                if (_editReview.IsReviewing(path))
                    _editReview.CommitPath(path, baseline, isNew); // open review → write baseline to buffer + save, or delete+close
                else
                    RevertOnDisk(path, baseline, isNew);
            }
            ResetTurnReviewState();
            Post(new JObject { ["type"] = "editReview.turnListClear" });
        }

        /// <summary>Reverts a not-currently-reviewed file on disk: restore its pre-turn baseline, or delete a
        /// file created this turn. Best-effort.</summary>
        private static void RevertOnDisk(string path, string baseline, bool deleteFile)
        {
            try
            {
                if (deleteFile)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                else
                {
                    System.IO.File.WriteAllText(path, baseline);
                }
            }
            catch { /* best effort */ }
        }

        // ── pattern-based auto-approve (Settings + "Always" button) ──────────────

        /// <summary>The string a permission request is matched against: the command for
        /// Bash/PowerShell/shell tools, or the tool name for MCP tools. null = not eligible
        /// (e.g. Edit/Write use the diff card, not pattern approval).</summary>
        private static string? AutoApproveKey(string toolName, JObject input)
        {
            var cmd = input?.Value<string>("command")
                      ?? input?.Value<string>("cmd")
                      ?? input?.Value<string>("script");
            if (!string.IsNullOrWhiteSpace(cmd))
                return cmd!.Trim();
            if (!string.IsNullOrEmpty(toolName) && toolName.StartsWith("mcp__", StringComparison.Ordinal))
                return toolName;
            return null;
        }

        /// <summary>Suggested patterns to pre-fill the "Always" popover: the MCP tool name, or
        /// the command split into its top-level sub-commands (each becomes its own pattern).</summary>
        private static List<string> AutoApproveSuggestions(string toolName, JObject input)
        {
            if (!string.IsNullOrEmpty(toolName) && toolName.StartsWith("mcp__", StringComparison.Ordinal))
                return new List<string> { toolName };
            var cmd = input?.Value<string>("command") ?? input?.Value<string>("cmd") ?? input?.Value<string>("script");
            if (string.IsNullOrWhiteSpace(cmd))
                return new List<string>();
            // Real sub-commands only (variable assignments dropped); each becomes a reusable
            // glob pattern with quoted argument values wildcarded.
            var commands = ShellCommandSplitter.ExtractCommands(cmd!);
            if (commands.Count == 0)
                commands = new List<string> { cmd!.Trim() };
            return commands.Select(ShellCommandSplitter.Wildcardize)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<string> AutoApprovePatterns() =>
            (_package.GetOptions().AutoApprovePatterns ?? new List<string>())
                .Select(p => (p ?? "").Trim())
                .Where(p => p.Length > 0);

        private bool IsAutoApprovedByPattern(string toolName, JObject input)
        {
            var patterns = AutoApprovePatterns().ToList();
            if (patterns.Count == 0)
                return false;

            // MCP tools match on the tool name.
            if (!string.IsNullOrEmpty(toolName) && toolName.StartsWith("mcp__", StringComparison.Ordinal))
                return patterns.Any(p => ShellCommandSplitter.MatchesGlob(toolName, p));

            var cmd = input?.Value<string>("command") ?? input?.Value<string>("cmd") ?? input?.Value<string>("script");
            if (string.IsNullOrWhiteSpace(cmd))
                return false;

            // Auto-approve only when EVERY meaningful sub-command is covered by an approved pattern
            // (see ShellCommandSplitter.IsCommandCovered) — never silently approve a chain just
            // because one of several &-joined parts matches.
            return ShellCommandSplitter.IsCommandCovered(cmd!, patterns);
        }

        /// <summary>Appends a pattern to the saved list (skips duplicates) and persists. UI thread.</summary>
        private void AddAutoApprovePattern(string pattern)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pattern = pattern?.Trim() ?? "";
            if (pattern.Length == 0)
                return;
            var existing = AutoApprovePatterns().ToList();
            if (existing.Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)))
                return; // already covered
            existing.Add(pattern);
            _package.GetOptions().AutoApprovePatterns = existing;
            _package.SaveOptions();
        }

        /// <summary>"Always" button on a permission card: approve this call AND remember the
        /// (user-edited) patterns from the popover. Falls back to the auto-derived suggestions
        /// when no explicit list is supplied.</summary>
        private void HandleApproveAlways(string requestId, JArray? patterns)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _pendingPermissions.TryGetValue(requestId, out var pending);

            var toAdd = new List<string>();
            if (patterns != null)
                toAdd.AddRange(patterns.Select(p => p.Value<string>() ?? "").Where(p => p.Trim().Length > 0));
            else if (pending != null)
                toAdd.AddRange(AutoApproveSuggestions(pending.ToolName, pending.Input));

            foreach (var pat in toAdd)
                AddAutoApprovePattern(pat);
            if (toAdd.Count > 0)
                PostSystemNote("Auto-approving from now on: " + string.Join(", ", toAdd.Select(p => p.Trim())));

            var p = ResolvePending(requestId, "allow", null); // null updatedInput → bridge echoes input
            if (p == null)
                return; // unknown/stale
            RecordPermissionMessage(p, "approved");
            if (_session.IsBusy)
                PostStatus("working");
        }

        /// <summary>Inline-diff payload for Edit/Write (v1); other tools show their JSON input.</summary>
        private static JObject? BuildPermissionDiff(string toolName, JObject input)
        {
            try
            {
                var path = input.Value<string>("file_path");
                if (string.IsNullOrEmpty(path))
                    return null;
                if (toolName == "Edit")
                {
                    var oldText = input.Value<string>("old_string") ?? "";
                    return new JObject
                    {
                        ["path"] = path,
                        ["oldText"] = oldText,
                        ["newText"] = input.Value<string>("new_string") ?? "",
                        ["startLine"] = FileStartLine(path!, oldText), // real file line of the edit
                    };
                }
                if (toolName == "Write")
                {
                    var oldText = "";
                    try { if (System.IO.File.Exists(path)) oldText = System.IO.File.ReadAllText(path); } catch { }
                    return new JObject { ["path"] = path, ["oldText"] = oldText, ["newText"] = input.Value<string>("content") ?? "", ["startLine"] = 1 };
                }
            }
            catch { }
            return null;
        }

        /// <summary>1-based file line where <paramref name="oldString"/> begins (1 if not found/unreadable).</summary>
        private static int FileStartLine(string path, string oldString)
        {
            try
            {
                if (string.IsNullOrEmpty(oldString) || !System.IO.File.Exists(path))
                    return 1;
                var content = System.IO.File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");
                var needle = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
                var idx = content.IndexOf(needle, StringComparison.Ordinal);
                if (idx < 0)
                    return 1;
                int line = 1;
                for (int i = 0; i < idx; i++)
                    if (content[i] == '\n') line++;
                return line;
            }
            catch { return 1; }
        }

        /// <summary>Guards turn-disrupting actions: while a turn runs (working or awaiting a permission
        /// decision) switching/clearing the session would orphan the live process; while a post-turn edit
        /// review is still pending it would silently abandon that review. Posts a note and returns true so
        /// the caller bails. (The UI also disables these buttons — this is the authoritative backstop.)</summary>
        private bool TurnRunningBlocks(string action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_session.IsBusy)
            {
                PostSystemNote("Stop the current turn first to " + action + ".");
                return true;
            }
            if (HasPendingTurnReview())
            {
                PostSystemNote("Review the changed files above before you " + action + ".");
                return true;
            }
            return false;
        }

        private void HandleSessionNew()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (TurnRunningBlocks("start a new chat"))
                return;
            lock (_history)
                _history.StartNew();
            _session.ResetSession();
            _sessionStartAnnounced = false;
            _activeFileSessionEnabled = ActiveFileDefaultOn; // per-session override resets to the option default
            SendSessionInit();
            SendActiveFile();
            SaveHistory();
        }

        /// <summary>
        /// First render after `ready`: re-attach the in-progress session (WebView reload),
        /// restore the workspace's most recent session (if enabled), or start empty.
        /// </summary>
        private void SendInitialSession()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SessionRecord? restore = null;
            lock (_history)
            {
                if (_history.Current.Messages.Count > 0)
                {
                    restore = _history.Current; // WebView reloaded mid-session
                }
                else if (_package.GetOptions().RestoreLastSession)
                {
                    var latest = _history.ToSessionList().FirstOrDefault() as JObject;
                    var id = latest?.Value<string>("id");
                    if (!string.IsNullOrEmpty(id))
                        restore = _history.Load(id!);
                }
            }

            if (restore == null)
            {
                SendSessionInit();
                return;
            }

            AttachToRecord(restore);
            SendSessionInit();      // session.init first — it resets the transcript view…
            SendTranscript(restore); // …then the messages land on the clean slate
            RepostTurnReviewList(); // WebView reloaded mid-review → restore the list + the send gate
        }

        private void HandleSessionLoad(string sessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (TurnRunningBlocks("switch sessions"))
                return;
            SessionRecord? record;
            lock (_history)
                record = _history.Load(sessionId);
            if (record == null)
                return;

            AttachToRecord(record);
            _activeFileSessionEnabled = ActiveFileDefaultOn; // per-session override resets to the option default
            SendSessionInit();
            SendActiveFile();
            SendTranscript(record);
        }

        /// <summary>Rename modal in the UI (§5.1) — UI updates its title optimistically.</summary>
        private void HandleSessionRename(string? sessionId, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            title = title.Trim();
            if (title.Length == 0)
                return;
            if (title.Length > 120)
                title = title.Substring(0, 120);

            bool renamed;
            lock (_history)
                renamed = _history.Rename(
                    string.IsNullOrEmpty(sessionId) ? _history.Current.Id : sessionId!, title);
            if (renamed)
                SaveHistory();
        }

        /// <summary>
        /// Deletes a session from the history (web → host <c>session.delete</c>, confirmed in the UI).
        /// If the active session was deleted, behaves like "new chat" (fresh empty session + re-init).
        /// Always re-sends <c>session.list</c> so an open history popover refreshes in place.
        /// </summary>
        private void HandleSessionDelete(string? sessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(sessionId))
                return;

            // Deleting the *active* session while its turn runs would behave like "new chat" mid-turn
            // (orphaned process). Deleting any other session is harmless.
            if (_session.IsBusy)
            {
                string currentId;
                lock (_history)
                    currentId = _history.Current.Id;
                if (sessionId == currentId)
                {
                    PostSystemNote("Stop the current turn first to delete the active session.");
                    return;
                }
            }

            bool deleted, wasCurrent;
            lock (_history)
                (deleted, wasCurrent) = _history.Delete(sessionId!);
            if (!deleted)
                return;

            SaveHistory();

            if (wasCurrent)
            {
                _session.ResetSession();
                _sessionStartAnnounced = false;
                _activeFileSessionEnabled = ActiveFileDefaultOn; // per-session override resets to the option default
                // The deleted session's pending post-turn review goes with it (edits stay on disk).
                if (HasPendingTurnReview())
                {
                    _editReview.CloseAllTurnReviews();
                    ResetTurnReviewState();
                }
                SendSessionInit();                // resets the transcript view to the fresh session (clears the UI review list)
                SendActiveFile();
            }

            JArray sessions;
            lock (_history)
                sessions = _history.ToSessionList();
            Post(new JObject { ["type"] = "session.list", ["sessions"] = sessions });
        }

        private void AttachToRecord(SessionRecord record)
        {
            if (record.HasCliSession)
                _session.AttachSession(record.Id);
            else
                _session.ResetSession();
            _sessionStartAnnounced = true; // resumed sessions don't re-announce
        }

        private void SendTranscript(SessionRecord record)
        {
            JArray messages;
            lock (_history)
            {
                messages = new JArray();
                foreach (var m in record.Messages)
                    messages.Add(m.DeepClone());
            }

            Post(new JObject
            {
                ["type"] = "transcript.load",
                ["sessionId"] = record.Id,
                ["title"] = record.Title,
                ["messages"] = messages,
            });
        }

        private void HandleThemeSetMode(string mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var normalized = mode is "dark" or "light" ? mode : "auto";
            _package.GetOptions().ThemeModeString = normalized;
            _package.SaveOptions();
            SendTheme();
        }

        /// <summary>Persists the custom brand/accent color (CSS hex, or empty to reset to the
        /// per-theme default). The UI applies it optimistically; here we just store it so it
        /// survives new chats + VS restarts. Invalid values are coerced to empty (= default).</summary>
        private void HandleAccentSet(string color)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package.GetOptions().AccentColor = NormalizeHexColor(color);
            _package.SaveOptions();
        }

        /// <summary>Returns a normalized <c>#rrggbb</c> string, or "" if not a valid hex color.</summary>
        private static string NormalizeHexColor(string color)
        {
            color = (color ?? "").Trim();
            if (color.Length == 0)
                return "";
            return Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
                ? color.ToLowerInvariant()
                : "";
        }

        private void HandleVerbositySet(string level)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var normalized = level is "compact" or "detailed" ? level : "normal";
            _package.GetOptions().VerbosityString = normalized;
            _package.SaveOptions();
        }

        private void HandleSlashRun(string command, string? args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            switch (command)
            {
                case "/clear":
                    HandleSessionNew();
                    break;
                case "/login":
                    PostSyntheticAssistant(
                        "Sign-in happens through the Claude Code CLI (it needs a browser):\n\n" +
                        "1. Open a terminal\n2. Run `claude /login`\n3. Come back and start a new chat.");
                    break;
                case "/help":
                    // The CLI rejects /help in headless mode — answer it host-side instead.
                    PostSyntheticAssistant(BuildHelpText());
                    break;
                default:
                    if (_session.IsBusy)
                        return;
                    var prompt = string.IsNullOrEmpty(args) ? command : command + " " + args;
                    RecordMessage(new JObject
                    {
                        ["role"] = "user",
                        ["id"] = Guid.NewGuid().ToString("n"),
                        ["text"] = prompt,
                        ["ts"] = DateTime.UtcNow.ToString("o"),
                    });
                    PostStatus("working");
                    RunPrompt(prompt);
                    break;
            }
        }

        // ── remote control (interactive `claude --remote-control` in the VS terminal) ──

        private void HandleRemoteStart()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_session.IsBusy)
            {
                PostRemoteTerminalState("error", "A turn is still running — stop it before starting remote control.");
                return;
            }
            if (HasPendingTurnReview())
            {
                PostRemoteTerminalState("error", "Review the changed files above before starting remote control.");
                return;
            }
            if (RemoteSessionActive)
            {
                // already running (e.g. UI reloaded) — re-announce the current state
                PostRemoteTerminalState("ready", RemoteRunningMessage());
                return;
            }

            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            if (exe == null)
            {
                PostRemoteTerminalState("error", "Claude CLI not found — set the executable path via the gear menu → Advanced options.");
                return;
            }

            // Resume the current chat's CLI session if it has one (≥1 turn); otherwise start fresh.
            string? resumeId;
            lock (_history)
                resumeId = _history.Current.HasCliSession ? _history.Current.Id : null;

            var cwd = _package.GetSolutionDirectory();
            // remote control refuses an untrusted workspace — pre-accept it (best-effort).
            ClaudeWorkspaceTrust.EnsureTrusted(cwd);

            if (_remoteTerminal == null)
            {
                _remoteTerminal = new RemoteTerminalLauncher();
                _remoteTerminal.Ended += OnRemoteTerminalEnded;
            }

            PostRemoteTerminalState("starting");

            var launcher = _remoteTerminal;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                bool ok;
                try { ok = await launcher.StartAsync(exe, resumeId, cwd, _package.DisposalToken); }
                catch { ok = false; }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ok)
                    PostRemoteTerminalState("ready", RemoteRunningMessage(resumeId != null));
                else
                    PostRemoteTerminalState("error",
                        "Couldn't start remote control" + (launcher.LastError != null ? ": " + launcher.LastError : "."));
            }).Task.Forget();
        }

        /// <summary>
        /// Ends the interactive remote session (closes the terminal). The launcher's Ended event
        /// then imports the now-advanced conversation and reloads it (see <see cref="OnRemoteTerminalEnded"/>).
        /// </summary>
        private void HandleRemoteStop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var launcher = _remoteTerminal;
            if (launcher == null || !launcher.IsActive)
            {
                PostRemoteTerminalState("stopped");
                return;
            }
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await launcher.EndAsync(_package.DisposalToken); }
                catch { /* Ended still fires; the import/unlock happens there */ }
            }).Task.Forget();
        }

        /// <summary>
        /// Fired (off the UI thread) when the remote terminal/console closes — imports the CLI sessions
        /// touched since start and loads the most recent one (unlocking the UI first). For a resumed
        /// session this is the same conversation, now advanced by whatever happened on the phone/web.
        /// </summary>
        private void OnRemoteTerminalEnded()
        {
            // small slack: the pre-created session file may predate StartedUtc slightly
            var since = (_remoteTerminal?.StartedUtc ?? DateTime.UtcNow).AddSeconds(-10);

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var cwd = _package.GetSolutionDirectory() ?? ""; // UI-thread-only — capture here

                await TaskScheduler.Default;
                await Task.Delay(500).ConfigureAwait(false); // let the CLI flush its session files

                var imported = new System.Collections.Generic.List<ImportedCliSession>();
                string? discoveryError = null;
                try
                {
                    var projectDir = CliSessionReader.GetProjectDirectory(cwd);
                    if (projectDir != null)
                    {
                        foreach (var file in CliSessionReader.FindSessionsSince(projectDir, since))
                        {
                            var session = CliSessionReader.ImportTranscript(file);
                            if (session != null)
                                imported.Add(session);
                        }
                    }
                }
                catch (Exception ex)
                {
                    discoveryError = ex.Message;
                }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                PostRemoteTerminalState("stopped"); // always unlock the UI first

                if (discoveryError != null)
                {
                    PostError("Importing the remote session failed: " + discoveryError);
                    return;
                }

                if (imported.Count == 0)
                {
                    PostSystemNote("Remote control ended — no changes to import");
                    return;
                }

                SessionRecord? latest = null;
                lock (_history)
                {
                    foreach (var s in imported)
                    {
                        var record = _history.Import(s.SessionId, s.Title, s.Messages, s.UpdatedAtUtc, s.ContextTokens);
                        if (latest == null || record.UpdatedAtUtc > latest.UpdatedAtUtc)
                            latest = record;
                    }
                    _history.Load(latest!.Id);
                }

                AttachToRecord(latest!);
                _activeFileSessionEnabled = ActiveFileDefaultOn; // per-session override resets to the option default
                SendSessionInit();
                SendActiveFile();
                SendTranscript(latest!);
                PostSystemNote(imported.Count == 1
                    ? "Remote session imported"
                    : $"Remote control ended — {imported.Count} sessions imported (latest loaded)");
                SaveHistory();
            }).Task.Forget();
        }

        /// <summary>True while an interactive remote-control session is open in the terminal/console.</summary>
        private bool RemoteSessionActive => _remoteTerminal?.IsActive == true;

        /// <summary>User-facing status line shown in the (locked) remote panel while a session runs.</summary>
        private string RemoteRunningMessage(bool resumed = true)
        {
            return (resumed ? "This chat is now live in the terminal. " : "A new remote session is live in the terminal. ")
                + "Open the link shown there from the Claude app or claude.ai/code. "
                + "When you're done, close the terminal or click \"End remote session\" to return here.";
        }

        private void PostRemoteTerminalState(string state, string? message = null)
        {
            var msg = new JObject
            {
                ["type"] = "remote.state",
                ["state"] = state,
                ["inTerminal"] = true,
            };
            if (message != null)
                msg["message"] = message;
            Post(msg);
        }

        /// <summary>Host-side /help (the headless CLI rejects it, decision in NOTES).</summary>
        private string BuildHelpText()
        {
            string resumeHint;
            lock (_history)
                resumeHint = _history.Current.HasCliSession
                    ? "`claude --resume " + _history.Current.Id + "`"
                    : "`claude --resume <session-id>`";

            return
                "**Code Astrogator — Help**\n\n" +
                "- **Enter** sends, **Shift+Enter** inserts a newline.\n" +
                "- **@** mentions workspace files and folders; the **+** menu attaches files; **Ctrl+V** pastes copied files/images as attachments.\n" +
                "- The **Model · Mode** button picks model, effort, permission mode and Ultracode; the gear sets appearance and verbosity.\n" +
                "- The **/** menu lists the slash commands the CLI supports in this panel (reported by the CLI itself).\n" +
                "- Interactive-only commands (`/remote-control`, `/config`, `/login`, …) need a terminal: run " + resumeHint + " and use them there.";
        }

        /// <summary>Pushes the CLI-reported slash-command list to the UI (if known yet).</summary>
        private void SendSlashCommands()
        {
            if (_slashCommands == null)
                return;
            Post(new JObject { ["type"] = "slash.commands", ["commands"] = _slashCommands.DeepClone() });
        }

        private void HandleAttachFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Add files to the conversation",
            };
            if (dialog.ShowDialog() != true)
                return;
            AddFileAttachments(dialog.FileNames);
        }

        /// <summary>Adds files/folders (Explorer drag-drop or the picker) as @-reference
        /// attachment chips. Posts attach.added; existing chip flow takes it from there.</summary>
        public void AddFileAttachments(System.Collections.Generic.IEnumerable<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (RemoteSessionActive)
                return; // composer (and attachments) are locked during remote control

            var attachments = new JArray();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)
                    || (!System.IO.File.Exists(p) && !System.IO.Directory.Exists(p)))
                    continue;
                attachments.Add(new JObject
                {
                    ["name"] = System.IO.Path.GetFileName(p.TrimEnd('\\', '/')),
                    ["path"] = p,
                });
            }
            if (attachments.Count > 0)
                PostOrQueue(new JObject { ["type"] = "attach.added", ["attachments"] = attachments });
        }

        /// <summary>
        /// Adds an editor selection to the composer as an @-reference attachment chip labelled
        /// with the file name + line range (from the editor right-click "Add selection to Claude
        /// prompt"). The chip carries the range as a <c>#L&lt;start&gt;[-&lt;end&gt;]</c> suffix on
        /// its path; the CLI reads those lines itself when the turn is sent — same mechanism as the
        /// active-file selection chip. Queued until the WebUI is ready so it survives the tool
        /// window just having opened.
        /// </summary>
        public void AddSelectionToPrompt(string? path, int startLine, int endLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (RemoteSessionActive)
                return; // composer (and attachments) are locked during remote control

            var name = string.IsNullOrEmpty(path) ? "selection" : System.IO.Path.GetFileName(path);
            var range = endLine > startLine ? $"{startLine}-{endLine}" : $"{startLine}";
            var chipName = $"{name}:{range}";
            var chipPath = string.IsNullOrEmpty(path) ? chipName : path + "#L" + range;
            PostOrQueue(new JObject
            {
                ["type"] = "attach.added",
                ["attachments"] = new JArray { new JObject { ["name"] = chipName, ["path"] = chipPath } },
            });
        }

        /// <summary>
        /// Splits a trailing <c>#L&lt;start&gt;[-&lt;end&gt;]</c> line suffix (added by the editor
        /// "Add selection to Claude prompt" chip) off a path, so it can be emitted outside the
        /// quotes of a spaced path. Paths without such a suffix are returned unchanged.
        /// </summary>
        private static (string path, string suffix) SplitLineSuffix(string path)
        {
            var i = path.LastIndexOf("#L", StringComparison.Ordinal);
            if (i > 0 && i + 2 < path.Length && char.IsDigit(path[i + 2]))
                return (path.Substring(0, i), path.Substring(i));
            return (path, "");
        }

        /// <summary>
        /// Ctrl+V in the composer: read the Windows clipboard and turn pasted files /
        /// images into attachments (the CLI reads files by path). File drops are
        /// referenced in place; a bitmap is written to a temp PNG first.
        /// </summary>
        private void HandleClipboardPaste()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var attachments = new JArray();
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    foreach (var file in System.Windows.Clipboard.GetFileDropList())
                    {
                        if (string.IsNullOrEmpty(file))
                            continue;
                        attachments.Add(new JObject
                        {
                            ["name"] = System.IO.Path.GetFileName(file),
                            ["path"] = file,
                        });
                    }
                }
                else if (System.Windows.Clipboard.ContainsImage())
                {
                    var path = SavePastedImage(System.Windows.Clipboard.GetImage());
                    if (path != null)
                        attachments.Add(new JObject
                        {
                            ["name"] = System.IO.Path.GetFileName(path),
                            ["path"] = path,
                        });
                }
            }
            catch (Exception ex)
            {
                PostError("Paste from clipboard failed: " + ex.Message);
                return;
            }

            if (attachments.Count > 0)
                Post(new JObject { ["type"] = "attach.added", ["attachments"] = attachments });
        }

        /// <summary>Encodes a clipboard bitmap to a PNG under %LocalAppData%\CodeAstrogator\pasted.</summary>
        private static string? SavePastedImage(System.Windows.Media.Imaging.BitmapSource? image)
        {
            if (image == null)
                return null;

            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "pasted");
            System.IO.Directory.CreateDirectory(dir);

            var path = System.IO.Path.Combine(dir, "paste-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var fs = System.IO.File.Create(path))
                encoder.Save(fs);
            return path;
        }

        private void HandleAttachContext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // v1: attach the active document as context (folder picker comes later).
            var dte = _package.GetDte();
            var path = dte?.ActiveDocument?.FullName;
            if (string.IsNullOrEmpty(path))
                return;

            Post(new JObject
            {
                ["type"] = "attach.added",
                ["attachments"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = System.IO.Path.GetFileName(path),
                        ["path"] = path,
                    },
                },
            });
        }

        private DateTime _filesListFetchedUtc;
        private JArray? _filesListCache;

        /// <summary>Workspace file list for the @-mention autocomplete (30 s cache).</summary>
        private void HandleFilesListRequest()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_filesListCache != null && (DateTime.UtcNow - _filesListFetchedUtc).TotalSeconds < 30)
            {
                Post(new JObject { ["type"] = "files.list", ["files"] = _filesListCache.DeepClone() });
                return;
            }

            var root = _package.GetSolutionDirectory();
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var arr = new JArray();
                foreach (var entry in WorkspaceFileLister.List(root))
                {
                    arr.Add(new JObject { ["path"] = entry.Path, ["isDir"] = entry.IsDir });
                }
                _filesListCache = arr;
                _filesListFetchedUtc = DateTime.UtcNow;
                Post(new JObject { ["type"] = "files.list", ["files"] = arr.DeepClone() });
            }).Task.Forget();
        }

        private void HandleEditorInsert(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = _package.GetDte();
                if (dte?.ActiveDocument?.Selection is TextSelection selection)
                    selection.Insert(text);
            }
            catch (Exception ex)
            {
                PostError("Insert into editor failed: " + ex.Message);
            }
        }

        /// <summary>web→host editor.openFile: open the file referenced by an Edit/Write/Read card in the
        /// VS text editor (plain open, not the diff review). UI thread.</summary>
        private void HandleOpenFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!System.IO.File.Exists(path))
            {
                PostError("File not found: " + path);
                return;
            }
            try
            {
                VsShellUtilities.OpenDocument(_package, path, VSConstants.LOGVIEWID_TextView,
                    out _, out _, out IVsWindowFrame frame);
                frame?.Show();
            }
            catch (Exception ex)
            {
                PostError("Could not open the file in Visual Studio: " + ex.Message);
            }
        }

        // ── session events (background threads) → host → web ─────────────────

        private void OnSessionEvent(ClaudeEvent ev)
        {
            switch (ev)
            {
                case SessionInitEvent init when !string.IsNullOrEmpty(init.SessionId):
                    // history id/resumability is adopted at turn end (num_turns > 0) —
                    // local-only turns like /help report ids --resume cannot use
                    if (init.SlashCommands.Count > 0)
                    {
                        _slashCommands = new JArray(init.SlashCommands);
                        SendSlashCommands();
                    }
                    if (!_sessionStartAnnounced) // decision #9
                    {
                        _sessionStartAnnounced = true;
                        var parts = new System.Collections.Generic.List<string> { "Session started" };
                        if (!string.IsNullOrEmpty(init.Model))
                            parts.Add(init.Model!);
                        if (!string.IsNullOrEmpty(init.Cwd))
                            parts.Add(init.Cwd!);
                        PostSystemNote(string.Join(" · ", parts));
                    }
                    break;

                case ThinkingStartEvent ts: // decision #13
                    Post(new JObject { ["type"] = "thinking.start", ["id"] = ts.BlockId });
                    break;

                case ThinkingDeltaEvent td:
                    Post(new JObject
                    {
                        ["type"] = "thinking.delta",
                        ["id"] = td.BlockId,
                        ["text"] = td.Text,
                        ["estimatedTokens"] = td.EstimatedTokens,
                    });
                    break;

                case ThinkingEndEvent te:
                    Post(new JObject { ["type"] = "thinking.end", ["id"] = te.BlockId });
                    break;

                case CompactBoundaryEvent compact: // decision #12
                    if (compact.PostTokens > 0)
                    {
                        // the /compact turn's result reports usage 0 — the boundary
                        // metadata is the only source for the new context size
                        _history.Current.ContextTokens = compact.PostTokens;
                        Post(new JObject
                        {
                            ["type"] = "usage.update",
                            ["contextTokens"] = compact.PostTokens,
                        });
                    }
                    PostSystemNote(compact.PreTokens > 0 && compact.PostTokens > 0
                        ? $"Context compacted · {compact.PreTokens:N0} → {compact.PostTokens:N0} tokens"
                        : "Context compacted");
                    break;

                case StatusEvent status when status.Status == "compacting":
                    PostStatus("working", "Compacting context…");
                    break;

                case AssistantStartEvent start:
                    _streamingAssistant = new JObject
                    {
                        ["role"] = "assistant",
                        ["id"] = start.MessageId,
                        ["text"] = "",
                        ["ts"] = DateTime.UtcNow.ToString("o"),
                    };
                    RecordMessage(_streamingAssistant);
                    _turnHadAssistantOutput = true;
                    Post(new JObject { ["type"] = "assistant.start", ["id"] = start.MessageId });
                    break;

                case AssistantDeltaEvent delta:
                    if (_streamingAssistant != null)
                        _streamingAssistant["text"] = (_streamingAssistant.Value<string>("text") ?? "") + delta.Text;
                    Post(new JObject { ["type"] = "assistant.delta", ["id"] = delta.MessageId, ["text"] = delta.Text });
                    break;

                case AssistantEndEvent end:
                    _streamingAssistant = null;
                    Post(new JObject { ["type"] = "assistant.end", ["id"] = end.MessageId });
                    break;

                case ToolUseEvent tool:
                    RecordMessage(new JObject
                    {
                        ["role"] = "tool",
                        ["id"] = tool.ToolUseId,
                        ["toolName"] = tool.Name,
                        ["input"] = tool.Input.DeepClone(),
                        ["status"] = "running",
                        ["ts"] = DateTime.UtcNow.ToString("o"),
                    });
                    Post(new JObject
                    {
                        ["type"] = "tool.use",
                        ["id"] = tool.ToolUseId,
                        ["name"] = tool.Name,
                        ["input"] = tool.Input.DeepClone(),
                        ["status"] = "running",
                    });
                    // decision #19 — edits the CLI approves without asking (acceptEdits/bypass) get
                    // the SAME green permission card as the manual flow (pre-decided "approved"),
                    // not a plain tool card + "Auto-approved" note. tool.result upgrades it to
                    // Applied/Failed via UpgradePermissionResult, exactly like an approved edit.
                    // Match the mode the RUNNING process was launched with — not the live setting.
                    // After a mid-turn switch (e.g. plan approval flips Settings to acceptEdits) the
                    // process is still in its old mode and fires the real permission hook; pre-rendering
                    // an auto-approved card here would then collide with it (duplicate cards).
                    // When "Review edits at end of turn" is active, edits are routed through the permission
                    // hook (which posts this same green card + captures the baseline), so skip it here to
                    // avoid a duplicate card. Reviewable edits go through the hook; NotebookEdit still auto-
                    // applies CLI-side and is pre-rendered here as before.
                    if (IsEditTool(tool.Name)
                        && _session.LaunchedPermissionMode is "acceptEdits" or "bypass"
                        && !(_session.EditsRouteThroughHook && EditReviewSession.IsReviewableTool(tool.Name)))
                    {
                        var diff = BuildPermissionDiff(tool.Name, tool.Input);
                        var card = new JObject
                        {
                            ["type"] = "permission.request",
                            ["requestId"] = tool.ToolUseId,
                            ["toolName"] = tool.Name,
                            ["input"] = tool.Input.DeepClone(),
                            ["autoApproved"] = true, // render pre-decided, no Approve/Reject buttons
                        };
                        if (diff != null)
                            card["diff"] = diff;
                        Post(card);
                        RecordPermissionMessage(
                            new PendingPermission { RequestId = tool.ToolUseId, ToolName = tool.Name, Input = tool.Input, Diff = diff },
                            "approved");
                    }
                    break;

                case ToolResultEvent result:
                    // A result for a tool whose permission/question prompt is STILL open means the
                    // CLI abandoned the prompt itself (e.g. the AskUserQuestion/permission tool timed
                    // out and the turn moved on). Settle it, close the orphaned card, and restore the
                    // status — otherwise the card stays interactive and the "working" rocket never
                    // comes back (status stuck on "waiting-permission" for the rest of the turn).
                    if (!string.IsNullOrEmpty(result.ToolUseId)
                        && _pendingPermissions.ContainsKey(result.ToolUseId))
                    {
                        ResolvePending(result.ToolUseId, "deny", "Timed out — no answer");
                        // Close() is thread-safe — it marshals to the UI thread itself (this runs on a
                        // CLI background thread). The VSTHRD010 analyzer can't see that, hence the suppression.
#pragma warning disable VSTHRD010
                        _editReview.Close(result.ToolUseId); // tear down any open editor-review adornments
#pragma warning restore VSTHRD010
                        Post(new JObject { ["type"] = "permission.expire", ["requestId"] = result.ToolUseId });
                        // background thread → can't call PostStatusAfterDecision (UI-thread only); Post
                        // marshals, so compute the same state inline (working unless others are open).
                        if (_session.IsBusy)
                            PostStatus(_pendingPermissions.IsEmpty ? "working" : "waiting-permission");
                    }
                    Post(new JObject
                    {
                        ["type"] = "tool.result",
                        ["id"] = result.ToolUseId,
                        ["status"] = result.IsError ? "error" : "ok",
                        ["summary"] = result.Summary,
                    });
                    UpgradePermissionResult(result.ToolUseId, result.IsError); // approved card → applied/failed
                    break;

                case TurnResultEvent turn when !string.IsNullOrEmpty(turn.ParentToolUseId):
                    // A subagent (Task tool) finished — the CLI emits its own `result` tagged with
                    // parent_tool_use_id. This is NOT the turn's end: render it as a subordinate
                    // agent footer (its own time/tokens/cost, a subset of the turn total) instead of
                    // a second turn footer, and skip all turn-end bookkeeping (context size, session
                    // id, /usage refresh — those belong to the main result that follows).
                    Post(new JObject
                    {
                        ["type"] = "agent.result",
                        ["parentToolUseId"] = turn.ParentToolUseId,
                        ["costUsd"] = turn.CostUsd,
                        ["durationMs"] = turn.DurationMs,
                        ["turnOutputTokens"] = turn.OutputTokens,
                        ["isError"] = turn.IsError,
                    });
                    break;

                case TurnResultEvent turn:
                    _history.Current.UpdatedAtUtc = DateTime.UtcNow;
                    if (turn.NumTurns > 0 && !string.IsNullOrEmpty(turn.SessionId))
                    {
                        _history.Current.Id = turn.SessionId;
                        _history.Current.HasCliSession = true;
                    }
                    // context size = the LAST assistant message's usage (one API call). The
                    // result-event aggregate (InputTokens) re-counts the cached context for every
                    // round-trip in the turn, so it massively over-counts (showed Ctx 100% / huge
                    // footer). ContextInputTokens/OutputTokens hold the real occupancy.
                    long contextTokens = turn.ContextInputTokens + turn.ContextOutputTokens;
                    if (contextTokens > 0)
                        _history.Current.ContextTokens = contextTokens;
                    // result-only turns (e.g. /help) carry no assistant-message usage — fall back
                    // to the aggregate (a single round-trip, so it equals the real size there)
                    long footerInput = contextTokens > 0 ? turn.ContextInputTokens : turn.InputTokens;
                    long footerOutput = contextTokens > 0 ? turn.ContextOutputTokens : turn.OutputTokens;
                    // decision #8 — slash commands & friends often answer only via the
                    // result line; render it as a full block when nothing was streamed.
                    if (!_turnHadAssistantOutput && !turn.IsError
                        && !string.IsNullOrWhiteSpace(turn.ResultText))
                    {
                        PostSyntheticAssistant(turn.ResultText!);
                        _turnHadAssistantOutput = true;
                    }
                    Post(new JObject
                    {
                        ["type"] = "turn.result",
                        ["sessionId"] = turn.SessionId,
                        ["costUsd"] = turn.CostUsd,
                        // footer mirrors the real context size (not the aggregated round-trip
                        // total) so the chat footer and the Ctx meter agree
                        ["tokens"] = new JObject
                        {
                            ["input"] = footerInput,
                            ["output"] = footerOutput,
                            ["total"] = footerInput + footerOutput,
                        },
                        ["durationMs"] = turn.DurationMs,
                        // tokens GENERATED this turn (aggregate output over all round-trips) — the
                        // turn-footer's "work produced" figure. Distinct from the context-size
                        // tokens above (last message), which feed the Ctx meter.
                        ["turnOutputTokens"] = turn.OutputTokens,
                        ["contextTokens"] = _history.Current.ContextTokens,
                        // last known utilization; RefreshUsage() right after turn end updates it
                        ["limits"] = BuildLimits(),
                    });
                    // "Review edits at end of turn": surface the collected changed-files list above the
                    // composer and gate the next prompt until it's cleared. Safe on this background thread:
                    // it only reads files + posts + forms UI callbacks (invoked later on the UI thread) and
                    // the auto-attach marshals internally — the analyzer can't see that, hence the suppression.
#pragma warning disable VSTHRD010
                    BuildAndPostTurnReviewList();
#pragma warning restore VSTHRD010
                    break;

                case ApiRetryEvent retry:
                    Post(new JObject
                    {
                        ["type"] = "status",
                        ["state"] = "working",
                        ["text"] = string.IsNullOrEmpty(retry.Message) ? "Retrying…" : retry.Message,
                    });
                    break;

                case StreamErrorEvent error:
                    PostError(error.Message);
                    break;
            }
        }

        private void OnTurnCompleted(ClaudeTurnExit exit, string? error)
        {
            // A turn can end while a tools/call is still blocked (CLI crash/error/normal end);
            // settle any open permission so the card doesn't orphan and the handler task frees.
            // Its only UI work (EditReviewController.Close / Post) self-marshals, so it is safe on this
            // background callback; VSTHRD010 propagates the inner Close affinity here, hence suppression.
#pragma warning disable VSTHRD010
            DenyAllPendingPermissions("Turn ended");
#pragma warning restore VSTHRD010
            lock (_history) _recordedPermissions.Clear(); // tool.result correlation is per-turn
            if (error != null)
            {
                PostError(error);
                PostStatus("error");
            }
            else
            {
                PostStatus("ready");
            }
            RefreshUsage(); // the turn just consumed quota — update the meters
            SaveHistory();
        }

        /// <summary>When an approved edit's tool.result arrives, upgrade the persisted card +
        /// live UI status to applied/failed so the outcome is visible (and survives reload).</summary>
        private void UpgradePermissionResult(string toolUseId, bool isError)
        {
            if (string.IsNullOrEmpty(toolUseId))
                return;
            var status = isError ? "failed" : "applied";
            lock (_history)
            {
                if (!_recordedPermissions.TryGetValue(toolUseId, out var rec))
                    return;
                rec["status"] = status;
                _recordedPermissions.Remove(toolUseId);
            }
            Post(new JObject { ["type"] = "permission.result", ["requestId"] = toolUseId, ["status"] = status });
        }

        /// <summary>Persists the history file in the background (best-effort).</summary>
        private void SaveHistory()
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                lock (_history)
                    _history.Save();
            }).Task.Forget();
        }

        /// <summary>
        /// Fetches session/weekly utilization by running the CLI's own <c>/usage</c>
        /// command (best-effort, background) and pushes a usage.update to the statusbar.
        /// </summary>
        private void RefreshUsage()
        {
            // Reentrancy guard: the once-a-minute timer must never stack a second /usage
            // process on top of one that is still running (e.g. a slow/30s-timeout fetch).
            if (System.Threading.Interlocked.CompareExchange(ref _usageRefreshInFlight, 1, 0) != 0)
                return;
            // Thread-agnostic: the async body switches to the UI thread to read options,
            // then off it to run the CLI. Callers may be on the UI or a background thread.
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await RefreshUsageAsync(); }
                finally { System.Threading.Interlocked.Exchange(ref _usageRefreshInFlight, 0); }
            }).Task.Forget();
        }

        private async System.Threading.Tasks.Task RefreshUsageAsync()
        {
            // GetSolutionDirectory()/options are UI-thread reads; capture before going async.
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var exeOverride = _package.GetOptions().ClaudeExecutablePath;
            var cwd = _package.GetSolutionDirectory();

            await TaskScheduler.Default;

            _planLabel ??= ClaudeUsageClient.ReadPlanLabel();

            var fresh = await ClaudeUsageClient.FetchAsync(exeOverride, cwd).ConfigureAwait(false);
            // `/usage` sometimes returns only the "What's contributing" section and drops the
            // whole percentage block; a single quick retry usually gets it (best-effort, bounded).
            if (fresh == null || (fresh.SessionPct == null && fresh.WeeklyPct == null))
            {
                try { await System.Threading.Tasks.Task.Delay(3000).ConfigureAwait(false); } catch { }
                var retry = await ClaudeUsageClient.FetchAsync(exeOverride, cwd).ConfigureAwait(false);
                if (retry != null && (retry.SessionPct != null || retry.WeeklyPct != null))
                    fresh = retry;
            }

            if (fresh == null && _lastUsage == null && _planLabel == null)
                return; // API-key mode or offline, nothing cached yet — leave the meters alone

            // Merge per-window into the last-known-good so a partial/empty report never zeroes a meter.
            _lastUsage = ClaudeUsageClient.Merge(_lastUsage, fresh, DateTimeOffset.Now);

            long contextTokens;
            lock (_history)
                contextTokens = _history.Current.ContextTokens;
            var msg = new JObject
            {
                ["type"] = "usage.update",
                ["tokens"] = _session.TotalTokens,
                ["contextTokens"] = contextTokens,
                ["sessionPct"] = _lastUsage?.SessionPct ?? 0,
                ["weeklyPct"] = _lastUsage?.WeeklyPct ?? 0,
                ["sessionResetsAt"] = _lastUsage?.SessionResetsAt?.ToString("o"),
                ["weeklyResetsAt"] = _lastUsage?.WeeklyResetsAt?.ToString("o"),
                ["sessionFetchedAt"] = _lastUsage?.SessionFetchedAt?.ToString("o"),
                ["weeklyFetchedAt"] = _lastUsage?.WeeklyFetchedAt?.ToString("o"),
            };
            if (_planLabel != null)
                msg["plan"] = _planLabel;
            Post(msg);
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_package.GetOptions().ThemeModeString == "auto")
                SendTheme();
        }

        /// <summary>A Unified Settings value changed (settings UI or our own write-back).</summary>
        private void OnOptionsChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SendTheme();
            SendActiveFile(); // the auto-add-active-file toggle may have changed
            SendBannerSettings(); // announcement/update opt-in may have changed in the settings window
            var promptTimeoutMs = PromptTimeoutMs(_package.GetOptions());
            _session.Settings.McpToolTimeoutMs = promptTimeoutMs;       // env-var fallback, applies next turn
            _permission.UpdateToolTimeout(promptTimeoutMs);            // config `timeout` (the value the CLI prefers)
            ApplyProcessHostOption(); // persistent-CLI toggle may have changed
        }

        /// <summary>Configured prompt timeout (settings, minutes) as milliseconds for the CLI env var.</summary>
        private static int PromptTimeoutMs(AstrogatorOptions o) =>
            AstrogatorOptions.ClampPromptTimeoutMinutes(o.PromptTimeoutMinutes) * 60_000;

        private void OnActiveDocumentChanged(string? path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SendActiveFile();
        }

        /// <summary>
        /// Chip toggle in the composer — a per-session override only; it does NOT change
        /// the option. The option (settings) is the master switch and takes priority.
        /// </summary>
        private void HandleActiveFileSetEnabled(bool enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _activeFileSessionEnabled = enabled;
            SendActiveFile();
        }

        /// <summary>True only when both the option and the per-session toggle are on.</summary>
        private bool ActiveFileEffective =>
            _package.GetOptions().AutoAddActiveFile && _activeFileSessionEnabled;

        /// <summary>Initial state of the per-session active-file toggle for a fresh session
        /// (the "Reference it by default in new chats" option; the user can still flip it per chat).</summary>
        private bool ActiveFileDefaultOn => _package.GetOptions().ActiveFileOnByDefault;

        /// <summary>
        /// Tells the UI about the active editor file: <c>optionEnabled</c> governs whether
        /// the chip shows at all, <c>enabled</c> is the per-session toggle (strike-through),
        /// <c>lines</c> is the selected line range (e.g. "10-20", null when none/disabled).
        /// </summary>
        private void SendActiveFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = _activeDocs.CurrentPath;
            var sel = GetActiveSelection();
            Post(new JObject
            {
                ["type"] = "activeFile",
                ["path"] = path,
                ["name"] = string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path),
                ["optionEnabled"] = _package.GetOptions().AutoAddActiveFile,
                ["enabled"] = _activeFileSessionEnabled,
                ["lines"] = sel == null ? null
                    : (sel.Value.top == sel.Value.bottom ? sel.Value.top.ToString()
                                                          : sel.Value.top + "-" + sel.Value.bottom),
            });
        }

        /// <summary>
        /// Selected line range (1-based) in the active editor, or null when there is no
        /// selection, the option is off, or the active document isn't the tracked file.
        /// </summary>
        private (int top, int bottom)? GetActiveSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_package.GetOptions().IncludeSelectedLines || string.IsNullOrEmpty(_activeDocs.CurrentPath))
                return null;
            try
            {
                var doc = _package.GetDte()?.ActiveDocument;
                if (doc == null
                    || !string.Equals(doc.FullName, _activeDocs.CurrentPath, StringComparison.OrdinalIgnoreCase)
                    || doc.Selection is not EnvDTE.TextSelection sel || sel.IsEmpty)
                {
                    return null;
                }
                int top = sel.TopPoint.Line;
                int bottom = sel.BottomPoint.Line;
                // a selection ending at column 1 doesn't actually cover that last line
                if (bottom > top && sel.BottomPoint.LineCharOffset == 1)
                    bottom--;
                return (top, bottom < top ? top : bottom);
            }
            catch
            {
                return null;
            }
        }

        // ── host → web senders ────────────────────────────────────────────────

        private void SendTheme()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Post(ThemeService.BuildThemeMessage(_package.GetOptions().ThemeModeString));
        }

        private void SendAuthState()
        {
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            var (loggedIn, mode) = ClaudeExecutableLocator.ProbeAuthState();
            Post(new JObject
            {
                ["type"] = "auth.state",
                ["loggedIn"] = exe != null && loggedIn,
                ["mode"] = exe == null ? "none" : mode,
            });
        }

        private void SendSessionInit()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = _package.GetOptions();
            Post(new JObject
            {
                ["type"] = "session.init",
                ["sessionId"] = _history.Current.Id,
                ["title"] = _history.Current.Title,
                ["model"] = _session.Settings.Model ?? options.DefaultModel ?? "",
                ["effort"] = _session.Settings.Effort,
                ["planMode"] = _session.Settings.PlanMode,
                ["ultracode"] = _session.Settings.Ultracode,
                ["permissionMode"] = _session.Settings.PermissionMode,
                ["autoAcceptCommands"] = options.AutoAcceptCommands,
                ["reviewEditsInEditor"] = options.ReviewEditsInEditor,
                ["reviewEditsAtTurnEnd"] = options.ReviewEditsAtTurnEnd,
                ["cwd"] = _package.GetSolutionDirectory() ?? "",
                ["tokens"] = _session.TotalTokens,
                ["contextTokens"] = _history.Current.ContextTokens,
                ["limits"] = BuildLimits(),
                ["plan"] = _planLabel ?? "",
                ["verbosity"] = options.VerbosityString,
                ["accent"] = options.AccentColor ?? "",
                ["noticeFetchEnabled"] = options.NoticeFetchEnabled,
                ["noticeFetchDecided"] = options.NoticeFetchDecided,
                ["updateCheckEnabled"] = options.UpdateCheckEnabled,
                ["updateCheckDecided"] = options.UpdateCheckDecided,
                ["appVersion"] = GetInstalledVersion(),
            });
        }

        /// <summary>Pushes the current announcement + update opt-in (and installed version) to the UI
        /// (live update after a settings change). The initial values also ride along in session.init.</summary>
        private void SendBannerSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = _package.GetOptions();
            Post(new JObject
            {
                ["type"] = "banner.settings",
                ["noticeEnabled"] = options.NoticeFetchEnabled,
                ["noticeDecided"] = options.NoticeFetchDecided,
                ["updateEnabled"] = options.UpdateCheckEnabled,
                ["updateDecided"] = options.UpdateCheckDecided,
                ["appVersion"] = GetInstalledVersion(),
            });
        }

        private static string? _installedVersion;
        /// <summary>Installed extension version, read from the deployed extension.vsixmanifest
        /// (Identity/@Version) next to the assembly. Cached; falls back to the assembly version.</summary>
        private static string GetInstalledVersion()
        {
            if (_installedVersion != null)
                return _installedVersion;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(WebViewBridge).Assembly.Location) ?? "";
                var manifest = System.IO.Path.Combine(dir, "extension.vsixmanifest");
                if (System.IO.File.Exists(manifest))
                {
                    var doc = System.Xml.Linq.XDocument.Load(manifest);
                    var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
                    var v = identity?.Attribute("Version")?.Value;
                    if (!string.IsNullOrEmpty(v))
                        return _installedVersion = v!;
                }
            }
            catch { /* fall back below */ }
            try
            {
                var v = typeof(WebViewBridge).Assembly.GetName().Version?.ToString();
                if (!string.IsNullOrEmpty(v))
                    return _installedVersion = v!;
            }
            catch { }
            return _installedVersion = "";
        }

        /// <summary>Limits payload incl. reset times and per-window fetch stamps (meter tooltips + staleness hint).</summary>
        private JObject BuildLimits() => new JObject
        {
            ["sessionPct"] = _lastUsage?.SessionPct ?? 0,
            ["weeklyPct"] = _lastUsage?.WeeklyPct ?? 0,
            ["sessionResetsAt"] = _lastUsage?.SessionResetsAt?.ToString("o"),
            ["weeklyResetsAt"] = _lastUsage?.WeeklyResetsAt?.ToString("o"),
            ["sessionFetchedAt"] = _lastUsage?.SessionFetchedAt?.ToString("o"),
            ["weeklyFetchedAt"] = _lastUsage?.WeeklyFetchedAt?.ToString("o"),
        };

        private static bool IsEditTool(string name) =>
            name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit";

        /// <summary>Dim one-line system note in the transcript (display option "C").</summary>
        private void PostSystemNote(string text)
        {
            var id = "note-" + Guid.NewGuid().ToString("n");
            RecordMessage(new JObject
            {
                ["role"] = "system",
                ["id"] = id,
                ["text"] = text,
                ["ts"] = DateTime.UtcNow.ToString("o"),
            });
            Post(new JObject { ["type"] = "system.note", ["id"] = id, ["text"] = text });
        }

        private void PostStatus(string state, string? text = null)
        {
            var msg = new JObject { ["type"] = "status", ["state"] = state };
            if (text != null)
                msg["text"] = text;
            Post(msg);
        }

        private void PostError(string message)
        {
            RecordMessage(new JObject
            {
                ["role"] = "error",
                ["id"] = Guid.NewGuid().ToString("n"),
                ["text"] = message,
                ["ts"] = DateTime.UtcNow.ToString("o"),
            });
            Post(new JObject { ["type"] = "error", ["message"] = message });
        }

        /// <summary>Host-generated informational message rendered as an assistant block.</summary>
        private void PostSyntheticAssistant(string text)
        {
            var id = "host-" + Guid.NewGuid().ToString("n");
            RecordMessage(new JObject
            {
                ["role"] = "assistant",
                ["id"] = id,
                ["text"] = text,
                ["ts"] = DateTime.UtcNow.ToString("o"),
            });
            Post(new JObject { ["type"] = "assistant.start", ["id"] = id });
            Post(new JObject { ["type"] = "assistant.delta", ["id"] = id, ["text"] = text });
            Post(new JObject { ["type"] = "assistant.end", ["id"] = id });
        }

        private void RecordMessage(JObject message)
        {
            lock (_history)
            {
                _history.Current.Messages.Add(message);
                _history.Current.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Posts a message if the WebUI is ready, else queues it until the "ready" handshake
        /// (so an action triggered before the window has loaded — e.g. an editor right-click command
        /// that just opened the tool window — is not silently dropped). UI thread.</summary>
        private void PostOrQueue(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_uiReady)
                Post(msg);
            else
                _queuedUiMessages.Add(msg);
        }

        /// <summary>Marks the UI ready and delivers anything queued before the "ready" handshake.</summary>
        private void FlushQueuedUiMessages()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _uiReady = true;
            if (_queuedUiMessages.Count == 0)
                return;
            var pending = _queuedUiMessages.ToArray();
            _queuedUiMessages.Clear();
            foreach (var m in pending)
                Post(m);
        }

        /// <summary>Posts a host→web message, marshalling to the UI thread as required by WebView2.</summary>
        private void Post(JObject msg)
        {
            if (_disposed)
                return;
            var json = msg.ToString(Newtonsoft.Json.Formatting.None);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed)
                    return;
                try
                {
                    _webView.PostWebMessageAsJson(json);
                }
                catch
                {
                    // WebView already torn down
                }
            }).Task.Forget();
        }
    }
}
