<div align="center">

<img src="Resources/preview-200.png" alt="Code Astrogator" width="120" />

# Code Astrogator

**Claude Code chat tool window for Visual Studio 2026**

Integrates the [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) into a dockable
chat panel — streaming responses, live tool activity, and interactive approve/reject for
Claude's edits and commands, right inside the IDE.

Created by Jan Hüls "finex7070" StickyStoneStudio GmbH 🚀

</div>

---

## What it is

Code Astrogator brings Claude Code into Visual Studio as a dockable chat panel. It drives the
**Claude Code CLI** behind the scenes, so it works with whatever you already use to sign in:
**both a Claude subscription (OAuth) and an API key** are supported — the extension never handles
your credentials itself.

## Features

- **Streaming chat** with Markdown (incl. tables), code blocks with copy / insert-into-editor, and
  extended-thinking display.
- **Tool activity cards** for Read / Grep / Bash / Edit / Write / MCP tools, with collapsible
  input/output and success/error tinting.
- **Permission cards** — review and approve or reject Claude's file edits and commands; auto-approved
  edits show as a green pre-decided card. *(An inline diff shown directly in the editor is planned.)*
- **Auto-approve patterns** — allow chosen commands/MCP tools to run without prompting, plus an
  "Always" button on permission cards.
- **Interactive questions** — Claude's follow-up questions appear as in-turn cards with clickable
  options and a free-text "Other".
- **Model · Mode popover** — pick model, effort, ultracode, and permission mode (Ask / Auto-accept
  edits / Plan / Bypass); your choices persist across chats and restarts.
- **Usage meters** — session and weekly usage with reset-time tooltips and a plan badge (read locally,
  no extra cost).
- **Session history** per workspace; conversations resume across VS restarts.
- **Context attachments** — `@`-mention autocomplete, file drag-and-drop from Explorer, clipboard
  image/file paste, an active-file reference (with selected line ranges), and editor right-click →
  "Add file / selection to Claude prompt".
- **Remote control** — broadcast a session to a QR-code/link, then import it back when you stop.
- **Theming** — Dark / Light / Auto (follows the VS theme) and a configurable accent color.

## Requirements

- **Visual Studio 2026** (Community, Professional, or Enterprise).
- The **[Claude Code CLI](https://docs.claude.com/en/docs/claude-code)** installed and on your `PATH`
  (or point to it in settings) and signed in:
  - **Subscription:** run `claude /login`.
  - **API key:** sign in with your Anthropic API key.

## Installation

1. Download the latest **`CodeAstrogator.vsix`** from the
   [**Releases**](https://github.com/finex7070/CodeAstrogator/releases) page.
2. **Close Visual Studio**, then double-click the `.vsix` to install (it over-installs any previous
   version).
3. Reopen Visual Studio and show the panel via **View → Other Windows → Code Astrogator**.

## Getting started

1. Make sure the Claude Code CLI is signed in (see [Requirements](#requirements)). If not, the panel
   shows a sign-in hint.
2. Type a prompt and press **Enter** (Shift+Enter for a new line).
3. When Claude wants to edit a file or run a command, an approval card appears — review it and
   **Approve** or **Reject**.
4. Use the toolbar to attach files (`+`), run slash commands (`/`), or change model and permission
   mode (**Model · Mode**).

The working directory of each turn is your open solution/folder, so Claude has your project context.

## Settings

Open settings via the **gear icon → "Advanced options…"**. From there you can configure, among others:

- The Claude CLI path (if it isn't on `PATH`), default model / effort, theme, and verbosity.
- Permission mode and the auto-approve pattern list.
- Restore-last-session, auto-add active file, include selected lines.
- Prompt timeout, and whether to use a persistent CLI session.
- Whether to receive announcements and update notifications.

Quick appearance options (theme, accent color) are also available directly in the gear popover.

## Release notes

See [`CHANGELOG.md`](CHANGELOG.md) for the version history.

## License

GNU Affero General Public License v3.0 (AGPLv3)

This project is open-source and available under the AGPLv3 license.

✅ Commercial Use: You are free to use this extension for commercial work (e.g., in a paid development environment or for client projects).
✅ Modification: You are free to modify the code to suit your needs.
🔄 Copyleft: If you distribute a modified version — or make one available to others — you must release your modifications under the same AGPLv3 license. Closed-source proprietary forks are not allowed.

See the [LICENSE](LICENSE) file for full details.

> "Claude" and "Claude Code" are products of Anthropic; this extension integrates the Claude Code CLI but is not affiliated with or endorsed by Anthropic.

---

> Made with ❤️ by Jan Hüls "finex7070" [StickyStoneStudio GmbH](https://www.stickystonestudio.com)
