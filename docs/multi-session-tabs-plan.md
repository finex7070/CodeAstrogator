# Plan: Multiple sessions as tabs (target 1.4.0)

> **Status 2026-11-18: built (phases 1–5), not yet verified in real Visual Studio.** The current state is in
> `docs/NOTES.md` ("Chat tabs"); this document stays as the design rationale (like `git-checkpoints-plan.md`).
> Deviations: `tabs.state` carries only ids (the page derives titles and markers); background tabs are restored
> eagerly, not lazily; the inline-review conflict needed no new code (`EditReviewController.Open` already refuses
> a second review of a file); a workspace switch closes all tabs but the first, then restores the new
> workspace's saved tabs.

## Goal
Up to **three Claude sessions at the same time in one chat window**, switched through a **tab strip in
the header** (where the chat title is today). Each tab shows whether its session is working, needs the
user, or has finished since the user last looked. Everything that is not about one conversation (history,
limits, settings, banners, panels) stays **shared**.

Motivation: users already run parallel sessions by opening Visual Studio twice. Inside one window that is
not possible today, and switching windows hides which session is waiting.

## Decisions
- **One WebView, not one per tab.** The page already shows a lot that is the same for every session
  (history, limits, settings, banners, the plugins panel); one WebView keeps that in one place and costs one
  renderer.
- **At most 3 tabs.** Enough for parallel work, keeps memory/CLI processes and the shared usage quota in
  check, and the strip fits a narrow panel.
- **Model, effort, permission mode, Ultracode stay global** — they are the persisted sticky defaults
  (`AstrogatorOptions`) as today, and every turn re-reads them (`SyncSessionSettingsFromOptions`). Per-tab
  values for these are a possible follow-up, not part of this plan.
- **Plan mode is per tab.** It already is per session on the host (`SessionSettings.PlanMode`, deliberately
  not synced from the options, not persisted); `mode.set` carries the tab, the Model · Mode button shows the
  active tab's value.
- **"New chat" in the header resets the active tab** (today's behaviour); **"+" in the tab strip opens a new
  tab.**
- **Tab id ≠ session id.** A chat's id changes from the local GUID to the CLI session id after its first turn
  (`SessionRecord.Id`); tabs get their own stable ids (`t1`, `t2`, … per window).
- **Ruled out:** one WebView per tab (duplicates the shared UI, three renderers, the tab strip has to be
  kept in sync in three pages) and VS multi-instance tool windows (`MultiInstances = true`: cheap, but no
  in-panel tab strip and no status markers — and the shared-resource problems below exist all the same).

## What belongs to a session and what is shared

| Area | Scope |
|---|---|
| Transcript (messages, tool/permission/question cards, working indicator with Stop) | **per tab** |
| Bars above the composer: tasks, background tasks, running subagents, end-of-turn review list | **per tab** |
| Remote Control panel (QR/link, above the transcript) | **per tab** — only visible on the tab running it |
| Composer: text, caret, attachments, queued prompt, prompt suggestion, active-file toggle | **per tab** (one DOM, contents swapped) |
| Plan mode | **per tab** |
| Statusbar: Working/Ready/Awaiting approval, **Context %** meter | **per tab — shows the active tab** |
| Statusbar: Session/Weekly limits, plan label | shared |
| Header: tab strip (replaces the title + rename button), History, Remote Control, New chat, gear | shared |
| Model, effort, permission mode, Ultracode, auto-accept/review toggles | shared (persisted options) |
| Notice / update banners, notify stack, consent popup, "What's new" popup | shared |
| "Plugins, skills & MCP" panel (`ManagementPanel`) | shared — plugin/MCP changes apply to the next turn of every tab |
| Settings, theme, fonts, model catalog, slash-command list | shared |
| Chat history store (per workspace) | shared — **one** store for all tabs, several open chats |
| Checkpoint service, edit-review controller, toast notifier | shared (`ChatHost`), with per-tab ownership — see below |
| Remote Control | per session, **at most one at a time** |

## Architecture

### Host: split `WebViewBridge`
Today one `WebViewBridge` (~4750 lines) owns the WebView *and* the whole state of one session
(`_session` 64×, `_history` 106× of which `_history.Current` 49×, ~80 `Post` calls).

- **`ChatHost`** (one per tool window): the WebView and `Post`, the `ready` handshake + queued UI messages,
  theme/fonts/appearance, options, usage meters (the `/usage` timer — one for all tabs, not three), model
  catalog, slash commands, notices/update/manual checks/"What's new", `ManagementPanel`, workspace switching,
  active-document tracking, attachments from outside the page (context menu, drag & drop, file dialog,
  clipboard), the **single `SessionHistoryStore`**, the **single `GitCheckpointService`**, the **single
  `EditReviewController`**, the **single `WindowsToastNotifier`**, the tab list and tab status.
- **`ChatSession`** (one per tab): `ClaudeSessionService` + process host, its own `McpPermissionBridge`
  (own port/config file `mcp-permission-<port>.json` — simplest, no routing inside the MCP server), pending
  permissions/questions, the turn state (`_streamingAssistant`, `_turnHadAssistantOutput`, follow-up flag,
  result text, `_sessionStartAnnounced`), checkpoint turn state + pending restore note, `TaskOutputWatcher`,
  shell calls and background shells, the background task list, end-of-turn review baselines/reviews,
  recorded permissions, the active-file toggle (`_activeFileSessionEnabled`), the remote session and its
  mirror state, and **the `SessionRecord` it shows** (a reference, no longer `_history.Current`).
- **Routing:** every host→web message that belongs to a session carries **`tab`** (the tab id); every
  web→host message about a session carries it back. Messages without `tab` are shared.
- **Composer input from outside the page** (`attachments.add` from the context menu, drag & drop, file
  dialog, clipboard paste; `activeFile`) stays **without `tab`**: the composer exists once, so it always
  lands in the active tab.

### History store: several open chats
`SessionHistoryStore` knows exactly **one** `Current` today: `StartNew()`/`Load()` archive it, `Delete()`
replaces it, `Enumerate()`/`ToSessionList()` put it first, `Import()`/`Rename()`/`Contains()` special-case
it, `ReloadFrom()` resets it. With tabs:
- The store holds all records in one list; **"open" is a property of the tab**, not of the store. A
  `ChatSession` keeps its `SessionRecord` reference; the store offers `StartNew()` (returns a fresh record,
  not yet listed until it has messages), `Find(id)`, `Delete(id)`, `Rename(id)`, `Import(…)`.
- An empty record is not persisted (today's `ArchiveCurrent` rule: only records with messages are saved).
- `ToSessionList()` returns the most recent first; the WebUI marks the chats open in a tab (`openIn: tabId`).
- `Save()` stays "rewrite the whole file under the store lock" — with one store for all tabs that is safe
  within a window. Two VS instances on the same workspace still overwrite each other's file (pre-existing,
  out of scope).

### Contract additions (host ↔ web)
- host→web **`tabs.state`** `{ tabs: [{ id, title, state, unseen, remote }], activeId }` — `state` =
  `idle | working | attention | error`; `unseen` = finished or failed since the tab was last active;
  `remote` = this tab runs Remote Control.
- web→host **`tab.new`**, **`tab.select { id }`**, **`tab.close { id }`**.
- Every existing session message gains `tab`: host→web `assistant.*`, `tool.*`, `permission.*`,
  `question.*`, `turn.result`, `status`, `context.usage`, `prompt.suggestion`, `session.init`,
  `transcript.load`, `checkpoint.*`, `turnReview.*`, `background.tasks`, `remote.*`, …; web→host
  `prompt.send`, `turn.stop`, `tool.stop`, `background.stop`, `session.new`, `session.load`,
  `permission.decision`, `permission.approveAlways`, `question.answer`, `mode.set`, `checkpoint.*`,
  `editReview.*`, `activeFile.setEnabled`, `prompt.suggestionDismissed`, `remote.*`, `slash.run`.
- `session.list` entries gain `openIn` (tab id or absent).

### WebUI
- Session-scoped state moves from module-level variables and the global `state` into **one object per
  tab**. From `state`: `sessionId`, `title`, `planMode`, `messages`, `activeAssistant`, `activeThinking`,
  `pendingPermissions`, `tasks`, `tasksDismissed`, `turnReview`, `runningAgents`, `status`, `remoteActive`,
  `contextTokens`, `contextWindow`, `contextUsage`, `tokens`, `attachments`, `queued`, the active-file
  `enabled` toggle, `expiredCheckpoints`. Module variables: `promptSuggestion`, `stopRequested`, `taskSeq`,
  `taskBatchClosed`, `curTurnDivider`/`curTurnFooter`/`footerChain`, `lastUserRow`, `workingEl`/
  `workingPhraseIdx`, the background task list. Stays global: model/effort/permission/verbosity/theme/
  limits/plan/checkpoint settings, workspace file list, overlays/modals/autocomplete, `manageEl`,
  `historyPanelEl`, `cpPreview` (a modal — closes on tab switch).
- **One transcript container per tab** in the DOM; switching shows/hides containers, nothing is re-rendered.
  Messages for a background tab render into that tab's container.
- **The composer exists once**: on a tab switch its text, caret, attachments, queued state and suggestion
  are saved to the old tab and restored from the new one.
- Bars above the composer and the Remote Control panel are re-rendered from the active tab's state on a
  switch.
- **Tab strip** in the header: title per tab (truncated), status marker, ✕ on hover/active, "+" while fewer
  than 3 tabs. **Rename = pencil button of the active tab or double-click on its title** (opens today's
  rename modal; the separate `#btn-rename` goes). Narrow panel: tabs shrink to marker + short title; the
  active one keeps the most room.
- **Status markers:** ⟳ working · 🟠 needs you (permission, question, plan) · 🟢 finished and not yet
  seen · red on error · remote icon on the tab running Remote Control. `unseen` clears when the tab becomes
  active.
- **Keyboard:** Ctrl+1/2/3 inside the panel only if VS lets them reach the WebView (verify first — VS keeps
  Ctrl+Tab for itself); otherwise no shortcut in 1.4.0.

## Shared resources — what must be guarded
- **Chat history:** one store owned by `ChatHost`, all tabs under its lock (see "History store").
- **One session, one tab:** opening a chat from the history that is already open in another tab selects
  that tab instead. Two CLI processes must never `--resume` the same session id.
- **History panel:** "load" opens the chat in the active tab (or switches, see above) and is refused only
  while **the active tab** has a running turn (`TurnRunningBlocks` per tab, not over all tabs); delete is
  refused for a chat that is open in a tab with a running turn; deleting a chat open in an idle tab resets
  that tab (today's "deleted the current chat" path).
- **Checkpoints:** all snapshots of a workspace share **one index file** (`.git/ca-index`,
  `GitCheckpointService.GetIndexFile`) — two tabs snapshotting at once fail on `ca-index.lock`. One
  `GitCheckpointService` in `ChatHost` plus a **per-repo lock** (`SemaphoreSlim` keyed by repo dir) around
  snapshot, restore, prune and `gc`; today's `_initLock` is per instance and not enough. Two VS instances on
  the same workspace have the same race already today — an in-process lock does not cover that
  (pre-existing; a named mutex is a possible follow-up).
  Side effects to document: a turn's diff (before/after snapshot) also contains what another tab changed
  meanwhile, and a **rewind can undo changes made by another tab** — the rewind dialog says so when other
  tabs are open on the same workspace.
- **Edit review (inline and end-of-turn):** one `EditReviewController` in `ChatHost` that records which tab
  owns which file. Inline: a second tab's request for a file under review falls back to the diff card in
  the chat. End-of-turn: a file already in another tab's pending review is not reviewed a second time (it
  stays in the first tab's list; the second tab gets a note) — otherwise "Discard" in one tab would revert
  the file to *its* baseline and wipe the other tab's edits, and both would fight over the read-only lock.
- **Toasts:** one `WindowsToastNotifier` in `ChatHost`; tag = process **and tab** (`p<pid>-t<tab>-<tag>`,
  the existing front truncation to 64 chars keeps the prefix); the attribution names the tab's chat; a
  click selects that tab — `WindowsToastNotifier(Action onActivated)` becomes `Action<string tag>`.
- **Composer input from outside** (editor context menu "Add file/selection to prompt", drag & drop onto the
  panel, file dialog, clipboard paste) goes to the active tab.
- **Remote Control:** one at a time; the tab running it is locked as today, the other tabs stay usable.
  `TryAdoptRemoteSession` checks **every open tab's** session id (today only `_history.Current`); a still
  running background session goes to exactly the tab that shows it (restored tabs included); an ended one
  is imported into the history as today.
- **Workspace switch** (solution opened/closed): applies to all tabs; it waits until no tab has a running
  turn or remote session (today's rule, now over all tabs). Then all sessions end and the **new workspace's
  saved tabs are restored** (same path as a start, see "Restart behaviour").
- **Usage quota:** three tabs share the plan's limits — nothing to guard, the limits meter is shared. More
  turn ends mean more `/usage` refreshes; the existing reentrancy guard keeps them from overlapping.
- **Same project in several tabs:** sessions can change the same files. The extension cannot prevent
  this; a short hint when a second tab starts a turn in the same workspace while another runs.

## WebView reload and restart behaviour
- **WebView reload (`ready`):** the host re-sends everything per tab — `tabs.state`, then for each tab
  `session.init` + `transcript.load`, its end-of-turn review list, background tasks, remote state, pending
  permission/question cards — today `SendInitialSession` + `RepostTurnReviewList` do this for one session.
- **Restart:** the open tabs (chat ids, order, active tab) are persisted per workspace and restored on the
  next start (replaces today's single "restore last session"; the option keeps its meaning: off = start with
  one empty tab).
- Background tabs restore lazily (transcript loaded on first selection) to keep the start fast.

## Phases
Each phase ends with a build, all tests green and a check against today's behaviour.

1. **Host refactor, no visible change.**
   - **1a — history store with several open chats.** `SessionRecord` references instead of `Current`
     (see "History store"); the bridge holds the shown record itself. New tests for the store (open/load/
     delete/rename/import with records held outside, empty records not persisted).
   - **1b — extract `ChatSession`.** `ChatSession` from `WebViewBridge`, `ChatHost` keeps the shared part;
     exactly one tab. Messages carry `tab` (the WebUI ignores it). Existing tests unchanged.
2. **WebUI refactor, no visible change.** Session state into per-tab objects (list above), transcript
   container per tab, composer save/restore; still one tab. Mock adapter updated.
3. **Tabs live.** Tab strip (incl. rename via pencil/double-click), `tabs.state` / `tab.new|select|close`,
   status markers, unseen, plan mode per tab, Remote Control panel per tab, close with a confirmation while
   a turn runs, up to 3 tabs, WebView reload with several tabs.
4. **Guards.** One-session-one-tab, history panel rules, checkpoint repo lock + rewind hint, edit-review
   ownership (inline + end-of-turn), toast tags + click-to-tab, single Remote Control + adopt over all tabs,
   workspace switch over all tabs, restore open tabs.
5. **Docs & release.** Tests (store, tab status logic, routing, checkpoint lock), `docs/NOTES.md` section +
   contract additions, `CHANGELOG.md` 1.4.0, screenshots with fictional data (SampleShop).

## Risks
- **Size of the refactor:** `app.js` (~7240 lines) and the bridge (~4750 lines) both change. Phases 1 and 2
  are pure refactors so regressions show up before any tab exists; 1a/1b keep the store change apart from
  the class split.
- **Missed session state** (a module variable still global in `app.js`, a bridge field left in
  `ChatHost`): shows up as one tab's content leaking into another — test with two tabs streaming at once.
- **Undocumented CLI files keyed by session** (task output files, background sessions) are already per
  session; verify nothing assumes "one session per window".
- **Memory/CPU:** up to three CLI processes plus their MCP listeners; acceptable, but measure.

## Open questions
- Per-tab model/effort/permission mode (follow-up, see "Decisions").
- Which keys reach the WebView for tab switching (verify in phase 3).
