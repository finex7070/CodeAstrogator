# Plan: Multiple sessions as tabs (target 1.3.0)

> **Status 2026-11-04: draft, not started.** Once built, the current state goes to `docs/NOTES.md`
> and this document stays as the design rationale (like `git-checkpoints-plan.md`).

## Goal
Up to **three Claude sessions at the same time in one chat window**, switched through a **tab strip in
the header** (where the chat title is today). Each tab shows whether its session is working, needs the
user, or has finished since the user last looked. Everything that is not about one conversation (history,
limits, settings, banners) stays **shared**.

Motivation: users already run parallel sessions by opening Visual Studio twice. Inside one window that is
not possible today, and switching windows hides which session is waiting.

## Decisions
- **One WebView, not one per tab.** The page already shows a lot that is the same for every session
  (history, limits, settings, banners); one WebView keeps that in one place and costs one renderer.
- **At most 3 tabs.** Enough for parallel work, keeps memory/CLI processes and the shared usage quota in
  check, and the strip fits a narrow panel.
- **Model · Mode stays global in 1.3.0** (the persisted sticky defaults, as today). Per-tab model/mode is a
  possible follow-up, not part of this plan.
- **Ruled out:** one WebView per tab (duplicates the shared UI, three renderers, the tab strip has to be
  kept in sync in three pages) and VS multi-instance tool windows (`MultiInstances = true`: cheap, but no
  in-panel tab strip and no status markers — and the shared-resource problems below exist all the same).

## What belongs to a session and what is shared

| Area | Scope |
|---|---|
| Transcript (messages, tool/permission/question cards, working indicator with Stop) | **per tab** |
| Bars above the composer: tasks, background tasks, running subagents, end-of-turn review list | **per tab** |
| Composer: text, attachments, queued prompt, prompt suggestion, active-file toggle | **per tab** (one DOM, contents swapped) |
| Statusbar: Working/Ready/Awaiting approval, **Context %** meter | **per tab — shows the active tab** |
| Statusbar: Session/Weekly limits, plan label | shared |
| Header: tab strip (replaces the title), History, Remote Control, New chat, gear | shared |
| Notice / update banners, notify stack, consent popup | shared |
| Settings, theme, fonts, model catalog | shared |
| Chat history store (per workspace) | shared — **one** store for all tabs |
| Remote Control | per session, **at most one at a time** |

## Architecture

### Host: split `WebViewBridge`
Today one `WebViewBridge` (~4700 lines) owns the WebView *and* the whole state of one session.

- **`ChatHost`** (one per tool window): the WebView and `Post`, theme, options, usage meters (the
  `/usage` timer — one for all tabs, not three), model catalog, notices/update/manual checks, workspace
  switching, active-document tracking, the **single `SessionHistoryStore`**, the tab list and tab status.
- **`ChatSession`** (one per tab): `ClaudeSessionService` + process host, its own `McpPermissionBridge`
  (own port/config — simplest, no routing inside the MCP server), pending permissions/questions, the turn
  state (`_turnHadAssistantOutput`, follow-up flag, result text), checkpoint turn state, `TaskOutputWatcher`
  and shell calls, end-of-turn review baselines, edit-review sessions, the remote session, the session
  record it shows.
- **Routing:** every host→web message that belongs to a session carries **`tab`** (the tab id); every
  web→host message about a session carries it back. Messages without `tab` are shared.

### Contract additions (host ↔ web)
- host→web **`tabs.state`** `{ tabs: [{ id, title, state, unseen }], activeId }` — `state` =
  `idle | working | attention | error`; `unseen` = finished or failed since the tab was last active.
- web→host **`tab.new`**, **`tab.select { id }`**, **`tab.close { id }`**.
- Every existing session message gains `tab` (`assistant.*`, `tool.*`, `permission.*`, `question.*`,
  `turn.result`, `status`, `context.usage`, `prompt.suggestion`, `session.init`, `transcript.load`, …;
  `prompt.send`, `turn.stop`, `permission.decision`, `question.answer`, `rewind.*`, …).

### WebUI
- Session-scoped state moves from module-level variables and the global `state` into **one object per
  tab** (messages, active assistant, pending permissions, tasks, turn footer refs, working indicator,
  queued flag, composer contents, turn review, agents, background tasks, status, context usage).
- **One transcript container per tab** in the DOM; switching shows/hides containers, nothing is re-rendered.
  Messages for a background tab render into that tab's container.
- **The composer exists once**: on a tab switch its text, caret, attachments, queued state and suggestion
  are saved to the old tab and restored from the new one.
- Bars above the composer are re-rendered from the active tab's state on a switch.
- **Tab strip** in the header: title per tab (truncated), status marker, ✕ on hover/active, "+" while fewer
  than 3 tabs. Rename = click the active tab's title (today's title behaviour). Narrow panel: tabs shrink to
  marker + short title; the active one keeps the most room.
- **Status markers:** ⟳ working · 🟠 needs you (permission, question, plan) · 🟢 finished and not yet
  seen · red on error. `unseen` clears when the tab becomes active.

## Shared resources — what must be guarded
- **Chat history:** `SessionHistoryStore.Save()` rewrites the whole file. With one store owned by
  `ChatHost` (all tabs under one lock) the tabs can no longer overwrite each other's chats.
- **One session, one tab:** opening a chat from the history that is already open in another tab selects
  that tab instead. Two CLI processes must never `--resume` the same session id.
- **History panel:** "load" opens the chat in the active tab (or switches, see above); delete is refused
  for a chat that is open in a tab with a running turn.
- **Checkpoints:** the shadow repo is per workspace — concurrent snapshots from two tabs need a
  **per-repo lock** (git `index.lock`). A **rewind can undo changes made by another tab** in the same
  workspace: document it and say so in the rewind dialog when other tabs are open on the same workspace.
- **Toasts:** tag = process **and tab** (`p<pid>-t<tab>-<tag>`); the attribution names the tab's chat; a
  click selects that tab.
- **Editor context menu** ("Add file/selection to prompt") goes to the active tab.
- **Remote Control:** one at a time; the tab running it is locked as today; re-adopting a background
  session on start goes to exactly one tab.
- **Inline edit review:** two tabs reviewing the same file at once would fight over the adornments — the
  second request falls back to the diff card in the chat.
- **Workspace switch** (solution opened/closed): applies to all tabs; it waits until no tab has a running
  turn (today's rule, now over all tabs). The open tabs are closed/reset to the new workspace.
- **Usage quota:** three tabs share the plan's limits — nothing to guard, but the limits meter is shared.
- **Same project in several tabs:** sessions can change the same files. The extension cannot prevent
  this; a short hint when a second tab starts a turn in the same workspace while another runs.

## Restart behaviour
- The open tabs (session ids, order, active tab) are persisted per workspace and restored on the next
  start (replaces today's single "restore last session"; the option keeps its meaning: off = start with one
  empty tab).
- Background tabs restore lazily (transcript loaded on first selection) to keep the start fast.

## Phases
Each phase ends with a build, all tests green and a check against today's behaviour.

1. **Host refactor, no visible change.** Extract `ChatSession` from `WebViewBridge`, `ChatHost` keeps the
   shared part; exactly one tab. Messages carry `tab` (the WebUI ignores it). Existing tests unchanged.
2. **WebUI refactor, no visible change.** Session state into per-tab objects, transcript container per tab,
   composer save/restore; still one tab. Mock adapter updated.
3. **Tabs live.** Tab strip, `tabs.state` / `tab.new|select|close`, status markers, unseen, close with a
   confirmation while a turn runs, up to 3 tabs.
4. **Guards.** Shared history store, one-session-one-tab, checkpoint repo lock + rewind hint, toast tags +
   click-to-tab, single Remote Control, inline-review conflict fallback, workspace switch over all tabs,
   restore open tabs.
5. **Docs & release.** Tests (shared store, tab status logic, routing), `docs/NOTES.md` section + contract
   additions, `CHANGELOG.md` 1.3.0, screenshots with fictional data (SampleShop).

## Risks
- **Size of the refactor:** `app.js` (~6200 lines) and the bridge both change. Phases 1 and 2 are pure
  refactors so regressions show up before any tab exists.
- **Missed session state** (a module variable still global in `app.js`, a bridge field left in
  `ChatHost`): shows up as one tab's content leaking into another — test with two tabs streaming at once.
- **Undocumented CLI files keyed by session** (task output files, background sessions) are already per
  session; verify nothing assumes "one session per window".
- **Memory/CPU:** up to three CLI processes plus their MCP listeners; acceptable, but measure.

## Open questions
- Per-tab Model · Mode (follow-up?).
- Should "New chat" in the header open a new tab or reset the active one? (Proposal: reset the active tab,
  "+" in the strip opens a new tab.)
- Keyboard shortcuts for switching tabs (Ctrl+1/2/3 inside the panel?).
