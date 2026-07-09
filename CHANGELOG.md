# Changelog

All notable changes to Code Astrogator are documented in this file.

---

## [0.6.1] – 2026-07-09

### Changed
- **The turn-end review now completes per file only when you say so.** Previously, deciding the last change in a file auto-finished that file — it dropped out of the review and off the chat list immediately. Now the file stays until you explicitly finish it. The editor's floating review toolbar gained a **Finish** button (enabled once every change is decided) and a **Reset** button (clears all decisions in the file so you can start over), and each file's chip in the chat list now has its own **Finish** button that enables as soon as all its changes are decided. (Permission-prompt reviews in Ask mode are unchanged — those still resolve automatically once every change is decided.)

### Added
- **Model list reordered by capability.** The model picker now lists the strongest model first: Fable 5, then Opus 4.8, Opus 4.7, Sonnet 4.6, Haiku 4.5. (The default model is unchanged; only the display order moved.)
- **Clearer inline edit-review state.** The per-change Keep/Revert (and Accept/Reject) buttons in the editor are now colored — green for keep/accept, red for revert/reject — and the chosen one fills solid with bold white text so a decision you've already made stands out. The highlights follow the decision too: the winning side (kept new text, or restored old ghost text) is emphasized while the losing side is dimmed, so it's obvious at a glance what each change will end up as.
- **Review controls got a visual pass.** The editor's floating review toolbar is now uniformly styled — flat neutral Prev/Next arrows, an amber Reset, and a solid-green primary Finish (all with hover/disabled feedback). In the chat review list, "Keep all" highlights yellow and "Discard all" red on hover. The redundant expand chevron was removed from approval cards so every card starts with the same tool icon.
- **Permission/approval cards now carry the same tool icon as tool cards.** Approval cards previously had no icon, so an Edit/Write approval card looked different from its plain tool card. They now show the matching icon (the pencil for Edit/Write/MultiEdit), so every edit card is consistent.
- **Bare Edit/Write tool cards now show an inline diff too.** Edit, Write, and MultiEdit tool cards that didn't go through the approval/diff path (e.g. an edit the CLI ran or rejected without prompting) previously showed their raw JSON input. They now render the same red/green inline diff, built from the tool's own input (Edit/MultiEdit from `old_string`→`new_string`, Write from `content` shown as additions). Line numbers are omitted since the bare tool input carries no file position; cards that still lack a diffable input fall back to JSON as before.
- **The editor tab shows the review's line counts.** While a file is under inline edit review (turn-end review), its Visual Studio document tab caption gains a "(+N -M)" suffix (added/removed lines) — e.g. `GraphicPixelExplosion.cs (+24 -3)` — matching the count on the file's chip. The suffix is removed again when the review is decided, kept, discarded, or the tab is closed.
- **Open a file card's file directly in Visual Studio.** File-based cards — Edit, Write, Read, MultiEdit, Notebook edits, and their permission cards — now show a small open-in-editor button right after the file path in the card header. Click it to open that file in the Visual Studio text editor (a plain open, independent of the inline edit-review). The button only appears when the card refers to a file, and clicking it no longer collapses or expands the card.

### Fixed
- **Usage meters no longer show a misleading 0% before the first `/usage` report.** Right after Visual Studio started, the Session and Weekly meters read a solid 0% (as if nothing had been used) until the first usage fetch completed. A window with no fetched value yet now shows a dimmed "—" instead, so an unknown value is never mistaken for "nothing used"; it fills in as soon as real data arrives (a genuine 0% still shows as 0%).
- **Already-open files now show their inline review immediately at turn end.** With "Review edits at end of turn" on, if a changed file was already open in the editor, its green/red inline diff only appeared after clicking the file's chip. Changed files that are already open now get the inline review attached right away (files that aren't open are still opened on demand when you click their chip).
- **Closing a file from the turn-end review list and reopening it now works.** In the "Review N changed files before continuing" list, opening a file's chip, then closing that editor tab, left the review "stuck open" internally, so clicking the chip again did nothing. Closing the tab is now treated like leaving the review pending — the chip stays in the list and reopens the file cleanly.
- **Inline edit-review Prev/Next buttons now work correctly.** The floating "▲ ▼ N to review" toolbar's arrows jumped to the wrong change and ignored where you'd scrolled. They now navigate relative to the current view: ▼ scrolls to the first change below the visible area and ▲ to the first change above it (skipping changes already on screen), wrapping around at the ends — so they behave correctly even after you scroll manually between presses.

---

## [0.6.0] – 2026-07-03

### Added
- **Running agents now show as a live list at the bottom of the chat.** While a prompt has one or more subagents (the Task tool) running, a small panel appears just above the message box listing each running agent with a spinner and its task label. Each entry disappears as its agent finishes, and the panel clears when the turn ends.
- **New "Review all edits at end of turn" option for Auto-accept edits mode.** Turn it on (under *Auto-accept edits* in the model·mode popover) and Claude still applies every edit automatically as it works — but when the turn finishes, a list of **every file it changed** appears just above the message box, and you can't send the next prompt until you've dealt with it. Click a file to open it in the code editor: because the changes are already saved, the **new lines are highlighted green in place** and any **removed old lines are shown in red as ghost text** above them, with per-change **Keep / Revert** buttons. **Keep** leaves the change; **Revert** undoes just that change back to how the file was before the turn. When every change in a file is decided the file drops off the list; once the list is empty (or you press **Keep all** to accept everything as-is, or **Discard all** to revert every change from the turn — deleting files created this turn), the composer unlocks. It's a way to let Claude run freely and then do one collected review of all its edits at the end, instead of approving each edit up front. Reverting *all* changes to a file Claude *created* this turn deletes the file. The list survives a window reload, and the file is read-only while you review it so an accidental keystroke or Ctrl+S can't disturb the diff. Off by default.
- **Switching to Bypass permission mode now asks for confirmation.** Choosing **Bypass** in the model·mode popover opens a short warning dialog explaining that Claude will apply every file edit and run every command without asking — and to use it only in a workspace you trust. **Enable Bypass** confirms the switch; **Cancel** leaves the current mode untouched. Re-selecting Bypass when it's already active does not prompt, and the other modes still switch instantly.

### Fixed
- **A prompt that spawned subagents no longer shows several turn footers.** When a turn ran one or more Task subagents, each could render its own `time · tok · $` footer at the end, so the turn appeared to "end" several times. Only a single turn footer (the real total) is now shown per turn.

---

## [0.5.4] – 2026-07-02

### Added
- **The Fable 5 model is now selectable** in the Model · Mode picker (between Opus 4.7 and Sonnet 4.6). Picking it runs the session on `claude-fable-5`; the choice sticks across new chats and restarts like the other models.

### Fixed
- **The plan-limit meters no longer drop to 0% when a `/usage` report comes back incomplete.** The meters are fed by running the CLI's `/usage` command, but that command intermittently returns only its "What's contributing to your limits usage?" section and **omits the three `Current session` / `Current week` percentage lines** entirely (an async server-side fetch that quietly times out). Previously such a response could blank out or zero the meters until the next good fetch. Now each `/usage` result is merged into a **per-window last-known-good**: only the windows that actually came back overwrite the displayed value (a genuine "0% used" is still honoured — it's now distinguished from "not reported"), and a completely empty report leaves the meters untouched. A single quick retry is attempted when the whole block is missing. If a window keeps failing to report for more than 5 minutes the extension keeps showing its last value but **dims that meter** and notes in its tooltip when it was last updated (e.g. *"Session usage: 65% · resets 8pm · updated 14:32 (may be out of date)"*) instead of silently showing a stale or wrong number.

---

## [0.5.3] – 2026-07-01

### Changed
- **Approved edits/commands now show a spinner while they run.** After you approve a permission prompt, the card shows "✓ Approved" until the action finishes, then flips to "✓ Applied". During that gap the action is actually executing, but nothing indicated it was still working. A small spinner now appears at the far right of the card while it runs (matching the Bash/command tool cards) and disappears once the result ("Applied"/"Failed") arrives.
- **The composer placeholder now hints at Shift+Enter, and the Ctrl+Esc shortcut was removed.** The empty message box used to read *"ctrl esc to focus or unfocus Claude"* — but Windows reserves Ctrl+Esc for the Start menu, so that shortcut couldn't work anyway. The placeholder now reads **"Chart a course for Claude…  ·  Shift+Enter for a new line"**, and the (non-functional) Ctrl+Esc focus toggle has been dropped.
- **"Add selection to prompt" now adds a compact attachment chip instead of pasting a code block.** Right-clicking an editor selection and choosing *Add selection to prompt* used to dump a fenced code block (the file name, then the raw selected lines) straight into the composer text. It now adds a small `@`-reference chip labelled with the file name and line range (e.g. `CLAUDE.md:14-17`) to the composer's attachment strip — the same place and style as attached files and the active-file chip — leaving what you type untouched. Claude reads exactly those lines from the file when the turn is sent.
- **The two editor right-click commands are now named "Add file to prompt" and "Add selection to prompt".** Dropped the redundant "Claude" from the labels (they already live under the Code Astrogator icon).
- **A subagent's result is now shown as its own labelled line, not a confusing second turn footer.** When a prompt runs an extra agent (the Task tool), the CLI reports that agent's own time/tokens/cost separately from the turn total — so you'd see *two* `time · tokens · cost` footers stacked at the bottom, the second one looking like a duplicate turn end. The subagent's figures now render as a distinct, indented **"Agent finished · …"** line with an accent rail (no turn divider), making clear it's work nested inside the turn; the turn's own footer below it still shows the total (which already includes the subagent).
- **The plan-limit meters now refresh every minute, including while a prompt is running.** The session/weekly usage meters in the status bar used to update only when the window opened, after each turn ended, and every 5 minutes while idle — so during a long-running turn they stayed frozen at their pre-turn values. They now refresh once a minute regardless of whether a turn is in flight (the `/usage` query is a separate lightweight process, so it doesn't interfere with the running turn), giving you a live view of how much of your limit a long turn is consuming.

### Fixed
- **The extension icon no longer looks washed-out/greenish in the menus.** The Code Astrogator icon (in the editor right-click menu, under View → Other Windows, and on the tool-window tab) rendered with a muddy green tint instead of its blue astronaut colours on dark themes. Visual Studio was applying its automatic dark-theme colour inversion to the icon; it's now marked to render exactly as authored.

---

## [0.5.2] – 2026-06-30

### Added
- **Each permission mode now has a short description, and its extra toggle is clearly nested under it.** In the model·mode popover's Permission section every mode (Ask before edits, Auto-accept edits, Plan, Bypass) shows a one-line explanation under its name, so it's clear what each does. The mode's extra toggle ("Review edits in the editor" under Ask, "Auto-accept commands" under Auto-accept edits) now sits nested *inside* its mode — indented under the mode's name with an accent rail down the side — so the toggle and its hint read as belonging to that mode instead of looking like a loose row. The whole toggle block (its name, switch *and* description) is clickable, not just the switch.
- **The model·mode popover now grows to use the space above it.** Instead of being capped at a fixed height (which forced scrolling once the mode descriptions were added), the popover expands upward toward the top of the window when there's room, and only scrolls if it would otherwise run off-screen.
- **The turn footer now shows tokens and cost, not just the elapsed time.** The dim divider line below each completed turn now reads e.g. **`9s · 340 tok · $0.012`** — the time taken, the number of tokens Claude *generated* in that turn (its output across all steps), and the turn's cost. (The token figure is the work produced this turn; how full the context window is stays on the separate Ctx meter.) Each value is omitted when it's zero, so trivial local turns stay clean.

### Fixed
- **Approval prompts no longer expire after ~5 minutes.** A permission prompt or an `AskUserQuestion` left open would give up after roughly five minutes and be retried (a fresh prompt appeared), even though the "Prompt timeout" was set to 60. The cause was a transport-level HTTP timeout in the CLI's client that the timeout setting can't lift: while waiting for your decision the bridge held the connection open without sending anything, so the client aborted it. The bridge now streams the approval response and sends a small keep-alive every 25 seconds while you decide, so the only limit that applies is your configured "Prompt timeout". Affected both permission prompts and AskUserQuestion (they share the same path).
- **Plan-mode approval no longer shows the plan twice.** When an `ExitPlanMode` plan needed approval, it appeared *both* as a nicely rendered Markdown plan card *and* again as raw JSON (with literal `\n`s) inside the Approve/Reject card. The approval card now renders the plan as Markdown itself and the duplicate plan card is dropped, so the plan shows once — readable, in the card that also carries the Approve/Reject buttons.
- **Turn failures now show the actual error, not just `claude exited with code 1`.** When the CLI fails, the useful message (e.g. *"API Error…"*, *"Credit balance is too low"*, *"Prompt is too long"*, an invalid model name) usually arrives on stdout as a `result` event while stderr stays empty — so previously that text was discarded and you were left with the bare exit code. The error message now carries the CLI's own error text (`claude exited with code N:` followed by the detail), falling back to the stderr tail, and finally an explicit *"(no error output)"* when the CLI genuinely emitted nothing.
- **Auto-approve wildcards (`*`) now match regardless of quoting.** When you approve a command "from now on", quoted arguments are turned into a `*` so the pattern stays reusable (e.g. `git commit -m "first"` → `git commit -m *`). Previously the quotes were *kept* in the pattern (`git commit -m "*"`), so it only matched commands that used the exact same quote style — a later `git commit -m 'other'` (single quotes) or an unquoted value silently failed to match, which is why the wildcard sometimes seemed not to work. The quotes are now dropped, so the pattern matches whether the next invocation uses double quotes, single quotes or none. (This is safe: a command is still only ever silently re-approved when *every* one of its sub-commands matches a pattern, and the wildcard can never stretch across a command separator.)
- **The "Always" button now detects (splits) commands far more reliably.** When you approve a command "from now on", it's broken into its individual sub-commands so each becomes its own reusable auto-approve pattern (and a command is only ever silently re-approved when *every* one of its parts is already trusted). That splitter was tripped up by quoting and shell syntax. It now correctly handles: **escaped characters** — PowerShell backtick (`` ` ``) and bash `\"` — so an escaped quote or separator no longer corrupts the split (e.g. `echo "a\" ; rm -rf /"` is now kept as a single command instead of leaking ` rm -rf /` out as its own suggested pattern); **doubled quotes** (`""` / `''`, PowerShell's literal-quote escape); and **nested syntax** — separators inside `(…)`, `$(…)`, `@(…)` and `{ … }` script blocks no longer split, so e.g. `foreach ($i in $x) { a; b }` stays whole. Windows paths like `WebUI\app.js` are untouched. The result: the patterns pre-filled into the "Always" popover are clean and accurate instead of garbled half-commands.

---

## [0.5.1] – 2026-06-21

### Added
- **The installed extension version is now shown in the gear popover.** Below the "Changelog…" entry the appearance popover displays "Version x.y.z" (using the version already sent in `session.init`), so you can tell at a glance which build you have installed.
- **"Max" effort now has a special looping effect.** Max is the top effort level, so when it's selected the **Max** segment in the model/mode popover and the **model·mode pill** at the bottom of the composer both light up with a flowing purple gradient and a pulsing glow that loops for as long as Max stays selected — a little reward for going all-in. Honours `prefers-reduced-motion` (keeps the gradient + a steady glow, drops the motion).
- **"Ultracode" now has its own looping effect too.** When Ultracode is on, its toggle switch in the popover and the bottom model·mode pill light up with a flowing multi-hue spectrum (cyan→indigo→purple→pink with a cyan/violet glow) — distinct from Max's purple, to evoke its multi-agent nature. When **both Max and Ultracode** are on, the bottom pill shows the **Ultracode** effect (Ultracode takes priority there). Also honours `prefers-reduced-motion`.
- **Tasks banner.** When Claude works through a multi-step task list (the `TaskCreate`/`TaskUpdate` tools), a collapsible banner appears below the header showing each task with its live status (☐ pending · ◐ in progress · ☑ completed) and a done/total count. It's scoped to the current turn — a new prompt's first task starts a fresh list rather than piling up — and is dismissible with ✕ (a new task re-shows it). It's a live-turn aid only: reopening a chat from the history does not show it.

### Fixed
- **Switching the conversation mid-turn no longer disrupts the running turn.** Starting a new chat, loading or deleting the **active** session, or starting remote control while a turn was running (or waiting for a permission decision) used to detach from the live CLI process — orphaning it and letting its output land on the wrong conversation. These actions are now blocked while a turn is in progress: the **New chat** and **remote-control** buttons are disabled, the history list is read-only (with a hint), and the host also rejects the action with a note ("Stop the current turn first…"). Deleting a *different* (non-active) session mid-turn still works.
- **Dragging files onto the chat works again.** Dropping a file or folder from Explorer showed a "no-drop" cursor and did nothing. The old approach relied on the WebView2 control forwarding OS drops as WPF events, which the current WebView2 runtime simply doesn't do (it rejected the drop outright). Drops are now accepted by the web view and the dropped item's real path is recovered host-side, so files **and folders** can again be dragged in as attachment chips (the CLI reads them by path).
- **Answering one of Claude's questions no longer leaves a "failed" card.** With `AskUserQuestion`, the interactive question card and the underlying tool call arrive over two independent channels; when they raced, answering left a duplicate tool card stuck showing "failed" even though the answer was accepted. The duplicate is now suppressed.

### Changed
- **Remote control now opens your *current* chat, interactively.** Clicking the remote-control button used to start a hidden `claude remote-control` server in a **brand-new** session and only loaded the result into the chat after you ended it. It now opens the **current conversation** in an **interactive** Claude Code session with Remote Control enabled — `claude --resume <id> --remote-control` — in a **PowerShell** console, so you continue *this* chat from the Claude app or claude.ai/code (a fresh chat with no session yet starts a new remote session). Open the connection link shown in that console. The chat panel locks while it's running; **closing the console or clicking "End remote session"** re-imports the now-advanced conversation. (It runs in a console rather than the VS integrated terminal, whose API can't be referenced from this project without dragging in incompatible VS-18/.NET-10 assemblies — see `docs/NOTES.md`.)

---

## [0.5.0] – 2026-06-19

### Added
- **Review edits in the editor (opt-in).** A new "Review edits in the editor" toggle (off by default). When it's on and Claude proposes a file edit (Edit/Write/MultiEdit) in a mode that actually asks (Ask/Plan), the chat shows a compact file card with **"Accept all"**, **"Open in editor"** and **"Reject all"** instead of the inline diff card. Clicking **Open in editor** opens the file and shows the proposed change **inline in the code editor** — to-be-deleted lines highlighted red, to-be-added lines as green "phantom" lines (the file itself is never modified during review) — with **Accept/Reject buttons per hunk**. Accepting only some hunks applies exactly those: the tool input is reconstructed from the accepted hunks and handed back to the CLI (rejecting everything denies the edit). This is the inline, Copilot-style review flow from the roadmap (permission hook + per-hunk partial apply). It only takes effect in Ask/Plan mode — in Auto-accept/Bypass the CLI applies edits without prompting. The diff and reconstruction logic is covered by unit tests.
- **"Accept all" on the edit-review file card.** Applies the whole proposed edit straight from the chat card without opening the editor (a plain allow, which the CLI receives as the full original edit). "Open in editor" is now a secondary action next to it.
- **Scrollbar marks for inline reviews.** While a review is open in the editor, each proposed change shows as a coloured tick next to the vertical scrollbar — like git change marks — so you can see at a glance where the changes are (green = additions, red = deletions, purple = changed, grey = already decided).
- **Jump to next/previous change.** A small floating toolbar (pinned to the top-right of the editor) with ▲/▼ buttons that scroll to the previous/next change in the review, plus a "N to review" counter.

### Changed
- **The "Review edits in the editor" toggle moved into the Permission menu** (the model/mode popover), nested directly beneath the mode it applies to: it now sits under **"Ask before edits"** and only shows while Ask is selected, mirroring **"Auto-accept commands"** which sits under "Auto-accept edits". A sub-toggle that doesn't apply to the currently selected mode is hidden rather than shown dimmed.

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
