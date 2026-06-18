# Changelog

All notable changes to Code Astrogator are documented in this file.

---

## [0.4.3] – 2026-06-18

### Changed
- **The "Code Astrogator" wordmark on the welcome screen now uses the accent color.** It was rendered in the default text color; it now picks up `--accent` (so it also follows a custom accent color set in the gear popover).
- **"Detailed" verbosity now actually differs from "Normal."** Verbosity is a transcript display setting with three levels; previously *Normal* and *Detailed* were nearly identical — the only difference was whether thinking cards started expanded, and even that didn't apply to cards already on screen when you switched. Now *Detailed* starts **both thinking and tool cards expanded** (showing each tool call's input and output inline), while *Normal* keeps them collapsed (click to expand) and *Compact* still hides system notes and thinking entirely. Switching the level also re-applies to the cards already in the current transcript, and the gear popover now shows a one-line description of each level.
- **The assistant message avatar is now the Code Astrogator head** instead of the `✳` sparkle glyph. Each of Claude's messages in the transcript is prefixed with the small astronaut-head logo (`WebUI\head.png`, derived from the head master); the user `›` glyph and system notes are unchanged.

### Added
- **"Changelog…" button in the gear popover.** Below "Advanced options…" the appearance popover now has a Changelog entry that opens the project changelog (`https://github.com/finex7070/CodeAstrogator/blob/main/CHANGELOG.md`) in the system browser.
- **Head-only logo for small icons.** Added a head-only master (`Resources\codeastrogator-head.png`) — the astronaut's helmet and face cropped from the full-body logo, with no shoulders and the raised hand removed, centered in the frame. The small icons used where the logo is shown tiny (`icon-16/20/24/32`, i.e. the "View → Other Windows → Code Astrogator" menu entry and the tool-window tab) are now rendered from this head variant, so they stay legible at 16–32 px instead of shrinking the whole body. The Marketplace icon/preview and the large welcome-screen logo keep the full-body figure.
- **New setting: "Reference it by default in new chats."** Controls the starting state of the active-file reference (the file chip in the composer) for each new chat. With it on (the default, unchanged from before), a new chat references the active editor file right away. With it off, a new chat starts with the reference toggled **off** (the chip is shown struck-through) so nothing is sent unless you click the chip to opt in for that chat. It's a sub-option of "Reference the active editor file in each prompt" (greyed out while the master switch is off) and does not change the current chat — only what fresh chats start with.

### Fixed
- **Remote sessions now start on the very first open of a project.** `claude remote-control` refuses to start in an untrusted workspace (`"Error: Workspace not trusted. Please run `claude` … first"`), unlike the headless `claude -p` chat turn which bypasses the workspace-trust check entirely. So opening a project for the first time and immediately starting a remote session failed with that error (you had to drop to a terminal and run `claude` once to accept the trust dialog). The extension now pre-accepts workspace trust for the directory you opened in Visual Studio — it sets the same `hasTrustDialogAccepted` flag in `~/.claude.json` that the interactive dialog writes — right before launching the remote-control server. (Best-effort and non-destructive: an existing project entry is reused and left untouched when already trusted; the global config is written atomically; any failure simply falls back to the CLI's own trust error. Verified against CLI 2.1.178.)

---

## [0.4.2] – 2026-06-17

### Added
- **Delete sessions from the history list.** Each entry in the history popover now has a trash button (revealed on hover) that, after an explicit confirmation dialog, removes the session from the chat history. Deleting the active session starts a fresh chat. (The chat history is already capped at 50 sessions / 400 messages each, persisted on a background thread, so it cannot grow without bound — this just lets you prune it manually.)

---

## [0.4.1] – 2026-06-17

### Changed
- **Settings window polish.** Long checkbox labels (under "Announcements & updates") now wrap instead of being clipped at the right edge, and the window's height is capped to the screen — when the content is taller than the display it scrolls instead of growing off-screen (fixes usability on smaller screens).
- **Usage meters now refresh periodically while idle.** Previously they only updated on window open and after each turn. They are now also refreshed every 5 minutes whenever no turn is running, so the session/weekly utilization stays current without sending a prompt (the `/usage` fetch runs locally, `num_turns: 0`, no cost). Skipped while a turn is in progress (the post-turn refresh covers that).

### Fixed
- **Better readability of the hint texts in the Model·Mode popover.** The two helper lines (under "Ultracode" and "Auto-accept commands") used the faintest text color and were hard to read; they now use the standard dimmed color (higher contrast in both dark and light themes).
- **"Prompt timeout" setting works again with the new CLI (2.1.178).** A full re-verification against CLI 2.1.178 found that the CLI now reads the MCP tool-call timeout from the config's `timeout` field and lets it **take precedence over** the `MCP_TOOL_TIMEOUT` environment variable (the reverse of older CLI versions). Because the config field was hardwired to 1 hour, the user-configured prompt timeout (sent only via the env var) had no effect. The configured value is now written into the config field (and rewritten when changed), so the setting takes effect again; the env var stays as a fallback. (Everything else re-verified unchanged: `--effort`/`--permission-mode` values, the MCP permission protocol, AskUserQuestion routing, and the `/usage` parser.)
- **Slash-command menu / autocomplete no longer overflows off-screen.** The CLI now reports 28+ commands (built-ins plus skills), and the popover had no height cap — so the list grew past the window and only the first entry was visible. The popover is now height-capped and scrolls internally (verified against CLI 2.1.178).
- **The "✻ Thinking…" indicator no longer hangs when a turn is stopped while it is showing.** When the prompt was stopped (or the turn ended abnormally) during the thinking phase, the interrupted CLI emits no `thinking.end`, so the transient "✻ Thinking…" line stayed on screen for the rest of the session. The line is now dropped whenever the turn reaches a terminal state (Stop / error / normal end); a streamed thinking card just loses its spinner. No effect on normal turns, where `thinking.end` already cleared it.

---

## [0.4.0] – 2026-06-16

### Added
- **Configurable notice/announcement banner via GitHub** (replaces the hard-wired "discontinuation" notice). A `notice.json` in the repo root is served via GitHub raw — editing it and pushing to `master` shows/changes/hides the banner in **all installed extensions without a new VSIX release**. Schema with exactly 5 values:
  - `enabled` (on/off), `from` / `to` (optional ISO time window controlling when it is shown), `title` (plain text), `content` (**Markdown**, including links).
- **Update banner:** A separate banner (can be shown alongside the announcement) that points to a new version. When update notifications are enabled, the **latest GitHub release** is queried on window open (`/releases/latest`); if its tag version is newer than the installed one, a banner with a link to the release appears. Same cache / 3-hour throttling logic as the announcement. (Publishing a new release = upload the build + tag = version number.)
- **Opt-in with consent popup (announcements + updates):** Since the fetches are network calls, the first time the window opens **one** in-window popup with two checkboxes asks whether announcements and/or update notifications should be active. The choices are persisted and can be toggled at any time in the **settings window** (section "Announcements & updates"). If an option is off, it is **never** fetched or displayed.
- **Local cache + 3-hour throttling:** When an announcement/update is active, the source is fetched on window open and cached in `localStorage` — but at most **every 3 hours** (between fetch attempts). If a fetch fails (offline/404/broken JSON), the last cached version applies; if there is no cache, nothing is shown. Retried on the next open (also 3-hour throttled).
- **Editor right-click → "Add file to Claude prompt" / "Add selection to Claude prompt".** In the code editor the current file can be attached to the prompt as an attachment chip; if text is selected, an additional entry appears that inserts the selection as a code block (with file + line range) into the composer. Opens the chat window if needed and defers the action until the UI is ready.
- **GitHub repo configurable in one place:** The `owner/repo` (and the branch for `notice.json`) for both banners now lives in **`WebUI/config.js`** and can be changed there quickly before building (e.g. for a fork).
- **"Working" phrases moved to `config.js`** (`workingPhrases`) and extended with a few new ones — editable before building.
- **Discontinuation notice disabled:** Anthropic postponed/replanned the subscription change — the banner is empty/off by default (`enabled:false`).

---

## [0.3.9] – 2026-06-16

### Fixed
- **File drag-and-drop did not work** (files could only be attached via copy & paste). The WebView2 control forwards external drops as bubbling WPF events, but the code listened for the `Preview` variants (which never fire in this case). Now correctly attached to `DragOver`/`Drop` → files/folders can again be dragged from Explorer onto the chat panel.
- **Questions/permission prompts timed out far too early** (long before the assumed 10 min). The cause was the CLI's short default tool-call timeout — the `timeout` field of the MCP config does not apply to it. The CLI process is now started with `MCP_TOOL_TIMEOUT` (+ `MCP_TIMEOUT`). The duration is **configurable in the settings window** ("Prompt timeout", in minutes, 1–240; default 60).
- **Timed-out / aborted question and permission cards no longer stay open.** When the CLI ends an AskUserQuestion/permission prompt itself (timeout) or the turn ends while a card is still open, the card is now closed (marked "Expired", no longer interactive) and the status is correctly restored — previously the card stayed clickable and the "working" indicator (rocket) disappeared for the rest of the turn.

---

## [0.3.8] – 2026-06-10

### Added
- **"Auto-accept commands" toggle** in the Model·Mode popover (below the permission selection). When on, in **Auto-accept edits** mode Bash, PowerShell and MCP calls are additionally executed without prompting (the CLI accepts edits itself in this mode anyway). **Questions (AskUserQuestion) remain interactive.** Only applies in Auto-accept edits (otherwise the toggle is greyed out); the setting is sticky (persisted).
- **Discontinuation notice** as a banner at the top of the window (below the header): points out that the extension will receive **no more updates** as of June 15, 2026 due to Anthropic's change to subscription usage of the Agent SDK / `claude -p`, including a link to the [support docs](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan). Closable with ✕; reappears each time the window is opened (after every VS start).

### Changed
- **External links (`target="_blank"`) now open in the system browser** instead of in a WebView2 popup window — applies to the banner link **and** links in rendered responses (`NewWindowRequested` handler, http/https only).

---

## [0.3.7] – 2026-06-10

### Changed
- **Usage meters now get their data via the CLI's `/usage` slash command** instead of the scraped OAuth token. `ClaudeUsageClient.FetchAsync` runs `claude -p /usage --output-format json` (runs **locally, no API turn, no cost** — `num_turns: 0`) and parses the report text: `Current session: N%` → session meter, `Current week (all models): N%` → weekly meter; the per-model lines (e.g. "Sonnet only") are ignored. Reset times (`resets Jun 10, 1pm`) are parsed as local time for the tooltip (including year-boundary rollover). Works with any CLI auth.

### Fixed
- **Parallel questions: answering one card wrongly set the other to "expired".** When Claude calls multiple tools at once (e.g. Edit + Bash permission, or Bash permission + AskUserQuestion), multiple cards are open. When one was answered, the host immediately posted `working`, causing the UI to expire the still-open card(s). Now the status stays `waiting-permission` as long as prompts remain open (only the last answer switches to `working`), and the UI tracks **all** open cards (`pendingPermissions` set) instead of just one — on real abandonment (turn end/error) all remaining ones expire correctly.
- **Pasted screenshots/files were not passed for user paths with spaces.** Pasted images land under `%LocalAppData%\…\pasted\`; with a space in the Windows username (e.g. "Jan Huels") the CLI's `@`-path parser broke at the space and silently dropped the file. Paths with spaces are now quoted (`@"C:\a b\f.png"`), the optional `#L` line suffix stays outside the quotes. Also affects active-file / manual attachments under paths with spaces.

### Removed
- **Direct access to the OAuth token + the undocumented endpoint `api.anthropic.com/api/oauth/usage`** is gone (no more reading `~/.claude/.credentials.json` for usage). The plan badge still comes from `~/.claude.json`.

---

## [0.3.6] – 2026-06-08

### Fixed
- **Plan approve now leaves plan mode.** After approving an `ExitPlanMode` plan, the permission mode automatically switches from "Plan" to "Auto-accept edits" (`acceptEdits`) — host-side (the next turn correctly sends `--permission-mode acceptEdits` instead of still `plan`, so Claude can actually carry out the approved plan) **and** in the UI (the mode selector at the bottom updates immediately). A rejected plan stays in plan mode.
- **Duplicate permission card / stuck approve buttons on mid-turn mode change fixed.** When the mode was set to "Auto-accept" during a running turn (e.g. before plan approve), two cards were created for the same tool call: a host-side pre-decided "Applied" card **and** a real permission card with buttons (the running process was still in the old mode). Clicking "Approve" did not close the open card. Now (1) the host decides the auto-approve card at the **actual turn-start mode** (`LaunchedPermissionMode`) instead of the live setting — the double card never arises; (2) as a safeguard the UI discards an already-present `perm-card` with the same `requestId` (most recent request wins).

### Added
- **`mode.update` message** (host → web): updates `permissionMode`/`planMode` in the UI selector without clearing the transcript (unlike `session.init`).

---

## [0.3.5] – 2026-06-05

### Changed
- **Always popover** is now as wide as the permission card and shows patterns as an editable list (input + ✕ per row, "+ Add pattern") instead of a textarea.
- **`ShellCommandSplitter.ExtractCommands`** splits commands quote- and here-string-aware into real sub-commands (newlines / `;` / `&&` / `||` / `&` / `|`), discarding variable assignments and bare variables. `Wildcardize` turns quote contents into `*`.
- **Stricter matching:** A call is only auto-approved when every sub-command is covered by a pattern.

### Fixed
- 5 new tests (115 total green).

---

## [0.3.4] – 2026-06-05

### Added
- **Configurable accent color:** Gear popover → "Accent color" section with preset swatches, custom color picker and "Default" reset. The choice takes effect immediately and is persisted (`AstrogatorOptions.AccentColor`). `session.init` carries `accent`.
- **`accent.set` message** (web → host): hex validated via `NormalizeHexColor`.

### Changed
- **Auto-approve patterns as a JSON array:** `AstrogatorOptions.AutoApprovePatterns` is now `List<string>`; the WritableSettingsStore persists it as a JSON array. Backward compatible: `ParsePatterns` also reads the old newline format.
- **Claude bubble** (background + ✳ icon) in the accent color (from clay orange to purple).
- **Tool cards** get a soft result tint: success → subtle green (`.tool-ok`), error → subtle red (`.tool-err`).
- 5 new tests for `AutoApprovePatternsTests` (110 total green).

---

## [0.3.3] – 2026-06-05

### Changed
- **Auto-approve patterns in the settings window** now as a theme-tinted `DataGrid` list (one column, editable cells) with "Add" and "Remove" buttons instead of a multi-line TextBox.

---

## [0.3.2] – 2026-06-05

### Fixed
- **MCP tool icon:** `toolIcon` matched by substring (`…ManageEditor` got the Edit icon); now a uniform plug icon for all `mcp__` tools.
- **Hang on two simultaneous permission requests:** `HttpReadRequestAsync` discarded over-read bytes of the next pipelined request; fixed via a `carry` buffer.
- New test `HttpRead_HandlesTwoPipelinedRequestsOnOneConnection` (105 total green).

---

## [0.3.1] – 2026-06-05

### Added
- **"Always" button on permission cards** opens an editable popover (Cancel / "Add & approve") with suggested patterns.
- **Nested commands** (`a & b && c ; d`) are split into individual patterns (`Core/ShellCommandSplitter`, quote-aware; `2>&1`/pipes stay together).
- New bridge message `permission.approveAlways {requestId, patterns}`.
- 6 new splitter tests (104 total green).

### Fixed
- The "Always" button only appears when a match key exists (Bash/PowerShell command or MCP tool), not for Edit/Write.

---

## [0.3.0] – 2026-06-05

### Added
- **Auto-approve patterns:** Settings field (glob, `*` wildcard) for commands/MCP tools that are allowed without a prompt.
- "Always" button on every Bash/PowerShell/MCP permission card: approves the call and saves the command as a pattern.
- `IsAutoApprovedByPattern` check in `HandlePermissionRequestedAsync`; on a hit, silent `allow`.

---

## [0.2.4] – 2026-06-05

### Added
- **AskUserQuestion = a real in-turn card via the permission hook.** The card shows clickable options + free text ("Other"); the choice goes back via the `deny` `message`, which the CLI feeds to the model as a tool result.
- New bridge message `question.request` / `question.answer`.
- The card collapses after answering (chevron + answer summary, click re-expands).

### Changed
- `sendPrompt` always scrolls all the way down after sending (unconditional `scrollToBottom()`).

---

## [0.2.3] – 2026-06-05

### Added
- **MCP tool cards** (`mcp__server__tool`): show only the tool name, no trailing "{" anymore.

### Changed
- **Tool card title** now comes from the input instead of the output line: Read shows `File.cs:offset-end`, Edit/Write the file name, Grep the pattern, Bash/PowerShell the command. Applies live and historically.
- **Command preview** in the tool and permission card header for Bash/PowerShell (`input.command`, newlines → spaces, truncated).

---

## [0.2.2] – 2026-06-05

### Added
- **Markdown tables (GFM):** detection of header + delimiter row, alignment, inline-formatted cells in a scrollable `.md-table-wrap`.

### Changed
- **Message backgrounds:** user bubbles blue, Claude bubbles orange-tinted (theme-aware vars in dark & light).
- **Auto-approved edits** appear as a green permission card (pre-decided, `autoApproved:true`) instead of a tool card with a system note.

### Fixed
- Attachment chips were built but never attached to the DOM (`appendChild` missing) → now visible in user bubbles, live and historically.

---

## [0.2.1] – 2026-06-05

### Added
- **"Working" indicator:** animated rocket + a random space-astrogator one-liner + animated dots while `status === "working"`. `prefers-reduced-motion` stops the animation.

### Changed
- **Turn footer** slimmed down: price and token count removed; footer = `<hr>` + readable duration (`formatDuration`: `130000ms → "2m 10s"`).
- **Ctx % meter** and `/compact` click trigger removed entirely. `contextTokens` stays in the contract but is no longer displayed.
- **Thinking line** now shows only `✻ Thinking…` (no `~n tok` suffix).

### Fixed
- **Auto-scroll bugfix:** `atBottom` is now captured before the DOM mutation; `scrollToBottom` pins via `requestAnimationFrame` afterwards (layout/highlight/reflow race).
- **Ctx token bugfix:** the meter showed 100% due to aggregated `cache_read` values. Fix: context from the usage of the last assistant message.

---

## [0.2.0] – 2026-06-05

### Added
- **File drag-and-drop:** drag files/folders from Windows Explorer onto the chat panel → attachment chips. Host-side via WPF `PreviewDrop` + `WebViewBridge.AddFileAttachments(paths)`.
- **Model·Mode popover is persisted:** model, effort, ultracode and permission mode survive new chats and VS restarts (`AstrogatorOptions.DefaultModel` / `DefaultEffortString` / `UltracodeEnabled` / `PermissionModeString`).

---

## [0.1.2] – 2026-06-04

### Added
- **Active file reference:** the file in the active editor tab as a chip to the right of the slash button. Click = session toggle (strikethrough). Option `autoAddActiveFile` (master switch).
- **Selected lines:** the reference appends the line range (`@<path>#L<top>-<bottom>`), controlled via `includeSelectedLines`.
- Live update via `activeFile.refresh` on composer focus and window focus.
- Bridge message `activeFile { path, name, optionEnabled, enabled, lines }`.

### Fixed
- Settings read pipeline: explicit initial read per setting on package load.

---

## [0.1.1] – 2026-06-04

### Added
- **Extension logo:** Astrogator robot logo (astronaut robot, purple) in all sizes (16/20/24/32/90/200 px). ImageMoniker for the menu command and the tool window tab.
- **Settings window (`AstrogatorSettingsWindow`):** opened via gear → "Advanced options…". VS-themed (dark/light via `VsResourceKeys` styles). Save/Cancel semantics (Cancel/X discards, only Save persists).

### Changed
- **Settings rebuild:** Unified Settings in-proc API removed (service not available in VS 2026). Back to pure VSSDK + `WritableSettingsStore` (`AstrogatorSettingsStore`).
- Empty state: inline SVG robot replaced with the logo; wordmark → "Code Astrogator"; tagline → "// pre-flight check complete. where to, captain?".

---

## [0.1.0] – 2026-06-04

### Added
- **Permission hook phase 1:** in-process MCP server (`Core/McpPermissionBridge`, TcpListener HTTP) for `--permission-prompt-tool`. Interactive permission card in the chat with Allow/Deny. The card replaces the tool card with the same `tool_use_id`. Status transition: running → diff → Applied / Failed / Rejected. Collapsible with line numbers. Persisted as `role:"permission"`.
- **Persistent CLI mode** (opt-in, off by default): `ClaudePersistentProcessHost` — a single long-lived `claude -p --input-format stream-json` process instead of spawning per turn. In-place interrupt via `control_request`. The toggle takes effect live when idle.
- Tests: `PermissionBridgeTests`, `PersistentProcessHostTests`.

---

## [0.0.2] – 2026-06-03

### Added
- **Remote control:** button → QR code/link → Stop → session import. `Core/RemoteControlHost` + `CliSessionReader` (imports CLI sessions after Stop). QR code dependency-free via `WebUI/qr.js`.
- **Ctx meter** (later removed in 0.2.1): context usage of the current session, `/compact` click, near-limit coloring from 75% / 90%.
- **Dynamic slash menu:** command list from `system/init.slash_commands`; `/help` host-side (the CLI rejects it headless).
- **Copy context menu** (custom popover, `AreDefaultContextMenusEnabled=false`).
- **Ctrl+V:** file/image paste via `clipboard.paste` → the host reads the Windows clipboard, PNG under `%LocalAppData%\CodeAstrogator\pasted\`.
- `session.rename` (edit icon, rename modal, persisted via `SessionHistoryStore.Rename`).
- Limits `resets_at` tooltips on the S/W meters.
- `compact_boundary` evaluation (`post_tokens` → system note "Context compacted").

### Fixed
- Theme fix: `applyTheme` clears old inline vars before setting new ones (`removeProperty`).

---

## [0.0.1] – 2026-06-03

### Added
- Initial release: VSIX extension for Visual Studio 2026 with a WebView2-based chat tool window.
- Claude Code CLI integration (`claude -p --output-format stream-json`, prompt via stdin).
- NDJSON stream parser (`Core/NdjsonParser`, `ClaudeSessionService`).
- Session history (JSON, `%LocalAppData%\CodeAstrogator\history\`, 50 sessions, 400 messages per session).
- `--resume` robustness: session ID only when `num_turns > 0`; automatic retry on "No conversation found".
- 5 effort levels (`low` / `medium` / `high` / `xhigh` / `max`), ultracode keyword injection.
- `@` mention autocomplete (workspace file list, BFS, max 2000 entries).
- Extended thinking display (`thinking.start/delta/end`).
- Mock adapter for isolated browser tests (`WebUI/index.html`).
