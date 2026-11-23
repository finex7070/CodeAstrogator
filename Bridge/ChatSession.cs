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

namespace CodeAstrogator.Bridge
{
    internal sealed partial class WebViewBridge
    {
        /// <summary>
        /// One chat tab: its CLI session (<see cref="ClaudeSessionService"/> + process host), its own MCP
        /// permission server, the chat record it shows, and all turn state — permissions and questions,
        /// checkpoints, end-of-turn review, shell calls, background tasks, Remote Control. Everything shared by
        /// the tabs (WebView, history store, checkpoint service, edit-review controller, toasts, options, usage,
        /// theme, …) lives in the enclosing <see cref="WebViewBridge"/> (the "chat host"). Every host→web message
        /// this class posts carries <c>tab</c> = <see cref="Id"/> (see docs/multi-session-tabs-plan.md).
        /// </summary>
        internal sealed class ChatSession : IDisposable
        {
            private readonly WebViewBridge _host;

            /// <summary>Stable tab id ("t1", "t2", …) — never the chat's id, which changes after the first turn.</summary>
            public string Id { get; }

            private readonly ClaudeSessionService _session;
            private readonly IClaudeProcessHost _processHost; // one CLI process per turn
            // The chat this tab shows (open in the store). Replaced only on the UI thread; read and mutated under
            // the _history lock like every other record.
            private SessionRecord _record;
            private JObject? _streamingAssistant; // transcript accumulator for the active assistant block
            private bool _turnHadAssistantOutput; // gates the result-text fallback (decision #8)
            private bool _inFollowUpTurn;         // the running turn was started by the CLI (background task), not a prompt
            private bool _sessionStartAnnounced;  // "Session started" note, once per session (decision #9)
            private IRemoteSession? _remoteTerminal; // Remote Control: CLI background session (or the console fallback)
            private DateTime _remoteMirrorStamp;     // last-seen write time of the remote session's transcript
            private long _remoteMirrorSize;          // …and its size (several writes within one timestamp tick)
            private bool _remoteAdoptChecked;        // looked for a still-running background session once
            private bool _remoteMirrorFromStart;     // adopted session: the shown chat may be stale → mirror the first look too
            private string? _remoteProjectDir;       // ~\.claude\projects\<munged cwd> (guarded by _history)
            // Prompting the remote session from the panel (RemotePromptSender): one prompt at a time; while Claude works
            // there it waits here and goes out at the next "idle" poll. UI thread.
            private string? _remotePendingPrompt;
            // "Remote control" was clicked while a turn ran: two processes must not work on one conversation, so the
            // start waits for the turn to let go (OnTurnReleased). UI thread.
            private bool _remoteStartPending;
            private const string RemotePendingMessage =
                "Remote control starts as soon as Claude has finished this turn.";
            private bool _remoteSending;
            // Live mirror: what the page shows of the remote transcript (one JSON string per message) → only changes are
            // posted (transcript.mirror). null = the page shows something else (rebuild on the next change). Guarded by
            // _remoteMirrorLock (the status poll and the file watcher both mirror).
            private System.Collections.Generic.List<string>? _remoteMirrorSent;
            private readonly object _remoteMirrorLock = new object();
            private System.IO.FileSystemWatcher? _remoteWatcher;
            private string? _remoteWatchedFile;
            private bool _activeFileSessionEnabled = true; // per-session override (seeded from ActiveFileOnByDefault; AutoAddActiveFile has priority)
            private readonly McpPermissionBridge _permission; // in-process MCP permission server (§A5), one per tab
            // "Notify me" (opt-in): Windows notifications at turn end and for prompts that wait for the user.
            // Prompt toasts are tagged with their request id (removed once answered), the turn-end toast with TurnToastTag.
            private const string TurnToastTag = "turn";
            private string? _turnResultText; // the main result's text, for the turn-end toast (stream thread)
            private string? _toastWorkspace; // the running turn's cwd (set in RunPrompt — GetSolutionDirectory is UI-thread only)

            // ── live console output of running shell commands ───────────────────
            // Tails the CLI's per-task .output file (announced by system/task_started) and streams it into
            // the Bash/PowerShell card as tool.output. Session id + cwd come from the turn's system/init.
            private readonly TaskOutputWatcher _taskOutput;
            private string? _turnSessionId;
            private string? _turnCwd;
            // Running foreground shell commands by tool_use_id, for the card's stop button (tool.stop):
            // tool name + command from tool_use, task id from system/task_started. Per turn.
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShellCall> _shellCalls =
                new System.Collections.Concurrent.ConcurrentDictionary<string, ShellCall>(StringComparer.Ordinal);
            // Running background shells by task id (outlive their turn — see ClaudeSessionService), for the
            // stop buttons of the background-task bar (background.stop).
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShellCall> _backgroundShells =
                new System.Collections.Concurrent.ConcurrentDictionary<string, ShellCall>(StringComparer.Ordinal);

            // ── file checkpoints (rewind) ────────────────────────────────────────────
            // A snapshot of the work-tree is taken before each prompt and at the end of each turn, so
            // "what changed during Claude's turn" is a tree-to-tree diff (covers bash/scripts, not just
            // the edit tools). The shas ride along on the user message in the history.
            private JObject? _checkpointTurnMessage;    // user message of the turn about to start (UI thread)
            private CheckpointTurnState? _checkpointTurn; // in-flight turn: where to put the "after" snapshot
            private string? _pendingRestoreNote;          // prepended to the next prompt after a rewind

            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingPermission> _pendingPermissions
                = new System.Collections.Concurrent.ConcurrentDictionary<string, PendingPermission>();
            // recorded (approved) permission messages by tool_use_id, so a later tool.result can
            // upgrade their persisted status to applied/failed. Cleared at turn end.
            private readonly System.Collections.Generic.Dictionary<string, JObject> _recordedPermissions
                = new System.Collections.Generic.Dictionary<string, JObject>();
            // ── "Review edits at end of turn" (opt-in, acceptEdits) ──────────────────
            // All three are guarded by _turnReviewLock (written from the MCP/stream background threads,
            // read/mutated on the UI thread). Baselines accumulate during a turn; the reviews are built at
            // turn end and persist until every file is decided or "Keep all"/"Discard all". Further prompts
            // are allowed meanwhile: the baselines of still-pending files are kept, so the next turn's edits
            // join the same list, each file still compared against its content before Claude first touched it.
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

            private bool _closed; // this tab was disposed (the window may live on)
            // A prompt.send that arrived while the session was still busy — the previous turn's "ready" reaches the
            // page before the busy flag drops (see ClaudeSessionService.TurnReleased), and a queued prompt is sent
            // at once. Dropping it lost the prompt (the page had already shown it). Runs on TurnReleased. UI thread.
            private JObject? _deferredPrompt;
            private bool _mayRestoreLast; // the first init may restore the workspace's last chat ("Restore last session")

            // ── what the tabs share (owned by the host) ─────────────────────────────
            private CodeAstrogatorPackage _package => _host._package;
            private SessionHistoryStore _history => _host._history;
            private GitCheckpointService _checkpoints => _host._checkpoints;
            private EditReviewController _editReview => _host._editReview;
            private WindowsToastNotifier _toasts => _host._toasts;
            private ActiveDocumentTracker _activeDocs => _host._activeDocs;
            private bool _disposed => _closed || _host._disposed;
            private string? _planLabel => _host._planLabel;
            private void PostNotify(string message, string level = "error", int autoCloseMs = 0) =>
                _host.PostNotify(message, level, autoCloseMs);
            private void SaveHistory() => _host.SaveHistory();
            private void RefreshUsage() => _host.RefreshUsage();
            private string BuildHelpText() => _host.BuildHelpText();
            private JObject BuildLimits() => _host.BuildLimits();
            private JObject BuildCheckpointsState() => _host.BuildCheckpointsState();
            private void CheckWorkspaceSwitch()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _host.CheckWorkspaceSwitch();
            }
            private (int top, int bottom)? GetActiveSelection()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _host.GetActiveSelection();
            }
            // thread-agnostic (the turn end calls it on the stream thread; the switch itself marshals)
#pragma warning disable VSTHRD010
            private void RetryPendingWorkspaceSwitch() => _host.RetryPendingWorkspaceSwitch();
#pragma warning restore VSTHRD010

            public ChatSession(WebViewBridge host, string id, bool mayRestoreLast)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _host = host;
                Id = id;
                _mayRestoreLast = mayRestoreLast;
                _processHost = new ClaudeCliProcessHost();
                _session = new ClaudeSessionService(_processHost);
                _taskOutput = new TaskOutputWatcher(PostToolOutput);
                var opt = _package.GetOptions();
                // seed the session from the persisted Model·Mode popover state (sticky across new
                // chats + VS restarts); the popover writes these back via SaveOptions on each change
                SyncSessionSettingsFromOptions();
                _activeFileSessionEnabled = opt.ActiveFileOnByDefault; // initial per-session toggle = option default

                // In-process MCP permission server (Teil A §A5), one per tab (own port + config file, so no
                // routing inside the server). Best-effort: if it fails to start, IsAvailable stays false and
                // edits fall back to the CLI default (no prompt flags).
                _permission = new McpPermissionBridge
                {
                    OnPermissionRequested = HandlePermissionRequestedAsync,
                    ToolTimeoutMs = PromptTimeoutMs(opt), // config `timeout` wins over the env var (CLI 2.1.178)
                };
                try { _permission.Start(); } catch { /* leave unavailable */ }
                _session.PermissionBridge = _permission;

                lock (_history)
                    _record = _history.StartNew();

                _session.EventReceived += OnSessionEvent;
                _session.TurnCompleted += OnTurnCompleted;
                _session.TurnReleased += OnTurnReleased;
            }

            /// <summary>Ends this tab: the running turn and a process kept for background tasks stop, open prompts
            /// are denied, the MCP listener goes. A running Remote Control session keeps running (re-adopted on
            /// the next start). UI thread.</summary>
            public void Dispose()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_closed)
                    return;
                _session.EventReceived -= OnSessionEvent;
                _session.TurnCompleted -= OnTurnCompleted;
                _session.TurnReleased -= OnTurnReleased;
                _deferredPrompt = null;
                _session.StopTurn();
                _session.Shutdown(); // a process kept alive for background tasks goes too
                DenyAllPendingPermissions("Tool window closed");
                _closed = true;
                _permission.Dispose(); // stops the MCP listener + deletes the config file
                _taskOutput.Dispose(); // stop tailing any still-running command output
                StopWatchingRemoteTranscript();
                _remoteTerminal?.Dispose(); // stops polling; the session itself keeps running (re-adopted next start)
            }

            /// <summary>Remote control waits for this tab's turn to end (see <see cref="HandleRemoteStart"/>).</summary>
            internal bool RemoteStartPending => _remoteStartPending;

            /// <summary>True while a turn of this tab runs (working or waiting for a permission/answer).</summary>
            internal bool IsBusy => _session.IsBusy;

            /// <summary>The chat this tab shows. Read under the history lock.</summary>
            internal SessionRecord Record => _record;

            /// <summary>Tokens this tab's CLI session reported so far (statusbar).</summary>
            internal long TotalTokens => _session.TotalTokens;

            /// <summary>The prompt timeout changed in the settings → rewrite this tab's MCP config.</summary>
            internal void UpdateToolTimeout(int timeoutMs) => _permission.UpdateToolTimeout(timeoutMs);

            /// <summary>Posts a host→web message of this tab (tagged with <c>tab</c>).</summary>
            private void Post(JObject msg)
            {
                if (_closed)
                    return; // the page dropped this tab
                msg["tab"] = Id;
                _host.Post(msg);
            }
            /// <summary>Mirrors every persisted Model·Mode popover value onto the live session settings.
            /// The WebUI renders these from the OPTIONS, so any drift between the two shows a control that
            /// lies (most sharply "Review all edits at end of turn": the toggle read on while the turn ran
            /// with it off, so no baseline was captured and no end-of-turn review ever appeared). Called
            /// from the constructor, on every options change, and before each turn.
            /// <see cref="SessionSettings.PlanMode"/> is deliberately NOT synced — it is per-session state
            /// with no option behind it.</summary>
            internal void SyncSessionSettingsFromOptions()
            {
                var opt = _package.GetOptions();
                _session.Settings.Model = string.IsNullOrEmpty(opt.DefaultModel) ? null : opt.DefaultModel;
                _session.Settings.Effort = opt.DefaultEffortString;
                _session.Settings.Ultracode = opt.UltracodeEnabled;
                _session.Settings.PermissionMode = opt.PermissionModeString;
                _session.Settings.ReviewEditsAtTurnEnd = opt.ReviewEditsAtTurnEnd;
                _session.Settings.McpToolTimeoutMs = PromptTimeoutMs(opt);
                _session.Settings.PromptSuggestions = opt.PromptSuggestions;
            }

            // ── web → host ────────────────────────────────────────────────────────

            private void HandlePromptSend(JObject msg)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var text = msg.Value<string>("text") ?? "";
                // text OR at least one explicit attachment — a screenshot alone is a valid prompt
                var hasAttachments = msg["attachments"] is JArray { Count: > 0 };
                if (string.IsNullOrWhiteSpace(text) && !hasAttachments)
                    return;
                if (_session.IsBusy)
                {
                    // The page only sends while it shows "ready" — so this is the previous turn winding down (or a
                    // follow-up turn the CLI just started). Hold it and run it once the session lets go.
                    _deferredPrompt = msg;
                    return;
                }
                // A pending post-turn edit review no longer gates the next prompt: its changes keep being
                // collected (see CarryTurnReviewIntoNextTurn).

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
                if (RemoteSessionActive)
                {
                    // Remote Control owns the conversation: the prompt goes into the running remote session (typed into
                    // its input box, see RemotePromptSender); the live mirror then shows it. Files as @-references only.
                    SendRemotePrompt(BuildReferenceText(text, references));
                    return;
                }
                // Images go along as base64 image blocks in the stream-json message — the CLI downscales them
                // itself (an @-referenced image is only expanded up to 256 KiB). Their path stays in the list
                // as plain text, so the model still knows where the file lives.
                var inlineImages = Core.CliImageAttachments.SelectInline(
                    references.Where(r => string.IsNullOrEmpty(r.suffix)).Select(r => r.path));
                if (references.Count > 0)
                {
                    var sb = new System.Text.StringBuilder(text.Trim().Length > 0 ? text + "\n\n" : "");
                    sb.Append("Attached files:");
                    foreach (var r in references)
                    {
                        sb.Append('\n');
                        if (string.IsNullOrEmpty(r.suffix) && inlineImages.Contains(r.path, StringComparer.OrdinalIgnoreCase))
                            sb.Append(r.path).Append(" (image, included in this message)");
                        else
                            sb.Append(Core.CliReferenceFormatter.FormatFileReference(r.path, r.suffix));
                    }
                    // Whatever image could not go inline (beyond the size caps, or a format like TIFF) is an
                    // @-reference again, which the CLI drops silently above 256 KiB. The model can still see
                    // the file via Read - say so.
                    var hint = Core.CliAttachmentHint.BuildReadHint(
                        references.Select(r => r.path)
                            .Where(p => !inlineImages.Contains(p, StringComparer.OrdinalIgnoreCase))
                            .Where(Core.CliAttachmentHint.IsOversizedImage));
                    if (hint != null)
                        sb.Append("\n\n").Append(hint);
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
                if (_record.Title == "Untitled")
                {
                    // an attachment-only prompt titles the session after its first file
                    var titleText = string.IsNullOrWhiteSpace(typedText)
                        ? display.FirstOrDefault()?.Value<string>("name") ?? "Untitled"
                        : typedText;
                    _record.Title = titleText.Length > 48 ? titleText.Substring(0, 48) + "…" : titleText;
                    Post(new JObject { ["type"] = "session.title", ["title"] = _record.Title }); // the tab strip shows it
                }
                // This message owns the turn's checkpoints (RunPrompt takes the "before" snapshot and
                // stores its sha here; OnTurnCompleted adds the "after" one).
                _checkpointTurnMessage = userMsg;

                PostStatus("working");
                RunPrompt(text, inlineImages);
            }

            private void RunPrompt(string text, System.Collections.Generic.IReadOnlyList<string>? images = null)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var options = _package.GetOptions();
                var cwd = _package.GetSolutionDirectory();
                // The options are the source of truth for everything the popover shows; re-mirror them here
                // so a turn can never run with settings the UI does not display (see
                // SyncSessionSettingsFromOptions). RunTurnAsync pins the mode right after this.
                SyncSessionSettingsFromOptions();

                _turnHadAssistantOutput = false;
                _inFollowUpTurn = false;       // a turn the user started (its footer starts a new sum)
                _turnResultText = null;
                _toastWorkspace = cwd;
                _toasts.Remove(ToastTag(TurnToastTag));  // the user is back — the last "finished" notification is done
                lock (_history)
                    _record.PromptSuggestion = null; // it predicted this prompt — used up either way
                CarryTurnReviewIntoNextTurn(); // a pending review keeps collecting this turn's changes

                // Checkpoint bookkeeping for this turn (UI-thread reads; the snapshot itself runs below,
                // off-thread, before the CLI starts). A slash-command turn has no user message, so it
                // gets a snapshot but no rewind button.
                var checkpointMessage = _checkpointTurnMessage;
                _checkpointTurnMessage = null;
                var checkpointsOn = options.CheckpointsEnabled
                    && !string.IsNullOrEmpty(cwd)
                    && GitCheckpointService.IsGitAvailable();
                var turnKey = checkpointMessage?.Value<string>("id") ?? Guid.NewGuid().ToString("n");
                var sessionKey = _record.Id;
                _checkpointTurn = checkpointsOn
                    ? new CheckpointTurnState
                    {
                        Cwd = cwd!,
                        PostRef = GitCheckpointService.BuildRefName(sessionKey, turnKey + "-post"),
                        Message = checkpointMessage,
                    }
                    : null;

                // A rewind since the last prompt → tell Claude where the files were rolled back to, so it
                // doesn't keep reasoning about a file state that no longer exists. Consumed once.
                var restoreNote = _pendingRestoreNote;
                _pendingRestoreNote = null;
                if (!string.IsNullOrEmpty(restoreNote))
                    text = restoreNote + "\n\n" + text;

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    if (checkpointsOn)
                    {
                        // ConfigureAwait(false): inside a JoinableTask the default would resume on the
                        // main thread, and the turn must keep running off it.
                        await TakePreTurnCheckpointAsync(cwd!, sessionKey, turnKey, checkpointMessage)
                            .ConfigureAwait(false);
                    }
                    try
                    {
                        await _session.RunTurnAsync(text, options.ClaudeExecutablePath, cwd, images);
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
                    NotifyToast(req.RequestId, isQuestion
                        ? ToastContent.ForQuestion(req.Input)
                        : ToastContent.ForPermission(req.ToolName, req.Input));
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
                    var msgs = _record.Messages;
                    for (int i = msgs.Count - 1; i >= 0; i--)
                        if (msgs[i].Value<string>("role") == "tool" && msgs[i].Value<string>("id") == p.RequestId)
                            msgs.RemoveAt(i);
                    msgs.Add(rec);
                    _record.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            /// <summary>Settles a pending permission once: removes it, disposes its ct registration,
            /// completes the TCS. Returns the removed entry (null if unknown/already settled).</summary>
            private PendingPermission? ResolvePending(string requestId, string behavior, string? message, JObject? updatedInput = null)
            {
                if (!_pendingPermissions.TryRemove(requestId, out var p))
                    return null;
                p.Registration.Dispose();
                _toasts.Remove(ToastTag(requestId)); // answered / expired — its notification is stale now
                p.Tcs.TrySetResult(new PermissionDecision { Behavior = behavior, Message = message, UpdatedInput = updatedInput });
                return p;
            }

            /// <summary>Records a decided permission card (role "permission") into the history, replacing
            /// the redundant tool message of the same tool_use_id (the permission card represents it).</summary>
            private void RecordPermissionMessage(PendingPermission p, string status, bool autoApproved = false)
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
                if (autoApproved)
                    rec["autoApproved"] = true; // no decision was asked for → Compact hides it after a reload too
                lock (_history)
                {
                    var msgs = _record.Messages;
                    for (int i = msgs.Count - 1; i >= 0; i--)
                        if (msgs[i].Value<string>("role") == "tool" && msgs[i].Value<string>("id") == p.RequestId)
                            msgs.RemoveAt(i);
                    msgs.Add(rec);
                    _record.UpdatedAtUtc = DateTime.UtcNow;
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
                catch (Exception ex) { PostNotify("Could not open the edit for review: " + ex.Message); }
            }

            /// <summary>Called by the EditReviewController once every hunk of a request is decided.
            /// Reconstructs the tool input from the accepted hunks and settles the blocked CLI call:
            /// allow + updatedInput when anything was accepted, otherwise deny. UI thread.</summary>
            internal void FinalizeEditReview(string requestId)
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
                        "approved", autoApproved: true);
                }).Task.Forget();
            }

            /// <summary>Builds the cumulative per-file review from the captured baselines vs. the current disk
            /// content and posts the changed-files list above the composer. Called at turn end (and on stop).
            /// Idempotent — safe if it runs from both paths.</summary>
            private void BuildAndPostTurnReviewList()
            {
                if (!_session.EditsRouteThroughHook)
                {
                    // The toggle is on, yet this turn did NOT route edits through the hook — so the CLI
                    // applied them itself and no pre-edit baseline exists. Every failure below used to be
                    // silent ("the option does nothing"), which is the hardest kind to diagnose: say it.
                    if (_package.GetOptions().ReviewEditsAtTurnEnd
                        && _session.LaunchedPermissionMode == "acceptEdits")
                    {
                        PostSystemNote("End-of-turn edit review was not armed for this turn — the CLI applied "
                            + "the edits itself, so there is no pre-edit baseline to review. Switch \"Review all "
                            + "edits at end of turn\" off and on again to re-arm it.");
                    }
                    return; // feature off / not acceptEdits this turn
                }

                // Snapshot the state under the lock; the notes are posted outside it (PostSystemNote takes
                // the history lock, and nesting the two in one order only would be a trap for later edits).
                System.Collections.Generic.Dictionary<string, string>? baselines = null;
                int skipped;
                lock (_turnReviewLock)
                {
                    skipped = _turnBaselineSkip.Count;
                    if (_turnEditBaselines.Count > 0)
                        baselines = new System.Collections.Generic.Dictionary<string, string>(_turnEditBaselines, StringComparer.OrdinalIgnoreCase);
                }
                if (skipped > 0)
                    PostBaselineSkipNote(skipped);
                if (baselines == null)
                    return;

                var files = new JArray();
                var built = new System.Collections.Generic.Dictionary<string, TurnReview>(StringComparer.OrdinalIgnoreCase);
                System.Collections.Generic.Dictionary<string, TurnReview> previous;
                lock (_turnReviewLock)
                    previous = new System.Collections.Generic.Dictionary<string, TurnReview>(_turnReviews, StringComparer.OrdinalIgnoreCase);
                var otherTabs = 0;
                foreach (var kv in baselines)
                {
                    var path = kv.Key;
                    var baseline = kv.Value;
                    // Another tab already reviews this file (both changed it): one review per file — a second one
                    // would revert to ITS baseline and wipe the other tab's edits. It stays in that tab's list.
                    if (_host.IsInOtherTabsTurnReview(this, path))
                    {
                        otherTabs++;
                        continue;
                    }
                    string current;
                    bool exists;
                    try
                    {
                        exists = System.IO.File.Exists(path);
                        current = exists ? System.IO.File.ReadAllText(path) : "";
                    }
                    catch { continue; } // unreadable now → skip
                    if (!exists) continue; // edited then deleted this turn → nothing to review on disk

                    // Still pending from an earlier turn and untouched since: keep that review as it is (with
                    // the decisions already made in the editor).
                    if (previous.TryGetValue(path, out var earlier) && earlier.Current == current)
                    {
                        built[path] = earlier;
                        files.Add(TurnReviewFileEntry(path, earlier.Session));
                        continue;
                    }

                    // Model the whole-file change as a Write: old = baseline, new = current disk content.
                    var input = new JObject { ["file_path"] = path, ["content"] = current };
                    EditReviewSession session;
                    try { session = EditReviewSession.Build("Write", input, () => baseline); }
                    catch { continue; }
                    if (!session.HasHunks) continue; // edited then reverted to identical → drop

                    built[path] = new TurnReview { Session = session, Baseline = baseline, IsNew = baseline.Length == 0, Current = current };
                    files.Add(TurnReviewFileEntry(path, session));
                }

                lock (_turnReviewLock)
                {
                    _turnReviews.Clear();
                    foreach (var kv in built)
                        _turnReviews[kv.Key] = kv.Value;
                }
                if (otherTabs > 0)
                    PostSystemNote(otherTabs == 1
                        ? "1 changed file is already in another tab's review list — review it there."
                        : otherTabs + " changed files are already in another tab's review list — review them there.");
                if (files.Count == 0)
                {
                    // a carried-over list whose files all ended up unchanged (edited back) → empty it
                    if (previous.Count > 0)
                        Post(new JObject { ["type"] = "editReview.turnListClear" });
                    return;
                }
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

            internal bool HasPendingTurnReview()
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

            /// <summary>Reports files whose pristine content could not be captured (the write had already
            /// landed, or the read failed). Those are deliberately never reviewed — reverting against an
            /// untrustworthy baseline would destroy content — but staying quiet about it made the feature
            /// look broken instead of partial.</summary>
            private void PostBaselineSkipNote(int skipped) =>
                PostSystemNote(skipped == 1
                    ? "1 changed file could not be reviewed: its pre-edit baseline was not readable."
                    : skipped + " changed files could not be reviewed: their pre-edit baselines were not readable.");

            /// <summary>A new prompt starts. Without a pending review everything starts fresh (as before). With one,
            /// the pending files keep their baselines — this turn's edits are captured first-touch for the other
            /// files and the list is rebuilt cumulatively at turn end. Open in-editor reviews are closed first:
            /// they lock the buffer read-only, and Claude may edit those very files now (the reviews themselves
            /// stay, with their decisions, and are re-attached at turn end). UI thread.</summary>
            private void CarryTurnReviewIntoNextTurn()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool pending;
                lock (_turnReviewLock)
                {
                    pending = _turnReviews.Count > 0;
                    _turnBaselineSkip.Clear();
                    if (!pending)
                    {
                        _turnEditBaselines.Clear();
                    }
                    else
                    {
                        // only the still-pending files carry their baseline over; anything else (decided,
                        // edited back to identical) gets a fresh first-touch baseline if Claude edits it again
                        foreach (var path in _turnEditBaselines.Keys.Where(p => !_turnReviews.ContainsKey(p)).ToList())
                            _turnEditBaselines.Remove(path);
                    }
                }
                if (pending)
                    CloseOwnTurnReviews();
            }

            /// <summary>The review actions (open/finish/keep/discard) rewrite files — not while a turn may be
            /// editing them. The WebUI greys them out meanwhile; this is the authoritative half.</summary>
            private bool TurnReviewActionBlocked()
            {
                if (!_session.IsBusy)
                    return false;
                PostSystemNote("Wait until the turn has finished before reviewing the changed files.");
                return true;
            }

            /// <summary>Clears all captured baselines and pending reviews (session change, Keep/Discard all).</summary>
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
                if (TurnReviewActionBlocked())
                    return;
                TurnReview? review;
                lock (_turnReviewLock)
                    _turnReviews.TryGetValue(path ?? "", out review);
                if (review == null)
                    return; // stale (already decided / cleared)
                try { _editReview.OpenForPath(path!, review.Session, () => FinalizeTurnFileReview(path!), () => PostTurnFileState(path!)); }
                catch (Exception ex) { PostNotify("Could not open the changed file for review: " + ex.Message); }
            }

            /// <summary>web→host editReview.finishTurnFile: the user pressed Finish (editor toolbar or the chat
            /// chip) to complete a file whose every change is decided → commit it and drop it from the list.
            /// Ignored unless every hunk is decided (the chip's button is disabled until then anyway). UI thread.</summary>
            private void HandleFinishTurnFile(string path)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (TurnReviewActionBlocked())
                    return;
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
                {
                    _turnReviews.Remove(path!);
                    // decided — a later edit (e.g. a follow-up turn) needs a fresh first-touch baseline
                    _turnEditBaselines.Remove(path!);
                }
                Post(new JObject { ["type"] = "editReview.turnFileDone", ["path"] = path });
            }

            /// <summary>web→host editReview.keepAll: accept every changed file as-is (no disk changes), close any
            /// open review, and clear the list + gate.</summary>
            private void HandleKeepAllTurnReviews()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (TurnReviewActionBlocked())
                    return;
                CloseOwnTurnReviews(); // restore any open review buffers to the applied content
                ResetTurnReviewState();
                Post(new JObject { ["type"] = "editReview.turnListClear" });
            }

            /// <summary>web→host editReview.discardAll: revert every changed file to its pre-turn baseline
            /// (delete files created this turn), then clear the list + gate. Files currently open in a review go
            /// through the controller (buffer + save); the rest are written to disk directly.</summary>
            private void HandleDiscardAllTurnReviews()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (TurnReviewActionBlocked())
                    return;
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
                {
                    _history.Close(_record);
                    _record = _history.StartNew();
                }
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
            internal void SendInitialSession()
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                SessionRecord? restore = null;
                lock (_history)
                {
                    if (_record.Messages.Count > 0)
                    {
                        restore = _record; // WebView reloaded mid-session
                    }
                    else if (_mayRestoreLast && _package.GetOptions().RestoreLastSession)
                    {
                        // the most recent chat not already open in another tab (one chat, one tab)
                        var id = _history.ToSessionList().OfType<JObject>()
                            .Select(s => s.Value<string>("id"))
                            .FirstOrDefault(i => !string.IsNullOrEmpty(i) && !_history.IsOpen(i!));
                        if (!string.IsNullOrEmpty(id))
                            restore = SwitchRecord(id!);
                    }
                    _mayRestoreLast = false; // once: a WebView reload re-shows what is open, a new tab starts empty
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
                    record = SwitchRecord(sessionId);
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
                        string.IsNullOrEmpty(sessionId) ? _record.Id : sessionId!, title);
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
                        currentId = _record.Id;
                    if (sessionId == currentId)
                    {
                        PostSystemNote("Stop the current turn first to delete the active session.");
                        return;
                    }
                }

                bool deleted, wasCurrent;
                lock (_history)
                {
                    wasCurrent = ReferenceEquals(_history.Find(sessionId!), _record);
                    (deleted, _) = _history.Delete(sessionId!);
                    if (deleted && wasCurrent)
                        _record = _history.StartNew();
                }
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
                        CloseOwnTurnReviews();
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

            /// <summary>Shows the chat with this id instead of the current one (call under the _history lock): the
            /// current record is closed in the store (dropped there when empty), the other one opened. Returns the
            /// shown record, or null when the id is unknown (nothing changes then).</summary>
            private SessionRecord? SwitchRecord(string id)
            {
                var found = _history.Find(id);
                if (found == null)
                    return null;
                if (!ReferenceEquals(found, _record))
                {
                    _history.Close(_record);
                    _history.Open(found.Id);
                    _record = found;
                }
                return found;
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
                ThreadHelper.ThrowIfNotOnUIThread(); // every caller re-inits the session on the UI thread first
                lock (_remoteMirrorLock)
                    _remoteMirrorSent = null; // the page shows the record now — a running mirror rebuilds on its next change
                JArray messages;
                lock (_history)
                {
                    messages = new JArray();
                    foreach (var m in record.Messages)
                        messages.Add(m.DeepClone());
                }
                MarkAttachmentExistence(messages); // flag which attachment files still exist (previewable)

                Post(new JObject
                {
                    ["type"] = "transcript.load",
                    ["sessionId"] = record.Id,
                    ["title"] = record.Title,
                    ["messages"] = messages,
                    // the last turn's next-prompt prediction, back after a restart (off → none)
                    ["promptSuggestion"] = _package.GetOptions().PromptSuggestions ? record.PromptSuggestion : null,
                });
                PostCheckpointExpiry(record); // grey out rewind buttons whose snapshot the retention sweep took
            }

            private void HandleSlashRun(string command, string? args)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                switch (command)
                {
                    case "/clear":
                        HandleSessionNew();
                        break;
                    case "/login": // the sign-in dialog (claude auth login, see AuthController)
                        _host._auth.StartLogin(console: false);
                        break;
                    case "/logout":
                        _host._auth.Logout();
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
                    if (_host.RemoteRunsInOtherTab(this))
                    {
                        PostRemoteTerminalState("error", "Remote control already runs in another tab — end it there first.");
                        return;
                    }
                    _remoteStartPending = true; // started by OnTurnReleased
                    PostRemoteTerminalState("pending", RemotePendingMessage);
                    return;
                }
                _remoteStartPending = false;
                if (HasPendingTurnReview() && !RemoteSessionActive)
                {
                    // Remote control takes the chat over — a pending end-of-turn review would be stranded (and a
                    // remote start armed during the turn used to fail on the list that turn just built): keep the
                    // changes as they are, like "Keep all".
                    CloseOwnTurnReviews();
                    ResetTurnReviewState();
                    Post(new JObject { ["type"] = "editReview.turnListClear" });
                    PostSystemNote("Changed files kept as they are — remote control takes over this chat.");
                }
                if (RemoteSessionActive)
                {
                    // already running (e.g. UI reloaded) — re-announce the current state
                    PostRemoteSessionState();
                    return;
                }
                if (_host.RemoteRunsInOtherTab(this))
                {
                    PostRemoteTerminalState("error", "Remote control already runs in another tab — end it there first.");
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
                string title;
                lock (_history)
                {
                    resumeId = _record.HasCliSession ? _record.Id : null;
                    title = _record.Title;
                }

                var cwd = _package.GetSolutionDirectory();
                // remote control refuses an untrusted workspace — pre-accept it (best-effort).
                ClaudeWorkspaceTrust.EnsureTrusted(cwd);

                PostRemoteTerminalState("starting");

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    // This chat's own CLI process may still be alive (finishing a prompt suggestion right after the
                    // turn, or kept for background tasks). `--resume` of a session that is still running starts a COPY
                    // under a new id (CLI 2.1.33x) — end it first so the remote session continues this very chat.
                    await _session.ShutdownAsync().ConfigureAwait(false);
                    // CLI 2.1.29x+: a background session (no window; link/QR in the panel). Older: the console.
                    var caps = await ClaudeCliCapabilities.GetAsync(exe).ConfigureAwait(false);
                    IRemoteSession session = caps.SupportsBackgroundSessions
                        ? new BackgroundRemoteSession()
                        : new RemoteTerminalLauncher();
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AttachRemoteSession(session);

                    // The name the session gets in the Claude app's list
                    var name = string.IsNullOrWhiteSpace(title) || title == "Untitled" ? "Code Astrogator" : title;
                    bool ok;
                    try { ok = await session.StartAsync(exe, resumeId, cwd, name, _package.DisposalToken); }
                    catch { ok = false; }

                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ok)
                        PostRemoteSessionState(resumeId != null);
                    else
                        PostRemoteTerminalState("error",
                            "Couldn't start remote control" + (session.LastError != null ? ": " + session.LastError : "."));
                }).Task.Forget();
            }

            /// <summary>The prompt text with its file references as plain @-references (no inline images).</summary>
            private static string BuildReferenceText(string text,
                System.Collections.Generic.IReadOnlyList<(string path, string suffix)> references)
            {
                if (references.Count == 0)
                    return text;
                var sb = new System.Text.StringBuilder(text.Trim().Length > 0 ? text + "\n\n" : "");
                sb.Append("Attached files:");
                foreach (var r in references)
                    sb.Append('\n').Append(Core.CliReferenceFormatter.FormatFileReference(r.path, r.suffix));
                return sb.ToString();
            }

            /// <summary>A prompt from the panel while Remote Control runs: sent now, or held while Claude works there.</summary>
            private void SendRemotePrompt(string text)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_remoteTerminal is not BackgroundRemoteSession bg || bg.BackgroundId == null)
                {
                    PostRemotePrompt("error", "Prompts from the panel need a remote session that runs in the background "
                        + "(Claude Code CLI 2.1.29x or newer). Use the terminal instead.");
                    return;
                }
                if (_remoteSending || _remotePendingPrompt != null)
                {
                    PostRemotePrompt("error", "A prompt is already on its way to the remote session.");
                    return;
                }
                if (string.Equals(bg.Activity, "busy", StringComparison.OrdinalIgnoreCase))
                {
                    _remotePendingPrompt = text;
                    PostRemotePrompt("queued");
                    return;
                }
                StartRemoteSend(bg, text);
            }

            private void StartRemoteSend(BackgroundRemoteSession bg, string text)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
                var id = bg.BackgroundId;
                if (exe == null || id == null)
                {
                    PostRemotePrompt("error", "Claude CLI not found.");
                    return;
                }
                var cwd = _package.GetSolutionDirectory();
                _remoteSending = true;
                PostRemotePrompt("sending");
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    string? error;
                    try { error = await RemotePromptSender.SendAsync(exe, id, text, cwd, _package.DisposalToken).ConfigureAwait(false); }
                    catch (Exception ex) { error = ex.Message; }
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _remoteSending = false;
                    PostRemotePrompt(error == null ? "sent" : "error", error);
                    if (error == null && _remoteTerminal == bg)
                        MirrorRemoteTranscript(bg); // show it right away
                }).Task.Forget();
            }

            /// <summary>Status poll / file change (any thread): a held prompt goes out once the remote session is idle.</summary>
            private void FlushRemotePrompt(IRemoteSession session)
            {
                if (!string.Equals(session.Activity, "idle", StringComparison.OrdinalIgnoreCase))
                    return;
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var text = _remotePendingPrompt;
                    if (text == null || _remoteSending || _remoteTerminal != session
                        || session is not BackgroundRemoteSession bg || !bg.IsActive)
                        return;
                    _remotePendingPrompt = null;
                    StartRemoteSend(bg, text);
                }).Task.Forget();
            }

            /// <summary>web→host <c>remote.cancelPrompt</c>: take back a held prompt (it stays in the page's input).</summary>
            private void CancelRemotePrompt()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_remotePendingPrompt == null)
                    return;
                _remotePendingPrompt = null;
                PostRemotePrompt("cancelled");
            }

            /// <summary>host→web <c>remote.prompt { state: sending|queued|sent|error|cancelled, message? }</c>.</summary>
            private void PostRemotePrompt(string state, string? message = null)
            {
                var msg = new JObject { ["type"] = "remote.prompt", ["state"] = state };
                if (message != null)
                    msg["message"] = message;
                Post(msg);
            }

            /// <summary>Makes <paramref name="session"/> the current remote session and wires its events.</summary>
            private void AttachRemoteSession(IRemoteSession session)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_remoteTerminal != null)
                {
                    _remoteTerminal.Ended -= OnRemoteTerminalEnded;
                    _remoteTerminal.Changed -= OnRemoteSessionChanged;
                    _remoteTerminal.Dispose();
                }
                _remoteTerminal = session;
                StopWatchingRemoteTranscript();
                lock (_remoteMirrorLock)
                {
                    _remoteMirrorStamp = DateTime.MinValue;
                    _remoteMirrorSize = 0;
                }
                _remoteMirrorFromStart = false;
                _remotePendingPrompt = null;
                // the CLI's transcript folder for this workspace (GetSolutionDirectory is UI-thread-only)
                var projectDir = CliSessionReader.GetProjectDirectory(_package.GetSolutionDirectory() ?? "");
                lock (_history)
                    _remoteProjectDir = projectDir;
                session.Ended += OnRemoteTerminalEnded;
                session.Changed += OnRemoteSessionChanged;
            }

            /// <summary>Remote sessions from before a VS restart (or a reload of the tool window), once per bridge:
            /// <list type="bullet">
            /// <item>one that <b>ended while VS was closed</b> (PC shut down, ended from the phone, …) never got its
            /// end-of-session import — its conversation is imported into the chat's history now and the leftover
            /// registry entry removed (<c>claude rm</c>), so this happens once;</item>
            /// <item>one <b>still running</b> on the current chat is adopted: the panel locks again instead of
            /// letting a prompt race it.</item>
            /// </list></summary>
            internal void TryAdoptRemoteSession()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_remoteAdoptChecked || RemoteSessionActive)
                    return;
                _remoteAdoptChecked = true;
                var importEnded = _host.IsFirstTab(this);
                var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
                if (exe == null)
                    return;
                string? currentId;
                lock (_history)
                    currentId = _record.HasCliSession ? _record.Id : null;
                var cwd = _package.GetSolutionDirectory();
                var projectDir = CliSessionReader.GetProjectDirectory(cwd ?? "");

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        var caps = await ClaudeCliCapabilities.GetAsync(exe).ConfigureAwait(false);
                        if (!caps.SupportsBackgroundSessions)
                            return;
                        var run = await ClaudeBackgroundSessions.RunAsync(exe, "agents --json --all", cwd, 30000, _package.DisposalToken)
                            .ConfigureAwait(false);
                        var background = ClaudeBackgroundSessions.ParseAgents(run.Output)
                            .Where(s => s.Kind == "background" && !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.SessionId))
                            .ToList();

                        // ended while VS was closed → read the transcripts of this workspace's chats (off the UI thread);
                        // once per window — the first tab does it, every tab only adopts its own running session
                        var ended = new List<(BackgroundSessionInfo Info, ImportedCliSession Transcript)>();
                        if (projectDir != null && importEnded)
                        {
                            foreach (var s in background.Where(s => ClaudeBackgroundSessions.HasEnded(s)))
                            {
                                bool known;
                                lock (_history)
                                    known = _history.Contains(s.SessionId!);
                                if (!known)
                                    continue;
                                var transcript = CliSessionReader.ImportTranscript(
                                    System.IO.Path.Combine(projectDir, s.SessionId + ".jsonl"));
                                if (transcript != null)
                                    ended.Add((s, transcript));
                            }
                        }

                        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var removed = new List<string>();
                        foreach (var (info, transcript) in ended)
                        {
                            if (ImportEndedRemoteSession(transcript))
                                removed.Add(info.Id!);
                        }
                        if (removed.Count > 0)
                        {
                            // drop the leftover registry entries (the transcripts stay) — best-effort, off the UI thread
                            _ = Task.Run(async () =>
                            {
                                foreach (var id in removed)
                                    await ClaudeBackgroundSessions.RunAsync(exe, "rm " + id, cwd, 30000, CancellationToken.None)
                                        .ConfigureAwait(false);
                            });
                        }

                        var running = currentId == null ? null : background.FirstOrDefault(s =>
                            string.Equals(s.SessionId, currentId, StringComparison.OrdinalIgnoreCase)
                            && !ClaudeBackgroundSessions.HasEnded(s));
                        if (running == null || RemoteSessionActive || _session.IsBusy)
                            return;
                        var session = new BackgroundRemoteSession();
                        AttachRemoteSession(session);
                        _remoteMirrorFromStart = true; // the chat may have moved on on the phone while VS was closed
                        session.Adopt(exe, cwd, running);
                        PostRemoteSessionState();
                        PostSystemNote("Remote control is still running for this chat");
                    }
                    catch
                    {
                        // best-effort — nothing adopted
                    }
                }).Task.Forget();
            }

            /// <summary>Puts the conversation of a remote session that ended while VS was closed into its chat
            /// (the CLI transcript is authoritative — it has every turn, local and remote) and reloads the view
            /// when it is the open chat. False when the open chat is busy (left for the next start).</summary>
            private bool ImportEndedRemoteSession(ImportedCliSession transcript)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool isCurrent;
                lock (_history)
                    isCurrent = string.Equals(_record.Id, transcript.SessionId, StringComparison.OrdinalIgnoreCase);
                if (isCurrent && _session.IsBusy)
                    return false;

                SessionRecord record;
                lock (_history)
                    record = _history.Import(transcript.SessionId, transcript.Title, transcript.Messages,
                        transcript.UpdatedAtUtc, transcript.ContextTokens);
                SaveHistory();

                if (isCurrent)
                {
                    AttachToRecord(record);
                    SendSessionInit();
                    SendActiveFile();
                    SendTranscript(record);
                    PostSystemNote("Remote session imported — it ended while Visual Studio was closed");
                }
                else
                {
                    PostSystemNote($"Remote session \"{record.Title}\" imported to the history — it ended while Visual Studio was closed");
                }
                return true;
            }

            /// <summary>Background thread: the remote session polled. Updates the panel (link/QR once known,
            /// busy/idle) and mirrors the conversation into the transcript when its file changed — so what is done
            /// from the phone shows up here live.</summary>
            private void OnRemoteSessionChanged()
            {
                var session = _remoteTerminal;
                if (session == null || !session.IsActive)
                    return;
                PostRemoteSessionState();
                WatchRemoteTranscript(session);
                MirrorRemoteTranscript(session);
#pragma warning disable VSTHRD010 // thread-agnostic: marshals to the UI thread itself
                FlushRemotePrompt(session);
#pragma warning restore VSTHRD010
            }

            /// <summary>Mirrors on every write of the session's transcript (debounced) instead of only at the 3 s status
            /// poll — the panel follows the remote conversation within a fraction of a second. Any thread.</summary>
            private void WatchRemoteTranscript(IRemoteSession session)
            {
                var sessionId = session.SessionId;
                string? projectDir;
                lock (_history)
                    projectDir = _remoteProjectDir;
                if (string.IsNullOrEmpty(sessionId) || projectDir == null || !System.IO.Directory.Exists(projectDir))
                    return;
                var file = sessionId + ".jsonl";
                lock (_remoteMirrorLock)
                {
                    if (_remoteWatchedFile == file && _remoteWatcher != null)
                        return;
                    _remoteWatcher?.Dispose();
                    _remoteWatchedFile = file;
                    try
                    {
                        var watcher = new System.IO.FileSystemWatcher(projectDir, file)
                        {
                            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                        };
                        var debounce = new System.Threading.Timer(_ =>
                        {
                            var current = _remoteTerminal;
                            if (current == session && current.IsActive)
                                MirrorRemoteTranscript(current);
                        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                        watcher.Changed += (_, __) => debounce.Change(250, System.Threading.Timeout.Infinite);
                        watcher.Disposed += (_, __) => debounce.Dispose();
                        watcher.EnableRaisingEvents = true;
                        _remoteWatcher = watcher;
                    }
                    catch
                    {
                        _remoteWatcher = null; // the 3 s poll still mirrors
                    }
                }
            }

            private void StopWatchingRemoteTranscript()
            {
                lock (_remoteMirrorLock)
                {
                    _remoteWatcher?.Dispose();
                    _remoteWatcher = null;
                    _remoteWatchedFile = null;
                    _remoteMirrorSent = null;
                }
            }

            private void MirrorRemoteTranscript(IRemoteSession session)
            {
                var sessionId = session.SessionId;
                if (string.IsNullOrEmpty(sessionId))
                    return;
                try
                {
                    string? projectDir;
                    lock (_history)
                        projectDir = _remoteProjectDir;
                    if (projectDir == null)
                        return;
                    var file = System.IO.Path.Combine(projectDir, sessionId + ".jsonl");
                    if (!System.IO.File.Exists(file))
                        return;
                    lock (_remoteMirrorLock)
                    {
                        var stamp = System.IO.File.GetLastWriteTimeUtc(file);
                        var size = new System.IO.FileInfo(file).Length;
                        if (stamp <= _remoteMirrorStamp && size == _remoteMirrorSize)
                            return;
                        var first = _remoteMirrorStamp == DateTime.MinValue;
                        _remoteMirrorStamp = stamp;
                        _remoteMirrorSize = size;
                        if (first && !_remoteMirrorFromStart)
                            return; // the panel already shows this state — only mirror what changes from now on
                        var imported = CliSessionReader.ImportTranscript(file);
                        if (imported == null)
                            return;
                        // Only what changed: the first message that differs from what the page has, and everything
                        // after it (usually just the newest message, or a tool card whose result arrived). The first
                        // mirror rebuilds the view — before it the page shows the chat's record, not this import.
                        var current = imported.Messages.Select(m => m.ToString(Newtonsoft.Json.Formatting.None)).ToList();
                        var sent = _remoteMirrorSent;
                        var from = 0;
                        if (sent != null)
                        {
                            while (from < sent.Count && from < current.Count && sent[from] == current[from])
                                from++;
                            if (from == sent.Count && from == current.Count)
                                return; // nothing new (e.g. only metadata lines were written)
                        }
                        var changed = new JArray();
                        for (var i = from; i < imported.Messages.Count; i++)
                            changed.Add(imported.Messages[i].DeepClone());
                        Post(new JObject
                        {
                            ["type"] = "transcript.mirror",
                            ["sessionId"] = imported.SessionId,
                            ["title"] = imported.Title,
                            ["reset"] = sent == null,
                            ["from"] = from,
                            ["messages"] = changed,
                        });
                        _remoteMirrorSent = current;
                    }
                }
                catch
                {
                    // the mirror is best-effort; the full import still happens when the session ends
                }
            }

            /// <summary>
            /// Ends the interactive remote session (closes the terminal). The launcher's Ended event
            /// then imports the now-advanced conversation and reloads it (see <see cref="OnRemoteTerminalEnded"/>).
            /// </summary>
            /// <summary>A running Bash/PowerShell command, tracked for the card's stop button.</summary>
            private sealed class ShellCall
            {
                public ShellCall(string toolName, string? command)
                {
                    ToolName = toolName;
                    Command = command;
                }

                public string ToolName { get; }
                public string? Command { get; }
                public string? TaskId { get; set; } // set by system/task_started
            }

            /// <summary>web→host <c>tool.stop</c>: kills just this command's shell (process tree) — the CLI
            /// reports it as failed (exit code 1) and the turn goes on. On failure the card's button is
            /// re-enabled via <c>tool.stopFailed</c> plus a note.</summary>
            private void HandleToolStop(string? toolUseId)
            {
                if (string.IsNullOrEmpty(toolUseId))
                    return;
                var pid = _session.ActiveProcessId;
                if (pid == null || !_shellCalls.TryGetValue(toolUseId!, out var call))
                {
                    PostToolStopFailed(toolUseId!, "The command is no longer running.");
                    return;
                }

                // WMI + taskkill take a moment — keep them off the UI thread.
                _ = Task.Run(() =>
                {
                    try
                    {
                        var target = ShellTaskProcess.Find(ProcessTree.GetChildren(pid.Value), call.TaskId, call.ToolName, call.Command);
                        if (target == null)
                        {
                            PostToolStopFailed(toolUseId!, "Could not find the process of this command — use the turn's Stop button instead.");
                            return;
                        }
                        ProcessTree.KillTree(target.Value);
                    }
                    catch (Exception ex)
                    {
                        PostToolStopFailed(toolUseId!, "Stopping the command failed: " + ex.Message);
                    }
                });
            }

            // Last list sent as background.tasks — re-sent after a WebView reload.
            private JArray _backgroundTaskList = new JArray();

            /// <summary>host→web <c>background.tasks</c>: the background tasks still running (the bar above the
            /// composer). They outlive their turn — the CLI process stays alive for them.</summary>
            private void PostBackgroundTasks(BackgroundTasksChangedEvent bg)
            {
                var list = new JArray();
                foreach (var t in bg.Tasks)
                {
                    list.Add(new JObject
                    {
                        ["id"] = t.TaskId,
                        ["description"] = string.IsNullOrWhiteSpace(t.Description) ? "Background task" : t.Description,
                    });
                }
                // forget shells whose task is gone
                foreach (var id in _backgroundShells.Keys)
                {
                    if (!bg.Tasks.Any(t => t.TaskId == id))
                        _backgroundShells.TryRemove(id, out _);
                }
                var hadTasks = _backgroundTaskList.Count > 0;
                _backgroundTaskList = list;
                Post(new JObject { ["type"] = "background.tasks", ["tasks"] = list.DeepClone() });
                // The process that kept them alive is gone (Stop, a new chat, changed model/mode, the bar's
                // stop button) — say so; the model learns it from the CLI's "stopped" notification next turn.
                if (bg.ProcessEnded && hadTasks)
                    PostSystemNote("Background tasks stopped");
            }

            /// <summary>web→host <c>background.stop { id }</c>: stops one background task — kills just its
            /// shell when it can be identified (the CLI then reports it and answers with a follow-up turn),
            /// otherwise ends the process that keeps the tasks alive (all of them stop; the conversation is
            /// unaffected). While a turn runs, only the single-shell kill is attempted.</summary>
            private void HandleBackgroundStop(string? taskId)
            {
                var pid = _session.ActiveProcessId;
                ShellCall? call = null;
                if (!string.IsNullOrEmpty(taskId))
                    _backgroundShells.TryGetValue(taskId!, out call);

                _ = Task.Run(() =>
                {
                    try
                    {
                        if (pid != null && call != null)
                        {
                            var target = ShellTaskProcess.Find(ProcessTree.GetChildren(pid.Value), call.TaskId, call.ToolName, call.Command);
                            if (target != null)
                            {
                                ProcessTree.KillTree(target.Value);
                                return;
                            }
                        }
                        if (_session.IsBusy)
                        {
                            PostSystemNote("⚠ Could not find the process of this background task — stop the turn to end it.");
                            return;
                        }
                        _session.StopBackgroundTasks(); // the exit clears the bar and posts the note
                    }
                    catch (Exception ex)
                    {
                        PostSystemNote("⚠ Stopping the background task failed: " + ex.Message);
                    }
                });
            }

            private void PostToolStopFailed(string toolUseId, string reason)
            {
                Post(new JObject { ["type"] = "tool.stopFailed", ["id"] = toolUseId });
                PostSystemNote("⚠ " + reason);
            }

            private void HandleRemoteStop()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_remoteStartPending)
                {
                    _remoteStartPending = false; // cancelled before it started
                    PostRemoteTerminalState("stopped");
                    return;
                }

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
                StopWatchingRemoteTranscript();
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_remotePendingPrompt == null)
                        return;
                    _remotePendingPrompt = null; // the page keeps it in the input
                    PostRemotePrompt("error", "Remote control ended — the waiting prompt was not sent. It is still in the input.");
                }).Task.Forget();

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
                        PostNotify("Importing the remote session failed: " + discoveryError);
                        CheckWorkspaceSwitch();
                        return;
                    }

                    if (imported.Count == 0)
                    {
                        PostSystemNote("Remote control ended — no changes to import");
                        CheckWorkspaceSwitch();
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
                        SwitchRecord(latest!.Id);
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
                    CheckWorkspaceSwitch(); // the solution changed while the remote session ran
                }).Task.Forget();
            }

            /// <summary>True while an interactive remote-control session is open in the terminal/console.</summary>
            internal bool RemoteSessionActive => _remoteTerminal?.IsActive == true;

            /// <summary>User-facing status line shown in the (locked) remote panel while a session runs.</summary>
            private string RemoteRunningMessage(bool resumed = true)
            {
                if (_remoteTerminal is BackgroundRemoteSession bg)
                {
                    if (bg.Url == null)
                        return "Connecting to Remote Control…";
                    return (resumed ? "This chat is live for Remote Control. " : "A new remote session is live. ")
                        + "Scan the code or open the link in the Claude app / claude.ai/code — what happens there shows "
                        + "up below. \"End remote session\" returns the chat to this window.";
                }
                return (resumed ? "This chat is now live in the terminal. " : "A new remote session is live in the terminal. ")
                    + "Open the link shown there from the Claude app or claude.ai/code. "
                    + "When you're done, close the terminal or click \"End remote session\" to return here.";
            }

            /// <summary>Posts the running session's state: <c>ready</c> + message, and for a background session
            /// the link (→ QR), busy/idle and whether it can be opened in a terminal. Any thread.</summary>
            private void PostRemoteSessionState(bool resumed = true)
            {
                var session = _remoteTerminal;
                if (session == null || !session.IsActive)
                    return;
                PostRemoteTerminalState("ready", RemoteRunningMessage(resumed), session);
            }

            private void PostRemoteTerminalState(string state, string? message = null, IRemoteSession? session = null)
            {
                var msg = new JObject
                {
                    ["type"] = "remote.state",
                    ["state"] = state,
                    ["inTerminal"] = session == null || session is RemoteTerminalLauncher,
                };
                if (message != null)
                    msg["message"] = message;
                if (session != null)
                {
                    if (session.Url != null)
                        msg["url"] = session.Url;
                    if (session.Activity != null)
                        msg["activity"] = session.Activity;
                    msg["canOpenTerminal"] = session.CanOpenTerminal;
                    // the composer stays usable: prompts go into the background session (RemotePromptSender)
                    msg["canPrompt"] = session is BackgroundRemoteSession { BackgroundId: not null };
                }
                Post(msg);
            }

            private void OnSessionEvent(ClaudeEvent ev)
            {
                switch (ev)
                {
                    case FollowUpTurnStartedEvent:
                        // A background task finished and the CLI answers it with a turn of its own (the process
                        // was kept alive for its tasks) — show it like any turn. Its result is flagged followUp, so the
                        // UI folds its footer into the one of the turn before instead of adding one per reaction.
                        _turnHadAssistantOutput = false;
                        _inFollowUpTurn = true;
                        PostStatus("working");
                        // it may edit files under review — free their read-only buffers (marshals itself)
    #pragma warning disable VSTHRD010
                        CloseOwnTurnReviews();
    #pragma warning restore VSTHRD010
                        break;

                    case BackgroundTasksChangedEvent bg:
                        PostBackgroundTasks(bg);
                        break;

                    case SessionInitEvent init when !string.IsNullOrEmpty(init.SessionId):
                        // the live-output watcher needs both to locate the CLI's task files
                        _turnSessionId = init.SessionId;
                        if (!string.IsNullOrEmpty(init.Cwd))
                            _turnCwd = init.Cwd;
                        // history id/resumability is adopted at turn end (num_turns > 0) —
                        // local-only turns like /help report ids --resume cannot use
                        if (init.SlashCommands.Count > 0)
                        {
                            _host._slashCommands = new JArray(init.SlashCommands); // shared by the tabs
                            _host.SendSlashCommands();
                        }
                        _host._manage.SetLoadedSkills(init.Skills);
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
                            _record.ContextTokens = compact.PostTokens;
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
                        if (tool.Name is "Bash" or "PowerShell" && !string.IsNullOrEmpty(tool.ToolUseId))
                            _shellCalls[tool.ToolUseId] = new ShellCall(tool.Name, tool.Input.Value<string>("command"));
                        // decision #19— edits the CLI approves without asking (acceptEdits/bypass) get
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
                                "approved", autoApproved: true);
                        }
                        break;

                    case TaskStartedEvent task:
                        // A foreground shell command started: stream its output into the card while it
                        // runs. Background tasks answer their tool_use immediately ("running in the
                        // background") and are killed with the per-turn process anyway — not tailed.
                        // A background shell: remember its command for the stop button of the background bar.
                        if (task.IsBackgrounded && !string.IsNullOrEmpty(task.TaskId) && !string.IsNullOrEmpty(task.ToolUseId)
                            && _shellCalls.TryGetValue(task.ToolUseId, out var bgCall))
                        {
                            _backgroundShells[task.TaskId] = new ShellCall(bgCall.ToolName, bgCall.Command) { TaskId = task.TaskId };
                        }
                        if (task.TaskType == "local_bash" && !task.IsBackgrounded)
                        {
                            if (!string.IsNullOrEmpty(task.ToolUseId) && _shellCalls.TryGetValue(task.ToolUseId, out var call))
                                call.TaskId = task.TaskId;
                            _taskOutput.Start(task.TaskId, task.ToolUseId,
                                string.IsNullOrEmpty(task.SessionId) ? _turnSessionId : task.SessionId,
                                _turnCwd);
                            // Open the (empty) console right away so the card shows it is live even while
                            // the command prints nothing yet — e.g. `… | Select-Object -Last 8` holds every
                            // line back until the command ends.
                            Post(new JObject { ["type"] = "tool.output", ["id"] = task.ToolUseId, ["text"] = "", ["start"] = true });
                        }
                        break;

                    case TaskNotificationEvent task:
                        _taskOutput.Stop(task.TaskId); // final read, then stop tailing
                        break;

                    case ToolResultEvent result:
                        _taskOutput.StopByToolUse(result.ToolUseId); // flush any live output before the result
                        if (!string.IsNullOrEmpty(result.ToolUseId))
                            _shellCalls.TryRemove(result.ToolUseId, out _);
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
                        // A subagent finished (CLI 2.1.287 sends no result event of its own for it any more):
                        // its footer comes from the hand-back's stats — time, tool uses, tokens; no cost.
                        if (result.Agent is { } agent && (agent.DurationMs > 0 || agent.TotalTokens > 0))
                        {
                            Post(new JObject
                            {
                                ["type"] = "agent.result",
                                ["parentToolUseId"] = result.ToolUseId,
                                ["durationMs"] = agent.DurationMs,
                                ["totalTokens"] = agent.TotalTokens,
                                ["toolUses"] = agent.ToolUses,
                                ["model"] = agent.Model,
                                ["isError"] = result.IsError || agent.Status is "failed" or "error",
                            });
                        }
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

                    case NotificationTurnResultEvent notification:
                        // opens a resumed run: its total is the session total before the prompt — a baseline
                        // for the turn cost when the record has none yet
                        if (_record.CostTotalUsd == null && notification.CostUsd > 0)
                            _record.CostTotalUsd = notification.CostUsd;
                        break;

                    case TurnResultEvent turn:
                        _turnResultText = turn.ResultText; // for the turn-end notification (OnTurnCompleted)
                        if (turn.AuthFailed && string.IsNullOrEmpty(turn.ParentToolUseId))
                        {
                            // not signed in / login expired → a "Sign in again" card in this tab, the header follows
                            Post(new JObject { ["type"] = "auth.required", ["message"] = turn.ResultText });
                            _host._auth.OnAuthFailed();
                        }
                        var turnCost = TurnCostUsd(turn); // before HasCliSession flips below
                        _record.UpdatedAtUtc = DateTime.UtcNow;
                        if (turn.NumTurns > 0 && !string.IsNullOrEmpty(turn.SessionId))
                        {
                            _record.Id = turn.SessionId;
                            _record.HasCliSession = true;
                        }
                        // context size = the LAST assistant message's usage (one API call). The
                        // result-event aggregate (InputTokens) re-counts the cached context for every
                        // round-trip in the turn, so it massively over-counts (showed Ctx 100% / huge
                        // footer). ContextInputTokens/OutputTokens hold the real occupancy.
                        long contextTokens = turn.ContextInputTokens + turn.ContextOutputTokens;
                        if (contextTokens > 0)
                            _record.ContextTokens = contextTokens;
                        if (turn.ContextWindow > 0)
                            _record.ContextWindow = turn.ContextWindow;
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
                            // a turn the CLI started by itself (background task reported) — not one the user sent
                            ["followUp"] = _inFollowUpTurn,
                            // this turn's cost (session total minus the total after the previous turn) —
                            // null when no baseline is known; the session total goes along for the tooltip
                            ["costUsd"] = turnCost,
                            ["sessionCostUsd"] = turn.CostUsd,
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
                            ["contextTokens"] = _record.ContextTokens,
                            ["contextWindow"] = _record.ContextWindow,
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

                    case ContextUsageEvent usage:
                        // The CLI's answer to the context request sent at the turn's result (see
                        // ClaudeSessionService.OnTurnResult) — arrives right after the turn was handed back.
                        var contextUsage = BuildContextUsage(usage);
                        _record.ContextUsage = contextUsage;
                        if (usage.MaxTokens > 0)
                            _record.ContextWindow = usage.MaxTokens;
                        Post(new JObject
                        {
                            ["type"] = "context.usage",
                            ["usage"] = contextUsage.DeepClone(),
                        });
                        break;

                    case PromptSuggestionEvent suggestion:
                        // Arrives after the turn was already handed back (see ClaudeSessionService). The UI
                        // shows it as ghost text in the empty composer; Tab takes it over. Kept on the chat record
                        // so it is back after a VS restart / solution reload (sent along with transcript.load).
                        lock (_history)
                        {
                            if (string.IsNullOrEmpty(suggestion.SessionId)
                                || string.Equals(suggestion.SessionId, _record.Id, StringComparison.OrdinalIgnoreCase))
                                _record.PromptSuggestion = suggestion.Suggestion;
                        }
                        SaveHistory();
                        Post(new JObject
                        {
                            ["type"] = "prompt.suggestion",
                            ["text"] = suggestion.Suggestion,
                            ["sessionId"] = suggestion.SessionId,
                        });
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

            /// <summary>The session let go of its busy flag (any thread): run a prompt that came in just before.</summary>
            private void OnTurnReleased()
            {
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_disposed)
                        return;
                    if (_remoteStartPending)
                    {
                        // still busy (a follow-up turn took the flag)? → HandleRemoteStart keeps it pending
                        HandleRemoteStart();
                        return; // a prompt held meanwhile would race the remote session — the remote session wins
                    }
                    var msg = _deferredPrompt;
                    if (msg == null)
                        return;
                    _deferredPrompt = null;
                    HandlePromptSend(msg); // still busy (a follow-up turn took the flag)? → held again until it ends
                }).Task.Forget();
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
                _taskOutput.StopAll(); // the process is gone — nothing more will be written
                _shellCalls.Clear();
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
                // a turn the user stopped needs no notification — they are right there
                if (!exit.WasCancelled)
                    NotifyToast(TurnToastTag, ToastContent.ForTurnEnd(_turnResultText, error));
                _turnResultText = null;
                RefreshUsage(); // the turn just consumed quota — update the meters
                TakePostTurnCheckpoint(); // "after" snapshot → this turn's diff is pre..post
                SaveHistory();
#pragma warning disable VSTHRD010 // thread-agnostic: marshals to the UI thread itself
                RetryPendingWorkspaceSwitch(); // the solution changed while this turn ran
#pragma warning restore VSTHRD010
            }

            /// <summary>
            /// This turn's cost. The CLI's <c>total_cost_usd</c> is the running total of the whole session —
            /// across turns in one process and across <c>--resume</c> (measured 2.1.287) — so the footer used to
            /// show e.g. "$141.06" for a 1½-minute turn of a long session. The turn's share is the difference
            /// to the total after the previous turn (kept on the session record, persisted). A brand-new
            /// conversation starts at 0; a resumed one without a known baseline (older record, imported
            /// session) yields null — the footer then leaves the cost out once. Updates the baseline.
            /// </summary>
            private double? TurnCostUsd(TurnResultEvent turn)
            {
                var record = _record;
                var cost = TurnCost(record.CostTotalUsd, record.HasCliSession, turn.CostUsd);
                if (turn.NumTurns > 0 || turn.CostUsd > 0)
                    record.CostTotalUsd = turn.CostUsd;
                return cost;
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
            internal void SendActiveFile()
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
            /// Snapshot taken BEFORE the CLI starts — deliberately awaited, since an edit made during the
            /// turn must not end up inside that turn's own "before" state. Best-effort: on failure the turn
            /// just runs without a rewind point for this message.
            /// </summary>
            private async Task TakePreTurnCheckpointAsync(string cwd, string sessionKey, string turnKey, JObject? message)
            {
                PostStatus("working", "Creating checkpoint…", GitCheckpointService.IsFirstSnapshot(cwd)
                    ? "Creating the first checkpoint of this workspace — a one-time step that can take a minute in a large project."
                    : null);
                var info = await _checkpoints.SnapshotAsync(cwd,
                    GitCheckpointService.BuildRefName(sessionKey, turnKey + "-pre"),
                    "before turn · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    CheckpointSnapshotTimeoutMs).ConfigureAwait(false);
                PostStatus("working");
                if (info == null || message == null)
                    return;

                lock (_history)
                {
                    message["checkpointSha"] = info.Sha;
                    message["checkpointShortSha"] = info.ShortSha;
                }
                Post(new JObject
                {
                    ["type"] = "checkpoint.created",
                    ["messageId"] = message.Value<string>("id") ?? "",
                    ["sha"] = info.Sha,
                    ["shortSha"] = info.ShortSha,
                    ["createdAt"] = info.CreatedUtc.ToString("o"),
                });
            }

            /// <summary>End-of-turn snapshot (fire-and-forget; also runs after a stop or an error, so the
            /// turn's diff is always bounded). Without it the next prompt's "-pre" acts as the fallback.</summary>
            private void TakePostTurnCheckpoint()
            {
                var turn = _checkpointTurn;
                _checkpointTurn = null;
                if (turn == null)
                    return;
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    var info = await _checkpoints.SnapshotAsync(turn.Cwd, turn.PostRef,
                        "after turn · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        CheckpointSnapshotTimeoutMs).ConfigureAwait(false);
                    // Pack loose objects once enough have accumulated — cheap no-op otherwise, and it keeps
                    // the shadow repo from turning into tens of thousands of small files.
                    await _checkpoints.MaintainAsync(turn.Cwd).ConfigureAwait(false);
                    if (info == null || turn.Message == null)
                        return;
                    lock (_history)
                        turn.Message["checkpointPostSha"] = info.Sha;
                    SaveHistory();
                }).Task.Forget();
            }

            /// <summary>After a transcript load: any checkpoint whose snapshot the retention sweep already
            /// removed is reported so the UI can grey out its rewind button ("Checkpoint expired").</summary>
            private void PostCheckpointExpiry(SessionRecord record)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!_package.GetOptions().CheckpointsEnabled || !GitCheckpointService.IsGitAvailable())
                    return;
                var cwd = _package.GetSolutionDirectory();
                if (string.IsNullOrEmpty(cwd))
                    return;

                var shas = new List<string>();
                lock (_history)
                {
                    foreach (var m in record.Messages)
                    {
                        var sha = m.Value<string>("checkpointSha");
                        if (!string.IsNullOrEmpty(sha))
                            shas.Add(sha!);
                    }
                }
                if (shas.Count == 0)
                    return;

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    var alive = await _checkpoints.FilterExistingAsync(cwd!, shas).ConfigureAwait(false);
                    var gone = shas.Where(s => !alive.Contains(s)).Distinct().ToArray();
                    if (gone.Length == 0)
                        return;
                    Post(new JObject
                    {
                        ["type"] = "checkpoint.expired",
                        ["shas"] = new JArray(gone),
                    });
                }).Task.Forget();
            }

            /// <summary>The set of files Claude's turns touched since a checkpoint (see class comment).</summary>
            private sealed class CheckpointScope
            {
                public List<string> Paths = new List<string>();
                public bool PostMissing;  // a turn ended without its "after" snapshot → the diff may include
                                          // edits the user made between turns
            }

            /// <summary>
            /// Walks the turns from the target checkpoint forward and unions each turn's pre..post diff.
            /// Returns null when the checkpoint isn't in the current transcript (loaded from an older
            /// history, no post shas at all) — the caller then falls back to the whole work-tree.
            /// </summary>
            private async Task<CheckpointScope?> BuildTurnScopeAsync(string cwd, string sha)
            {
                var turns = new List<(string Pre, string? Post)>();
                lock (_history)
                {
                    var messages = _record.Messages;
                    var start = -1;
                    for (var i = 0; i < messages.Count; i++)
                    {
                        if (string.Equals(messages[i].Value<string>("checkpointSha"), sha, StringComparison.OrdinalIgnoreCase))
                        {
                            start = i;
                            break;
                        }
                    }
                    if (start < 0)
                        return null;
                    for (var i = start; i < messages.Count && turns.Count < CheckpointScopeMaxTurns; i++)
                    {
                        var pre = messages[i].Value<string>("checkpointSha");
                        if (string.IsNullOrEmpty(pre))
                            continue;
                        turns.Add((pre!, messages[i].Value<string>("checkpointPostSha")));
                    }
                }
                if (turns.Count == 0)
                    return null;

                var scope = new CheckpointScope();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < turns.Count; i++)
                {
                    var post = turns[i].Post;
                    if (string.IsNullOrEmpty(post))
                    {
                        // No end-of-turn snapshot (stop/crash). The next turn's "before" snapshot is the
                        // closest bound; for the newest turn compare against the work-tree (post = null).
                        var isLast = i == turns.Count - 1;
                        post = isLast ? null : turns[i + 1].Pre;
                        scope.PostMissing = true;
                    }
                    var changes = await _checkpoints.DiffAsync(cwd, turns[i].Pre, post, null).ConfigureAwait(false);
                    foreach (var change in changes)
                        paths.Add(change.Path);
                }
                scope.Paths = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                return scope;
            }

            /// <summary>web→host <c>checkpoint.previewRequest</c>: what a rewind to this point would change.</summary>
            private void HandleCheckpointPreview(string sha, string scope)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var cwd = _package.GetSolutionDirectory();
                if (string.IsNullOrEmpty(sha) || string.IsNullOrEmpty(cwd))
                {
                    Post(new JObject { ["type"] = "checkpoint.preview", ["sha"] = sha, ["files"] = new JArray() });
                    return;
                }
                var wantAll = scope == "all";

                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    var filtered = false;
                    var postMissing = false;
                    IReadOnlyList<CheckpointFileChange> changes;
                    if (wantAll)
                    {
                        changes = await _checkpoints.DiffAsync(cwd!, sha, null, null).ConfigureAwait(false);
                    }
                    else
                    {
                        var turnScope = await BuildTurnScopeAsync(cwd!, sha).ConfigureAwait(false);
                        if (turnScope == null)
                        {
                            changes = await _checkpoints.DiffAsync(cwd!, sha, null, null).ConfigureAwait(false);
                        }
                        else
                        {
                            filtered = true;
                            postMissing = turnScope.PostMissing;
                            changes = turnScope.Paths.Count == 0
                                ? new List<CheckpointFileChange>()
                                : await _checkpoints.DiffAsync(cwd!, sha, null, turnScope.Paths).ConfigureAwait(false);
                        }
                    }

                    var files = new JArray();
                    foreach (var change in changes.Take(400))
                    {
                        files.Add(new JObject
                        {
                            ["path"] = change.Path,
                            ["added"] = change.Added,
                            ["removed"] = change.Removed,
                            ["binary"] = change.Binary,
                            ["status"] = change.Status,
                        });
                    }
                    Post(new JObject
                    {
                        ["type"] = "checkpoint.preview",
                        ["sha"] = sha,
                        ["scope"] = wantAll ? "all" : "turns",
                        ["filtered"] = filtered,
                        ["postMissing"] = postMissing,
                        ["truncated"] = changes.Count > 400,
                        ["files"] = files,
                    });
                }).Task.Forget();
            }

            /// <summary>
            /// web→host <c>checkpoint.restore</c>. Scope "code" writes the selected files back, "conversation"
            /// marks the later turns as discarded (and tells the next prompt about it), "both" does each.
            /// Blocked while a turn runs.
            /// </summary>
            private void HandleCheckpointRestore(JObject msg)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var sha = msg.Value<string>("sha") ?? "";
                var scope = msg.Value<string>("scope") ?? "code";
                if (string.IsNullOrEmpty(sha))
                    return;
                if (_session.IsBusy || RemoteSessionActive)
                {
                    PostNotify("Stop the current turn before rewinding.", "warning", 4000);
                    return;
                }

                var cwd = _package.GetSolutionDirectory();
                var restoreCode = scope == "code" || scope == "both";
                var restoreConversation = scope == "conversation" || scope == "both";

                // paths semantics: absent or allFiles → restore everything that changed; present with
                // entries → exactly those; present but EMPTY → nothing to undo (the preview was empty or
                // the user deselected every file), so the file part is skipped rather than widened to the
                // whole work-tree, which would revert the user's own edits too.
                List<string>? paths = null;
                if (msg["paths"] is JArray selected && !(msg.Value<bool?>("allFiles") ?? false))
                {
                    paths = selected.Select(p => p.Value<string>() ?? "").Where(p => p.Length > 0).ToList();
                    if (paths.Count == 0)
                        restoreCode = false;
                }

                var turnNumber = restoreConversation ? MarkConversationRewound(sha) : 0;

                if (!restoreCode || string.IsNullOrEmpty(cwd))
                {
                    FinishCheckpointRestore(sha, scope, new RestoreResult(), turnNumber, restoreConversation);
                    return;
                }

                PostStatus("working", "Rewinding files…");
                _package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await TaskScheduler.Default;
                    var safetyRef = GitCheckpointService.BuildRefName(
                        _record.Id, "safety-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                    var result = await _checkpoints.RestoreAsync(cwd!, sha, paths, safetyRef).ConfigureAwait(false);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    PostStatus("ready");
                    FinishCheckpointRestore(sha, scope, result, turnNumber, restoreConversation);
                }).Task.Forget();
            }

            /// <summary>Reports the outcome, notes it in the transcript and queues the hint for Claude.</summary>
            private void FinishCheckpointRestore(string sha, string scope, RestoreResult result,
                int turnNumber, bool restoredConversation)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var ok = result.Error == null;
                Post(new JObject
                {
                    ["type"] = "checkpoint.restored",
                    ["sha"] = sha,
                    ["scope"] = scope,
                    ["ok"] = ok,
                    ["restoredCount"] = result.RestoredCount,
                    ["deletedCount"] = result.DeletedCount,
                    ["skipped"] = new JArray(result.Skipped.Cast<object>().ToArray()),
                    ["error"] = result.Error,
                });

                if (!ok)
                {
                    PostNotify("Rewind failed: " + result.Error, "error");
                    return;
                }

                var shortSha = sha.Length >= 8 ? sha.Substring(0, 8) : sha;
                var parts = new List<string>();
                if (scope != "conversation")
                    parts.Add(result.RestoredCount + result.DeletedCount == 0
                        ? "no file changes to undo"
                        : result.RestoredCount + " file(s) restored"
                          + (result.DeletedCount > 0 ? ", " + result.DeletedCount + " removed" : ""));
                if (restoredConversation)
                    parts.Add("conversation marked as discarded from this point");
                if (result.Skipped.Count > 0)
                    parts.Add("skipped " + result.Skipped.Count + " linked/locked file(s)");
                // Expandable note: the summary line is always visible, the file list only when opened.
                PostRewindNote("Rewound to checkpoint " + shortSha + " — " + string.Join("; ", parts) + ".",
                    BuildRewindNoteFiles(result));

                // What the next prompt tells Claude. Files and conversation are worded separately: the CLI
                // session still HAS the later turns in its context, so it has to be told to ignore them.
                var turnText = turnNumber > 0 ? "turn " + turnNumber : "the selected turn";
                var restoredFiles = result.RestoredCount + result.DeletedCount > 0;
                var note = new System.Text.StringBuilder("[System] ");
                if (scope != "conversation" && restoredFiles)
                {
                    note.Append("These files were rewound to their state before ").Append(turnText)
                        .Append(" (checkpoint ").Append(shortSha).Append("): ")
                        .Append(FormatPathList(result.Touched, 40));
                    if (result.Skipped.Count > 0)
                        note.Append(" (").Append(result.Skipped.Count).Append(" linked/locked file(s) were left untouched)");
                    note.Append(". Any change made to them after that point is gone. ");
                }
                if (restoredConversation)
                {
                    note.Append("The conversation was rewound to just before ").Append(turnText)
                        .Append(restoredFiles ? " as well" : " (checkpoint " + shortSha + ")")
                        .Append(": treat ").Append(turnText)
                        .Append(" and everything after it in this conversation as discarded and ignore it. ");
                    if (!restoredFiles)
                        note.Append("No file changes had to be undone. ");
                }
                note.Append("Re-read files before relying on their contents.");
                _pendingRestoreNote = note.ToString();
                SaveHistory();
            }

            /// <summary>File rows for the expandable rewind note — same `+n/−m` figures the preview
            /// showed, plus what happened to each path.</summary>
            private static JArray BuildRewindNoteFiles(RestoreResult result)
            {
                var deleted = new HashSet<string>(result.Deleted, StringComparer.OrdinalIgnoreCase);
                var files = new JArray();
                foreach (var change in result.Applied)
                {
                    files.Add(new JObject
                    {
                        ["path"] = change.Path,
                        ["added"] = change.Added,
                        ["removed"] = change.Removed,
                        ["binary"] = change.Binary,
                        ["status"] = change.Status,
                        ["action"] = deleted.Contains(change.Path) ? "deleted" : "restored",
                    });
                }
                foreach (var path in result.Skipped)
                    files.Add(new JObject { ["path"] = path, ["action"] = "skipped" });
                return files;
            }

            /// <summary>Dim transcript line like a system note, but with a chevron that reveals the list of
            /// files the rewind touched. Persisted as its own message role so it survives a reload.</summary>
            private void PostRewindNote(string text, JArray files)
            {
                var id = "rewind-" + Guid.NewGuid().ToString("n");
                var message = new JObject
                {
                    ["role"] = "rewind",
                    ["id"] = id,
                    ["text"] = text,
                    ["files"] = files,
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                };
                RecordMessage(message);
                Post(new JObject
                {
                    ["type"] = "rewind.note",
                    ["id"] = id,
                    ["text"] = text,
                    ["files"] = files.DeepClone(),
                });
            }

            /// <summary>Comma-separated path list, capped so a wide rewind doesn't flood the transcript
            /// note (or the prompt) — the rest is summarized as "…and N more".</summary>
            private static string FormatPathList(IEnumerable<string> paths, int max)
            {
                var all = paths.ToList();
                if (all.Count == 0)
                    return "(none)";
                var shown = string.Join(", ", all.Take(max));
                return all.Count <= max ? shown : shown + ", …and " + (all.Count - max) + " more";
            }

            /// <summary>Flags every message after the checkpoint as rewound (persisted, so the greyed-out
            /// state survives a reload). Returns the 1-based turn number of the target message.</summary>
            private int MarkConversationRewound(string sha)
            {
                lock (_history)
                {
                    var messages = _record.Messages;
                    var start = -1;
                    var turnNumber = 0;
                    for (var i = 0; i < messages.Count; i++)
                    {
                        if (messages[i].Value<string>("role") == "user")
                            turnNumber++;
                        if (string.Equals(messages[i].Value<string>("checkpointSha"), sha, StringComparison.OrdinalIgnoreCase))
                        {
                            start = i;
                            break;
                        }
                    }
                    if (start < 0)
                        return 0;
                    // From the target INCLUSIVE: the checkpoint is the state before that turn ran, so the
                    // prompt itself is discarded as well (the UI puts it back into the composer).
                    for (var i = start; i < messages.Count; i++)
                        messages[i]["rewound"] = true;
                    return turnNumber;
                }
            }

            // ── host → web senders ────────────────────────────────────────────────

            private void SendSessionInit()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var options = _package.GetOptions();
                Post(new JObject
                {
                    ["type"] = "session.init",
                    ["sessionId"] = _record.Id,
                    ["title"] = _record.Title,
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
                    ["contextTokens"] = _record.ContextTokens,
                    ["contextWindow"] = _record.ContextWindow,
                    ["contextUsage"] = _record.ContextUsage?.DeepClone(),
                    ["limits"] = BuildLimits(),
                    ["plan"] = _planLabel ?? "",
                    ["verbosity"] = options.VerbosityString,
                    ["accent"] = options.AccentColor ?? "",
                    ["noticeFetchEnabled"] = options.NoticeFetchEnabled,
                    ["noticeFetchDecided"] = options.NoticeFetchDecided,
                    ["updateCheckEnabled"] = options.UpdateCheckEnabled,
                    ["updateCheckDecided"] = options.UpdateCheckDecided,
                    ["modelCatalogFetchEnabled"] = options.ModelCatalogFetchEnabled,
                    ["modelCatalogFetchDecided"] = options.ModelCatalogFetchDecided,
                    ["autoUpdateEnabled"] = options.AutoUpdateEnabled,
                    ["autoUpdateDecided"] = options.AutoUpdateDecided,
                    ["checkpoints"] = BuildCheckpointsState(),
                    ["appVersion"] = GetInstalledVersion(),
                });
            }

            /// <summary>Live console text of a running Bash/PowerShell command (<c>tool.output</c>, appended
            /// by the WebUI to the card's console). Called from the watcher's timer thread; Post marshals.</summary>
            private void PostToolOutput(string toolUseId, string text)
            {
                if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(text))
                    return;
                Post(new JObject { ["type"] = "tool.output", ["id"] = toolUseId, ["text"] = text });
            }

            /// <summary>Re-pushes the Model·Mode popover state (mode + its sub-toggles) after an options
            /// change. Uses <c>mode.update</c> rather than <c>session.init</c> on purpose: session.init
            /// clears the transcript view. Without this the popover kept rendering whatever it was last told
            /// while the host had moved on — the toggle looked on while the turn ran with it off.</summary>
            internal void SendModeState()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var options = _package.GetOptions();
                Post(new JObject
                {
                    ["type"] = "mode.update",
                    ["permissionMode"] = _session.Settings.PermissionMode,
                    ["planMode"] = _session.Settings.PlanMode,
                    ["autoAcceptCommands"] = options.AutoAcceptCommands,
                    ["reviewEditsInEditor"] = options.ReviewEditsInEditor,
                    ["reviewEditsAtTurnEnd"] = options.ReviewEditsAtTurnEnd,
                });
            }

            /// <summary>Dim one-line system note in the transcript (display option "C").</summary>
            internal void PostSystemNote(string text)
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

            /// <param name="detail">Longer explanation, shown only as the statusbar tooltip.</param>
            internal void PostStatus(string state, string? text = null, string? detail = null)
            {
                var msg = new JObject { ["type"] = "status", ["state"] = state };
                if (text != null)
                    msg["text"] = text;
                if (detail != null)
                    msg["detail"] = detail;
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

            /// <summary>"Notify me" option: a Windows notification — unless the option is off, or the user is
            /// looking at Visual Studio and asked to be notified only when it is in the background. Any thread.</summary>
            /// <summary>Toast tags are per tab ("t2-turn", "t2-&lt;request id&gt;"): the tabs share one notifier, and a click
            /// on a toast selects its tab (<see cref="WebViewBridge.OnToastActivated"/>).</summary>
            private string ToastTag(string tag) => Id + "-" + tag;

            private void NotifyToast(string tag, ToastText text)
            {
                var options = _package.GetOptions();
                if (_disposed || !options.ToastNotifications)
                    return;
                if (options.ToastOnlyWhenInactive && WindowsToastNotifier.IsVisualStudioForeground())
                    return;
                string title;
                lock (_history)
                    title = _record.Title;
                // workspace first: with several Visual Studio instances open, it says which one is meant
                _toasts.Show(ToastTag(tag), text, ToastContent.Attribution(_toastWorkspace, title));
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
                    _record.Messages.Add(message);
                    _record.UpdatedAtUtc = DateTime.UtcNow;
                }
            }


            // ── web → host (this tab's messages) ─────────────────────────────────

            /// <summary>Handles a web→host message that belongs to a chat tab. False = not a session message (the
            /// host handles it). UI thread.</summary>
            internal bool HandleMessage(JObject msg)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                switch (msg.Value<string>("type"))
                {
                    case "prompt.send":
                        HandlePromptSend(msg);
                        return true;
                    case "turn.stop":
                        DenyAllPendingPermissions("Turn stopped"); // let blocked MCP calls return
                        _session.StopTurn();
                        PostSystemNote("Turn stopped"); // decision #11
                        // A cancelled turn emits no TurnResultEvent, so build the changed-files review list
                        // here from whatever baselines were captured before the stop (edits already applied).
                        BuildAndPostTurnReviewList();
                        return true;
                    case "tool.stop":
                        HandleToolStop(msg.Value<string>("id"));
                        return true;
                    case "background.stop":
                        HandleBackgroundStop(msg.Value<string>("id"));
                        return true;
                    case "session.new":
                        HandleSessionNew();
                        return true;
                    case "session.load":
                        HandleSessionLoad(msg.Value<string>("sessionId") ?? "");
                        return true;
                    case "session.rename":
                        HandleSessionRename(msg.Value<string>("sessionId"), msg.Value<string>("title") ?? "");
                        return true;
                    case "session.delete":
                        HandleSessionDelete(msg.Value<string>("sessionId"));
                        return true;
                    case "mode.set": // plan mode is per tab (not a persisted option)
                        _session.Settings.PlanMode = msg.Value<bool?>("planMode") ?? false;
                        return true;
                    case "editReview.open":
                        HandleEditReviewOpen(msg.Value<string>("requestId") ?? "");
                        return true;
                    case "editReview.openTurnFile":
                        HandleOpenTurnFile(msg.Value<string>("path") ?? "");
                        return true;
                    case "editReview.finishTurnFile":
                        HandleFinishTurnFile(msg.Value<string>("path") ?? "");
                        return true;
                    case "editReview.keepAll":
                        HandleKeepAllTurnReviews();
                        return true;
                    case "editReview.discardAll":
                        HandleDiscardAllTurnReviews();
                        return true;
                    case "checkpoint.previewRequest":
                        HandleCheckpointPreview(msg.Value<string>("sha") ?? "", msg.Value<string>("scope") ?? "turns");
                        return true;
                    case "checkpoint.restore":
                        HandleCheckpointRestore(msg);
                        return true;
                    case "prompt.suggestionDismissed": // Escape on the ghost text → don't bring it back after a restart
                        lock (_history)
                            _record.PromptSuggestion = null;
                        _host.SaveHistory();
                        return true;
                    case "permission.decision":
                        HandlePermissionDecision(msg);
                        return true;
                    case "permission.approveAlways":
                        HandleApproveAlways(msg.Value<string>("requestId") ?? "", msg["patterns"] as JArray);
                        return true;
                    case "question.answer":
                        HandleQuestionAnswer(msg);
                        return true;
                    case "slash.run":
                        HandleSlashRun(msg.Value<string>("command") ?? "", msg.Value<string>("args"));
                        return true;
                    case "remote.start":
                        HandleRemoteStart();
                        return true;
                    case "remote.openTerminal":
                        _remoteTerminal?.OpenTerminal();
                        return true;
                    case "remote.stop":
                        HandleRemoteStop();
                        return true;
                    case "remote.cancelPrompt":
                        CancelRemotePrompt();
                        return true;
                    case "activeFile.setEnabled":
                        HandleActiveFileSetEnabled(msg.Value<bool?>("enabled") ?? true);
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>The host switched the history store to another workspace (<see cref="WebViewBridge.CheckWorkspaceSwitch"/>,
            /// only while this tab is idle): start a fresh chat in the new store and drop the old workspace's CLI
            /// session (and a process kept for its tasks) and pending review. UI thread, store already reloaded.</summary>
            internal void ResetForWorkspace()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                lock (_history)
                    _record = _history.StartNew();
                _mayRestoreLast = true; // the new workspace's last chat comes back like on a start
                _session.ResetSession();
                _sessionStartAnnounced = false;
                _remoteAdoptChecked = false;
                _activeFileSessionEnabled = ActiveFileDefaultOn;
                if (HasPendingTurnReview())
                {
                    CloseOwnTurnReviews();
                    ResetTurnReviewState();
                }
            }

            /// <summary>Shows the chat with this id (restoring the open tabs on a start / workspace switch). False when it
            /// is unknown or already open elsewhere. Call before the tab's first <see cref="SendInitialSession"/>.</summary>
            internal bool OpenChat(string id)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                SessionRecord? record;
                lock (_history)
                {
                    if (_history.IsOpen(id) && !string.Equals(_record.Id, id, StringComparison.OrdinalIgnoreCase))
                        return false;
                    record = SwitchRecord(id);
                }
                if (record == null)
                    return false;
                AttachToRecord(record);
                _mayRestoreLast = false;
                return true;
            }

            /// <summary>True when <paramref name="path"/> is in this tab's pending end-of-turn review.</summary>
            internal bool HasTurnReviewFor(string path)
            {
                lock (_turnReviewLock)
                    return _turnReviews.ContainsKey(path ?? "");
            }

            /// <summary>Cancels this tab's open in-editor end-of-turn reviews (never another tab's).</summary>
            private void CloseOwnTurnReviews()
            {
                List<string> paths;
                lock (_turnReviewLock)
                    paths = _turnReviews.Keys.ToList();
                if (paths.Count > 0)
#pragma warning disable VSTHRD010 // thread-safe: marshals to the UI thread itself
                    _editReview.CloseTurnReviews(paths);
#pragma warning restore VSTHRD010
            }

            /// <summary>True when this tab has the permission/question request with this id open.</summary>
            internal bool HasPendingRequest(string requestId) => _pendingPermissions.ContainsKey(requestId ?? "");

            /// <summary>The tab is closed by the user: like <see cref="Dispose"/>, plus its chat is closed in the
            /// store (kept in the history when it has messages) and its pending end-of-turn review is dropped
            /// (the edits stay on disk). UI thread.</summary>
            internal void Close()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (HasPendingTurnReview())
                {
                    CloseOwnTurnReviews();
                    ResetTurnReviewState();
                }
                Dispose();
                lock (_history)
                    _history.Close(_record);
            }

            /// <summary>WebView reloaded while background tasks run → their bar comes back.</summary>
            internal void RepostBackgroundTasks()
            {
                if (_backgroundTaskList.Count > 0)
                    Post(new JObject { ["type"] = "background.tasks", ["tasks"] = _backgroundTaskList.DeepClone() });
            }

            /// <summary>On <c>ready</c>: re-announce a remote session that still runs (WebView reload), or look for one
            /// still running from before a VS restart.</summary>
            internal void ResumeRemoteOnReady()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (RemoteSessionActive)
                    PostRemoteSessionState();
                else if (_remoteStartPending)
                    PostRemoteTerminalState("pending", RemotePendingMessage);
                else
                    TryAdoptRemoteSession();
            }
        }
    }
}
