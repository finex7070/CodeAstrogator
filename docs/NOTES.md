# Implementation notes (deviations / additions vs. the plan)

Status: 2026-06-16 — v1 + numerous extensions. Spec = `docs/claude-vs-2026-plan.md`
(unchanged, Parts A/B); this document records all deviations and the current state.
The **chronological feature/version history lives in `CHANGELOG.md`** — here you only find the
current reference/architecture knowledge (protocols, design decisions, code map, roadmap).
Superseded/removed features are no longer kept around "for traceability" (that is in Git).

**Versioning:** Version in `source.extension.vsixmanifest` (`<Identity Version>`). The convention
is in **`CLAUDE.md`** (section "Versioning"): +0.0.1/prompt, +0.1.0/larger change,
Major only on request.

**Verification status (in VS 2026):**
- ✓ 2026-06-03 features verified (Remote Control E2E incl. Stop fix, Ctrl+V
  file/image, dynamic slash menu + /help, Ctx meter + /compact).
- ✓ **Permission hook Phase 1 verified in VS (2026-06-04):** "Ask" → Edit/Write/Bash trigger the
  permission card, Approve writes, Reject lets Claude re-plan; **one** card per tool call
  (permission card replaces the tool card), transition running → Diff → **✓ Applied / ✗ Failed /
  ✕ Rejected**, collapsible with chevron on the left + line numbers; read-only commands don't prompt;
  decided card survives reload.
- ⏳ **Still to be verified (2026-06-04, only build/mock checked):**
  1. **Settings via the new window** (WritableSettingsStore): gear → "Advanced options…"
     → window opens, all options editable (Model/Effort have moved into the popover),
     theme-consistent (Dark/Light: text + field
     backgrounds follow); "Reset to defaults" loads defaults into the form; **Save** applies +
     persists (persistent across VS restart), **Cancel/X** discards; Theme/Verbosity take effect
     live. On store error: ⚠ system note in the chat +
     `%LocalAppData%\CodeAstrogator\settings-error.log`.
  2. Active-file chip: name+`:lines`, truncation on long names, click = session toggle
     (struck through), disappears when the option is off.
  3. **Persistent CLI mode** (Settings → Advanced → "Use a persistent CLI session"): turns
     run, follow-up turns noticeably faster (no spawn); **Stop** aborts in-place (process
     survives); `session.new`/`session.load`/model switch restart the process cleanly; toggle
     off/on takes effect (idle immediately, otherwise on reopen). The protocol was verified
     empirically, but the C# threading/lifecycle only via build/unit test. (The permission hook
     ran in VS — the persistent mode as its carrier is thus indirectly partly tested, but not
     specifically.)

**Defender false positive (2026-06-04, harmless):** Defender reported
`Backdoor:ASP/Dirtelti.G!MTB` on a **CLI session log file**
(`~/.claude/projects/<munged>/<id>.jsonl`) — a pure conversation transcript, **not**
code/no VSIX. The heuristic (`!MTB`) triggered on Remote-Control terms + Base64
signatures in the log. Recommendation: letting the file be removed is fine; on repetition,
add a Defender exception for `%USERPROFILE%\.claude\projects`. The VSIX itself was not
flagged.

## Getting back in (Build / Test / Install)
```powershell
# Build (ONLY VS-MSBuild — dotnet build can't handle the VSIX targets):
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  CodeAstrogator.slnx /t:Restore,Build /p:Configuration=Release /m /v:m

# Tests (78 green as of the status above — pick the path matching the build configuration!):
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" `
  CodeAstrogator.Tests\bin\Release\net472\CodeAstrogator.Tests.dll

# Install: close VS, double-click bin\Release\net472\CodeAstrogator.vsix
# (overinstalls the old version). Debugging: F5 → Experimental Instance.
# Test the UI in isolation: open WebUI\index.html in the browser (mock adapter).
# JS syntax check after each change: node --check WebUI\app.js (and WebUI\qr.js)
```
Checked against **Claude Code CLI 2.1.178** (full re-verification 2026-06-17; earlier baseline
2.1.161/2.1.162 — re-test flags/endpoints on CLI update). **2.1.178 re-test summary:** `--effort`
values (low/medium/high/xhigh/max) ✓ unchanged; `--permission-mode` still accepts
default/acceptEdits/plan/bypassPermissions (new, unused: `auto`, `dontAsk`) ✓; `/usage` report text +
`ParseUsageText` regexes ✓ unchanged (`num_turns:0`, no cost); MCP permission protocol ✓ unchanged
(`protocolVersion 2025-11-25`, tools/call arg **`input`**, allow needs `updatedInput`); AskUserQuestion
✓ routes through the hook (deny+message feeds the answer back); `slash_commands` ✓ still a JSON string
array (now **28** entries, skills listed first → see "Slash commands" popover-scroll fix). **One change
found & fixed:** the MCP tool-call **timeout deliverer** — the config `timeout` field now takes
**precedence** over `MCP_TOOL_TIMEOUT` (see "Prompt timeout too short").

## Structure / code map
- By request, **everything in one project** (`CodeAstrogator.csproj`) instead of the four
  projects named in the plan. Tests separately in `CodeAstrogator.Tests` (xUnit, net472, NDJSON fixtures).
- `Core/` (UI-free, testable):
  `NdjsonParser` (stream-json → domain events), `ClaudeEvents` (event types),
  `ClaudeCliProcessHost` + `IClaudeProcessHost` (one `claude -p` process per turn, prompt via stdin),
  `ClaudePersistentProcessHost` (opt-in: one long-lived bidirectional stream-json process, see "Persistent CLI mode"),
  `ClaudeSessionService` (turn orchestration, --resume, retry, Ultracode injection),
  `ClaudeExecutableLocator` (CLI discovery + auth probe), `ClaudeUsageClient` (limits/plan),
  `WorkspaceFileLister` (@-mention file list), `IPermissionBridge` + `McpPermissionBridge`
  (in-process MCP server for `--permission-prompt-tool`, Phase 1, see "Permission hook"),
  `CliSessionReader` (CLI session discovery + transcript import, see "Remote Control";
  remote control itself now lives in `Services/RemoteTerminalLauncher` — interactive console, see "Remote Control").
- `Bridge/WebViewBridge.cs` — complete §3 message contract host-side, thread marshaling.
- `Services/` — `ThemeService` (VS colors → CSS vars), `SessionHistoryStore`
  (JSON persistence), `ActiveDocumentTracker` (`IVsMonitorSelection` → active editor file).
- `ToolWindows/` — `ClaudeChatWindow(Control)` (WebView2, virtual host `codeastrogator.local`).
- `Options/AstrogatorOptions.cs` — in-memory snapshot of the Unified Settings (CLI path, model,
  Effort, Theme, Verbosity, Restore last session, AutoAddActiveFile, IncludeSelectedLines,
  ActiveFileOnByDefault, UsePersistentCli);
  definitions in `CodeAstrogatorExtension.cs` (see "Unified Settings").
- `WebUI/` — index.html / app.css / app.js / qr.js (dependency-free; mock adapter when the
  host is missing; qr.js = own QR encoder for the remote link).

## Notice/announcement + update banner (2026-06-10, generalized + remote-gated + update check 2026-06-16)
- **What:** Generic notice bar **below the header** (`#notice-banner` in `index.html`,
  `.notice-banner` in `app.css`): info icon + title + Markdown content + ✕ button. **Content and
  on/off come from a file delivered via GitHub raw** — no longer hardcoded.
- **notice.json (repo root, delivered via GitHub raw) — exactly 5 values:**
  - `enabled` (bool) — master switch.
  - `from` (ISO date+time or empty) — from when to show.
  - `to` (ISO date+time or empty) — until when to show.
  - `title` (**plain text**, rendered bold).
  - `content` (**Markdown** — links etc. via `renderMarkdown`).
  **Editing this file + pushing to `master`** shows/changes/hides the banner in all
  installed extensions — **without a VSIX release**. URL hardcoded as `NOTICE_SOURCE_URL` in `app.js`
  (`raw.githubusercontent.com/finex7070/CodeAstrogator/master/notice.json`; raw sends
  `Access-Control-Allow-Origin: *`, CORS fetch ok). There is **no** bundled notice.json anymore.
- **Update banner (parallel, 2026-06-16):** Second, independent banner (`#update-banner`, class
  `notice-banner update-banner`) **directly below** the announcement banner — **both visible at the
  same time**. Source: **GitHub Releases API** (`UPDATE_SOURCE_URL` =
  `api.github.com/repos/finex7070/CodeAstrogator/releases/latest`; CORS-capable, the WebView sets its own
  user agent, 3-h throttling keeps us under the 60/h limit). `/releases/latest` = the newest
  **non-draft/non-prerelease**; we use `tag_name` (= version, leading "v" is stripped)
  and `html_url` (release page). `app.js` compares `tag_name` against the **installed** version
  (`appVersion` from `session.init`) via `isNewerVersion` (numeric, dot-separated); if remote is **truly
  newer** → banner "Update available — Version X.Y.Z … [View release ↗]" (link → system browser).
  The host reads the installed version from the deployed `extension.vsixmanifest` (`Identity/@Version`,
  `GetInstalledVersion()`, cached). **Releases:** upload the build + tag = version (e.g. `0.4.0` or
  `v0.4.0`) → as soon as the version is > the installed one, the banner appears.
- **Repo configurable (build time):** The GitHub `owner/repo` (+ `noticeBranch`) is in
  **`WebUI/config.js`** (`window.CPFC_CONFIG`), which is loaded in `index.html` **before** `app.js`. `app.js`
  builds `NOTICE_SOURCE_URL`/`UPDATE_SOURCE_URL` from it (fallback = `finex7070/CodeAstrogator`/`master`,
  if config.js is missing, e.g. browser isolation test). **Only changeable before the build** (it is bundled
  into the VSIX; no runtime/settings override) → change, rebuild, reinstall.
- **Working phrases in config.js:** `CPFC_CONFIG.workingPhrases` (the "Space Astrogator" one-liners next to the
  rocket). `app.js` filters out empty/non-string entries and otherwise uses the fallback `["Working…"]`
  (if config.js is missing/empty). Editable like the rest of the config: before the build.
- **Opt-in / consent (privacy) — shared for both banners:** Both fetches are network calls →
  **gated**. Persisted in `AstrogatorOptions.NoticeFetchEnabled`/`NoticeFetchDecided` **and**
  `UpdateCheckEnabled`/`UpdateCheckDecided`. As long as **one** is not yet decided, `app.js` shows
  **one** in-window consent popup (`.modal-backdrop`/`.modal`) when the window opens, with **two checkboxes**
  (Announcements + Updates) + "Save". Answer → **`consent.set {noticeEnabled, updateEnabled}`** (web→host)
  → sets both enabled + both decided=true. Also changeable in the **settings window** (two checkboxes under
  "Announcements & updates"); Save sets decided=true and pushes **`banner.settings`** (host→web, via
  `OnOptionsChanged`/`SendBannerSettings`) → banners are loaded/hidden live.
- **Load logic (`app.js`):** `applySessionInit` → `evaluateBanners({notice*, update*, appVersion})` (once
  per window load, flag `bannersEvaluated`): one not decided → consent popup; otherwise, depending on opt-in,
  `loadNotice()` and/or `loadUpdate()`; **disabled → no fetch at all, no display at all.**
- **Fetch policy + cache (shared, `fetchThrottledCached`):** Cache in **`localStorage`** (persisted via
  a fixed WebView2 UserDataFolder): `cpfc.notice.*` resp. `cpfc.update.*` (`.cache` = last success, `.lastFetch`
  = ms timestamp of the last **attempt**). Fetch only if **≥3 h** (`MIN_INTERVAL`) since the last attempt
  (timestamp set **before** the attempt → failures throttled too). Success → cache + render. **Failure
  → render from cache; no cache → nothing.** Retry on the next open (3-h throttled).
- **Behavior:** Banners are `hidden` by default in the HTML. ✕ (`#notice-close`/`#update-close`) sets
  `hidden` — **only for the session, no persistence**; re-evaluated on the next open.
- **External link:** `ClaudeChatWindowControl` now registers `core.NewWindowRequested`
  (`OnNewWindowRequested`) → `target="_blank"` links (banner **and** rendered Markdown links) open
  in the **system browser** (`Process.Start`, `UseShellExecute=true`; http/https only) instead of in a
  bare WebView2 popup.

## Tasks banner (2026-06-24)
- **What:** A third banner below the header (`#tasks-banner`, class `notice-banner tasks-banner`)
  that aggregates the CLI's **`Task*` tool calls** into a live, collapsible checklist (☐ pending /
  ◐ in_progress / ☑ completed + a done/total count) so a long multi-step turn can be followed at a
  glance without scrolling the individual tool cards. The cards still render as normal in the
  transcript — the banner is an additive summary.
- **Pure web-side — no new host↔web messages.** Driven entirely by `app.js` off the existing
  `tool.use` / `tool.result` stream and the `transcript.load` history (`role:"tool"` records).
  Nothing changed in the Bridge/Core.
- **Tracking (`app.js`):** `trackTaskTool(name, id, input)` dispatches `TaskCreate`→`taskOnCreate`,
  `TaskUpdate`→`taskOnUpdate`. State lives in `state.tasks` (`[{id, subject, activeForm, status}]`).
  - **Id mapping:** `TaskCreate` input has **no** id, so a task gets a **provisional sequential id**
    (`taskSeq`, matching the CLI's 1-based numbering); on its `tool.result` (`reconcileTaskId`) the
    real id is parsed from the summary text **`Task #N created…`** and overwrites the provisional one
    (so deletions/resumes can't desync the `taskId` mapping). History has no result text → the
    sequential id stands (correct in the common, no-deletion case).
  - `TaskUpdate` (`input.taskId` + `status`/`subject`/`activeForm`) updates the matching row; an
    update for an unseen task (truncated history) **synthesizes** a row; `status:"deleted"` removes it.
  - In_progress rows show `activeForm` (falls back to `subject`); others show `subject`.
- **Lifecycle — one list per turn (2026-06-24):** A task list belongs to **one turn**. When a turn
  ends (`turnResult` sets `taskBatchClosed=true`), the **next `TaskCreate` clears the previous,
  worked-through list and starts a fresh one** instead of appending — so each follow-up prompt shows
  *its own* list, not an ever-growing pile. Mere `TaskUpdate`s (no new create) keep updating the
  current list, and additional `TaskCreate`s **within the same turn** still append. `resetTasks()`
  (from `applySessionInit` / `loadTranscript`) clears everything for a new/loaded chat. ✕
  (`#tasks-close`) dismisses **for the session** (`state.tasksDismissed`); a **new `TaskCreate`**
  re-shows it (status updates do **not** un-dismiss). Head row toggles `.collapsed` to fold just the list.
- **History = no banner (2026-06-24):** The tasks banner is a **live-turn aid only**. Reopening a
  chat (`transcript.load`) does **NOT** rebuild it — `renderHistoricMessage` deliberately skips
  `trackTaskTool` for historic `tool` records, and `loadTranscript`'s `resetTasks()` leaves the banner
  empty. (Live tracking runs solely from `toolUse` / `toolResult`.)
- **Mock:** `WebUI/index.html` (browser isolation) simulates a 3-task `TaskCreate` burst + a
  `TaskUpdate` walk (in_progress → completed) so the banner can be verified without VS.

## Open items / Roadmap (from plan §A8 + findings)
1. **MCP permission bridge + inline diff review** — **Phase 1 implemented** (standard chat card,
   allow/deny), still to be verified in real VS. **Phase 2/3 open** (extended editor inline
   diff per hunk, pulls in #3 `updatedInput`). Details see section "Permission hook &
   inline diff review". `Core/McpPermissionBridge` is live; `IPermissionBridge` is no longer a stub.
2. ~~Persistent bidirectional CLI mode~~ — **implemented (opt-in, default off)**, see
   section "Persistent CLI mode". Still to be verified in real VS.
3. `updatedInput` (edit the diff before approve) — **redeemed with #1 (extended mode)**.
   Remaining open: multi-tab, context injection of the active editor.
4. Remote Control expansion (optional): worktree spawn mode (`--spawn worktree`),
   show remote sessions in the history already **during** the run,
   `CONTEXT_MAX_TOKENS` model-dependent instead of fixed 200k.

Done (2026-06-03): `session.rename` + limits `resets_at` tooltips; theme fix
(inline vars), copy context menu, Ctrl+V file/image paste, dynamic slash menu +
host-side `/help`, **Remote Control** (button → QR/link → Stop → session import),
**Ctx meter** (context instead of token sum, /compact click, near-limit colors,
compact_boundary evaluation) — details in the respective sections below.

## Contract additions (Part B §3)
- **`attach.added` (host → web)** — not defined in the plan, but necessary: on
  `attach.files`/`attach.context`/`attach.browse` the host opens a picker and returns the result as
  `{ type: "attach.added", attachments: [{ name, path }] }`; the UI renders chips.
- `prompt.send.attachments` carries `{ id, name, path? }`; the host appends `@<path>` references
  to the prompt (the CLI reads the files itself). **Paths with spaces are quoted**
  (`Core/CliReferenceFormatter.FormatFileReference`): otherwise the CLI's `@` parser breaks at the
  first space → the file is silently discarded. Affects mainly **pasted
  screenshots** (which land under `%LocalAppData%\…\pasted\`, i.e. in the user profile; a space in
  the Windows username like "Jan Huels" dragged the path along). Quoted: `@"C:\a b\f.png"`;
  the `#L` line suffix (active file) sits **outside** the quotes: `@"…\f.cs"#L10-20`.
  Verified against the CLI (quoted paths are expanded, unquoted ones with a space are not).
- **`system.note` (host → web)** `{ id, text }` — dimmed one-liners in the transcript
  (session start, the turn footer source is `turn.result`, "Turn stopped", "Context compacted",
  "Permission denied by user"). Stored as role `system` in the history. (Auto-approved edits are
  no longer a system note since 2026-06-05, but a green permission card — see "Permission hook".)
- **`thinking.start/delta/end` (host → web)** `{ id, text?, estimatedTokens? }` —
  Extended Thinking. The UI renders a transient "✻ Thinking…" line (details see table
  "Transcript display"); cards only if real thinking texts arrive.
  **Stop cleanup (v0.4.1):** an interrupted turn emits no `thinking.end`, so `applyStatus`
  finalizes any orphaned `state.activeThinking` (`finalizeActiveThinking`) whenever the status
  leaves `working`/`waiting-permission` for a terminal state — the transient line is removed,
  a streamed card just loses its spinner. (Mirrors the permission-card expiry in the same hook;
  no-op on normal turns where `thinking.end` already cleared it.)
- **`accent.set` (web → host)** `{ color }` — custom brand color (CSS hex `#rgb`/`#rrggbb`, or ""
  = default). The host validates (`NormalizeHexColor`), stores it in `AstrogatorOptions.AccentColor` +
  `SaveOptions`. `session.init` additionally carries **`accent`**; the UI applies it via `applyAccent`
  (`:root` inline overrides). From the gear popover (swatches + custom picker).
- **`verbosity.set` (web → host)** `{ level: "compact"|"normal"|"detailed" }` +
  `session.init.verbosity` — purely a client-side display density (the host only persists
  the string + forwards it; CLI output is unaffected). **Compact** (`v-compact`) hides
  `.sys-note` + `.thinking-card` via CSS. **Normal** (no class) shows everything; thinking
  **and** tool cards start collapsed. **Detailed** (`v-detailed`) starts thinking **and** tool
  cards expanded (input/output visible) — wired via `collapsedInit()` at card creation
  (thinking `upgradeThinkingToCard`, live tool card, transcript-rebuild tool card).
  `applyVerbosity()` also **re-applies** the collapse state to existing
  `.thinking-card, .tool-card:not(.todo-card)` so switching takes effect on the current
  transcript live (todo cards stay open). (v0.4.3: before this, Normal vs Detailed only
  differed in the thinking-card default and didn't update existing cards.) Persisted in the
  Unified Settings. A `.mm-hint` line under the gear popover segment describes each level.
- **`files.listRequest` (web → host) / `files.list` (host → web)** `{ files: [{path, isDir}] }`
  — workspace file list for the `@`-mention autocomplete (BFS, bin/obj/.git/… excluded,
  max. 2000 entries, 30 s cache host-side).
- **`options.open` (web → host)** `{}` — "Advanced options…" entry at the bottom of the
  gear popover; the host opens Tools → Options → Code Astrogator → General
  (`Package.ShowOptionPage`). The mock shows a system note instead.
- **"Changelog…" entry (2026-06-18)** — directly below "Advanced options…" in the gear popover.
  No host message: it just calls `window.open(CHANGELOG_URL, "_blank")` with the GitHub URL, which
  the WebView routes through `OnNewWindowRequested` → system browser (same path as transcript links).
- **Installed version line (v0.5.1)** — directly below the "Changelog…" entry, a `.appearance-version`
  div renders `Version <x.y.z>` from the `appVersion` already carried by `session.init`
  (`GetInstalledVersion()` in `WebViewBridge.cs`). Hidden when `appVersion` is empty (e.g. mock).
- **`session.delete` (web → host) (v0.4.2)** `{ sessionId }` — trash button per history row (revealed on
  hover, `.hi-delete`), gated by a **confirmation modal** (`openConfirmModal`, reusable; the
  Delete button is styled `.modal-btn.danger`). Host: `HandleSessionDelete` → `SessionHistoryStore.Delete`
  (returns `(deleted, wasCurrent)`); if the **active** session was deleted it behaves like "new chat"
  (`ResetSession` + `SendSessionInit`); always re-sends `session.list` so an open popover refreshes.
  `.history-item` is now a `<div>` (was a `<button>`) so the delete button can nest without invalid markup.
  The CLI keeps its own conversation store, so a deleted session may still be `--resume`-able.
- **Turn-running guard (v0.5.1):** switching/clearing the conversation mid-turn would orphan the live CLI
  process (`ResetSession`/`AttachToRecord` don't stop it — events would land on the wrong session). So
  `HandleSessionNew` / `HandleSessionLoad` and `HandleSessionDelete` (active session only) bail via
  `TurnRunningBlocks(action)` → `PostSystemNote("Stop the current turn first…")` while `_session.IsBusy`
  (covers both *working* and *waiting-permission*). Client mirrors it: `isTurnActive()` =
  `status === "working" || "waiting-permission"`; `applyStatus` disables **New chat** + **remote** while
  active, the history popover renders read-only (`.history-item.disabled` + `.history-hint`), and the
  remote-start click is guarded. Deleting a *non-active* session mid-turn is still allowed.
- **`session.rename` (web → host)** `{ sessionId, title }` — the edit icon to the right of the
  header title (visible on header hover/focus) opens a rename modal (Enter = Save,
  Esc = Cancel). The UI sets the title optimistically; the host trims, caps at 120
  characters and persists via `SessionHistoryStore.Rename` (recency stays untouched).
  No echo needed — `session.list`/`session.init` deliver the title anyway.
- **Limits with reset times:** `limits` (in `session.init`/`turn.result`) and
  `usage.update` additionally carry `sessionResetsAt`/`weeklyResetsAt` (ISO-8601 from
  the usage endpoint's `resets_at`, null if unknown). The UI shows them as native
  tooltips on the S/W meters ("Session usage: 12% · resets 14:30"; today = time of day,
  tomorrow = "tomorrow", otherwise weekday+date).
- **Further additions from 2026-06-03** (details in dedicated sections below):
  `slash.commands` (host→web), `clipboard.paste` (web→host),
  `remote.start`/`remote.stop` (web→host) + `remote.state` (host→web),
  `contextTokens` in `session.init`/`turn.result`/`usage.update`.
- **Turn footer (2026-06-30):** the divider line below each turn now renders
  **`durationMs · turnOutputTokens tok · costUsd`** (was time-only). `turnOutputTokens`
  = `TurnResultEvent.OutputTokens` = `result.usage.output_tokens`, the **aggregate output
  generated across the whole turn** ("work produced") — deliberately NOT `contextTokens`
  (last-message size, which still feeds the Ctx meter). Each part is dropped when 0/absent
  (`app.js` `turnResult`); cost via `formatCost` ($0.012 sub-dollar → 3 dp, ≥$1 → 2 dp,
  `<$0.001` for tiny). **Cost is shown in every auth mode** (user decision 2026-06-30); note
  that in subscription mode `total_cost_usd` is the *estimated* API-equivalent, not a real
  charge — gating it to API-key / over-limit would be easy to add later (auth proxy:
  `ClaudeUsageClient.ReadPlanLabel`/`oauthAccount`; over-limit has no clean signal today,
  only session/weekly % from `/usage`).

## `+` menu & @-mentions (2026-06-03)
- *Add file…* → file picker (`attach.files` → `attach.added` chips).
- *Add context…* → inserts `@` at the caret; `@` autocomplete opens (files + folders,
  filter while typing, folder selection drills further). Typing `@` directly triggers it too.
- *Browse the web* → inserts `@browser:` at the caret (no autocomplete for it).

## Settings (WritableSettingsStore + own settings window, 2026-06-04 — CURRENT)
- **Why the switch:** The Unified-Settings **in-proc API does not work in VS 2026** —
  `GetServiceAsync<VisualStudioExtensibility>()` permanently throws `ServiceUnavailableException`
  ("service unavailable"), even after 30 s of retries. The in-proc Extensibility service host
  is never started despite a correct VSIX (`.vsextension/extension.json`, `ExtensionType=VSSDK+…`);
  the 17.14 SDK is the latest version (no 18.x). Hence the entire
  Extensibility hybrid layer was **removed** (Extension class + `SettingDefinitions` deleted,
  `VssdkCompatibleExtension` + Extensibility packages from the csproj, `ExtensionType` from the
  manifest → back to **pure VSSDK**; the MSB4011/CEE0027 warnings are gone with it).
- **Storage:** `Services/AstrogatorSettingsStore` wraps the classic **`WritableSettingsStore`**
  (`ShellSettingsManager` → `SettingsScope.UserSettings`, collection `CodeAstrogator`).
  Always available in-proc, synchronous, no preview API. The package reads at load into the
  `AstrogatorOptions` snapshot (`GetOptions()`), writes via `SaveOptions()`/`UpdateOptions()`
  and still fires `OptionsChanged` (Theme/Verbosity take effect live).
- **Settings UI:** `ToolWindows/AstrogatorSettingsWindow` (hosted VS `DialogWindow`, WPF in
  code, VS-theme-aware). Opened via the gear popover → "Advanced options…" (`options.open`
  → `Package.OpenOptions()` → `ShowModal()`). **Layout (v0.4.1):** content sits in a `ScrollViewer`;
  the window uses `SizeToContent.Height` **capped by `MaxHeight = WorkArea.Height − 40`** so it never
  exceeds the screen — past the cap the ScrollViewer scrolls (small-screen safe). Checkbox labels are
  wrapping `TextBlock`s (`MakeCheck`, `VerticalContentAlignment=Top`) so long descriptions don't clip
  at the right edge. Options editable (TextBox/Combo/Checkbox;
  Model/Effort have been popover-controlled since 2026-06-05 and are no longer here,
  Browse button for the CLI path), **"Reset to defaults"** (= `new AstrogatorOptions()`, only loads into
  the form, persists only on Save). The `IncludeSelectedLines` checkbox is greyed out when
  `AutoAddActiveFile` is off.
- **Buttons & persistence semantics (changed 2026-06-04):** Bottom left **"Reset to defaults"**,
  bottom right **"Cancel"** + **"Save"**. **Save** (`IsDefault`, Enter) → `ApplyAndPersist`
  (`UpdateOptions` → store write + OptionsChanged) + Close; **Cancel** (`IsCancel`, Esc) and the
  window X → **discard** (no writing). Previously every close persisted
  (`OnClosed` removed) — a deliberate behavior change on request (explicit Save/Cancel).
- **Theming (2026-06-04, 2nd iteration):** Every input control gets the **VS-themed
  style** via `SetResourceReference(StyleProperty, VsResourceKeys.…StyleKey)`
  (`TextBoxStyleKey`/`ComboBoxStyleKey`/`CheckBoxStyleKey`/`ButtonStyleKey`). The window `Background`/
  `Foreground` (VsBrushes.Window/WindowText) remains for the surface + labels.
  - **Why not just brushes:** The first attempt set only `Background`/`Foreground`/
    `BorderBrush` via `CommonControlsColors` brush keys. That works for `TextBox` (the template uses
    `TemplateBinding Background`), but **not** for `ComboBox`: its default WPF template has
    its own toggle-button chrome → dropdowns were light grey with unreadable text in Dark mode.
    The `VsResourceKeys` styles replace the complete template (incl. dropdown popup, hover,
    glyphs) theme-correctly for Dark & Light. Local values (MinWidth/Padding) are preserved.
- **Gear write paths** (`theme.setMode`/`verbosity.set`) set the snapshot and call
  `SaveOptions()` (instead of the former `PersistSetting`).
- On store errors: `RecordSettingsError` → `%LocalAppData%\CodeAstrogator\settings-error.log`
  + the bridge shows a ⚠ system note on `ready`; defaults still apply.

## Startup behavior & defaults
- **Restore last session** (Settings → Code Astrogator, default on): When the
  tool window opens, the workspace's most recently updated session is loaded
  (`session.init` first, then `transcript.load` — the other way round session.init wipes the
  just-rendered transcript again; this ordering bug previously lived in session.load).
- **Model·Mode defaults are persistent (2026-06-05):** Model/Effort/Ultracode/Permission are
  set in the popover and stored in `AstrogatorOptions` (default effort = high, Permission = ask,
  Model = CLI default, Ultracode = off). New chats and VS restarts start with the last
  chosen values. No longer in the settings window.

## Session resume robustness
- `--resume` adopts the `session_id` **only when `num_turns > 0`** — local turns like
  `/help` report an ID without a persisted conversation ("No conversation found" bug).
- If a `--resume` still fails with "No conversation found", the session ID is
  discarded and the turn is **automatically restarted fresh once**.

## Transcript display (decisions from 2026-06-03)
| Event | Display |
|---|---|
| Slash/result-only turns (`result.result` without stream) | full assistant block (fallback) |
| Session start, turn footer (time·cost·tokens), Stop, Compact, Auto-Approve, Deny | system line |
| Thinking | transient "✻ Thinking…" line (without token counter; the Print CLI redacts the text — empty `thinking_delta`s); disappears on thinking.end. Once text arrives per item (future CLI), upgrade to a collapsible card (Detailed: open) |
| Task/Agent tool | tool card with accent edge + description |
| TodoWrite | checklist card (☐/◐/☑, open, collapsible) |
| ExitPlanMode | plan card (Markdown, accent border). **When a permission prompt follows** (plan mode → the MCP hook fires), the perm-card renders the plan as Markdown too and the standalone `.plan-card` is dropped, so the plan shows **once** (not Markdown card + raw-JSON approval card). Dedup handles both arrival orders: `permissionRequest` removes any `.tool-card`/`.plan-card` with the same id; `planCard` skips if a `.perm-card` for the id already exists. |
| Long tool outputs (>1200 chars) | "Show more" collapse in the card (parser limit 10,000 chars) |
| Tool input streaming, stderr warnings | deliberately not shown |

## CLI integration (Part A §A3)
- The prompt is passed to `claude -p` via **stdin** (not argv) — robust against
  multiline prompts and the npm `claude.cmd` shim.
- **Effort:** 5 levels instead of the 3 from §5.4 — the CLI (checked against 2.1.161) knows
  `--effort low|medium|high|xhigh|max`; passed through per turn
  (default **high**, configurable in Tools → Options).
  "ultracode" is deliberately **not** a level (prompt keyword for multi-agent, not an effort level).
- **Turn-failure messages (v0.5.2):** a non-zero exit is no longer reported as the bare
  `claude exited with code N`. Many CLI failures (API errors, low credit balance, prompt-too-long,
  invalid model) arrive on **stdout** as a `result` event with `is_error:true` + a human `result`
  text while **stderr stays empty** — that text was previously discarded (the bridge only renders
  `result.result` when `!is_error`). `ClaudeSessionService` now caches the error `result` text per
  turn (`_lastResultError`, reset at turn start, set in `Bookkeep`) and folds it into the
  `TurnCompleted` error: `claude exited with code N:\n<detail>` where detail = result-error text,
  else the stderr tail, else `(no error output)`. The 40-line `StdErrTail` capture is unchanged.
  - **"Max" looping effect (v0.5.1):** the **max** segment always carries a `seg-effort-max` marker
    class, and `updateModelModeLabel()` toggles `is-max` on `#btn-modelmode` when `state.effort === "max"`
    (the effort click handler now calls `updateModelModeLabel()` so the pill flips immediately). CSS
    `.seg-btn.seg-effort-max.active` and `.modelmode-btn.is-max` apply a flowing purple gradient
    (`@keyframes max-flow`, `background-size:200%`) + pulsing glow — `max-glow` (outer halo, used on the
    free-standing pill) vs `max-glow-inset` (the segment sits in an `overflow:hidden` track, so an outer
    halo would be clipped). A `prefers-reduced-motion` block drops the motion but keeps gradient + a steady glow.
  - **"Ultracode" looping effect (v0.5.1):** the Ultracode toggle row carries an `ultra-row` marker class;
    `.ultra-row.on .toggle-switch` turns the switch into a flowing multi-hue spectrum (cyan→indigo→purple→pink,
    reusing `max-flow` + a tight glow — kept small because `.popover` is `overflow-y:auto`). The bottom pill
    is driven by `updateModelModeLabel()`, which toggles **`is-ultra`** when `state.ultracode` and **`is-max`**
    only when `state.effort === "max" && !state.ultracode` — i.e. **Ultracode wins over Max on the pill**.
    `.modelmode-btn.is-ultra` uses the spectrum gradient + `@keyframes ultra-glow` (cyan/violet halo). The
    `prefers-reduced-motion` block stops the motion (keeps gradient + steady glow).
- **Session/weekly limits:** stream-json delivers no limit data. The host therefore calls
  the **`/usage` slash command headless** (`claude -p /usage --output-format json`,
  `ClaudeUsageClient.FetchAsync(exe, cwd, ct)`): runs **locally, no API turn, no cost**
  (`num_turns: 0`), works with any CLI auth and replaces the former scraping of the
  OAuth token + `GET api.anthropic.com/api/oauth/usage`. stdin is closed immediately (the
  command takes no prompt → no 3-s "no stdin" wait). The report text is parsed:
  `Current session: N%` → S meter, `Current week (all models): N%` → W meter (the per-model
  lines like "(Sonnet only)" are ignored); `resets <Mon> <Day>, <h>pm` → reset tooltip
  (as local wall-clock time, year from "now" incl. year-boundary rollover). Fetched on `ready`
  (window open), after each turn end, **and periodically every 5 min while idle** (v0.4.1:
  `WebViewBridge._usageTimer`, `System.Threading.Timer`, `UsageRefreshIntervalMs`; the tick skips
  when `_session.IsBusy` — a running turn refreshes on its own end — and is disposed with the
  bridge; `RefreshUsage` is thread-agnostic, `Post` is teardown-guarded). Errors (API-key mode
  without limits, offline, 30 s timeout) → meters stay put. Plan badge still from
  `~/.claude.json` → `oauthAccount.organizationType`
  (claude_team → "Team Plan" …). Note: the report wording is undocumented — on a
  CLI update re-test the parser (regex in `ClaudeUsageClient.ParseUsageText`).

## Persistent CLI mode (Roadmap #2, 2026-06-04 — opt-in, default off)
- **What:** Instead of one process per turn, a **long-lived** `claude -p --input-format stream-json
  --output-format stream-json --verbose --include-partial-messages [--resume] [--model] [--effort]
  [--permission-mode]`. Advantage: no spawn per turn (latency) + **in-place interrupt**.
- **Activation:** Option `UsePersistentCli` (bool, default **off**) in the settings ("Advanced →
  Use a persistent CLI session"). The proven per-turn host (`ClaudeCliProcessHost`) stays
  the default. The toggle takes effect live when idle (bridge `ApplyProcessHostOption`), otherwise on the next
  tool-window open. Both implement `IClaudeProcessHost` → UI/session service unchanged.
- **Implementation:** `Core/ClaudePersistentProcessHost` (`IClaudeProcessHost, IDisposable`):
  - **Turn:** writes `{"type":"user","message":{"role":"user","content":[{"type":"text",
    "text":…}]}}` (newline-terminated) to stdin, reads up to the `result` line → `ClaudeTurnExit`,
    the process keeps living. stdin = UTF-8 `StreamWriter` on `BaseStream` (net472 has **no**
    `ProcessStartInfo.StandardInputEncoding`).
  - **Transparent restart:** `IsCompatible` compares a `FlagSig` (Model/Effort/Permission/
    cwd/exe/extraArgs/env) **and** the session: the host tracks the live `session_id` from the stream
    (`HandleLine`); reuse only if `request.SessionId == _liveSessionId` (resp. `== _startResumeId`).
    `session.new` (SessionId→null) and `session.load`/model switch ⇒ restart (fresh resp.
    `--resume <id>`). This keeps the session service's "No conversation found" retry intact
    (the process dies with stderr → ExitCode≠0 → service retries with SessionId=null → fresh).
  - **Stop = interrupt:** ct-cancel → `{"type":"control_request","request_id":…,"request":
    {"subtype":"interrupt"}}`. The CLI answers `control_response success` and ends the turn
    with `result subtype=error_during_execution` (process survives). **Kill fallback** after 4 s if
    no turn end. `WasCancelled=true` ⇒ the bridge shows no error (only "Turn stopped").
- **Empirically verified (CLI 2.1.162, probe):** Startup without an initial prompt arg; user-message
  format exactly as above; `system/init` arrives **per turn** (identical to the per-turn pattern — the bridge
  sends no transcript-wiping `session.init` on `SessionInitEvent`, so no regression);
  interrupt + "process keeps living afterwards" confirmed. Plain-string `content` is rejected
  (array required). Slash/TUI commands stay headless (like per-turn).
- **Tests:** `PersistentProcessHostTests` cover `BuildArguments` (resume explicit, not from
  SessionId; Model/Effort/Permission; default-Permission omit). Process lifecycle/interrupt =
  to be verified manually in VS.

## Model·Mode popover (Part B §5.4)
- **Persistence (2026-06-05):** All popover selections (Model/Effort/Ultracode/Permission) are
  persisted host-side. The bridge handlers (`model.set`/`effort.set`/`ultracode.set`/
  `permission.set`) write the value into `AstrogatorOptions` (`DefaultModel`/`DefaultEffortString`/
  `UltracodeEnabled`/`PermissionModeString`) and call `SaveOptions()` (no `OptionsChanged` →
  no loop). The `WebViewBridge` ctor seeds `_session.Settings` from these options. `session.new`
  does **not** reset the settings (`ResetSession` only nulls the `SessionId`), so they apply
  across new chats; after a VS restart they come from the store. The settings window no longer shows Model/Effort
  (but carries the values through unchanged on Save, so they aren't overwritten).
- The separate **plan-mode toggle was removed**: "Plan" in the permission radio is the same thing
  (`--permission-mode plan`) — two controls for one state was confusing.
  `mode.set` stays in the contract (the host still processes it), the UI no longer sends it.
- **`mode.update` (host→web, contract addition, v0.3.6):** `{ permissionMode, planMode }`. The host
  uses it to push a host-side mode change into the UI selector, **without** wiping the transcript
  (unlike `session.init`). Used on plan approve: `ApplyPlanApprovedMode` switches to
  `acceptEdits` and sends `mode.update`; the UI (`applyModeUpdate`) sets `state.permissionMode`/
  `state.planMode` and calls `updateModelModeLabel()`.
- **Ultracode toggle** (contract addition): web→host `ultracode.set { enabled }`;
  `session.init` additionally carries `ultracode: bool`. With the toggle active, the host appends
  the keyword `ultracode` to every prompt (opt-in for multi-agent workflows in the
  CLI; no CLI flag, hence prompt injection). No duplication if the user types the
  keyword themselves. The button label then shows e.g. `Opus 4.8 · Ask · Ultra`.

## Active-file reference (2026-06-04)
- **Feature:** The file in the active editor tab is automatically appended as an `@<path>` reference
  to every prompt. Shown as a chip to the right of the slash button ("GameManager.cs").
- **Two-level control (the option takes precedence):**
  - **Option** `autoAddActiveFile` (boolean, default on) in the Unified Settings
    (`SettingDefinitions` + `AstrogatorOptions.AutoAddActiveFile`) = **master switch**.
    Off ⇒ feature fully off, **chip completely hidden**, no reference.
  - **Session toggle** = click on the chip → web→host `activeFile.setEnabled { enabled }`.
    Changes **only** `_activeFileSessionEnabled` in the bridge, **does NOT persist** to the
    option; applies until the session changes (reset to `ActiveFileDefaultOn` in
    `HandleSessionNew`/`HandleSessionLoad`/remote import). Off = chip with
    **strikethrough** (CSS `.active-file-chip.off`).
  - **Default option** `ActiveFileOnByDefault` (boolean, default on, v0.4.3) = what the per-session
    toggle resets to at the start of a fresh chat (seeded in the bridge ctor + every session reset
    via `ActiveFileDefaultOn`). On = a new chat references the file immediately (legacy behaviour);
    off = a new chat starts with the chip **off** (strikethrough) and the user clicks it to opt in
    per chat. Sub-option of `AutoAddActiveFile` in the settings window (greyed out when the master
    switch is off, like `IncludeSelectedLines`). Does not retroactively flip the current chat.
  - Effective reference only when `ActiveFileEffective = Option && Session`.
- **Tracking host-side:** `Services/ActiveDocumentTracker` via `IVsMonitorSelection`
  on `SEID_DocumentFrame` (focusing a tool window — including our chat — does not change
  this frame, so the last code file stays put). Path from
  `VSFPROPID_pszMkDocument`. Bridge: `OnActiveDocumentChanged`/`OnOptionsChanged`/
  `ready`/session change → host→web **`activeFile { path, name, optionEnabled, enabled }`**
  (`optionEnabled` controls visibility, `enabled` = session toggle; name=null ⇒ chip off).
- **Selected lines (2026-06-04):** If text is selected in the active editor, the
  reference appends the line range: `@<path>#L<top>` resp. `#L<top>-<bottom>` — verified
  empirically against CLI 2.1.161 (the CLI reads exactly those lines). Own option
  `includeSelectedLines` (boolean, default on; only effective when `autoAddActiveFile`
  is on). The selection is read via **DTE** (`ActiveDocument.Selection` as
  `EnvDTE.TextSelection`, `GetActiveSelection()` in the bridge) — only when the active
  document == the tracked path; the bottom line at column 1 is reduced by 1
  (the selection doesn't actually cover that line). `activeFile` additionally carries `lines`
  (e.g. "42-58", null = none/off); the chip then shows `GameManager.cs:42-58`
  (name + lines in separate spans: the name truncates with an ellipsis, the line span
  never shrinks → lines stay visible even with long file names).
  Live update: the UI sends `activeFile.refresh` (web→host) on **composer focus AND window focus**
  (back into the chat tool window), the host re-reads option + current
  selection (DTE has no usable selection-changed event; the editor selection
  is preserved when focus moves into the WebView tool window). The window-focus
  trigger covers the case "option changed in Tools→Options, then back into the chat".
- **Unified Settings limitation:** A conditional grey-out/disable dependency
  ("grey out IncludeSelectedLines when AutoAddActiveFile is off") is **not** possible
  via the Extensibility settings API — the `Setting.*` types expose
  no `VisibleWhen`/`enableWhen`, and patching the generated `settingsRegistration.json`
  would be fragile (risk: breaks the whole settings registration).
  Functionally it is correct anyway: with `AutoAddActiveFile=false` the chip is off,
  no reference, lines irrelevant — the lines option then simply has no effect.
  When the main option is turned off, the chip disappears (the subscription fires per the docs
  also on external settings-UI changes; the window-focus refresh is the
  additional safeguard against commit timing).
- **Prompt attachment:** In `HandlePromptSend` the active file (if
  `ActiveFileEffective` + not already present as an attachment, case-insensitive) incl.
  `#L` line suffix is included in the same "Attached files:" block as manual attachments.
  With remote active the chip is locked too (`setRemoteLocked`).
- Mock: sends `GameManager.cs:42-58` (optionEnabled always on) and reflects the session toggle.

## Message backgrounds & attachment display (2026-06-05)
- **Color distinction:** `.msg-user .msg-body` (blue) vs. `.msg-assistant .msg-body` (orange),
  each a subtle background + left 2px border. Vars `--msg-user-bg`/`--msg-user-border`/`-fg` +
  `--msg-assistant-bg`/`--msg-assistant-border`/`-fg` in **both** palettes
  (`:root[data-theme=dark|light]`). Deliberately NOT the brand accent (purple) — blue/orange separate the
  roles clearly; orange is the former Claude clay color. `ThemeService` does not overwrite these vars
  (Auto mode falls back to the palette defaults).
- **Glyph in the card (2026-06-05):** `makeMsgRow` now places the role glyph (`›`/`✳`) **inside**
  the bubble (`.msg-body` = flex container with `.gutter` + `.msg-content`), no longer as an
  outer sibling. Glyph color role-specific: user = `--msg-user-fg` (blue), Claude `✳` =
  `--msg-assistant-fg` (orange). `makeMsgRow` still returns `body` = the content element
  (all append paths unchanged). System notes (empty glyph) keep their indentation.
  - **Assistant avatar (2026-06-18):** the assistant `✳` glyph was replaced by the head logo —
    `makeMsgRow` renders an `<img class="gutter-logo" src="head.png">` for `role === "assistant"`
    (user/system still use the text glyph). `WebUI\head.png` = 32-px head icon (downscaled from
    `Resources\codeastrogator-head.png`), served from the WebUI folder like `logo.png`; CSS
    `.gutter-logo` 18×18 + `.msg-assistant .gutter { width:18px }`. The `--msg-assistant-fg`
    glyph color no longer applies (it's an image now).
- **Attachment chips in the transcript:** `appendMsgAttachments(body, attachments)` renders one
  `.att-chip` per file (file icon + name, `path` as tooltip). Used by `renderUserMessage` (live) and
  `renderHistoricMessage` (role user). **Bug fixed:** the chips were previously built but never appended to
  the body element.
- **Live vs. host record:** Live takes `sendPrompt` `state.attachments` **plus** the active file
  (from `state.activeFile`, if `optionEnabled && enabled`; name incl. `:lines`). Host-side
  (`HandlePromptSend`) the user message is persisted with `attachments`=[{name,path}] (explicit + active
  file with `#L` suffix in the path) and `text`=the **typed** text (without the "Attached files:" block);
  to the CLI still goes the text **with** the `@<path>` block. `SessionHistoryStore`
  serializes the whole message → `attachments` survives reload. The mock history shows two chips.

## Remote Control (2026-06-03; reworked 2026-06-21 → interactive, resumes the current chat)
- **What it does (v0.5.1):** opens the **current chat** in an **interactive** Claude Code session with
  Remote Control enabled — `claude --resume <id> --remote-control` (a top-level flag) — in a standalone
  **PowerShell** console. So you continue *this* conversation from the Claude app / claude.ai/code.
  A fresh chat with no CLI session yet (`!HasCliSession`) starts a new remote session
  (`claude --remote-control`, no `--resume`).
- **QR vs tracking trade-off (user picked tracking):** the CLI renders its QR code only inside a
  Windows-Terminal session (cmd / standalone-PowerShell conhost show just the link — confirmed with the user).
  A `wt.exe` launch would show the QR, **but** wt hands off to the terminal process and exits immediately, so
  the session can't be tracked (no auto-reload on close, "End" can't reach the terminal). The user preferred
  the clean lifetime over the QR → we launch a standalone **PowerShell** console (trackable) and you get the
  **link** there, not a QR. Rendering the QR in the extension panel isn't possible either: it needs the
  connection URL, which isn't in any readable file (`session-env` empty, `remote-settings.json` minimal, not
  in the session `.jsonl`) and the interactive console isn't captured. `WebUI/qr.js` stays unused.
- **Why this shape (verified against CLI 2.1.178):** the headless `claude remote-control` *subcommand* has
  **no `--resume`** — it always pre-creates a brand-new session (that was the old behaviour: hidden server,
  takeover only on stop). The top-level `--remote-control` flag *does* combine with `--resume` (probed:
  `--remote-control --resume <id> --permission-mode …` co-parse). It needs a real TTY, so it can't run
  headless. The **VS integrated terminal** was the chosen target but isn't viable: its only API
  (`ITerminalService` in `Microsoft.VisualStudio.Terminal.dll`, a brokered service) drags the VS-18 / .NET-10
  ServiceHub + BCL set (`ServiceHub.Framework 4.10` → `System.Text.Json` / `Collections.Immutable` /
  `IO.Pipelines` 10, `StreamJsonRpc 2.25`, `System.Memory 4.0.5`, …) into this net472 / 17.14-SDK project →
  unresolvable `CS1705` cascade; even via reflection the brokered service may not be granted to a 3rd-party
  extension. Hence a standalone PowerShell console (zero VS-internal refs).
- **Contract:** web→host `remote.start {}` / `remote.stop {}`; host→web
  `remote.state { state: "starting"|"ready"|"stopped"|"error", inTerminal: true, message? }` (no url/QR — the
  terminal shows those). The WebUI shows `message` in the panel; the old QR canvas / link / Copy button were removed.
- **Host:** `Services/RemoteTerminalLauncher` runs `powershell.exe -NoExit -Command "& '<exe>' <args>"`
  (single-quoted path; `<args>` = `--resume <id> --remote-control` or just `--remote-control`) with
  `UseShellExecute=true` (its own console). Exposes `IsActive` / `StartedUtc` / `LastError` + an `Ended` event
  fired once (Interlocked) from `Process.Exited` or `EndAsync`. `EndAsync` = `taskkill /PID <id> /T /F` (the
  shell spawns the node CLI child). `WebViewBridge.HandleRemoteStart` picks the resume id
  (`_history.Current.Id` iff `HasCliSession`), pre-accepts workspace trust, launches, posts `ready` +
  `RemoteRunningMessage()`. `prompt.send` / attachment / selection adds are no-ops while `RemoteSessionActive`.
  Bridge releases the launcher in `Dispose()`.
- **Session reload on end (`OnRemoteTerminalEnded`, fired by `Ended`):** unchanged import path —
  `Core/CliSessionReader` maps cwd → CLI project folder (`~\.claude\projects\<munged>`, non-alphanumeric → `-`,
  case-insensitive drive letter), finds `*.jsonl` touched since `StartedUtc-10s`, imports into our message
  schema (assistant blocks of one message.id merged, tool_use cards with status from tool_result; meta/
  sidechain/command skipped; sessions without a user message skipped), `SessionHistoryStore.Import`, loads the
  latest (`remote.state stopped` first → UI unlocks before `session.init`/`transcript.load`). For a resumed
  session this is the same conversation, now advanced. **Pitfall (kept):** `GetSolutionDirectory()` is
  UI-thread-only — captured on the UI thread before the background discovery; `stopped` always sent via try/catch.
- **UI:** Broadcast button left of history; active = panel over the transcript (status line + "End remote
  session"). Locked then: composer (`canSend` + `input.disabled`), all composer controls (+, /, Model·Mode,
  attachment chips via `.remote-locked`) and Rename/History/New chat (`setRemoteLocked`); only the gear stays
  usable. Statusbar state `remote` (accent pulse). Host re-announces `remote.state ready` on WebView reload
  while active. (`WebUI/qr.js` is now unused by this flow but left in place.)
- **Limitation (deliberate):** no live mirror in the tool window — you work in the terminal; the chat reloads
  the advanced conversation when the session ends.
- **Workspace trust (v0.4.3, CLI 2.1.178):** Unlike headless `claude -p` (which bypasses the
  workspace-trust check entirely), `claude --remote-control` **refuses to start in an untrusted
  directory** (`"Error: Workspace not trusted. Please run `claude` … first"`). Opening a project
  for the first time and immediately starting a remote session therefore failed with that error.
  Fix: `Core/ClaudeWorkspaceTrust.EnsureTrusted(dir)` pre-sets `projects[dir].hasTrustDialogAccepted
  = true` in `~/.claude.json` (the same flag the interactive trust dialog writes) right before
  `HandleRemoteStart` launches the terminal — for the directory the user explicitly opened in
  VS. The project key is the working directory with **forward slashes** (e.g. `C:/Users/Jan/Repo`,
  trailing separators trimmed); an existing entry in any slash/case variant is reused (no duplicate
  key) and left byte-identical when already trusted. Read-modify-write is atomic (temp + replace,
  Newtonsoft `Formatting.Indented` = 2 spaces, matching the CLI). Best-effort: any I/O/parse failure
  is swallowed so the CLI still surfaces its own trust error. **Re-verify the `hasTrustDialogAccepted`
  key on CLI updates.**

## Slash commands (2026-06-03)
- **`slash.commands` (host → web)** `{ commands: ["clear", "compact", …] }` — the CLI's
  `system/init` event carries a `slash_commands` array with the commands actually available
  in headless mode (bare names, incl. skills). The parser
  (`SessionInitEvent.SlashCommands`) → the bridge caches the list and sends it on the
  init event as well as after `ready` (WebView reload). The UI replaces its static
  fallback list with it (in-place mutation, menu + autocomplete share the array);
  descriptions come from a UI map (`SLASH_DESCRIPTIONS`), unknown
  commands appear without subtext.
- **Popover scroll (v0.4.1, CLI 2.1.178):** the `slash_commands` list grew to **28+** entries
  (built-ins **+ skills**, skills now listed first → `deep-research` is index 0). The slash menu
  (`.popover`) and autocomplete (`.popover.autocomplete`) previously had **no height cap**, so the
  oversized list overflowed off-screen (`positionPopover` flipped it below the button → only the
  first item visible). Fix: `.popover` now has `max-height: min(70vh,460px)` + `overflow-y:auto`
  (one rule covers menu **and** autocomplete; short popovers never reach the cap). The
  `slash_commands` format itself is unchanged (still a JSON string array; parser ok).
  - **`grow` option (v0.5.2):** `openPopover(..., { grow: true })` lets a tall popover use the full
    space in its preferred direction (up to the window edge) instead of the static cap.
    `positionPopover(anchor, popEl, opts)` (signature changed to a single `opts` object — all callers
    updated) sets `maxHeight` to the available space above/below the anchor **before measuring**, then
    positions as usual (still flips when the preferred side is too small). The **model·mode popover**
    uses it (`side:"top"`) so it fills upward; other popovers keep the CSS cap.
- **`/help` host-side:** The headless CLI rejects `/help` ("isn't available in this
  environment") — the host instead answers with a synthetic help block
  (like `/login`), incl. a hint about the terminal + `claude --resume <id>` for
  interactive-only commands. `/help` is always appended to the dynamic menu list
  on the UI side.
- **Empirically clarified (CLI 2.1.161):** The persistent bidirectional mode too
  (`--input-format stream-json`) is headless — TUI commands (`/help`,
  `/remote-control`, `/config`) stay unavailable there. `claude remote-control`
  exists as a standalone server mode (own sessions, no live mirror into the
  tool window); integration deliberately deferred.

## Drag-and-drop of files (2026-06-05, rewritten 2026-06-24)
- **Goal:** Drag files/folders from Windows Explorer onto the chat panel → they get added as
  attachment chips (the CLI reads them via `@<path>`).
- **Why not the DOM:** HTML5 drag-drop delivers `File` objects **without** a filesystem path
  (Chromium sandbox), but the CLI contract needs the real path. So the path must be recovered host-side.
- **Approach = navigation interception (the one that actually works in this runtime):**
  `ClaudeChatWindowControl` sets **`_webView.AllowExternalDrop = true`** so Chromium **accepts** the
  drop, and the page has **no JS drop handler** → Chromium's default action for a dropped file is to
  **navigate to its `file://` URL**. We intercept that:
  - **`CoreWebView2.NavigationStarting`** → if `e.Uri` is `file:` → **`e.Cancel = true`** (never let the
    SPA navigate away) + `AttachDroppedFileUri` → `new Uri(uri).LocalPath` (decodes `%20` etc.) →
    `WebViewBridge.AddFileAttachments`.
  - **`CoreWebView2.NewWindowRequested`** (already used for `target=_blank` → system browser) also
    handles `file:` URLs (a multi-file drop can open extra files as new windows) → same attach path.
  - Both events fire on the **UI thread** (`ThrowIfNotOnUIThread`); folders work too (a dropped dir
    navigates to `file:///dir` → `AddFileAttachments` accepts dirs).
- **⚠️ Dead end — do NOT go back to it:** `AllowExternalDrop = false` + WPF routed drag events
  (`DragOver`/`Drop`, tunneling **or** bubbling). The "forward OS drops to WPF" behaviour is **not in
  the `AllowExternalDrop` spec** and **does not happen** in the current WebView2 runtime — `false` just
  makes Chromium **reject** the drop (the **no-drop cursor** everywhere). Two prior fixes betting on
  WPF routes (2026-06-16 bubbling-only; 2026-06-24 both routes) both failed for this reason.
- **`AddFileAttachments` (public):** filters to existing files/dirs, builds `attach.added`
  ({name,path}); shared with the `+` picker (`HandleAttachFiles`). With remote control active
  it is locked (composer/attachments are locked anyway then). No UI contract needed — the existing
  chip flow. (Drop anywhere on the panel; the appearing chips are the feedback.)

## Editor context menu → prompt (2026-06-16)
- **Two commands in the code-editor right-click menu** (`.vsct`: own group `EditorCtxGroup` in
  `IDM_VS_CTXT_CODEWIN`; buttons `cmdidAddFileToPrompt` 0x0101, `cmdidAddSelectionToPrompt` 0x0102 —
  both `DynamicVisibility`+`DefaultInvisible`):
  - **"Add file to Claude prompt"** → active file as an @-reference chip (`bridge.AddFileAttachments`).
    `BeforeQueryStatus` shows it when `ActiveDocument != null`.
  - **"Add selection to Claude prompt"** → the selected range as a code block (label `name:Lfrom-Lto`)
    into the composer (`bridge.AddSelectionToPrompt` → host→web **`composer.append {text}`** →
    `appendToComposer` appends, focuses, caret to the end). `BeforeQueryStatus` only on a non-empty
    selection (`TextSelection.IsEmpty`).
- **Handler** (`CodeAstrogatorPackage`): `OleMenuCommand` with `BeforeQueryStatus`; opens the
  tool window via `ShowAndGetBridgeAsync` and fetches the bridge via
  `ClaudeChatWindow.GetBridgeAsync()` → `ClaudeChatWindowControl.BridgeReady` (TCS, set as soon as the
  bridge exists). **Race protection:** If acted upon on the first open, before the WebUI reports `ready`,
  the bridge buffers the messages (`PostOrQueue`/`_queuedUiMessages`) and flushes them in
  `FlushQueuedUiMessages()` on the `ready` handshake (also applies to `attach.added`).
- With remote control active both are no-ops (composer/attachments locked).

## Clipboard / context menu (2026-06-03)
- **`clipboard.paste` (web → host)** `{}` — Ctrl+V in the composer: the `paste` handling
  in app.js only intercepts file/image content (the paste event has `kind === "file"` items
  resp. `files.length > 0`), `preventDefault` + `clipboard.paste`; plain text paste
  stays native. The host (`WebViewBridge.HandleClipboardPaste`) reads the Windows
  clipboard (WPF `System.Windows.Clipboard`): a file drop list → paths directly;
  otherwise an image → PNG under `%LocalAppData%\CodeAstrogator\pasted\` and answers with
  `attach.added` (the existing chip flow, the CLI reads the paths itself). The mock simulates
  `pasted-image.png`.
- **Copy context menu (UI-only, no contract):** Since `AreDefaultContextMenusEnabled=false`
  (ClaudeChatWindowControl), app.js shows on right-click on an existing
  text selection its own "Copy" popover at the mouse position (uses overlayLayer +
  closeAllOverlays/Esc; copy via `navigator.clipboard.writeText`, fallback execCommand).

## Theme application (Part B §8)
- `applyTheme` (app.js) now **replaces** the inline `vars` on `:root`, instead of only
  augmenting them: previously set keys are removed via `removeProperty` before the new ones
  take effect (`state.appliedThemeVars` remembers the keys). Bugfix: switching from "Auto"
  (the host sends VS-theme `vars` incl. `--bg`) to explicit Light/Dark (empty `vars`,
  palette via `data-theme`) otherwise left the old background vars standing — only the
  palette-only colors (`--comment`, `--accent` …) switched, the background did not.

## Permission bridge (Part A §A5)
- **Status: Phase 1 implemented** (real in-process MCP server `Core/McpPermissionBridge`,
  standard chat card allow/deny). `IPermissionBridge` is no longer just a stub. The extended
  editor inline diff mode (Phase 2/3) is still open. Details + spike findings + review fixes
  see section "Permission hook & inline diff review". Still to be verified in real VS.

## Auto-approve patterns (2026-06-05, v1.2.0)
- **Settings:** `AstrogatorOptions.AutoApprovePatterns` — **a `List<string>` since v1.3.0** (one
  glob pattern per entry, `*` = wildcard), persisted in the WritableSettingsStore as a **JSON array**
  (`JsonConvert.SerializeObject`). **Backward compatible:** `AstrogatorSettingsStore.ParsePatterns` reads
  at load both JSON (leading `[`) and the **old newline format** (split) — existing
  settings survive the upgrade; malformed JSON falls back to the legacy parse. `Normalize`
  (trim / drop blank lines / `Distinct` case-insensitive) runs on read **and** write.
  Store + `Copy` (deep list copy) extended. **UI (v1.2.5):** In the settings window as an editable
  **`DataGrid` list** (one column, theme-tinted via `VsBrushes` — Background/Foreground/Border/
  RowBackground as well as own `ColumnHeaderStyle`/`CellStyle` for Dark&Light) with **"Add"/"Remove"**
  buttons (section "Permissions"). `Load` splits the string at newlines into `PatternItem` rows,
  `ApplyAndPersist` commits the open edit (`CommitEdit`), trims, drops blank lines and dedupes
  (`Distinct`, case-insensitive) → again a `\n` string. "Remove" is disabled as long as the
  selection is empty. (Until v1.2.4 the field was a multiline TextBox.)
- **Match key / matching (reworked):** MCP tools (`mcp__…`) match on the **tool name**.
  Shell commands are split via **`ShellCommandSplitter.ExtractCommands`** into their **real sub-commands**
  (see below); the call is auto-approved only if **EVERY** sub-command is covered by a pattern.
  Since 2026-06-30 the matching is **one source of truth in Core**: `WebViewBridge.IsAutoApprovedByPattern`
  delegates the command case to **`ShellCommandSplitter.IsCommandCovered(cmd, patterns)`**
  (= `ExtractCommands` + `commands.All(sub => patterns.Any(p => MatchesGlob(sub,p)))`) and the MCP case to
  **`ShellCommandSplitter.MatchesGlob`** (the bridge's own private `MatchesGlob` was removed). Splitting an
  `&`-chain and requiring **all** parts prevents one part slipping the whole chain through. `MatchesGlob`
  = anchored, `*`→`.*`, case-insensitive, `Singleline` (so `*` spans newlines). Both helpers are now
  **unit-tested** (`ShellCommandSplitterTests`). Edit/Write → no pattern approval (diff card).
  `AutoApproveKey` remains only for the `canApproveAlways` check (≠ null).
- **`ShellCommandSplitter.ExtractCommands`** (quote- **and** here-string-aware `@'…'@`/`@"…"@`): splits
  on newlines, `;`, `&&`, `||`, whitespace `&` **and pipeline stages `|`**; **discards
  variable assignments** (`$x = …`, `VAR=…`, `AssignmentRx`) and bare variables (`$lorem`,
  `BareVariableRx`). So `$lorem = @'…'@ ; $lorem | Out-File -FilePath "x"` correctly yields
  `Out-File -FilePath "x"` instead of the assignment. **`Wildcardize`** replaces each quoted argument
  — **including the surrounding quotes** — with a bare `*` (`Out-File -FilePath "x"` → `Out-File -FilePath *`),
  collapsing runs `* *`→`*`. **Quote-agnostic (2026-06-30):** the old form kept the quotes (`"*"`),
  so a stored pattern only matched the *same* quote style — a command using single quotes / no quotes
  silently failed to match and the `*` "didn't work". Dropping the quotes makes the pattern match
  regardless of quoting. Safe because matching is per-split-sub-command, so a wildcard's `.*` can never
  span a real command separator. (The old `Split` stays for tests, but is no longer used in production.)
- **Splitter hardening (2026-06-30):** `SplitSegments` (the production splitter behind
  `ExtractCommands`) is now robust for both shells the extension drives. (1) **Escapes:** PowerShell
  backtick `` ` `` (unquoted + inside `"…"`) and bash backslash `\"` (inside `"…"`) escape the next
  char, so an escaped quote no longer flips the parser's quote state and an escaped separator no
  longer splits — e.g. `echo "a\" ; rm -rf /"` stays **one** command (previously the `\"` closed the
  string and ` ; rm -rf /` leaked out as a separate suggested command). A bare `\` stays literal so
  Windows paths (`WebUI\app.js`) are untouched — PowerShell has no `\` escape. (2) **Doubled quotes:**
  `""`/`''` are PowerShell's literal-quote escape and are kept inside the string. (3) **Nesting:** a
  `depth` counter for `(…)`/`$(…)`/`@(…)` and `{ … }` means separators inside a subexpression or
  script block don't split (`foreach ($i in $x) { a; b }` stays whole). Why it mattered: a buggy split
  pre-filled the "Always" popover with garbled/half-commands and could surface a dangerous fragment as
  its own suggestion. **Rejected alternative:** a real parser (`System.Management.Automation.Language.Parser`)
  parses only PowerShell, not the `Bash` tool's commands, and drags a version-fragile GAC assembly into
  the net472 VSIX; no maintained .NET bash parser exists — so the lightweight char scanner stays, hardened.
- **Hook:** `HandlePermissionRequestedAsync` checks `IsAutoApprovedByPattern` at the very top → on a hit
  **silently `allow`** (no card; null `updatedInput` → the MCP bridge echoes the input the CLI
  demands). Runs on the bridge background thread; the `GetOptions()` read is uncritical there.
- **"Always" button + popover (v1.2.2, UI reworked):** The card carries `canApproveAlways` +
  `approveAlwaysSuggestions` (`AutoApproveSuggestions` = `ExtractCommands` + `Wildcardize`, dedupe).
  A click on `.btn-always` opens a **popover** (`openApprovePopover`), which is now **as wide as the
  card** (`openPopover` `matchWidthEl`/`hAnchorEl` = `.perm-card`) and shows the patterns as an **editable
  list** (one row = one input + ✕, "+ Add pattern" — analogous to the settings window) instead of a
  textarea. "Cancel" (the card stays open) / "Add & approve". Save → web→host
  **`permission.approveAlways {requestId, patterns:[…]}`** → `HandleApproveAlways`: each pattern via
  `AddAutoApprovePattern` (dedupe, `SaveOptions`) + `ResolvePending(allow)` + card "Approved" +
  system note.
- **Boundary:** Read-only commands don't prompt anyway (CLI classifier) — patterns only apply
  to calls that would otherwise trigger the card.

## "Auto-accept commands" toggle (2026-06-10)
- **What:** A toggle in the Model·Mode popover **below** the permission selection. If it is on, in
  the **`acceptEdits`** mode additionally **all** non-question tools prompted by the hook (Bash/PowerShell/
  MCP/…) are **silently** auto-approved — edits the CLI accepts itself in `acceptEdits` anyway, the toggle
  extends the same trust to commands. **AskUserQuestion stays interactive.** Effectively "bypass
  without the question auto-decline trap" (true `bypass` would disable the hook → questions auto-declined).
- **Gate:** `HandlePermissionRequestedAsync` (directly after the pattern check): `!isQuestion &&
  GetOptions().AutoAcceptCommands && _session.LaunchedPermissionMode == "acceptEdits"` → silently `allow`.
  Deliberately **`LaunchedPermissionMode`** (fixed at turn start), not the live setting — only `acceptEdits`
  routes *commands* (not edits) through the hook, so a mid-turn switch can never accidentally silently approve an
  edit prompt. No card (like pattern approve); the normal tool card of the
  `ToolUseEvent` stays visible.
- **Persistence/sync:** `AstrogatorOptions.AutoAcceptCommands` (default **false**), in the store + `Copy` +
  settings-window snapshot (popover-managed, "carry over untouched") extended. web→host
  **`autoAcceptCommands.set {enabled}`**, host→web in `session.init` (`autoAcceptCommands`). UI:
  `state.autoAcceptCommands`, toggle row + hint; **dimmed** (`.toggle-row.disabled`) when the
  chosen mode ≠ `acceptEdits` (it stays clickable — it is a sticky preference).

## Permission hook & inline diff review (Roadmap #1+#3)
> **Phase 0-A + Phase 1 IMPLEMENTED** (2026-06-04), verified in real VS.
> **Phase 0-B + 2 + 3 (inline edit review in the editor) IMPLEMENTED** (2026-06-19) — code complete,
> builds, the pure diff/reconstruction core is unit-tested; the **WPF adornment rendering still needs
> manual VS verification** (see the "Inline edit review in the editor" subsection below). Plan file:
> `docs/permission-hook-inline-diff-review-plan.md`.

**Spike 0-A — empirically confirmed against CLI 2.1.162 (important, some research assumptions were wrong):**
- HTTP transport works; `GET /mcp` (SSE attempt) may be **405** (the CLI continues POST-only).
- `initialize.protocolVersion = "2025-11-25"` (not 2024-11-05) → reflect back; set
  `Mcp-Session-Id`; the `X-Auth` header comes with every request.
- `tools/call.arguments = { tool_name, input, tool_use_id, _meta }` — the field is called **`input`**
  (the research assumption "tool_input" was **wrong**).
- **Show-stopper:** allow MUST return `{"behavior":"allow","updatedInput":<input>}` — a
  **`null` updatedInput is treated as deny by the CLI** (all tool calls fail). Echo of the
  original input (or an edited variant). deny = `{"behavior":"deny","message":…}`; envelope
  `{content:[{type:"text",text:<json>}],isError:false}`.
- Slow decision (10 s) tolerated; `timeout:600000` in the mcp config.
- `TcpListener` chosen (no URL ACL needed); `HttpListener` avoided.

**Two UX levels:**
1. **Standard (like Claude in the chat):** interactive permission card in the chat (the card exists, §5.2),
   whole-call allow/deny. = "off" state of the add-on.
2. **Extended (toggleable):** only for file edits — **file list in the chat** (status
   open/accepted/rejected), a click opens the file in the editor, there an **inline diff** (red/green
   like VS Copilot) with **Accept/Reject per hunk**; partial acceptance via `updatedInput`. Toggle in the
   **Appearance popover** (gear, with Dark/Light/Auto).

**Mechanic reality (drives the design):** The MCP hook fires **per tool call and blocks** —
edits come one after another, not as a batch; at any moment exactly **one** edit is "open". The diff
is a **preview before the allow** — the CLI writes only on "Accept" (possibly with `updatedInput`).

**Phases:**
- **0-A — MCP protocol spike: ✅ done** (see spike block above).
- **1 — MCP bridge + standard chat card: ✅ implemented** (2026-06-04):
  - `Core/McpPermissionBridge : IPermissionBridge, IDisposable` — minimal HTTP/1.1 over
    **`TcpListener`** on `127.0.0.1:0`, `X-Auth`, dispatch (initialize/notifications/tools-list/
    tools-call) via purely testable builders (`BuildInitializeResult`/`BuildToolsListResult`/
    `BuildToolResult`/`BuildMcpConfig`/`IsAuthorized`); mcp config in
    `%LocalAppData%\CodeAstrogator\mcp-permission-<port>.json`; `Start()`/`Dispose()`.
  - `ClaudeSessionService.PermissionBridge` → in `RunTurnAsync` when `IsAvailable` **and the mode ≠
    bypass** `--mcp-config` + `--permission-prompt-tool mcp__vsbridge__permission_prompt` in
    `ExtraArgs` (works in both hosts; in the persistent host part of the `FlagSig` → stable).
    `--permission-mode` controls the firing (Ask=default → Edits/Bash prompt; acceptEdits → only
    Bash/others; plan → idle; bypass → no flags). Makes "Ask" usable in headless `-p` for the first time.
  - `WebViewBridge`: starts/disposes the bridge; `OnPermissionRequested` → requestId, build diff
    (Edit: old/new_string; Write: file→content), post `permission.request`, status
    `waiting-permission`, `ConcurrentDictionary<requestId, PendingPermission>`, await;
    `permission.decision` handler (replaces the stub). UI (`app.js` card §5.2) **unchanged**.
  - Tests: `PermissionBridgeTests` (RPC shaping, mcp config, auth, allow-echo, deny, dispatch).
  - **Review fixes (adversarial workflow review, 3 confirmed findings fixed):**
    (1) **HIGH** open permission on abnormal turn end → `OnTurnCompleted` now calls
    `DenyAllPendingPermissions` (no orphaned card/TCS leak). (2) **MED** `ct.Register`
    registrations → are disposed in `ResolvePending` (no accumulation on the long-lived `_cts`).
    (3) **MED** net472: `NetworkStream.ReadAsync` does **not** honor the CancellationToken mid-read
    → accepted `TcpClient`s are tracked and closed via `Close()` in `Dispose()`, so that
    parked keep-alive reads/sockets don't leak.
- **Card UX + read-only (2026-06-04, after VS verification):** After Approve/Reject the
  **card collapses** (diff + buttons gone) and shows in the header (chevron **at the far left**) a status badge
  + border/header tint; clicking the header re-expands the (read-only) diff. Status:
  **Approved/Rejected** (decision) → after execution **Applied (green) / Failed (red)**, as soon as
  the edit's `tool.result` arrives (`UpgradePermissionResult` correlates via the
  `tool_use_id` = `requestId`; new host→web message **`permission.result {requestId, status}`**).
  - **No double card (the permission card REPLACES the tool card):** The CLI sends the `tool_use`
    **before** the permission prompt, so the tool card is shown first. On `permission.request`
    the UI removes the tool card of the same `tool_use_id` (`querySelector('.tool-card[data-tool-id]')`
    .remove()); host-side `RecordPermissionMessage` deletes the previously recorded `role:"tool"`
    message of the same ID and creates the `permission` message. (Pre-suppression failed on the
    event order and left the "running…" card hanging — hence this replacement approach.)
    Auto-allowed read-only tools (without a permission card) keep their tool card. Applies live **and**
    persisted (only the permission card stays in the history).
  - **Persistence (fix):** The decided card is now written into the history as a `role:"permission"`
    message (id=requestId, toolName, input, diff, status, ts) via `RecordPermissionMessage`, and on
    reload rendered by the historic renderer (`renderHistoricMessage`) in the same
    collapsed style with a status badge. Auto-denies (turn end/Stop/Cancel) are
    **not** persisted (only explicit user decisions).
  - The diff shows **old + new line numbers** (gutter, width adjusted to the largest number) —
    **file-relative**: the host determines the real start line of `old_string` via `FileStartLine`
    (Write = 1) → `diff.startLine`; `buildDiff` numbers from there.
  - `app.js`/`app.css` (`permLabel`/`permStatusClass`/`setPermCardState`/`applyPermissionResult`/
    `buildDiff`), `WebViewBridge` (`RecordPermissionMessage`/`UpgradePermissionResult`).
  - **Read-only commands don't
  prompt:** the CLI has a built-in classifier; only **mutating** Bash/PowerShell calls
  (write/delete/install) go through the hook, reading ones (directory listing, `cat`, `git
  status` …) are auto-allowed — matches Claude Code standard behavior, not a bug.
- **Auto-approved edits = pre-decided card (2026-06-05):** In `acceptEdits`/`bypass` the
  MCP hook does **not** fire for edits (the CLI accepts itself), so no `permission.request` arrives.
  Previously: a normal tool card + system note "Auto-approved: …". Now the bridge itself sends, in the
  `ToolUseEvent` auto-approve branch, a `permission.request` with **`autoApproved:true`**
  (requestId = tool_use_id, diff via `BuildPermissionDiff`) and calls `RecordPermissionMessage(…,
  "approved")`. The UI (`permissionRequest`) renders the card on `autoApproved` directly as
  **decided/approved** (green, collapsed, **without** Approve/Reject buttons, no
  `waiting-permission`); the `tool.result` upgrades via the existing `UpgradePermissionResult`
  → `permission.result` to **Applied/Failed**. Applies live + persisted (role `permission`).
  Only `IsEditTool` (Edit/Write/MultiEdit/NotebookEdit); read-only keeps the normal tool card.
  The mock shows an auto-approved `Write` card in the demo turn.
- **Parallel prompts (fix 2026-06-10):** Claude can call several tools at the same time
  (parallel tool use) → several open cards (Permission + Permission, or Permission +
  AskUserQuestion) at the same time. Bug: When **one** was answered, the other jumped to
  "expired". Cause: (1) host-side, every decision/answer handler posted `PostStatus("working")`
  as soon as the session was busy — even when other prompts were still open; (2) the UI tracked only
  **one** open card (`state.pendingPermission`) and let exactly that one "expire" on the status change away from
  `waiting-permission`. Fix: (1) **`PostStatusAfterDecision()`** stays
  `waiting-permission` as long as `_pendingPermissions` is **not empty**, and only goes to `working` on the last
  prompt; (2) the UI now tracks a **`state.pendingPermissions` (set)**; the
  status-change expiry expires **all still-open** cards (true abandonment, e.g. turn end),
  answered ones are long gone from the set by then. The set is cleared on `session.init`/`transcript.load`.
  Affects `WebViewBridge.HandlePermissionDecision`/`HandleQuestionAnswer` + `app.js`.

### Inline edit review in the editor (Phase 0-B + 2 + 3, 2026-06-19)
Opt-in "Review edits in the editor" (toggle in the **Permission section of the model/mode popover** →
`AstrogatorOptions.ReviewEditsInEditor`, default off). Each **mode radio** carries a one-line description
(`.radio-text` column = label + `.mm-hint`, dot top-aligned via `align-items:flex-start`). Each mode's sub-toggle
is **nested INSIDE that mode's text column** (`text.appendChild(subToggles[mode])`, not a sibling row) and shown
only while that mode is selected (`mm-subtoggle` wrappers, `syncSubToggles` show/hides by `display`): "Review edits
in the editor" under **Ask**, "Auto-accept commands" under **Auto-accept edits**. The `mm-subtoggle` has a full
`--accent` rail down its left and indents the toggle under it, so it reads as a child of the mode (the toggle's
own click `stopPropagation`s so it doesn't re-select the mode).
When **on** and a file-edit prompt (Edit/Write/MultiEdit) fires **in a mode that actually prompts** (Ask/Plan — not
acceptEdits/bypass, where the CLI auto-applies edits), the chat shows a **file
card** ("Accept all" / "Open in editor" / "Reject all") instead of the inline diff card; the diff is reviewed
**in the code editor** with per-hunk Accept/Reject, and partial acceptance is returned via `updatedInput`.
**"Accept all"** applies the full edit straight from the chat card without opening the editor — it is a plain
`permission.decision allow` (no `updatedInput`), which the MCP bridge echoes back as the original input
(`decision.UpdatedInput ?? originalInput`), i.e. every hunk accepted.

- **Pure core (UI-free, unit-tested) — `Core/EditReview/`:**
  - `LineDiff.Compute(old,new)` → ordered `LineSegment`s (Unchanged/Changed). Trims common prefix/suffix,
    LCS-diffs the middle → **multiple hunks** (unlike the single-block WebUI `buildDiff`). Equality ignores a
    trailing `\r` (CRLF==LF) but segments emit verbatim lines so a **full accept reproduces `new_string`
    exactly**. LCS capped at 4000 lines/side (falls back to one big hunk).
  - `EditReviewSession.Build(tool, input, readFile)` → per-edit units (Edit/Write = 1; MultiEdit = one per
    `edits[]`), each diffed; `ReviewHunk { Index, UnitIndex, AnchorLine (1-based, anchored via the same
    CRLF-normalised `FindStartLine`), DeletedLines, AddedLines, mutable State }`. `BuildUpdatedInput()`
    reconstructs from **accepted hunks only**: Edit→`new_string`, Write→`content`, MultiEdit→surviving
    `edits[]` (fully-rejected edits dropped, `replace_all`/order preserved). **Nothing accepted → returns
    null → caller denies** (a null `updatedInput` would re-apply the full original edit). Tests: `EditReviewTests`.
- **Editor adornments (MEF) — `Editor/`:** `EditReviewMef.cs` exports two `AdornmentLayerDefinition`s
  (deletions below text, additions+buttons above) + an `ILineTransformSourceProvider`/`...Source` that
  **reserves bottom space** for phantom added lines by **adding to `line.DefaultLineTransform`** (composition,
  not absolute). `EditReviewViewAdorner.cs` (one per `IWpfTextView`, static `ConditionalWeakTable` registry)
  draws on `LayoutChanged`: red fill on to-be-deleted buffer lines, **green phantom added lines in the
  reserved gap** (buffer NEVER modified, positioned via `ITextViewLine.TextBottom`, **no `ViewportTop`
  subtraction** under `TextRelative`), and per-hunk Accept/Reject WPF buttons. Reserved space is a function of
  the **hunk set only** (not per-hunk decisions) so transforms compute once; `DisplayTextLineContainingBufferPosition`
  forces the relayout. Cancels the review if the buffer changes mid-review. The adorner also draws a **fixed
  Prev/Next nav toolbar** (▲/▼ + "N to review" count, `OwnerControlled` so it stays pinned top-right while
  scrolling) — `Navigate(±1)` jumps to the prev/next hunk anchor relative to the first visible line (wraps at
  the ends) via `ViewScroller.EnsureSpanVisible(AlwaysCenter)`. It raises a **`ReviewChanged`** event on
  attach/clear/decide and exposes **`ReviewHunks`** so the scrollbar margin can repaint.
- **Scrollbar marks (MEF) — `Editor/EditReviewScrollbarMargin.cs`:** an `IWpfTextViewMarginProvider`
  (`[MarginContainer(PredefinedMarginNames.VerticalScrollBarContainer)]`, `[Order(After=…OverviewChangeTracking)]`)
  adds a thin strip next to the vertical scrollbar (like git change / error marks). Gets the
  `IVerticalScrollBar` via `marginContainer.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar)`, draws
  one coloured tick per hunk at `GetYCoordinateOfBufferPosition(anchorLine.Start)` (green=add, red=delete,
  purple=changed, dim grey=decided). Repaints on the adorner's `ReviewChanged` and the scrollbar's
  `TrackSpanChanged`. **Note `PredefinedMarginNames`, not `PredefinedOverviewMarginNames`** (the latter doesn't
  exist in the 17.14 SDK).
- **Host wiring — `Services/EditReviewController.cs` + `Bridge/WebViewBridge.cs`:** the controller opens the
  doc (`VsShellUtilities.OpenDocument` + `IVsEditorAdaptersFactoryService.GetWpfTextView` via the package's
  new `GetComponentModel()`), hands the shared `EditReviewSession` to the view's adorner, and on **all hunks
  decided** invokes `FinalizeEditReview(requestId)` — the bridge then calls `BuildUpdatedInput()` itself and
  `ResolvePending(allow+updatedInput / deny)`. The edit-review request lives in `_pendingPermissions` like any
  prompt (status stays `waiting-permission`; teardown via `DenyAllPendingPermissions`/ToolResult-while-open/
  Dispose all `Close()` the adorner). **Manifest** now ships a `MefComponent` asset; csproj references
  `System.ComponentModel.Composition`.
- **Contract (web↔host):** host→web `permission.request` gains `editInEditor:true` + `hunkCount` (renders the
  file card); host→web **`permission.finalize {requestId,status}`** stamps the card decided + frees the
  composer (then the usual `permission.result` upgrades approved→applied/failed). web→host
  **`editReview.open {requestId}`** and **`reviewEditsInEditor.set {enabled}`**. "Accept all"/"Reject all"
  reuse the existing `permission.decision` path (allow/deny respectively; allow also `Close()`s any open
  editor review and echoes the original input = full accept). Persistence is unchanged
  (decided cards persist as role `permission` with the diff → historic render shows the read-only diff; the
  `editInEditor` flag is **not** persisted).
- **⚠ Needs manual VS verification (cannot be unit-tested):** that `ILineTransformSource.GetLineTransform`
  re-fires when a review attaches / after a hunk decision (the green text must land in a real reserved gap,
  not over code); button clickability vs. layer order; geometry/positioning. **Also unverifiable offline (added
  2026-06-19):** that the scrollbar-mark margin actually lands in the `VerticalScrollBarContainer` and its
  ticks line up with the scrollbar in both bar- and map-mode (the 6px strip may shift the scrollbar slightly);
  that the Prev/Next nav toolbar stays pinned and `EnsureSpanVisible` centers the target hunk. Fallbacks if the pure-phantom
  path misbehaves (same `EditReviewController` seam): (A) temporary buffer-insert + guaranteed revert,
  (B) `InterLineAdornmentTag` (settable Height), (C) VS diff viewer + per-file Accept/Reject in the chat card.
  An independent API-reflection panel (against the shipped 17.14.249 ref DLLs) confirmed every editor API used
  here exists with the signatures used; this re-fire side-effect is the one thing it could not validate offline.

## AskUserQuestion (interactive follow-up questions) — solved 2026-06-05 (real in-turn card via the permission hook)
- **Key finding (CLI 2.1.165, throwaway probes):** `AskUserQuestion` **routes through the
  `--permission-prompt-tool` hook** and **blocks** there like Edit/Write — `tools/call.arguments`
  carries `tool_name="AskUserQuestion"` + the full `input` (`questions`/`options`). So there **is**
  after all a mid-turn back channel (the earlier assumption "no channel" was wrong; it applied only **without**
  a configured hook — then the CLI auto-declines immediately: `-p` "The user did not answer the
  questions", bidirectional `is_error:true` "Answer questions?"). Verified: `behavior:"deny"` +
  `message:"<answer>"` → the CLI passes the `message` to the model as a **tool result**
  (`is_error:true`, but Claude correctly continues, e.g. "You chose **Apple**"). `allow` on the other hand
  lets the tool run → auto-decline; **deny+message is the way**.
- **Solution (implemented):** Reuse of the **`McpPermissionBridge`**. `WebViewBridge.HandlePermissionRequestedAsync`
  recognizes `ToolName=="AskUserQuestion"` and posts, instead of the diff card, a
  **`question.request {requestId, questions}`** (PendingPermission tracking identical). The UI
  (`buildQuestionCard`/`questionRequest`) replaces the previously shown tool card of the same id and
  renders per question `header`+`question`+clickable **`.q-option`** buttons (label+description) **plus
  a free-text field "Other"**. Single-select = radio, multiSelect = multiple + "Submit". A
  single single-select question **without** typed free text → 1 click submits immediately. Selection →
  **`question.answer {requestId, answers:[{header,question,selected[],custom}]}`** → host
  `HandleQuestionAnswer` → `FormatQuestionAnswers` (text "The user answered:\n- <header>: …") →
  `ResolvePending(deny, message)`. Persisted as role **`question`** (`questions`+`answers`),
  `RecordQuestionMessage` replaces the redundant `tool` message; `renderHistoricMessage` renders the
  card read-only with the marked answers.
- **Tool-card replacement:** The CLI sends the `tool_use` **before** the hook → briefly a generic
  tool card, which the `question.request` (as in the permission flow) removes via `data-tool-id`.
  - **Race fix (2026-06-24):** "tool_use before the hook" is NOT guaranteed at the UI — the hook
    arrives over the **HTTP MCP server** while `tool_use` arrives over **ndjson stdout**; the two are
    independent and `question.request` can reach the WebView **first**. Then the late `tool.use` built
    a **duplicate** tool card that `questionRequest` could no longer remove — and because the answer is
    returned as an MCP **deny** (answer as the message), the CLI's `tool_result` is **`is_error:true`**,
    so that orphaned card rendered as **"failed"** even though the answer was accepted (exact symptom the
    user hit). Fix: a **race guard** at the top of `toolUse` — if a `.q-card`/`.perm-card` with the same
    `data-request-id` already exists, skip building the tool card (the interactive card already
    represents the call). Covers both orderings and hardens the permission race too. History was already
    correct (`RecordQuestionMessage` replaces the `tool` record by id). Mock now fires `question.request`
    **before** the racing `tool.use` and emits the errored `tool.result` to reproduce/verify.
- **CSS:** `.q-card`/`.q-option`/`.q-other-input`/`.q-actions` (+ `.answered` read-only style) in
  `app.css` (replaces the old `.ask-*`). The mock posts `tool.use`+`question.request` in the demo turn.
- **Modes:** works in **"Ask", "Auto-accept" AND "Plan"** — the hook is wired for all modes except
  `bypass` (`ClaudeSessionService`, `PermissionMode != "bypass"`); AskUserQuestion is
  not an edit, so it prompts even with acceptEdits, and in Plan empirically confirmed (CLI 2.1.165: the hook
  fires, deny+message comes back). Only in "Bypass" there is no hook → auto-decline (accepted).
- **Removed old:** the former `askCard`/`sendText` "new message" fallback incl. `.ask-*` CSS.
- **Prompt timeout too short (fix 2026-06-16; precedence corrected 2026-06-17):** Questions/permission
  prompts timed out **well before** the assumed 10 min. **Cause:** the CLI's **default MCP tool-call
  timeout** is short (≈ 1 min). **The deliverer changed between CLI versions — verified empirically:**
  - **2.1.16x:** the config `timeout` field was **not** applied to tool calls; the **`MCP_TOOL_TIMEOUT`
    env var** was the only lever.
  - **2.1.178 (re-tested):** the config **`timeout` field IS applied to tool calls and TAKES PRECEDENCE
    over the env var** (probe: env=6 s + config=20 s → the CLI gives up after **20 s**; env=6 s + no config
    → 6 s; both in **ms**). So the env var alone no longer honoured the user's setting when a (fixed 1 h)
    config timeout was also present → the "Prompt timeout" setting was effectively pinned to 1 h.
  - **Fix:** the user's timeout now lives in the **config `timeout` field** (`McpPermissionBridge.ToolTimeoutMs`
    instance prop, seeded in the `WebViewBridge` ctor from `PromptTimeoutMs(opt)`, rewritten on
    `OnOptionsChanged` via `UpdateToolTimeout` → the `--mcp-config` file is re-written). `ClaudeSessionService`
    still sets `MCP_TOOL_TIMEOUT`/`MCP_TIMEOUT` as a **fallback** (in case a future CLI flips precedence back).
    `BuildMcpConfig(port, auth, timeoutMs)` now takes the value explicitly; the const is
    `McpPermissionBridge.DefaultToolTimeoutMs` (1 h).
  - **Configurable in the settings window** ("Prompt timeout", minutes): `AstrogatorOptions.PromptTimeoutMinutes`
    (default 60, clamped `Min/MaxPromptTimeoutMinutes` = 1–240). Flow: SettingsWindow → `AstrogatorOptions`
    (persisted via `SetInt32`) → `WebViewBridge` (ctor + `OnOptionsChanged`) → config file **and**
    `SessionSettings.McpToolTimeoutMs` (env fallback). **Per-turn host:** takes effect next turn (the file is
    re-read each turn). **Persistent host:** only after a process restart (config + env are fixed at process
    start). **Re-test on CLI update** (the deliverer + env var names are version-dependent — see above).
  - **Transport-level timeout (fix 2026-06-30 — the *real* "expires after ~5 min" cause):** the MCP
    `timeout` field is an **application-layer** limit; the CLI's HTTP client (Node/undici) ALSO enforces a
    **transport timeout** (~5 min of header/body inactivity — undici `headersTimeout`/`bodyTimeout` ≈ 300 s)
    that the `timeout` field does **not** lift. Our old server held the `tools/call` HTTP response open with
    **zero bytes** until the user decided → at ~5 min the client aborted **and retried** the call (a fresh
    permission/AskUserQuestion prompt), regardless of a 60 min `timeout`. **Both** permission prompts and
    AskUserQuestion ride the same `tools/call` path, so both were affected. **Fix:** `tools/call` is now
    streamed as **SSE** — `WriteToolsCallSseAsync` writes the `text/event-stream` headers immediately, emits a
    `: keep-alive` comment every `KeepAliveIntervalMs` (25 s, « 5 min) while the user decides, then the
    JSON-RPC result as one `data:` event and closes the connection (`Connection: close`). The periodic bytes
    reset the client's inactivity timer, so only the MCP `timeout` field bounds the wait. The client advertises
    `text/event-stream` (it opens a GET SSE stream — we still 405 that), so an SSE POST response is legitimate.
    Other methods (initialize/tools/list/notifications) stay plain `application/json`. **Re-verify on CLI
    update** that the CLI accepts the SSE-delivered tool result.
- **Timeout/abort cleanup (fix 2026-06-16):** If the CLI ends an open prompt itself (MCP tool
  timeout) **or** the turn ends while a card is still
  open, the card was previously still clickable **and** the "Working" indicator (rocket) did not come back
  (status hung on `waiting-permission`). Now: (1) `OnSessionEvent` `ToolResultEvent` checks whether the
  `tool_use_id` is still in `_pendingPermissions` (= the CLI resolved the prompt itself) →
  `ResolvePending(deny)` + host→web **`permission.expire {requestId}`** + status restored inline
  (`working`, if no further prompts are open). (2) `DenyAllPendingPermissions` (turn end/Stop/Dispose)
  also posts `permission.expire` per card. UI: new dispatcher case `permission.expire` →
  `expirePermissionCard`, which now **also handles `.q-card`** (`expireQuestionCard`: marks
  `answered expired`, collapses + read-only, "Expired — no answer"). `.q-card.expired` = warn-tinted.
- **0-B — editor adornment feasibility: PLANNED** (smallest MEF prototype: additions as
  "phantom" lines + deletions red + WPF buttons per hunk; `IWpfTextViewCreationListener` +
  AdornmentLayer + possibly `ILineTransformSource`, alternatively "temporarily in buffer +
  decorate + revert"). **The riskiest part**; it decides inline vs. fallback (VS diff viewer).
- **2 — Extended: file list + open + inline display (PLANNED):** Setting `AstrogatorOptions.ReviewEditsInEditor`
  (default off) + toggle in the Appearance popover; contract: web→host `editReview.setEnabled`,
  host→web `editReview.state`; edit-review card host→web `permission.fileEdit {requestId,path,
  status}` + `permission.fileEdit.update`; web→host `editReview.open {requestId}`. NEW
  `Services/EditReviewController` opens the doc, computes hunks (line-based, like `WebUI buildDiff`
  host-ported), shows inline adornments; initially Accept/Reject **per file**.
- **3 — Per-hunk + updatedInput (PLANNED):** Per-hunk buttons in the editor; on completion
  reconstruct `new_string`/`content`/MultiEdit from **only the accepted hunks** →
  `allow + UpdatedInput` (all rejected → deny). Pure merge logic → unit-testable (redeems #3).

**Out of scope (v1):** Diff for non-edit tools (JSON in the standard card), batch review across several
blocking calls, additional side-by-side.

**Contract additions:** **Phase 1 (implemented):** `permission.request`/`permission.decision`
(§3, existing) + new host→web **`permission.result {requestId, status:"applied"|"failed"}`**
(the edit execution result on the card); the persisted `role:"permission"` message carries
`status` (approved/rejected/applied/failed) + `diff.startLine`. **Phase 2/3 (planned):**
`editReview.setEnabled {enabled}` (web→host), `editReview.state {enabled}` (host→web),
`editReview.open {requestId}` (web→host), `permission.fileEdit {requestId,path,status}` +
`permission.fileEdit.update {requestId,status}` (host→web).

**Critical Files:** NEW `Core/McpPermissionBridge.cs`, NEW `Services/EditReviewController.cs`
(+ MEF adornments, possibly `Editor/`); `Core/ClaudeSessionService.cs`, `Bridge/WebViewBridge.cs`,
`Options/AstrogatorOptions.cs`, `Services/AstrogatorSettingsStore.cs`, `CodeAstrogatorPackage.cs`,
`WebUI/app.js`+`app.css`; NEW tests `PermissionBridgeTests.cs` + `EditReviewTests.cs`.

**Risks:** Editor adornments (phantom lines + in-editor buttons) = the most involved/riskiest
part, verifiable only via build + manual VS test (several iterations expected); MCP timeout
on a long review; correct updatedInput merge semantics vs. CLI write.

**Recommended order:** Phase 1 fully finished first (independently valuable, closes the #1 base),
then spike 0-B, then phases 2/3 with intermediate verification.

## Logo / icons (2026-06-04, logo+accent new 2026-06-05)
- **2026-06-05:** New logo (purple **astronaut robot**) brought in + accent color switched from
  orange (Claude clay) to **dark purple**. `Resources\codeastrogator.png` +
  `WebUI\logo.png` replaced by the new 256-px master; all 6 sizes (16/20/24/32/90/200)
  re-rendered via System.Drawing. CSS tokens `--accent`/`--accent-hover`/`--accent-faint`
  in `WebUI\app.css` changed: **dark** `#8d5fc7`/`#a079d4`/`rgba(141,95,199,0.16)`, **light**
  `#6a3fa0`/`#583488`/`rgba(106,63,160,0.12)`. The entire UI uses `var(--accent)` → one place
  per theme is enough. `ThemeService` does not overwrite `--accent` (the brand color stays fixed, the comment
  "clay" → "purple brand" updated).
- **Master:** `Resources\codeastrogator.png` (astronaut robot, 256×256, transparent). The
  Marketplace sizes (`icon-90`, `preview-200`) and the WebUI empty-state logo are generated
  from it via System.Drawing (HighQualityBicubic) — for a new logo just replace the master
  and re-render.
- **Head-only master (2026-06-18):** `Resources\codeastrogator-head.png` (256×256, transparent) —
  the helmet/face only, **no shoulders, no waving hand**, centered. The **small** sizes
  `icon-16/20/24/32` are generated from it (the full body is an unreadable blob at 16–32 px —
  menu entry + tool-window tab via the ImageMoniker).
  Re-render recipe (System.Drawing, HighQualityBicubic, all on a 32bpp ARGB copy):
  1. **Erase the raised hand** from a copy of the full master — it overlaps the helmet's lower-left,
     so a plain rectangular crop can't avoid it. Clear to transparent: `x≤92 & y≥84` (glove/forearm)
     **plus** `x≤72 & y≥55` (the fingertips poking up left of the left ear knob). Leaves the helmet
     outline (x>92 there) and the left ear knob (x 78–95, y 55–88) intact.
  2. **Crop the head box** `x76,y14,w117,h105` (left ear knob → antenna ball; dome top → collar).
  3. **Center aspect-correct** on a 256 canvas (margin 10 → drawn 236×212 at 10,22).
  4. **Downscale** to 16/20/24/32.
  Both masters are source-only (`<None Remove="Resources\**">` in csproj), not shipped in the VSIX.
- **VSIX manifest** (`source.extension.vsixmanifest`): `<Icon>Resources\icon-90.png` +
  `<PreviewImage>Resources\preview-200.png` (Extensions Manager / Marketplace) and
  `<Asset Type="Microsoft.VisualStudio.ImageManifest" Path="Resources\CodeAstrogator.imagemanifest"/>`.
- **Menu command + tool-window tab:** a shared **ImageMoniker** instead of two mechanisms.
  `Resources\CodeAstrogator.imagemanifest` defines Guid `854bf90d-…07c7` / ID `1` (`AstrogatorLogo`)
  with 16/20/24/32 sources via **pack URI** (`/CodeAstrogator;component/Resources/icon-NN.png`).
  - `.vsct`: `guidCodeAstrogatorIcons` symbol + `<Icon guid="guidCodeAstrogatorIcons" id="AstrogatorLogo"/>` +
    `<CommandFlag>IconIsMoniker</CommandFlag>` on the show-window button.
  - `ClaudeChatWindow`: `BitmapImageMoniker = new ImageMoniker { Guid = …07c7, Id = 1 }`.
- **csproj:** `icon-16/20/24/32.png` as **`Resource`** (embedded into the DLL → pack URI;
  verified: they live as `resources/icon-NN.png` in `*.g.resources`). `icon-90`, `preview-200`
  and the `.imagemanifest` as `Content` + `IncludeInVSIX`. `<None Remove="Resources\**">` before it,
  otherwise a duplicate item (the SDK default-None glob). Changing the GUID ⇒ keep it in sync at three places:
  imagemanifest, .vsct symbol, `ClaudeChatWindow.AstrogatorIconsGuid`.
- **⏳ Still to be checked visually in VS 2026:** the icon in the Extensions Manager, on the menu entry
  "View → Other Windows → Code Astrogator" and on the tool-window tab (16 px is small —
  possibly draw a higher-contrast mini variant).
- **Empty state (deviation from plan §5.2):** The former inline-SVG pixel robot
  (`buildRobot`) was replaced by the logo (`buildLogo` → `<img class="robot logo-img"
  src="logo.png">`, `WebUI\logo.png` = a copy of the 256-px master, loaded via virtual host/`file://`).
  Wordmark "✳ Claude Code" → **"Code Astrogator"** (sparkle glyph removed);
  tagline "// TODO:
  Everything…" → **"// pre-flight check complete. where to, captain?"**. The class `robot` stays
  (still hooks into `.h-short .robot { display:none }` at low height). The
  sign-in hint "Not signed in to Claude Code" stays deliberately (nominative = the CLI product).
  Note Light theme: the logo body is light — on white the dark outlines carry the
  contrast; if too pale, a light variant later.

## Miscellaneous
- **Session history: persisted** as JSON per workspace under
  `%LocalAppData%\CodeAstrogator\history\<sha1(solution-dir)>.json` — loaded when opening
  the tool window, saved after each turn / session change / on close.
  Caps: 50 sessions, 400 messages per session. CLI sessions remain resumable via `--resume` also
  across VS restarts (the CLI keeps the conversations itself).
  Note: two parallel VS instances on the same solution → last writer wins.
  - **"Too many sessions" — bounded by design (reviewed 2026-06-17):** `Save()` writes only the
    **50 most recent** sessions (orders by `UpdatedAtUtc` desc, breaks at the cap → oldest dropped)
    and at most **400 messages** each; tool outputs are already truncated (~10k chars) by the parser.
    `SaveHistory` runs on a **background thread** (`TaskScheduler.Default`) → no UI jank even on a large
    file. In-memory `_sessions` can transiently exceed 50 within one long VS session (ArchiveCurrent/
    Load/Import don't cap) but Save trims it and a restart reloads ≤50 → no unbounded growth. Manual
    pruning via the new per-row delete (see `session.delete`).
- Browser test of the UI: open `WebUI/index.html` directly (mock adapter active, simulates a
  complete turn incl. tool card and permission/diff card).
- Build: VS-2026-MSBuild (`…\18\Community\MSBuild\Current\Bin\MSBuild.exe`),
  Tests: `vstest.console.exe` from VS 2026.
