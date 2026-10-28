<div align="center">

<img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Resources/preview-200.png" alt="Code Astrogator" width="120" />

# Code Astrogator — Claude Code Chat

**Claude Code chat tool window for Visual Studio 2026**

Integrates the [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) into a dockable
chat panel — streaming responses, live tool and console output, interactive approve/reject for
Claude's edits and commands, checkpoints, and Remote Control from your phone, right inside the IDE.

<br />

<img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/streaming-turn.png" alt="Code Astrogator running a build with live console output inside Visual Studio 2026" width="400" />

</div>

---

## What it is

Code Astrogator brings Claude Code into Visual Studio as a dockable chat panel. It drives the
**Claude Code CLI** behind the scenes, so it works with whatever you already use to sign in:
**both a Claude subscription (OAuth) and an API key** are supported — the extension never handles
your credentials itself, and everything goes through the official `claude` binary.

## Features

**Chat & tools**
- **Streaming chat** with Markdown (incl. tables), code blocks with copy / insert-into-editor,
  "Copy as Markdown", and extended-thinking cards.
- **Tool cards** for Read / Grep / Bash / PowerShell / Edit / Write / MCP tools, with an open-in-VS button
  and success/error tinting. Subagents show their report and an "Agent finished · time · tokens" line.
- **Live console** — Bash and PowerShell commands stream their output into the card while they run,
  progress bars included. A **■ stop button** ends just that command; **Stop** ends the turn and every
  command it started.
- **Background tasks don't block you** — when Claude starts a log capture or a dev server in the
  background, the turn is done as soon as Claude has answered. A bar above the input lists what still
  runs (with a stop button each); your next message goes to the same session, so the task keeps running.
- **Prompt suggestions** — after a turn Claude Code predicts your likely next prompt; press **Tab** to take
  it over.
- **Interactive questions** — Claude's questions appear as cards with clickable options and a free-text
  "Other"; several questions are paged one at a time.

**Approvals & review**
- **Permission cards** — approve or reject file edits and commands inline, with a syntax-highlighted diff.
  An "Always" button and an **auto-approve pattern list** let trusted commands / MCP tools run silently.
- **Inline edit review in the editor (opt-in)** — review proposed edits *directly in the code editor*: red
  for deletions, green phantom lines for additions, **Accept / Reject per hunk**, scrollbar marks and a
  jump-to-change toolbar.
- **Review all edits at end of turn (opt-in)** — in Auto-accept mode Claude works without interruptions;
  afterwards the changed files are listed for review (Keep / Revert per change, or Keep all / Discard all).
  You can keep prompting meanwhile — further changes join the same list.
- **Permission modes** — Ask / Auto-accept edits (optionally auto-accept commands too) / Plan / Bypass.

**Safety net**
- **Checkpoints** — a snapshot before every prompt; hover any message to **rewind** code, conversation or
  both, file by file. Changes made by bash commands and scripts are covered too; your own git repository is
  never touched.

**Remote Control**
- Continue the current chat **from your phone or claude.ai/code**: a QR code and link appear at the top of
  the panel, the conversation below updates live, and "End remote session" brings it back. No console
  window — Claude Code runs the session in the background, so it keeps running even if you close Visual
  Studio.

**Context & usage**
- **Context meter** with a breakdown like the Context panel in the Claude app (messages, tools, MCP,
  memory files, skills, …) and a **Compact** button.
- **Usage meters** — session and weekly limits with reset times and a plan badge.
- **Turn footer** — duration, tokens and the cost of that turn.

**Everything else**
- **Model · Mode popover** — model (newest of each family first, older ones under "More models"),
  effort (Low … Max), ultracode and permission mode; your choices persist. New Claude models appear without
  an extension update.
- **Context attachments** — `@`-mention autocomplete, drag-and-drop from Explorer, clipboard image/file
  paste (a screenshot alone is a valid prompt), web browsing, the active editor file with the selected
  lines, and editor right-click → "Add file / selection to prompt".
- **Session history** per workspace; conversations resume across VS restarts.
- **Theming** — Dark / Light / Auto (follows VS), an accent color, three verbosity levels.
- **Automatic updates** (opt-in) from the GitHub releases, plus announcement banners.

## Screenshots

### Watch Claude work — and keep going while it runs

Commands stream their output live into the card (■ stops just that command). Long-running work like a log
capture keeps running in the background while you answer; tasks, background tasks and subagents are listed
above the input, and the footer shows duration, tokens and the cost of the turn.

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/streaming-turn.png" alt="PowerShell card with live console output and a stop button" width="370" />
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/background-tasks.png" alt="Finished turn with the task list and a running background task above the input" width="370" />
</p>

### Approve edits and answer questions

Edits and commands come as approval cards with a diff. Claude's questions are clickable cards — several at
once are paged, with a ✓ on every answered tab.

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/permission-diff.png" alt="Permission card with an inline diff and Approve/Reject buttons" width="370" />
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/interactive-question.png" alt="Paged question card with tabs and multi-select options" width="370" />
</p>

### Review in the editor

Review a proposed edit right in the code editor, hunk by hunk — or let Claude work in Auto-accept mode and
review everything it changed at the end of the turn.

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/inline-edit-review.png" alt="Inline edit review shown directly in the Visual Studio code editor" width="760" />
</p>
<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/turn-review.png" alt="List of changed files to review at the end of the turn" width="370" />
</p>

### Rewind and keep an eye on the context

Every prompt is a checkpoint: pick which files to roll back. The context meter shows how full the context
window is and what fills it, with a Compact button.

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/rewind.png" alt="Rewind dialog with the files changed since this message" width="370" />
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/context-popover.png" alt="Context breakdown popover with categories and a Compact button" width="370" />
</p>

### Remote Control

Scan the code with your phone (or open the link at claude.ai/code) and continue the chat there — what
happens remotely shows up live in the panel.

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/remote-control.png" alt="Remote Control bar with QR code and link above the live conversation" width="370" />
</p>

### Model, effort and permission mode

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/model-mode-popover.png" alt="Model and mode popover with the Claude 5 models, effort and Ask before edits" width="370" />
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/permission-modes.png" alt="Auto-accept edits with the Auto-accept commands and Review all edits at end of turn toggles" width="370" />
</p>

### Attach context and tune the look

<p align="center">
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/attach-menu.png" alt="Attachment menu: Add file, Add context, Browse the web" width="370" />
  <img src="https://raw.githubusercontent.com/finex7070/CodeAstrogator/main/Screenshots/appearance-popover.png" alt="Appearance popover with theme, verbosity and accent color" width="370" />
</p>

## Requirements

- **Visual Studio 2026** (Community, Professional, or Enterprise). Recommended target; the extension is
  built and tested against VS 2026.
- The **[Claude Code CLI](https://docs.claude.com/en/docs/claude-code)** installed and on your `PATH`
  (or point to it in settings) and signed in:
  - **Subscription:** run `claude /login`.
  - **API key:** sign in with your Anthropic API key.
- A recent CLI is recommended. A few features need a newer one and simply stay off with an older CLI:
  prompt suggestions (2.1.28x+) and Remote Control without a console window (2.1.29x+; older versions open
  a console instead).

## Installation

1. In Visual Studio, open **Extensions → Manage Extensions**, search for **Code Astrogator**, and
   click **Install** (or install this page's download and double-click the `.vsix`).
2. **Restart Visual Studio** to complete the installation.
3. Show the panel via **View → Other Windows → Code Astrogator**.

## Getting started

1. Make sure the Claude Code CLI is signed in (see **Requirements**). If not, the panel
   shows a sign-in hint.
2. Type a prompt and press **Enter** (**Shift+Enter** for a new line). Attachments alone — e.g. a pasted
   screenshot — can be sent too.
3. When Claude wants to edit a file or run a command, an approval card appears — **Approve** or
   **Reject** (or switch to Auto-accept in **Model · Mode**).
4. Use the toolbar to attach files (`+`), run slash commands (`/`), or change model and permission mode
   (**Model · Mode**). The broadcast icon in the header starts Remote Control, the clock opens the history.

The working directory of each turn is your open solution/folder, so Claude has your project context.

## Settings

Open settings via the **gear icon → "Advanced options…"**. Among others:

- The Claude CLI path (if it isn't on `PATH`), default model / effort, theme, and verbosity.
- Permission mode, the auto-approve pattern list, inline edit review and "Review all edits at end of turn".
- Checkpoints (on/off, what goes into a snapshot, retention) and chat-history retention.
- Prompt suggestions, prompt timeout, restore-last-session, auto-add active file / selected lines.
- Announcements, update notifications and automatic updates, and keeping the model list up to date from
  the project's GitHub — each with a "Check now" button.

Quick appearance options (theme, verbosity, accent color) are also available directly in the gear popover.

## Release notes

See the [changelog](https://github.com/finex7070/CodeAstrogator/blob/main/CHANGELOG.md) for the version history.

## License

GNU Affero General Public License v3.0 (AGPLv3). This project is open-source.

- ✅ **Commercial use** — free to use in a paid development environment or for client projects.
- ✅ **Modification** — free to modify the code to suit your needs.
- 🔄 **Copyleft** — if you distribute a modified version, you must release your modifications under the
  same AGPLv3 license. Closed-source proprietary forks are not allowed.

See the [project repository](https://github.com/finex7070/CodeAstrogator) for full details.

---

> "Claude" and "Claude Code" are products of Anthropic; this extension integrates the Claude Code CLI
> but is not affiliated with or endorsed by Anthropic.
