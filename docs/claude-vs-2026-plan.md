# Claude for Visual Studio 2026 — Implementation Plan

> Handover document for **Claude Code**. **Part A** describes the architecture and binding
> key decisions, **Part B** the concrete v1 implementation (chat UI).
> v1 goal = the chat UI as a WebView2 tool window. The CLI integration, the process host and the
> permission bridge are specified as interfaces (Part A) and can be wired up after the UI;
> the UI is immediately runnable and testable via its mock adapter.

---

# Part A — Architecture & Key Decisions

## A1. Overview & Goal
- A **Visual Studio 2026 extension** (not VS Code) that integrates Claude into the IDE.
- Integration via the **Claude Code CLI** (terminal installation), **not** the Agent SDK. Consequence: both **OAuth** (Pro/Max/Team subscription) and **API key** work, because the CLI handles authentication itself.
- v1 scope: an **improved chat UI** in a dockable tool window (details in Part B).

## A2. Extension model — VSSDK + WebView2 (binding)
- **Classic VSSDK** (in-process VSIX, `AsyncPackage`, `ToolWindowPane`) instead of the new out-of-process *VisualStudio.Extensibility* model.
- Reason: The chat UI is a rich web surface → **WebView2** in the tool window. The new model relies on a WPF "Remote UI" model (more restricted, still preview). VSSDK also provides full access to the editor/solution model for later features (context injection, diff display in the editor).
- VSIX target `[17.0,)` → runs on VS 2022 **and** VS 2026.
- Trade-off: in-process. The actual workhorse, however, is a **child process** (the CLI); the in-process layer stays thin, the crash risk low.

## A3. CLI integration — `claude -p` + stream-json (binding for v1)
- One process per turn (example):
  ```
  claude -p "<prompt>" \
    --output-format stream-json --verbose --include-partial-messages \
    --resume <sessionId> [--model <id>] \
    --mcp-config <config> --permission-prompt-tool mcp__vsbridge__permission_prompt
  ```
- `--resume <sessionId>` preserves the conversation across turns; `--include-partial-messages` delivers token streaming (`text_delta`). The `session_id` comes from the `system/init` event of the first turn.
- Output: **NDJSON events** on stdout (`system/init`, `assistant`/`stream_event` with `text_delta`, `tool_use`, `result` with `session_id`/`cost`/`usage`, `api_retry`, errors). A parser converts them into domain events.
- **Put the process host behind an interface** so that later a **persistent bidirectional** mode (`--input-format stream-json --output-format stream-json`) can be switched to (lower latency, interrupts) — without changes to the UI.
- Working directory of the child process = opened solution/folder (project context).
- **Executable resolution:** find `claude` via PATH / npm-global / native installation (on Windows possibly `claude.cmd`), plus a configurable override path.

## A4. Authentication — delegated to the CLI
- The extension implements **no** OAuth; it leaves the credential choice to the CLI.
- **OAuth mode:** use an existing login from `~/.claude`. Since OAuth requires a browser, the UI cannot do this silently → when "not signed in" guide the user via an integrated terminal to `claude /login` (or `claude setup-token` for a long-lived token).
- **API key mode:** set `ANTHROPIC_API_KEY` (or `CLAUDE_CODE_OAUTH_TOKEN`) on the **environment of the child process**, read from secure storage (Windows Credential Manager / DPAPI; **never** plaintext).
- The auth state is reported to the UI via `auth.state` (Part B §3).

## A5. Permission bridge & inline diffs (core feature, host-side)
- Mechanism: `--permission-prompt-tool` points to an **MCP tool** that Claude calls for tool calls requiring approval. It is **only** invoked when no static rule applies → do **not** pre-allow Edit/Write/Bash; only broadly grant read-only tools (Read/Grep/Glob).
- **In-process localhost MCP server**, co-hosted in the extension (C# MCP SDK `ModelContextProtocol` or a minimal SSE endpoint). This way the permission call lands **directly in the extension process** (no additional IPC hop) and can drive the diff UI as well as **block** on the user decision.
- Return contract of the tool: `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}`. `updatedInput` allows returning a proposal edited in the diff.
- **Flow:** Claude calls `Edit` (file_path/old_string/new_string) → permission tool fires → UI shows an **inline diff** → Approve → `allow` → Claude's Edit tool performs the write itself (no own file I/O needed); Reject → `deny`, Claude adapts.
- The MCP server is **persistent** (lives with the tool window), independent of the per-turn CLI processes.
- Note: `--permission-prompt-tool` is functional, but **not documented in `--help`** → **pin** the CLI version and test against updates. `PreToolUse` hooks are an alternative, but more cumbersome for an IDE.

## A6. Layered architecture & project structure

```
VSIX / AsyncPackage  (VSSDK, in-process)          ← Lifecycle, ToolWindow reg., Options page
  └─ Chat Tool Window → WebView2 → Web UI          ← Part B
       └─ Bridge (WebMessage ↔ C#)                 ← PostWebMessageAsJson / WebMessageReceived
            └─ ClaudeSession service               ← Turn orchestration
                 ├─ Process host (claude -p)        ← stdin/stdout/stderr, behind interface
                 │     └─ NDJSON protocol parser
                 ├─ MCP permission bridge           ← in-process localhost, --permission-prompt-tool
                 │     └─ Diff/approval coordinator → Editor + UI
                 └─ Auth provider + settings store
```

**Project structure (solution):**
- `Claude.VS.Extension` — VSIX, `AsyncPackage`, `ToolWindowPane`, commands, Options page (auth mode, theme mode, `claude` path, model defaults).
- `Claude.VS.Core` — `ClaudeSession`, process host, NDJSON parser, event models, MCP permission bridge. **UI-free and testable.**
- `Claude.VS.WebUI` — web assets (Part B), buildable separately, embedded into the VSIX.
- `Claude.VS.Core.Tests` — parser/session tests against recorded NDJSON fixtures.

**Threading/lifecycle:** `JoinableTaskFactory` for UI-thread marshaling; process I/O on background threads, marshal events onto the UI thread before the WebView post; terminate processes cleanly on tool-window close and solution unload.

## A7. Data flow
- **Chat turn:** user types → WebView posts `prompt.send` → `ClaudeSession` starts `claude -p … --resume` → CLI streams NDJSON → parser → `host→web` events (`assistant.start/delta/end`, `tool.use/result`, `turn.result`) → UI renders.
- **Permission/diff:** the CLI calls the MCP permission tool mid-turn → bridge sends `permission.request` to the UI → user Approve/Reject → `permission.decision` → bridge returns `allow`/`deny` to the CLI → turn continues.

## A8. Roadmap / scope
- **v1:** chat UI (Part B), including the UI-side permission/diff card. The host building blocks (CLI host, MCP bridge, auth) are gradually wired up for real behind it; the UI is immediately verifiable via the mock adapter.
- **Later:** persistent bidirectional CLI mode; real inline editing of the diff before approve (`updatedInput`); additional tools/permissions; multi-tab/parallel sessions; context injection from the active editor.

---

# Part B — Chat UI Specification (v1)

> Implementation brief for Claude Code. The goal is the **chat UI** (v1). The CLI integration,
> the process host and the permission bridge are separate building blocks; the UI is a **pure
> view** that talks to the C# host exclusively via the message contract defined below.

---

## 1. Context & classification

- Host: **VSSDK in-process extension**, one `ToolWindowPane` with a **WebView2** control.
- The UI is a **single, dependency-free HTML/CSS/JS page** (no CDN, no npm runtime dep in the WebView), embedded as a resource into the VSIX and loaded into WebView2.
- Communication with C# exclusively via the WebView2 bridge:
  - **web → host:** `window.chrome.webview.postMessage(jsonObj)`
  - **host → web:** `CoreWebView2.PostWebMessageAsJson(...)`, in JS via `window.chrome.webview.addEventListener('message', ...)`
- The UI knows **no** Claude-specific state from its own source. Sessions, history, tokens and limits come from the host (which derives them from the CLI / from stream-json).

**Background architecture (for orientation only, not to be implemented here):** Per turn the host starts `claude -p --output-format stream-json --verbose --include-partial-messages --resume <sessionId>`. Inline diffs run through an in-process localhost MCP server that serves as a permission handler via `--permission-prompt-tool`. Auth (OAuth or API key) is handled by the CLI itself.

---

## 2. Technical constraints of the UI layer

1. **No** `localStorage`/`sessionStorage` and **no** network calls from the WebView. State lives in-memory; persistence exclusively via the host.
2. **Theme-aware:** All colors via CSS variables. The host injects the resolved colors at startup and on every theme change via the `theme` message; the UI, however, ships complete **dark and light defaults** (see §6) so that it renders correctly even without a host (for isolated testing in the browser). Details on Dark/Light/Auto in §8.
3. **Fully responsive & resizable:** The user can change the tool window in **width and height** at any time (dock, drag narrow, float, make very short). The layout adapts **fluidly and without layout breakage** — no fixed heights that break it. Binding rules in §7.
4. **Keyboard first:** Enter = send, Shift+Enter = line break, `Ctrl+Esc` = focus in/out of composer (placeholder hints at it). All buttons focusable, visible focus ring.
5. **Streaming-capable:** Assistant text is appended token by token via `assistant.delta`; the UI must not re-layout on every delta (append to the existing text node, auto-scroll only when the user is at the bottom edge).
6. **Isolated testing:** When no host is present (`window.chrome?.webview` undefined), a **mock adapter** runs that simulates the host messages (empty state, one sample turn with streaming, a tool card and a permission/diff card). This way the UI is independently verifiable in the browser.
7. **Theme mode:** Dark / Light / **Auto** (follows the IDE theme). The source of truth is the host (see §8); the UI always renders the theme resolved by the host and can change the mode via `theme.setMode`.

---

## 3. Message contract (WebView2 ↔ C# host)

Every message: `{ "type": "<name>", ...payload }`. Identifiers in English.

### 3.1 host → web (rendered by the UI)

| type | Payload | Effect in the UI |
|---|---|---|
| `theme` | `{ mode: "dark"\|"light"\|"auto", resolved: "dark"\|"light", vars: { "--bg": "#…", … } }` | Sets `data-theme=resolved` + CSS variables on `:root`; reflects `mode` in the appearance selection. |
| `auth.state` | `{ loggedIn: bool, mode: "oauth"\|"apiKey"\|"none" }` | On `loggedIn:false` → login hint in the transcript area instead of the empty state. |
| `session.init` | `{ sessionId, title, model, effort, planMode, permissionMode, cwd, tokens, limits:{sessionPct, weeklyPct}, plan }` | Initial state: title, toolbar status, status bar. |
| `session.list` | `{ sessions: [{ id, title, updatedAt, preview }] }` | Populates the history panel. |
| `transcript.load` | `{ sessionId, title, messages: Message[] }` | Replaces the entire history (session switch). |
| `status` | `{ state: "ready"\|"working"\|"waiting-permission"\|"error", text? }` | Status bar indicator + possibly Send→Stop toggle. |
| `assistant.start` | `{ id }` | Creates an empty assistant block (with streaming caret). |
| `assistant.delta` | `{ id, text }` | Appends text to the block. |
| `assistant.end` | `{ id }` | Removes the caret, renders the markdown finally. |
| `tool.use` | `{ id, name, input, status:"running" }` | Tool card (e.g. Read/Grep/Bash). |
| `tool.result` | `{ id, status:"ok"\|"error", summary }` | Updates the tool card. |
| `permission.request` | `{ requestId, toolName, input, diff?:{ path, oldText, newText } }` | Permission/diff card with Approve/Reject (see §5.2). |
| `turn.result` | `{ sessionId, costUsd, tokens:{input,output,total}, durationMs, limits:{sessionPct,weeklyPct} }` | Turn end; updates tokens/limits. |
| `usage.update` | `{ tokens, sessionPct, weeklyPct }` | Pure status bar update (also outside of turns). |
| `error` | `{ message }` | Error block in the transcript + status `error`. |

`Message` object (for `transcript.load`):
`{ role: "user"|"assistant"|"system"|"tool"|"permission"|"error", id, text?, toolName?, input?, status?, diff?, ts }`

### 3.2 web → host (user actions)

| type | Payload | Trigger |
|---|---|---|
| `ready` | `{}` | UI loaded → host sends `theme`+`auth.state`+`session.init`. |
| `prompt.send` | `{ text, attachments? }` | Send button / Enter. |
| `turn.stop` | `{}` | Stop button during `working`. |
| `session.new` | `{}` | "New chat" button. |
| `session.listRequest` | `{}` | History button opened. |
| `session.load` | `{ sessionId }` | Click on a history entry. |
| `model.set` | `{ model }` | Model selection in the popover. |
| `effort.set` | `{ effort: "low"\|"medium"\|"high" }` | Effort selection. |
| `mode.set` | `{ planMode: bool }` | Plan-mode toggle. |
| `permission.set` | `{ mode: "ask"\|"acceptEdits"\|"plan"\|"bypass" }` | Permission selection. |
| `theme.setMode` | `{ mode: "dark"\|"light"\|"auto" }` | Appearance selection; host persists, resolves and replies with `theme`. |
| `permission.decision` | `{ requestId, behavior:"allow"\|"deny", updatedInput?, message? }` | Approve/Reject of a permission card. |
| `attach.files` / `attach.context` / `attach.browse` | `{}` | Entries in the `+` menu (host opens picker and returns the result as an attachment). |
| `slash.run` | `{ command, args? }` | Selection in the slash menu. |

---

## 4. Layout overview (component tree)

Vertical flex container, full height:

```
┌───────────────────────────────────────────────┐
│ Header  [Title ………………]        [🕘 History] [⊕]  │  ~48px, bottom border
├───────────────────────────────────────────────┤
│                                                 │
│  Transcript (scrollable, flex:1)                │
│   – Empty state: ✳ Claude Code / Robot / TODO   │
│   – or: history (user / assistant / tool /      │
│     permission / error)                         │
│                                                 │
├───────────────────────────────────────────────┤
│ Composer (rounded box, accent border)           │
│   ┌─────────────────────────────────────────┐   │
│   │ <textarea, multiline, auto-grow>         │   │
│   ├─────────────────────────────────────────┤   │
│   │ [+] [/]            [Model·Mode ▾]  [⬆]    │   │  inner toolbar
│   └─────────────────────────────────────────┘   │
├───────────────────────────────────────────────┤
│ Status bar  ● Ready · 0 tok · S 0% · W 0% · Plan│  ~28px, darker
└───────────────────────────────────────────────┘
```

Popovers (`+`, `/`, `Model·Mode`) and the history panel open as **overlapping, anchor-positioned layers** (no layout shift), close on outside click and `Esc`.

---

## 5. Component specification

### 5.1 Header
- **Title (left):** shows the `title` of the active session (default "Untitled"). Single-line, ellipsis on overflow. Click → renamable inline (sends `session.rename` later; in v1 optional, otherwise read-only).
- **History button (right, clock icon, round/ghost):** opens the **history panel** as a dropdown below the button. On open send `session.listRequest`. Panel lists `sessions` (title, relative timestamp, short preview). Click → `session.load` + close panel. Active session marked.
- **New-chat button (right, `⊕` round/ghost):** sends `session.new`. The UI immediately switches optimistically to the empty state with title "Untitled" (host confirms via `session.init`).
- **Settings button (right, gear icon, round/ghost):** a small, deliberate addition to the template. Opens an **appearance popover** with a segmented control **Dark / Light / Auto** → `theme.setMode`. Auto is the default. (Extensible with further UI preferences.) The authoritative setting lives host-side (VS *Tools → Options*); this popover is the convenient in-panel variant.

### 5.2 Transcript
- **Terminal feel:** monospace, dense lines, subtle role markers in the left gutter:
  - User: `›` (accent color), text slightly highlighted.
  - Assistant: `✳` (accent), normal foreground.
- **Empty state** (no messages, signed in): centered at the top the wordmark **"✳ Claude Code"** (serif + sparkle glyph in accent color); in the middle the **pixel robot** (inline SVG, accent color) and below it `// TODO: Everything. Let's start.` in muted comment gray.
- **Not-signed-in state** (`auth.state.loggedIn=false`): instead of the empty state a hint "Not signed in to Claude Code" + button "Sign in" that triggers `slash.run {command:"/login"}` (or a dedicated `auth.login` message). No own OAuth flow in the WebView.
- **Markdown rendering** for assistant text: paragraphs, lists, inline code, **code blocks** with:
  - Language label + **Copy** button + **Insert into editor** button (Insert sends `editor.insert {text}` to the host).
  - Own syntax highlighter without an external lib (lightweight; acceptable: just monospace + subtle token colors). External highlight libs only if embedded locally.
- **Tool cards** (`tool.use`/`tool.result`): compact line with icon, tool name, expandable/collapsible input/output, status spinner→checkmark/cross.
- **Permission/diff card** (`permission.request`) — core feature:
  - Header: tool (e.g. `Edit`) + file path.
  - If `diff` present: **inline diff** (old red / new green, line by line). Otherwise: input formatted as JSON.
  - Buttons **Approve** and **Reject**. Approve → `permission.decision {behavior:"allow"}` (optionally `updatedInput` if the user edited the diff). Reject → `{behavior:"deny", message}`.
  - While a card is open: status `waiting-permission`; the composer stays usable, but sending is blocked until a decision.
- **Auto-scroll:** only when the user is within ~40 px of the bottom edge; otherwise show a "Jump to latest" pill.

### 5.3 Composer
- **Textarea:** multiline, auto-grow from 1 line up to max ~40 % of the panel height, then scroll internally. Placeholder: `ctrl esc to focus or unfocus Claude`.
- **Keyboard:** Enter = `prompt.send` (when not empty and not `working`/`waiting-permission`); Shift+Enter = line break.
- **Attachments bar** (optional, above the textarea): chips for attached files/context with a remove X.
- **No mic icon** (no voice input).

### 5.4 Inner toolbar (in the composer)
Order exactly: **left** `+`, then `/` — **right** `Model·Mode`, then **Send**.

- **`+` (plus):** menu with entries analogous to the Claude Code VS Code extension:
  - *Add files…* → `attach.files`
  - *Add folder / context…* → `attach.context`
  - *Browse…* → `attach.browse`
  - (Extensible: `@`-file references, insert image.)
- **`/` (slash):** menu with slash commands (list from the host or static defaults: `/clear`, `/compact`, `/model`, `/init`, `/help`, …). Selection → `slash.run`. Bonus: if the user types `/` as the first character in the textarea, the same menu should appear as autocomplete.
- **`Model·Mode` button (one button, opens a popover):** the label shows the current state compactly, e.g. `Opus 4.8 · Ask`. Popover sections:
  1. **Model** — radio list (models supplied by the host, e.g. Opus 4.8 / Opus 4.7 / Sonnet 4.6 / Haiku 4.5) → `model.set`.
  2. **Effort / Thinking** — three levels low/medium/high → `effort.set`.
  3. **Plan Mode** — toggle → `mode.set {planMode}`.
  4. **Permission** — radio: *Ask before edits* / *Auto-accept edits* / *Plan* / *Bypass* → `permission.set`. (Default **Ask**, since the inline-diff flow is built exactly on that.)
- **Send button (`⬆`, far right, accent-color filled):** sends `prompt.send`. During `working`: becomes a **Stop** button (`turn.stop`). Disabled on empty input or `waiting-permission`.

### 5.5 Status bar (bottom)
A compact, dense line (darker than the rest). Contents left→right:
- **Status indicator:** colored dot + text — `ready` (green, "Ready"), `working` (accent/animated, "Working…"), `waiting-permission` (yellow, "Awaiting approval"), `error` (red, "Error").
- **Token usage of the current session:** e.g. `1,240 tokens` (from `tokens`).
- **Session limit %:** small meter + `S 12%` (from `limits.sessionPct`).
- **Weekly limit %:** small meter + `W 34%` (from `limits.weeklyPct`).
- **Plan badge** (right): `Team Plan` with a small bar icon (from `plan`).

At very narrow width: reduce labels to abbreviations (`S`/`W`), hide the plan badge first. Meters are display only; all values come from the host (`session.init`, `turn.result`, `usage.update`).

---

## 6. Visual design tokens

**Aesthetic:** refined-minimalist, "developer terminal", VS-native, *one* warm coral accent (Claude "clay"). No gradients, no decoration — precision in spacing/typography.

### Colors — Dark (default palette)
```
--bg:            #0f0f10   /* Transcript, near-black            */
--bg-chrome:     #1a1a1c   /* Header, Composer frame, status bar */
--bg-elevated:   #232325   /* Popovers, Cards, history panel     */
--border:        #2a2a2d
--border-subtle: #202023
--text:          #e6e6e6
--text-dim:      #8a8a8f
--text-faint:    #5f5f66
--comment:       #6f7d6a   /* TODO line / code comments          */
--accent:        #d97757   /* Claude clay – Sparkle, Send, Border */
--accent-hover:  #e08a6d
--accent-faint:  rgba(217,119,87,0.14)
--ok:            #6cc04a
--warn:          #d9a23f
--error:         #e0625a
--diff-add:      rgba(108,192,74,0.16)
--diff-del:      rgba(224,98,90,0.16)
```

### Colors — Light (default palette)
```
--bg:            #ffffff
--bg-chrome:     #f3f3f3
--bg-elevated:   #ffffff   /* + soft shadows instead of a lighter surface */
--border:        #e1e1e3
--border-subtle: #ededef
--text:          #1f1f1f
--text-dim:      #6a6a70
--text-faint:    #9a9aa0
--comment:       #4a7a3a
--accent:        #c15f3c   /* slightly deeper clay for contrast on white */
--accent-hover:  #a94f30
--accent-faint:  rgba(193,95,60,0.12)
--ok:            #2f8a2f
--warn:          #b8860b
--error:         #c0392b
--diff-add:      rgba(47,138,47,0.12)
--diff-del:      rgba(192,57,43,0.12)
```

### Theming mechanics
- Both palettes exist in the CSS, toggled via `:root[data-theme="dark"]` / `:root[data-theme="light"]`. Default on load: `dark`, until the first `theme` message arrives.
- The host **overrides** the variables via `theme.vars` to exactly match the VS theme colors (see §8). The bundled palettes are only a fallback and browser-test default.
- The accent color (Claude clay) is preserved in both themes; diff/status colors have matching tints per theme.

### Typography (only system-available fonts, no CDN)
- **Chrome/UI:** `"Segoe UI", system-ui, sans-serif`, 12–13 px.
- **Transcript/Code/Mono:** `"Cascadia Code", "Cascadia Mono", Consolas, monospace`, 12.5–13 px, line-height ~1.55.
- **Wordmark "Claude Code":** `Georgia, "Times New Roman", serif`, ~18 px, slightly tracked.

### Icons (inline SVG, stroke-based, ~16 px, `currentColor`)
- Clock (History), `⊕` (New), Plus, Slash-in-rounded-square, Chevron (popover), Arrow-up (Send), Stop square, Hand/shield (permission mode), small bar chart (Plan/Limits).
- **Sparkle ✳:** multi-ray star in `--accent` (Claude glyph).
- **Pixel robot:** inline SVG of rectangles in `--accent` (head with two side "ears"/antennas, two eyes, two legs) — like in the template.

### Form & spacing
- Radii: Composer box 10 px, buttons/cards 6–8 px, pills 999 px.
- Composer border at rest `--border`, on focus `--accent` + subtle glow (`box-shadow: 0 0 0 1px var(--accent-faint)`).
- Consistent 8-px spacing scale. Header/status bar with a thin `--border` divider.
- Micro-motion sparingly: message reveal (8 px slide-up + fade, ~140 ms), streaming caret blink, status-dot pulse on `working`. No excess.

---

## 7. Responsive behavior & scaling

The tool window is freely resizable (width **and** height, docked or floating). Binding rules:

- **Fluid structure:** root = flex column over 100 % height/width of the WebView. Header and status bar fixed, small height; composer grows by content; **Transcript = `flex:1` with `min-height:0`** and its own scroll. **Never** fixed px heights on transcript/container.
- **Container breakpoints (not viewport):** adaptations via CSS **container queries** on the root (WebView2/Chromium supports this fully). Levels:
  - **compact** (< 360 px): status bar only abbreviations (`S`/`W`), plan badge hidden; toolbar only icons; Model·Mode button shows only the model abbreviation.
  - **normal** (360–520 px): template layout.
  - **wide** (> 520 px): **limit the transcript text column to max. ~900 px and center it** (readability), chrome fills the full width; status bar with full labels.
- **Height adaptation:** with a low window, header + composer + status bar keep priority; transcript shrinks and scrolls. The `textarea` `max-height` is **relative to the current panel height** (≈ 40 %, min. 1 line) and is recalculated via a **`ResizeObserver` on the root** — **not** via `window.resize` (the WebView fills the pane, classic viewport events are unreliable). Below ~180 px height, reduce/hide the empty-state robot, keep the wordmark.
- **Popovers & history panel:** anchor-positioned, but **clamped within the viewport** (flip/shift); on resize reposition or close — never cut off.
- **Auto-scroll on resize:** if the user was at the bottom edge, scroll back to the end after resize.
- **Performance:** **debounce** `ResizeObserver` callbacks (~50 ms); bundle expensive recalculations (textarea height, possibly virtualization) in `requestAnimationFrame`; no layout-thrash loops.
- **Touch targets:** buttons ≥ 28×28 px at all levels.

---

## 8. Theme: Dark / Light / Auto

- **Three modes:** `dark`, `light`, `auto` (default **auto**), settable in the appearance popover (§5.1) → `theme.setMode`.
- **Auto follows the IDE theme — resolution host-side:** the extension reads the active VS theme and subscribes to `VSColorTheme.ThemeChanged`; on every change it sends a new `theme` message with `resolved` + matching `vars`. This way "Auto" follows even a runtime switch Dark↔Light of the IDE **without reload**.
- **Source of truth = host.** The chosen mode is persisted host-side (VS settings store / *Tools → Options*). The UI only changes it via `theme.setMode`, holds **no** divergent state of its own and always renders the `resolved`/`vars` delivered by the host.
- **Color mapping (host → §6 tokens):** the host maps VS `EnvironmentColors` to the variables (tool-window background → `--bg`/`--bg-chrome`, panel border → `--border`, foreground → `--text` etc.). The **Claude clay accent (`--accent`) stays fixed** (brand color) and is not taken from the VS theme.
- **UI application:** the `theme` message sets `:root[data-theme=resolved]` and then overrides the `vars` — a pure CSS-variable update, no reload, no flicker (optional ~120 ms transition on background/text).
- **Browser test (no host):** the mock adapter respects `prefers-color-scheme` for "Auto" and allows manual switching via the appearance popover.

---

## 9. States & edge cases
- **Cancel streaming:** Stop → `turn.stop`; the running assistant block stays with the text received so far, the caret is removed.
- **Long histories:** beyond many messages, recycle/virtualize off-screen nodes (at least mark as a TODO; v1 may use a simple limit).
- **Error turn:** `error` block + status bar `error`; re-enable the composer.
- **Permission timeout:** if the host withdraws an open card (`status` without `waiting-permission`), mark the card as "expired".
- **Resize:** full rules in §7.
- **Theme switch at runtime:** the `theme` message replaces `data-theme` + CSS variables without reload (§8).

---

## 10. v1 scope

**Included:** complete layout, all components and interactions named in §5, the message contract (§3) including the mock adapter for isolated testing.

**As stub / host-driven (do not solve in the WebView):** real CLI integration, auth flow, session persistence, actual limit calculation, file picker. The UI only calls the respective `web → host` messages.

**Out of scope v1:** voice input, inline editing of the diff before approve (provide a hook via `updatedInput`, UI optional), multi-tab.

---

## 11. Acceptance criteria (checklist)
- [ ] Layout matches the template at ~450 px width; clean at 320–520 px.
- [ ] Header: title left; History, New-chat and Settings buttons on the right functional.
- [ ] Empty state with wordmark, robot and TODO line exactly like the template (without mic).
- [ ] Transcript renders user/assistant/tool/permission/error; streaming appends fluidly; the auto-scroll rule applies.
- [ ] Code blocks with Copy + Insert button.
- [ ] Permission/diff card with inline diff and Approve/Reject → correct `permission.decision`.
- [ ] Composer multiline, auto-grow, Enter/Shift+Enter, Ctrl+Esc focus.
- [ ] Toolbar order `+ / … Model·Mode  ⬆`; all popovers/menus work; Send↔Stop toggles.
- [ ] Status bar shows status, session tokens, session % and weekly % (meters) + plan badge.
- [ ] **Responsive:** does not break at any width/height; container breakpoints compact/normal/wide apply; textarea `max-height` scales with the panel height via `ResizeObserver`; popovers stay within the viewport.
- [ ] **Theme:** Dark/Light/Auto switchable; the `theme` message sets `data-theme`+`vars` without reload/flicker; the accent color stays fixed in both themes.
- [ ] No `localStorage`/network usage; everything via the contract.
- [ ] The mock adapter allows a complete walkthrough without a host (incl. `prefers-color-scheme` for Auto).
