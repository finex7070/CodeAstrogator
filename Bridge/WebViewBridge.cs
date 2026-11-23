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
    /// Host side of the WebView2 message contract (Teil B §3) — the "chat host" of one tool window. Owns the
    /// WebView and everything the chat tabs share (history store, checkpoint service, edit-review controller,
    /// toasts, options, usage meters, theme, model catalog, banners, the plugins panel, workspace switching).
    /// The per-tab part — CLI session, permissions, turn state — is <see cref="ChatSession"/>. Receives web→host
    /// messages on the UI thread and routes the session ones to their tab; posts host→web messages (marshalled
    /// onto the UI thread).
    /// </summary>
    internal sealed partial class WebViewBridge : IDisposable
    {
        private readonly CoreWebView2 _webView;
        private readonly CodeAstrogatorPackage _package;
        private readonly SessionHistoryStore _history;
        // The chat tabs (strip order) and the one on screen. Shared things that concern "the chat" (a system note
        // from the host, the active editor file, Remote Control from the header) go to the active tab.
        private readonly List<ChatSession> _tabs = new List<ChatSession>();
        private ChatSession _active = null!;
        private ChatSession _chat => _active;
        private int _nextTabNumber = 1;       // tab ids t1, t2, … (never reused within a window)
        internal const int MaxTabs = 3;
        private bool _openTabsRestored;       // the store's saved tabs were restored (once per store / workspace)
        private UsageSnapshot? _lastUsage;    // session/weekly plan utilization (from `claude -p /usage`)
        private string? _planLabel;           // "Team Plan" etc. from ~/.claude.json
        private System.Threading.Timer? _usageTimer; // periodic refresh of the usage meters
        private int _usageRefreshInFlight;    // 0/1 reentrancy guard: never run two /usage fetches at once
        private const int UsageRefreshIntervalMs = 60 * 1000; // every minute (also while a turn runs)
        private JArray? _slashCommands;       // CLI-reported slash commands (system/init)
        private bool _workspaceSwitchPending; // the solution/folder changed during a turn/remote session → switch after
        private readonly ActiveDocumentTracker _activeDocs; // active editor tab → auto-reference
        // "Notify me" (opt-in): Windows notifications at turn end and for prompts that wait for the user (shared by
        // the tabs; the tags are made unique per tab by the tab).
        private readonly WindowsToastNotifier _toasts;
        private readonly ManagementPanel _manage; // "Plugins, skills & MCP" panel (manage.* messages)
        private readonly AuthController _auth;    // sign in / out of the Claude CLI (auth.* messages)
        // ── file checkpoints (rewind) — one service for all tabs (shadow repo per workspace) ──
        private readonly GitCheckpointService _checkpoints = new GitCheckpointService();
        private readonly EditReviewController _editReview; // inline edit-review adornments (opt-in)

        /// <summary>Checkpoint bookkeeping for the turn currently running.</summary>
        private sealed class CheckpointTurnState
        {
            public string Cwd = "";
            public string PostRef = "";
            public JObject? Message; // null for slash-command turns (no user bubble → no rewind button)
        }

        private sealed class TurnReview
        {
            public EditReviewSession Session = null!;
            public string Baseline = "";  // content before Claude first touched it (revert target)
            public bool IsNew;            // baseline was empty → file created by Claude
            public string Current = "";   // disk content the review was built from — unchanged ⇒ reuse it
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
            var opt = package.GetOptions();
            _checkpoints.Filter = BuildCheckpointFilter(opt);      // size limit + extension black/whitelist

            _manage = new ManagementPanel(_package, Post);
            _auth = new AuthController(_package, Post, SignOutBlockedReason, OnAccountChanged);
            _toasts = new WindowsToastNotifier(OnToastActivated,
                error => PostNotify("Windows notification could not be shown: " + error, "warning"));
            // Inline edit-review (opt-in): opens edited files with a red/green diff + per-hunk
            // Accept/Reject; calls FinalizeEditReview of the tab that asked once every hunk in a request is decided.
            _editReview = new EditReviewController(_package,
                requestId => _tabs.FirstOrDefault(tab => tab.HasPendingRequest(requestId))?.FinalizeEditReview(requestId));
            _history = SessionHistoryStore.LoadFrom(
                SessionHistoryStore.GetHistoryPath(package.GetSolutionDirectory()),
                package.GetOptions().HistoryRetentionDays);

            _activeDocs = new ActiveDocumentTracker(package);
            _activeDocs.ActiveDocumentChanged += OnActiveDocumentChanged;

            _active = AddTab(mayRestoreLast: true);

            // The history belongs to the open solution/folder. After a VS restart the tool window is often created
            // before the solution has finished loading (→ "global" history) — follow the workspace when it opens or
            // closes instead of staying on the store picked here (see CheckWorkspaceSwitch).
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder += OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseFolder += OnWorkspaceOpenedOrClosed;

            _webView.WebMessageReceived += OnWebMessageReceived;
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
            _package.OptionsChanged += OnOptionsChanged;
            _package.ManualCheckRequested += OnManualCheckRequested;

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
            _package.ManualCheckRequested -= OnManualCheckRequested;
            _activeDocs.ActiveDocumentChanged -= OnActiveDocumentChanged;
            _activeDocs.Dispose();
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder -= OnWorkspaceOpenedOrClosed;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseFolder -= OnWorkspaceOpenedOrClosed;
            _webView.WebMessageReceived -= OnWebMessageReceived;
            foreach (var tab in _tabs)
                tab.Dispose();     // stops its turn, denies open prompts, ends its MCP listener
            _editReview.Dispose(); // remove any open edit-review adornments
            _auth.Dispose();       // a running `claude auth login` ends with the window
            lock (_history)
            {
                _history.ActiveOpenId = _active.Record.Id;
                _history.Save(); // synchronous — tool window / VS is closing
            }
        }

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

            // One chat, one tab: a chat from the history that is already open in another tab is not loaded a second
            // time (two CLI processes must never --resume the same session) — that tab is selected instead. A delete
            // goes to the tab that holds the chat (it refuses while its turn runs, else starts a fresh chat).
            var type = msg.Value<string>("type");
            if (type == "session.load" || type == "session.delete")
            {
                var owner = TabShowing(msg.Value<string>("sessionId"));
                if (owner != null && owner != TabFor(msg))
                {
                    if (type == "session.delete")
                    {
                        owner.HandleMessage(msg);
                        return;
                    }
                    _active = owner;
                    PostTabsState();
                    owner.SendActiveFile();
                    return;
                }
            }

            // Session messages go to their tab (`tab`, else the active one); the rest is shared.
            if (TabFor(msg).HandleMessage(msg))
                return;

            switch (msg.Value<string>("type"))
            {
                case "ready":
                    CheckWorkspaceSwitch(); // the solution may have opened before the events were wired
                    SendTheme();
                    SendFontState();
                    SendAppearanceState();
                    _auth.SendState();   // quick account state; `claude auth status` follows
                    RestoreOpenTabs();   // the chats that were open in tabs last time (once)
                    PostTabsState();     // the strip first: the page creates the tabs the messages below go to
                    foreach (var tab in _tabs)
                        tab.SendInitialSession(); // a WebView reload re-renders every tab
                    SendSlashCommands(); // re-send the CLI-reported list after a WebView reload
                    SendModelCatalog();  // which models the installed CLI accepts (async probe)
                    SendUpdateState();   // staged update → banner; just-applied update → note
                    SendWhatsNew();      // after session.init: an open consent popup goes first, the page queues this behind it
                    foreach (var tab in _tabs)
                        tab.RepostBackgroundTasks(); // WebView reloaded while background tasks run
                    RunAutoUpdateCheck(force: false);
                    _chat.SendActiveFile();
                    if (_package.SettingsLoadError != null)
                        _chat.PostSystemNote("⚠ Settings could not be read — using defaults. " + _package.SettingsLoadError);
                    foreach (var tab in _tabs)
                        tab.ResumeRemoteOnReady(); // re-announce a running remote session, or adopt one from before a restart
                    RefreshUsage();
                    FlushQueuedUiMessages(); // deliver actions queued before the UI was ready
                    break;
                case "tab.new":
                    HandleTabNew();
                    break;
                case "tab.select":
                    HandleTabSelect(msg.Value<string>("id"));
                    break;
                case "tab.close":
                    HandleTabClose(msg.Value<string>("id"));
                    break;
                case "session.listRequest":
                    JArray sessions;
                    lock (_history)
                        sessions = _history.ToSessionList();
                    Post(new JObject { ["type"] = "session.list", ["sessions"] = sessions });
                    break;
                // Model · effort · permission mode · Ultracode are global, persisted options (sticky across new chats
                // + VS restarts); every tab mirrors them (and re-mirrors before each turn).
                case "model.set":
                    _package.GetOptions().DefaultModel = msg.Value<string>("model") ?? "";
                    _package.SaveOptions();
                    SyncAllTabsFromOptions();
                    break;
                case "effort.set":
                    var effort = msg.Value<string>("effort") ?? "medium";
                    // CLI 2.1.x accepts exactly these; ignore anything else.
                    if (effort is "low" or "medium" or "high" or "xhigh" or "max")
                    {
                        _package.GetOptions().DefaultEffortString = effort;
                        _package.SaveOptions();
                        SyncAllTabsFromOptions();
                    }
                    break;
                case "ultracode.set":
                    _package.GetOptions().UltracodeEnabled = msg.Value<bool?>("enabled") ?? false;
                    _package.SaveOptions();
                    SyncAllTabsFromOptions();
                    break;
                case "permission.set":
                    _package.GetOptions().PermissionModeString = msg.Value<string>("mode") ?? "ask";
                    _package.SaveOptions();
                    SyncAllTabsFromOptions();
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
                    _package.SaveOptions();
                    SyncAllTabsFromOptions();
                    break;
                case "update.restart":
                    HandleUpdateRestart();
                    break;
                case "check.result":
                    // Answer to a manual "Check now" for a banner (the WebUI did the fetch).
                    _package.ReportManualCheck(
                        msg.Value<string>("kind") ?? "",
                        msg.Value<bool?>("ok") ?? false,
                        msg.Value<string>("message") ?? "");
                    break;
                case "consent.set":
                    // first-run consent popup answered (announcements + updates + model list +
                    // checkpoints) → persist the choices and that they were made (so the popup never
                    // re-appears until a new opt-in is introduced).
                    _package.GetOptions().NoticeFetchEnabled = msg.Value<bool?>("noticeEnabled") ?? false;
                    _package.GetOptions().NoticeFetchDecided = true;
                    _package.GetOptions().UpdateCheckEnabled = msg.Value<bool?>("updateEnabled") ?? false;
                    _package.GetOptions().UpdateCheckDecided = true;
                    _package.GetOptions().ModelCatalogFetchEnabled = msg.Value<bool?>("modelsEnabled") ?? false;
                    _package.GetOptions().ModelCatalogFetchDecided = true;
                    _package.GetOptions().AutoUpdateEnabled = msg.Value<bool?>("autoUpdateEnabled") ?? false;
                    _package.GetOptions().AutoUpdateDecided = true;
                    _package.GetOptions().CheckpointsEnabled =
                        (msg.Value<bool?>("checkpointsEnabled") ?? false) && GitCheckpointService.IsGitAvailable();
                    _package.GetOptions().CheckpointsDecided = true;
                    _package.SaveOptions();
                    SendCheckpointSettings();
                    SendModelCatalog(); // the answer may just have allowed the repo fetch
                    RunAutoUpdateCheck(force: false);
                    break;
                case "theme.setMode":
                    HandleThemeSetMode(msg.Value<string>("mode") ?? "auto");
                    break;
                case "accent.set":
                    HandleAccentSet(msg.Value<string>("color") ?? "");
                    break;
                case "syntax.set":
                    _package.GetOptions().SyntaxTheme = AstrogatorOptions.NormalizeSyntaxTheme(msg.Value<string>("theme"));
                    _package.SaveOptions(); // applied optimistically in the UI
                    break;
                case "verbosity.set":
                    HandleVerbositySet(msg.Value<string>("level") ?? "normal");
                    break;
                case "options.open":
                    // Open the (modal) settings window only AFTER this WebMessageReceived callback has
                    // returned. A modal loop running inside the callback blocks WebView2 from delivering
                    // any further page → host message until the dialog closes (WebView2 does not re-enter
                    // its event handlers) — the window's "Check now" for the banners waits for the page's
                    // check.result and hung on "Checking…".
                    _package.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                        _package.OpenOptions();
                    }).Task.Forget();
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
                case "attachment.preview":
                    HandleAttachmentPreview(msg.Value<string>("path") ?? "", msg.Value<string>("token") ?? "");
                    break;
                case "activeFile.refresh":
                    // composer gained focus — re-read the current editor selection
                    _chat.SendActiveFile();
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
                case "whatsnew.seen":
                    _package.GetOptions().LastSeenVersion = msg.Value<string>("version") ?? GetInstalledVersion();
                    _package.SaveOptions();
                    break;
                default:
                    if (!_auth.Handle(msg))  // sign in / out (auth.*)
                        _manage.Handle(msg); // "Plugins, skills & MCP" panel (manage.*)
                    break;
            }
        }

        // ── chat tabs ──────────────────────────────────────────────────────────

        private ChatSession AddTab(bool mayRestoreLast)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tab = new ChatSession(this, "t" + _nextTabNumber++, mayRestoreLast);
            _tabs.Add(tab);
            return tab;
        }

        /// <summary>
        /// Brings back the chats that were open in tabs when the workspace's history was last saved (tab order, the
        /// active one selected) — once per store, and only while the panel is untouched (one empty, idle tab) and
        /// "Restore last session" is on. Without saved tabs (a file from before tabs) the first tab falls back to
        /// restoring the most recent chat (ChatSession.SendInitialSession).
        /// </summary>
        private void RestoreOpenTabs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_openTabsRestored)
                return;
            _openTabsRestored = true;
            if (!_package.GetOptions().RestoreLastSession || _tabs.Count != 1 || _tabs[0].IsBusy)
                return;
            IReadOnlyList<string> ids;
            string? activeId;
            lock (_history)
            {
                if (_tabs[0].Record.Messages.Count > 0)
                    return;
                ids = _history.SavedOpenIds;
                activeId = _history.SavedActiveId;
            }
            foreach (var id in ids.Take(MaxTabs))
            {
                var first = _tabs.Count == 1 && _tabs[0].Record.Messages.Count == 0;
                var tab = first ? _tabs[0] : AddTab(mayRestoreLast: false);
                if (!tab.OpenChat(id) && !first)
                {
                    tab.Close(); // unknown (deleted / past the retention window) → no tab for it
                    _tabs.Remove(tab);
                }
            }
            _active = _tabs.FirstOrDefault(tab => string.Equals(tab.Record.Id, activeId, StringComparison.OrdinalIgnoreCase))
                ?? _tabs[0];
        }

        /// <summary>The tab a web→host message is about (<c>tab</c>), else the active one.</summary>
        private ChatSession TabFor(JObject msg)
        {
            var id = msg.Value<string>("tab");
            return (id == null ? null : _tabs.FirstOrDefault(tab => tab.Id == id)) ?? _active;
        }

        /// <summary>True when a tab other than <paramref name="asking"/> has <paramref name="path"/> in its pending
        /// end-of-turn review (one review per file — see ChatSession.BuildAndPostTurnReviewList). Any thread.</summary>
        internal bool IsInOtherTabsTurnReview(ChatSession asking, string path)
        {
            foreach (var tab in _tabs.ToArray())
                if (tab != asking && tab.HasTurnReviewFor(path))
                    return true;
            return false;
        }

        /// <summary>A toast was clicked (thread pool): bring Visual Studio and the chat window up and select the tab the
        /// toast belongs to (its tag is "p&lt;pid&gt;-&lt;tab&gt;-…", see ChatSession.ToastTag).</summary>
        private void OnToastActivated(string? scopedTag)
        {
            _package.ActivateChatWindow();
            var m = scopedTag == null ? null : Regex.Match(scopedTag, @"^p\d+-(t\d+)-");
            if (m == null || !m.Success)
                return;
            var tabId = m.Groups[1].Value;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var tab = _tabs.FirstOrDefault(x => x.Id == tabId);
                if (_disposed || tab == null || tab == _active)
                    return;
                _active = tab;
                PostTabsState();
                tab.SendActiveFile();
            }).Task.Forget();
        }

        /// <summary>Remote Control runs at most once per window: true when another tab has it.</summary>
        internal bool RemoteRunsInOtherTab(ChatSession asking) =>
            _tabs.Any(tab => tab != asking && (tab.RemoteSessionActive || tab.RemoteStartPending));

        /// <summary>The first tab does the window-wide part of adopting remote sessions (importing the ones that ended
        /// while VS was closed).</summary>
        internal bool IsFirstTab(ChatSession tab) => _tabs.Count > 0 && _tabs[0] == tab;

        /// <summary>Signing out while a turn or a remote session runs would pull the login from under it.</summary>
        private string? SignOutBlockedReason()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_tabs.Any(tab => tab.IsBusy))
                return "Stop the running turn first, then sign out.";
            if (_tabs.Any(tab => tab.RemoteSessionActive))
                return "End remote control first, then sign out.";
            return null;
        }

        /// <summary>Signed in or out: the plan label and the usage meters belong to the account.</summary>
        private void OnAccountChanged()
        {
            _planLabel = null;
            _lastUsage = null;
            RefreshUsage();
        }

        /// <summary>The tab that shows the chat with this id, or null.</summary>
        private ChatSession? TabShowing(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
            lock (_history)
                return _tabs.FirstOrDefault(tab => string.Equals(tab.Record.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Re-mirrors the persisted Model · Mode options onto every tab's session (they are global).</summary>
        private void SyncAllTabsFromOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var tab in _tabs)
                tab.SyncSessionSettingsFromOptions();
        }

        /// <summary>host→web <c>tabs.state</c>: the tab ids in strip order and the active one. Titles, status markers
        /// and "unseen" are the page's (it gets every tab's session messages anyway).</summary>
        private void PostTabsState()
        {
            Post(new JObject
            {
                ["type"] = "tabs.state",
                ["tabs"] = new JArray(_tabs.Select(tab => (JToken)new JObject { ["id"] = tab.Id })),
                ["activeId"] = _active.Id,
                ["max"] = MaxTabs,
            });
        }

        /// <summary>web→host <c>tab.new</c> ("+" in the strip): a new, empty chat tab becomes the active one.</summary>
        private void HandleTabNew()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_tabs.Count >= MaxTabs)
            {
                PostTabsState(); // the page may have drawn one optimistically
                return;
            }
            _active = AddTab(mayRestoreLast: false);
            PostTabsState();
            _active.SendInitialSession();
            _active.SendActiveFile();
            SaveHistory();
        }

        /// <summary>web→host <c>tab.select</c>: the page switched tabs (it shows the new one right away).</summary>
        private void HandleTabSelect(string? id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tab = _tabs.FirstOrDefault(x => x.Id == id);
            if (tab == null || tab == _active)
                return;
            _active = tab;
            _active.SendActiveFile(); // the active-file toggle is per tab
            SaveHistory();
        }

        /// <summary>web→host <c>tab.close</c> (✕, confirmed in the page while a turn runs): the tab's turn stops, its
        /// chat stays in the history. Closing the last tab leaves one empty tab. A tab running Remote Control stays —
        /// the remote session would have no tab to show it.</summary>
        private void HandleTabClose(string? id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tab = _tabs.FirstOrDefault(x => x.Id == id);
            if (tab == null)
                return;
            if (tab.RemoteSessionActive)
            {
                PostNotify("End remote control before closing this tab.", "warning", 6000);
                PostTabsState();
                return;
            }
            var index = _tabs.IndexOf(tab);
            tab.Close();
            _tabs.Remove(tab);
            if (_tabs.Count == 0)
            {
                _active = AddTab(mayRestoreLast: false);
                PostTabsState();
                _active.SendInitialSession();
            }
            else
            {
                if (tab == _active)
                    _active = _tabs[Math.Min(index, _tabs.Count - 1)];
                PostTabsState();
            }
            _active.SendActiveFile();
            SaveHistory();
            CheckWorkspaceSwitch(); // a switch may have waited for this tab's turn
        }

        private void OnWorkspaceOpenedOrClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // solution events arrive on the UI thread
            CheckWorkspaceSwitch();
        }

        /// <summary>
        /// Makes the chat history follow the open solution/folder. The store was picked when the tool window was
        /// created; after a VS restart that is often before the solution finished loading, so the panel sat on the
        /// "global" history and the next prompts went on in that store's last chat — an unrelated, old conversation
        /// resumed in the real workspace (user report 2026-10-29: half an afternoon landed in a chat from August).
        /// Saves the current history, loads the new workspace's and restores its last chat like a fresh start. While
        /// a turn or a remote session runs the switch waits (<see cref="_workspaceSwitchPending"/>) and is retried
        /// when it ends. Also called on <c>ready</c>, in case the solution opened before these events were wired.
        /// </summary>
        private void CheckWorkspaceSwitch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
                return;
            var dir = _package.GetSolutionDirectory();
            var path = SessionHistoryStore.GetHistoryPath(dir);
            string? current;
            lock (_history)
                current = _history.FilePath;
            if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
            {
                _workspaceSwitchPending = false;
                return;
            }
            if (_tabs.Any(tab => tab.IsBusy || tab.RemoteSessionActive))
            {
                _workspaceSwitchPending = true; // retried from OnTurnCompleted / the remote end
                return;
            }
            _workspaceSwitchPending = false;

            lock (_history)
            {
                _history.Save();
                _history.ReloadFrom(path, _package.GetOptions().HistoryRetentionDays);
            }
            // The open tabs belonged to the old workspace: all but the first close, the first starts over in the
            // new store (its last chat comes back like on a start). The old CLI sessions (and task processes) end.
            foreach (var tab in _tabs.Skip(1).ToList())
            {
                tab.Close();
                _tabs.Remove(tab);
            }
            _active = _tabs[0];
            _active.ResetForWorkspace();
            _openTabsRestored = false;
            if (!_uiReady)
                return; // "ready" restores the new workspace's tabs and renders them
            RestoreOpenTabs();
            PostTabsState();
            foreach (var tab in _tabs)
                tab.SendInitialSession();
            _chat.SendActiveFile();
            foreach (var tab in _tabs)
                tab.TryAdoptRemoteSession();
            _chat.PostSystemNote(string.IsNullOrEmpty(dir)
                ? "No solution or folder open — showing the chats that belong to no workspace"
                : "Showing the chats of " + System.IO.Path.GetFileName(dir!.TrimEnd('\\', '/')));
        }

        /// <summary>Background-thread entry for a deferred workspace switch (turn ended, remote session ended).</summary>
        private void RetryPendingWorkspaceSwitch()
        {
            if (!_workspaceSwitchPending)
                return;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                CheckWorkspaceSwitch();
            }).Task.Forget();
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

        /// <summary>host→web <c>appearance.state</c>: syntax theme (gear popover), the color of your own messages, the
        /// bubble tint and color-blind diffs (settings window). Also switches the inline editor review's palette.</summary>
        private void SendAppearanceState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var o = _package.GetOptions();
            Editor.EditReviewPalette.ColorBlind = o.ColorBlindDiffs;
            Post(new JObject
            {
                ["type"] = "appearance.state",
                ["syntaxTheme"] = AstrogatorOptions.NormalizeSyntaxTheme(o.SyntaxTheme),
                ["userColor"] = NormalizeHexColor(o.UserMessageColor ?? ""),
                ["bubbleIntensity"] = AstrogatorOptions.NormalizeBubbleIntensity(o.BubbleIntensity),
                ["colorBlindDiffs"] = o.ColorBlindDiffs,
                ["showTimestamps"] = o.ShowMessageTimestamps,
            });
        }

        /// <summary>host→web <c>font.state</c>: chat and code font (id + size), the user's own fonts for the
        /// dropdowns, and — only for the ones in use — their files as data URIs (<c>faces</c>) for a FontFace.
        /// Also deletes stored fonts the settings window removed.</summary>
        private void SendFontState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = _package.GetOptions();
            var custom = (options.CustomFonts ?? new System.Collections.Generic.List<string>())
                .Where(CustomFontStore.Exists).ToList();
            // not during a live change of the settings window: it can still be cancelled (and the font back)
            if (!_package.OptionsPreview)
                CustomFontStore.DeleteUnused(custom);
            var chat = AstrogatorOptions.NormalizeFont(options.ChatFont, custom);
            var code = AstrogatorOptions.NormalizeFont(options.CodeFont, custom);

            var faces = new JObject();
            foreach (var id in new[] { chat, code }.Distinct())
            {
                if (!id.StartsWith(AstrogatorOptions.CustomFontPrefix, StringComparison.Ordinal))
                    continue;
                var data = CustomFontStore.ReadDataUri(id.Substring(AstrogatorOptions.CustomFontPrefix.Length));
                if (data != null)
                    faces[id] = data;
            }
            Post(new JObject
            {
                ["type"] = "font.state",
                ["chat"] = new JObject { ["font"] = chat, ["scale"] = AstrogatorOptions.ClampFontScale(options.ChatFontScale) },
                ["code"] = new JObject { ["font"] = code, ["scale"] = AstrogatorOptions.ClampFontScale(options.CodeFontScale) },
                ["custom"] = new JArray(custom.Select(f => (JToken)new JObject
                {
                    ["id"] = AstrogatorOptions.CustomFontPrefix + f,
                    ["name"] = CustomFontStore.DisplayName(f),
                })),
                ["faces"] = faces,
            });
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

        /// <summary>The <c>usage</c> object of host→web <c>context.usage</c> (also kept on the session record
        /// and replayed in <c>session.init</c>): totals, the categories in the CLI's order and the detail
        /// lists (memory files, MCP servers, agents, skills).</summary>
        internal static JObject BuildContextUsage(ContextUsageEvent usage)
        {
            JArray Items(IEnumerable<ContextItem> items)
            {
                var list = new JArray();
                foreach (var i in items)
                {
                    var o = new JObject { ["name"] = i.Name, ["tokens"] = i.Tokens };
                    if (!string.IsNullOrEmpty(i.Detail))
                        o["detail"] = i.Detail;
                    if (i.Count > 0)
                        o["count"] = i.Count;
                    list.Add(o);
                }
                return list;
            }

            var categories = new JArray();
            foreach (var c in usage.Categories)
                categories.Add(new JObject { ["name"] = c.Name, ["tokens"] = c.Tokens, ["kind"] = c.Kind });
            return new JObject
            {
                ["totalTokens"] = usage.TotalTokens,
                ["maxTokens"] = usage.MaxTokens,
                ["percentage"] = usage.Percentage,
                ["autoCompactThreshold"] = usage.AutoCompactThreshold,
                ["autoCompactEnabled"] = usage.AutoCompactEnabled,
                ["model"] = usage.Model,
                ["categories"] = categories,
                ["memoryFiles"] = Items(usage.MemoryFiles),
                ["mcpServers"] = Items(usage.McpServers),
                ["agents"] = Items(usage.Agents),
                ["skills"] = Items(usage.Skills),
                ["at"] = DateTime.UtcNow.ToString("o"),
            };
        }

        /// <summary>Host-side /help (the headless CLI rejects it, decision in NOTES).</summary>
        private string BuildHelpText()
        {
            string resumeHint;
            lock (_history)
                resumeHint = _chat.Record.HasCliSession
                    ? "`claude --resume " + _chat.Record.Id + "`"
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
            if (_chat.RemoteSessionActive)
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
            if (_chat.RemoteSessionActive)
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

        // ── Attachment previews ─────────────────────────────────────────────────
        // The WebUI shows expandable inline previews for image/text attachments in a user message.
        // It requests content lazily via `attachment.preview {path, token}`; we read the file off the
        // UI thread and post the result back keyed by `token`. A missing / oversized / binary file
        // yields ok:false → the WebUI downgrades the chip to a plain, non-expandable name.
        private static readonly HashSet<string> _attImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".apng", ".avif" };
        private static readonly HashSet<string> _attTextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".config",
          ".csv", ".tsv", ".log", ".cs", ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx", ".html", ".htm",
          ".css", ".scss", ".less", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".cc", ".go",
          ".rs", ".rb", ".php", ".sh", ".bat", ".ps1", ".psm1", ".sql", ".gradle", ".properties",
          ".editorconfig", ".gitignore", ".gitattributes", ".env", ".sln", ".csproj", ".props",
          ".targets", ".razor", ".cshtml", ".vue", ".svelte", ".swift", ".lua", ".pl", ".dart",
          ".scala", ".groovy" };

        private const long AttImageMaxBytes = 16 * 1024 * 1024; // 16 MB — refuse to inline larger images
        private const int AttTextMaxChars = 200 * 1024;         // ~200k chars of text, then mark truncated

        private static string AttImageMime(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".apng": return "image/apng";
                case ".avif": return "image/avif";
                default: return "application/octet-stream";
            }
        }

        private void HandleAttachmentPreview(string rawPath, string token)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(rawPath))
                return;
            var (path, _) = SplitLineSuffix(rawPath);
            // Read + encode off the UI thread; Post() marshals the reply back to the WebView.
            _ = Task.Run(() =>
            {
                var reply = BuildAttachmentPreview(path);
                reply["type"] = "attachment.preview";
                reply["path"] = rawPath; // echo exactly what the WebUI holds
                reply["token"] = token;
                Post(reply);
            });
        }

        private static JObject BuildAttachmentPreview(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    return new JObject { ["ok"] = false, ["reason"] = "missing" };
                var ext = System.IO.Path.GetExtension(path) ?? "";
                if (_attImageExts.Contains(ext))
                {
                    if (new System.IO.FileInfo(path).Length > AttImageMaxBytes)
                        return new JObject { ["ok"] = false, ["reason"] = "toolarge" };
                    var bytes = System.IO.File.ReadAllBytes(path);
                    return new JObject
                    {
                        ["ok"] = true,
                        ["kind"] = "image",
                        ["dataUri"] = "data:" + AttImageMime(ext) + ";base64," + Convert.ToBase64String(bytes),
                    };
                }
                if (_attTextExts.Contains(ext))
                {
                    string content;
                    bool truncated;
                    using (var reader = new System.IO.StreamReader(path, System.Text.Encoding.UTF8, true))
                    {
                        var buf = new char[AttTextMaxChars];
                        int read = reader.ReadBlock(buf, 0, AttTextMaxChars);
                        content = new string(buf, 0, read);
                        truncated = !reader.EndOfStream;
                    }
                    return new JObject
                    {
                        ["ok"] = true,
                        ["kind"] = "text",
                        ["text"] = content,
                        ["truncated"] = truncated,
                    };
                }
                return new JObject { ["ok"] = false, ["reason"] = "binary" };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["reason"] = "error", ["error"] = ex.Message };
            }
        }

        /// <summary>Sets <c>exists</c> on every attachment of every user message, so the WebUI can decide
        /// at render time whether a chip is previewable (files deleted since the chat was recorded stay
        /// plain, non-expandable name chips).</summary>
        private static void MarkAttachmentExistence(JArray messages)
        {
            foreach (var m in messages.OfType<JObject>())
            {
                if (!string.Equals(m.Value<string>("role"), "user", StringComparison.Ordinal))
                    continue;
                if (!(m["attachments"] is JArray atts))
                    continue;
                foreach (var a in atts.OfType<JObject>())
                {
                    var p = a.Value<string>("path");
                    bool ex = false;
                    if (!string.IsNullOrEmpty(p))
                    {
                        var (sp, _) = SplitLineSuffix(p!);
                        try { ex = System.IO.File.Exists(sp); } catch { ex = false; }
                    }
                    a["exists"] = ex;
                }
            }
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
                PostNotify("Paste from clipboard failed: " + ex.Message, autoCloseMs: 10000);
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
                PostNotify("Insert into editor failed: " + ex.Message, autoCloseMs: 10000);
            }
        }

        /// <summary>web→host editor.openFile: open the file referenced by an Edit/Write/Read card in the
        /// VS text editor (plain open, not the diff review). UI thread.</summary>
        private void HandleOpenFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path))
                return;
            // Directory targets (e.g. a Grep/Glob card whose `path` is a folder) can't be
            // opened as a document — reveal them in Windows Explorer instead.
            if (System.IO.Directory.Exists(path))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    PostNotify("Could not open the folder in Explorer: " + ex.Message, autoCloseMs: 10000);
                }
                return;
            }
            if (!System.IO.File.Exists(path))
            {
                PostNotify("File not found: " + path, autoCloseMs: 10000);
                return;
            }
            // Only file types VS owns (source, project, config, text) open as VS documents. Anything
            // Windows has a default program for — images, PDFs, Office docs, media — goes to that
            // program: a PNG belongs in the user's image viewer, not in VS's image editor
            // (FileOpenRouter.Decide). Unassociated/unknown types still land in VS, and executables /
            // installers / scripting hosts are only revealed in Explorer — a click in the transcript
            // must never *run* something a turn produced.
            var target = FileOpenRouter.Decide(path);
            if (target == FileOpenTarget.Explorer)
            {
                if (!RevealInExplorer(path))
                    PostNotify("Could not show the file in Explorer: " + path, autoCloseMs: 10000);
                return;
            }
            if (target == FileOpenTarget.DefaultProgram)
            {
                if (OpenWithDefaultProgram(path))
                    return;
                // No handler after all (association vanished, ShellExecute refused) → try VS, then Explorer.
            }
            try
            {
                // LOGVIEWID_Primary lets VS pick the appropriate editor for the file type
                // (text editor for code, notebook editor for .ipynb, …) instead of forcing
                // the text view onto a file it has a real editor for.
                VsShellUtilities.OpenDocument(_package, path, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out IVsWindowFrame frame);
                frame?.Show();
            }
            catch (Exception ex)
            {
                // Files VS has no editor for (e.g. .exe/.dll or an unregistered binary type) can't be
                // opened as a document — fall back to selecting the file in Windows Explorer.
                if (!RevealInExplorer(path))
                    PostNotify("Could not open the file in Visual Studio: " + ex.Message, autoCloseMs: 10000);
            }
        }

        /// <summary>Opens <paramref name="path"/> with its registered default program. False when the
        /// shell refused (no handler), so the caller can fall back to a VS editor.</summary>
        private static bool OpenWithDefaultProgram(string path)
        {
            try
            {
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                proc?.Dispose();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Selects <paramref name="path"/> in a Windows Explorer window. Returns false on failure.</summary>
        private static bool RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── session events (background threads) → host → web ─────────────────

        internal static double? TurnCost(double? previousTotal, bool resumedSession, double total)
        {
            var baseline = previousTotal ?? (resumedSession ? (double?)null : 0);
            if (baseline == null)
                return null;
            // a smaller total = the CLI started a fresh conversation (stale --resume retried without it)
            return total >= baseline.Value ? total - baseline.Value : total;
        }

        /// <summary>Persists the history file in the background (best-effort).</summary>
        private void SaveHistory()
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                lock (_history)
                {
                    _history.ActiveOpenId = _active?.Record.Id; // the next start selects this tab again
                    _history.Save();
                }
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

            long contextTokens, contextWindow;
            lock (_history)
            {
                contextTokens = _chat.Record.ContextTokens;
                contextWindow = _chat.Record.ContextWindow;
            }
            var msg = new JObject
            {
                ["type"] = "usage.update",
                ["tokens"] = _chat.TotalTokens,
                ["contextTokens"] = contextTokens,
                ["contextWindow"] = contextWindow,
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
            SendFontState();  // fonts added/removed in the settings window → dropdowns + active fonts
            SendAppearanceState(); // message color, bubble tint, color-blind diffs live in the settings window
            _chat.SendActiveFile(); // the auto-add-active-file toggle may have changed
            SendBannerSettings(); // announcement/update opt-in may have changed in the settings window
            SendCheckpointSettings(); // checkpoints are only configured there → push the new state
            foreach (var tab in _tabs)
                tab.SendModeState();  // popover state may have changed → keep the UI and the host in step
            _checkpoints.Filter = BuildCheckpointFilter(_package.GetOptions()); // applies to the next snapshot
            SyncAllTabsFromOptions(); // incl. the MCP timeout below (options are the source of truth)
            ClaudeCliCapabilities.Invalidate();  // the executable path may have changed → re-probe its --help
            ClaudeModelCatalog.Invalidate();     // …and re-probe which models that binary knows
            SendModelCatalog();                  // push the (possibly different) picker list
            RunAutoUpdateCheck(force: false);    // auto-update may just have been switched on (throttled)
            var promptTimeoutMs = PromptTimeoutMs(_package.GetOptions());
            foreach (var tab in _tabs)
                tab.UpdateToolTimeout(promptTimeoutMs);            // config `timeout` (the value the CLI prefers)
            if (!_package.GetOptions().PromptSuggestions)
                Post(new JObject { ["type"] = "prompt.suggestion", ["text"] = "" }); // just switched off → drop the shown one
        }

        /// <summary>Configured prompt timeout (settings, minutes) as milliseconds for the CLI env var.</summary>
        private static int PromptTimeoutMs(AstrogatorOptions o) =>
            AstrogatorOptions.ClampPromptTimeoutMinutes(o.PromptTimeoutMinutes) * 60_000;

        private void OnActiveDocumentChanged(string? path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _chat.SendActiveFile();
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

        // ── file checkpoints / rewind ─────────────────────────────────────────
        // A snapshot is taken before each prompt ("-pre") and at the end of each turn ("-post"), so a
        // turn's changes are the diff pre..post — which covers bash commands, scripts and subagents,
        // not only the Write/Edit tools. See docs/git-checkpoints-plan.md.

        private const int CheckpointSnapshotTimeoutMs = 30_000;
        /// <summary>Upper bound on how many past turns a restore scope walks (one diff each).</summary>
        private const int CheckpointScopeMaxTurns = 200;

        /// <summary>Snapshot filter from the settings (0 MB = no size limit).</summary>
        private static CheckpointFilter BuildCheckpointFilter(AstrogatorOptions o) => new CheckpointFilter
        {
            MaxFileBytes = (long)AstrogatorOptions.ClampCheckpointMaxFileMb(o.CheckpointMaxFileMb) * 1024 * 1024,
            Whitelist = o.CheckpointExtensionsAreWhitelist,
            Extensions = AstrogatorOptions.NormalizeExtensions(o.CheckpointExtensions),
        };

        /// <summary>Checkpoint state for session.init / live updates.</summary>
        private JObject BuildCheckpointsState()
        {
            var options = _package.GetOptions();
            var gitAvailable = GitCheckpointService.IsGitAvailable();
            return new JObject
            {
                ["enabled"] = options.CheckpointsEnabled && gitAvailable,
                ["gitAvailable"] = gitAvailable,
                ["decided"] = options.CheckpointsDecided,
                ["retentionDays"] = options.CheckpointRetentionDays,
            };
        }

        private void SendCheckpointSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var state = BuildCheckpointsState();
            state["type"] = "checkpoint.settings";
            Post(state);
        }

        private void SendTheme()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Post(ThemeService.BuildThemeMessage(_package.GetOptions().ThemeModeString));
        }


        /// <summary>
        /// Pushes the model list for the Model · Mode picker (<c>models.list</c>). Resolved off the UI
        /// thread: the catalog comes from <c>models.json</c> (repo copy, else the bundled one) and each
        /// ID is verified against the installed CLI, because a model can be released before the CLI
        /// accepts it — see <see cref="ClaudeModelCatalog"/> (cost-free, no API call). Partial results
        /// are pushed while a first-run sweep is still probing; until the first one arrives the WebUI
        /// keeps its built-in list, so a slow or missing CLI never empties the picker.
        /// </summary>
        private void SendModelCatalog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = _package.GetOptions();
            var exeOverride = options.ClaudeExecutablePath;
            var allowRemote = options.ModelCatalogFetchEnabled; // own opt-in (consent popup / settings)
            var cwd = _package.GetSolutionDirectory();
            var extensionDir = System.IO.Path.GetDirectoryName(typeof(WebViewBridge).Assembly.Location) ?? "";

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;

                var repoConfig = ClaudeModelCatalog.ReadRepoConfig(extensionDir);
                var context = new ModelCatalogContext
                {
                    ExePath = ClaudeExecutableLocator.Locate(exeOverride),
                    WorkingDirectory = cwd,
                    ExtensionDirectory = extensionDir,
                    AllowRemoteDefaults = allowRemote,
                    GitHubRepo = repoConfig.Repo,
                    Branch = repoConfig.Branch,
                };

                // Progress posts only happen while something is being probed. With a complete cache
                // (every start after the first sweep) nothing is probed, so the final list has to be
                // posted here — otherwise the WebUI silently stays on its built-in fallback.
                var rows = await ClaudeModelCatalog.RefreshAsync(context, PostModelCatalog).ConfigureAwait(false);
                PostModelCatalog(rows);
            }).Task.Forget();
        }

        /// <summary>
        /// Manual "Check now" from the settings window. The two banners are fetched by the WebUI (it
        /// owns their fetch, cache and rendering) → <c>check.run</c>, answered with <c>check.result</c>;
        /// the model catalog is the host's → forced refresh here. Either way the result goes back to
        /// the window via <see cref="CodeAstrogatorPackage.ReportManualCheck"/>.
        /// </summary>
        private void OnManualCheckRequested(string kind)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (kind)
            {
                case "notice":
                    Post(new JObject { ["type"] = "check.run", ["kind"] = kind });
                    break;
                case "update":
                    Post(new JObject { ["type"] = "check.run", ["kind"] = kind });
                    RunAutoUpdateCheck(force: true); // with auto-update on, also fetch + stage right away
                    break;
                case "models":
                    RunManualModelCheck();
                    break;
            }
        }

        // ── auto-update (ExtensionUpdater) ───────────────────────────────────

        /// <summary>On window open: a staged update shows its banner; an update applied since the last
        /// session is announced once and cleaned up.</summary>
        private void SendUpdateState()
        {
            var installed = CodeAstrogatorPackage.InstalledVersion();
            if (string.IsNullOrEmpty(installed))
                return;
            var applied = ExtensionUpdater.TakeAppliedUpdate(installed!);
            if (applied != null)
                PostSystemNote("Code Astrogator was updated to " + applied + ".");
            PostStagedUpdate(ExtensionUpdater.ReadPending(), installed!);
        }

        private void PostStagedUpdate(PendingUpdate? pending, string installed)
        {
            if (pending == null || !ExtensionUpdater.IsNewer(pending.Version, installed))
                return;
            Post(new JObject
            {
                ["type"] = "update.staged",
                ["version"] = pending.Version,
                ["installed"] = installed,
                ["url"] = pending.HtmlUrl ?? "",
            });
        }

        /// <summary>
        /// With auto-update on: check the newest release (throttled to 3 h unless <paramref name="force"/>),
        /// download + verify it, and show the "ready, installs when VS closes" banner. Off the UI thread.
        /// </summary>
        private void RunAutoUpdateCheck(bool force)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // On by default, but only acted on once the user has seen the choice (consent popup /
            // settings) — no download before the first-run popup was answered.
            var options = _package.GetOptions();
            if (!options.AutoUpdateEnabled || !options.AutoUpdateDecided)
                return;
            var extensionDir = System.IO.Path.GetDirectoryName(typeof(WebViewBridge).Assembly.Location) ?? "";

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var (id, installed) = CodeAstrogatorPackage.InstalledIdentity();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(installed))
                    return;
                var repo = ClaudeModelCatalog.ReadRepoConfig(extensionDir).Repo;
                var result = await ExtensionUpdater.CheckAndStageAsync(repo, id!, installed!, force).ConfigureAwait(false);
                if (result.Staged != null)
                    PostStagedUpdate(result.Staged, installed!);
                else if (force && result.Error != null)
                    PostSystemNote("Auto-update: " + result.Error);
            }).Task.Forget();
        }

        /// <summary>"Restart now" in the update banner: mark the staged update for relaunch and close
        /// Visual Studio the normal way (unsaved files are still prompted for). The shutdown hook starts
        /// the installer, which restarts VS when it is done.</summary>
        private void HandleUpdateRestart()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var installed = CodeAstrogatorPackage.InstalledVersion() ?? "";
            var pending = ExtensionUpdater.ReadPending();
            if (pending == null || !ExtensionUpdater.IsNewer(pending.Version, installed))
            {
                PostSystemNote("There is no downloaded update to install.");
                return;
            }
            pending.Relaunch = true;
            try
            {
                ExtensionUpdater.WritePending(ExtensionUpdater.DefaultUpdatesDir, pending);
                if (Package.GetGlobalService(typeof(DTE)) is DTE2 dte)
                    dte.ExecuteCommand("File.Exit");
            }
            catch (Exception ex)
            {
                PostSystemNote("Could not close Visual Studio for the update: " + ex.Message
                               + " — it installs the next time you close Visual Studio.");
            }
        }

        private void RunManualModelCheck()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var exeOverride = _package.GetOptions().ClaudeExecutablePath;
            var cwd = _package.GetSolutionDirectory();
            var extensionDir = System.IO.Path.GetDirectoryName(typeof(WebViewBridge).Assembly.Location) ?? "";

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                try
                {
                    var repoConfig = ClaudeModelCatalog.ReadRepoConfig(extensionDir);
                    var context = new ModelCatalogContext
                    {
                        ExePath = ClaudeExecutableLocator.Locate(exeOverride),
                        WorkingDirectory = cwd,
                        ExtensionDirectory = extensionDir,
                        AllowRemoteDefaults = true,
                        ForceRemoteFetch = true, // the click is the consent for this one request
                        GitHubRepo = repoConfig.Repo,
                        Branch = repoConfig.Branch,
                    };
                    var rows = await ClaudeModelCatalog.RefreshAsync(context, PostModelCatalog).ConfigureAwait(false);
                    PostModelCatalog(rows);

                    var report = ClaudeModelCatalog.LastReport;
                    if (report == null)
                        _package.ReportManualCheck("models", false, "No result — try again.");
                    else
                        _package.ReportManualCheck("models", report.Succeeded, report.Describe(rows.Count));
                }
                catch (Exception ex)
                {
                    _package.ReportManualCheck("models", false, "Check failed: " + ex.Message);
                }
            }).Task.Forget();
        }

        /// <summary>Sends one <c>models.list</c>; ignored while the list is still empty (nothing probed
        /// yet) so the UI keeps its built-in fallback instead of showing an empty picker.</summary>
        private void PostModelCatalog(IReadOnlyList<PickerModel> models)
        {
            if (models == null || models.Count == 0)
                return;

            var list = new JArray();
            foreach (var m in models)
                list.Add(new JObject
                {
                    ["id"] = m.Id,
                    ["label"] = m.Label,
                    ["family"] = m.Family,
                    ["primary"] = m.Primary,
                });

            Post(new JObject { ["type"] = "models.list", ["models"] = list });
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
                ["modelsEnabled"] = options.ModelCatalogFetchEnabled,
                ["modelsDecided"] = options.ModelCatalogFetchDecided,
                ["updateEnabled"] = options.UpdateCheckEnabled,
                ["updateDecided"] = options.UpdateCheckDecided,
                ["appVersion"] = GetInstalledVersion(),
            });
        }

        /// <summary>
        /// "What's new": the first time the panel runs on a newer version, the page shows the CHANGELOG.md sections
        /// since the last version the user saw (the file ships in the VSIX — no network). A fresh install only
        /// records the version (its first run brings the consent popup anyway); an existing install without a
        /// recorded version (upgraded from ≤ 1.2.1) sees the current version's notes. The page answers
        /// <c>whatsnew.seen</c> when the popup is closed; until then it comes back on the next start.
        /// </summary>
        private void SendWhatsNew()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var options = _package.GetOptions();
            var current = GetInstalledVersion();
            var last = string.IsNullOrWhiteSpace(options.LastSeenVersion) ? null : options.LastSeenVersion;
            if (!ChangelogReader.IsNewer(current, last))
                return;
            if (last == null && !(options.NoticeFetchDecided || options.UpdateCheckDecided || options.CheckpointsDecided
                                  || options.ModelCatalogFetchDecided || options.AutoUpdateDecided))
            {
                options.LastSeenVersion = current; // fresh install: nothing "new" to tell yet
                _package.SaveOptions();
                return;
            }
            IReadOnlyList<ChangelogEntry> entries;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(WebViewBridge).Assembly.Location) ?? "";
                var file = System.IO.Path.Combine(dir, "CHANGELOG.md");
                entries = System.IO.File.Exists(file)
                    ? ChangelogReader.Since(ChangelogReader.Parse(System.IO.File.ReadAllText(file)), last, current)
                    : Array.Empty<ChangelogEntry>();
            }
            catch
            {
                entries = Array.Empty<ChangelogEntry>();
            }
            if (entries.Count == 0)
            {
                options.LastSeenVersion = current; // nothing to show for this version
                _package.SaveOptions();
                return;
            }
            Post(new JObject
            {
                ["type"] = "whatsnew.show",
                ["version"] = current,
                ["entries"] = new JArray(entries.Select(e => new JObject
                {
                    ["version"] = e.Version,
                    ["date"] = e.Date,
                    ["markdown"] = e.Markdown,
                })),
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

        /// <summary>A dim system note for something that concerns the whole window (update, settings, workspace):
        /// shown in — and recorded into — the active tab.</summary>
        private void PostSystemNote(string text) => _chat.PostSystemNote(text);

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

        /// <summary>Transient, dismissible banner at the top of the chat for incidental action
        /// failures (e.g. "couldn't open this file"). Unlike <see cref="PostError"/> it is NOT
        /// recorded into the conversation history and does NOT change the turn status — use it for
        /// UI-action problems that shouldn't clutter the transcript. <paramref name="level"/> is
        /// "error" | "warning" | "info"; <paramref name="autoCloseMs"/> 0 = sticky (user dismisses).</summary>
        private void PostNotify(string message, string level = "error", int autoCloseMs = 0)
        {
            Post(new JObject
            {
                ["type"] = "notify",
                ["level"] = level,
                ["message"] = message,
                ["autoClose"] = autoCloseMs,
            });
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
