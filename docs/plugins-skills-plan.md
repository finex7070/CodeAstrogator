# Plan: Plugin, skill & MCP server management in the extension

> **Status 2026-11-10: all five phases done; shipped as 1.3.0 (not yet seen inside VS by the author).** Facts below were checked against
> CLI **2.1.319** the same day.
> Once built, the current state goes to `docs/NOTES.md`; this document stays as the design rationale.

## Goal
Manage Claude Code **plugins** (installed and from marketplaces), **marketplaces** and **skills** from inside
Visual Studio, without a terminal: see what is installed and what it costs in context, switch things on and
off, install / update / remove, and open or create skills.

## Ground rules
- **Everything through the official `claude` binary** (project rule): plugin and marketplace changes run
  `claude plugin …` as hidden child processes (like `ClaudeUsageClient`: `CreateNoWindow`, stdin closed,
  timeout). No direct writes into `~/.claude/plugins/**`.
- **The one exception: skill on/off.** There is no CLI command for it; the setting `skillOverrides` lives in
  the settings files, so the extension edits that one key (see "Skills").
- **Changes apply to the next turn by themselves:** the extension starts one CLI process per turn, so a new
  turn loads the new plugin set. A process kept alive for background tasks keeps the old set until it ends
  (say so in the UI).

## What the CLI offers (verified 2026-11-06, CLI 2.1.319)
| Need | Command | Machine-readable |
|---|---|---|
| Installed plugins | `claude plugin list --json` | array of `{ id, version, scope, enabled, installPath, installedAt, lastUpdated, projectEnabled }` |
| Marketplace catalog | `claude plugin list --available --json` | `{ installed: […], available: [{ pluginId, name, description, marketplaceName, source, installCount }] }` — **3924 entries** here |
| Install | `claude plugin install <plugin>[@marketplace] --json [-s user\|project\|local] [--config k=v]` | one result line |
| Enable / disable | `claude plugin enable\|disable <plugin> --json [-s scope]` | `{ command, outcome, plugin, message, failureCode?, alreadyInGoalState? }` (measured) |
| Update | `claude plugin update <plugin> --json [-s scope]` | one result line |
| Uninstall | `claude plugin uninstall <plugin> --json [-s scope] [--keep-data]` | one result line |
| Inventory + context cost | `claude plugin details <name>` | **text only** (skills/agents/hooks/MCP/LSP counts, always-on tokens, per-component table) |
| Plugin options | `claude plugin configure <plugin>` (`--values-stdin` to save) | — to check |
| Marketplaces | `claude plugin marketplace list --json`, `add <url\|path\|github>`, `remove`, `update [name]` | list is JSON |
| Skills of the session | `system/init` → `skills`, `slash_commands`, `plugins`, `plugin_warnings` | already parsed per turn |

**Phase 1 — verified 2026-11-06** (sandbox with `CLAUDE_CONFIG_DIR` pointed at a temp folder, so the
user's real config stayed untouched; plus the docs: settings, skills, plugins/cli-reference):
- `--json` = **the last stdout line** is one object; always `command`, `outcome` (`ok`|`failed`), `message`;
  when they apply `pluginId`, `scope`, `failureCode`. Measured per command:
  - install → `{command:"install", outcome:"ok", plugin, pluginId, scope}`
  - enable/disable → same; already in that state → `outcome:"failed"`, `failureCode:"already_in_goal_state"`,
    `alreadyInGoalState:true` (**treat as success**, exit code 0)
  - update → adds `updateOutcome` (`up_to_date` …), `oldVersion`, `newVersion`
  - uninstall → adds `keptData`; failure `failureCode:"settings_still_on"`
  - marketplace add/update/remove → `{command:"marketplace-add|update|remove", outcome, marketplace, message}`
    (`failureCode` e.g. `invalid_source`); `update` without a name refuses `--json`
- Exit codes: 0 success, 1 failure; a **usage error prints no result line**.
- `list --json` entries: always `id, version, scope, enabled, installPath`; when they apply `projectPath`,
  `projectEnabled`, `installedAt`, `lastUpdated`, `mcpServers`, `errors`, `notes`, `hasUserConfig`, `dataDirSize`.
  `--available` items: `pluginId, name, marketplaceName, source, description, version, installCount`.
- **Scope → file:** user `~/.claude/settings.json`; project `<workspace>/.claude/settings.json` (measured:
  `enabledPlugins` entry written there, `list` then shows `scope:"project"`, `projectPath`); local
  `<workspace>/.claude/settings.local.json` (measured: a local `disable` writes `false` there); `managed` only for
  `update`. Project/local commands need the **workspace as working directory**.
- `details` has **no JSON form** (text: inventory, "Always-on: ~N tok", per-component table) → parse with a
  raw-text fallback.
- `configure <name@marketplace> --json`, `--values-stdin` = JSON object of single-line strings; "Restart to apply".
- **Non-interactive installs:** without a TTY and without `-y` / `--accept-command`, an install that would show a
  marketplace command is refused (exit 1) with `shownCommand` (incl. `sha256`, `acceptCommandMatched`). A plain
  install (no command) needs neither (measured).
- **When changes apply:** the docs say "next session or `/reload-plugins`" — for us every turn is a new process,
  so the next prompt. Skill file edits are picked up without a restart.

**Marketplace-declared commands** (an install that runs a command, or a `headersHelper` that fetches the
archive): with `--json` the CLI reports the command and its `shownCommand.sha256` and **refuses until a person
has seen it**; the second run passes `--accept-command <sha256>`. The extension must show that command verbatim
and only pass the hash after an explicit click — never auto-accept.

## Skills
- **Where they come from:** user skills `~/.claude/skills/<name>/SKILL.md`, project skills
  `<workspace>/.claude/skills/<name>/SKILL.md`, skills inside plugins (`<installPath>/skills/…`), and the
  CLI's bundled skills. Name + description come from the `SKILL.md` front matter.
- **Visibility (verified in the docs 2026-11-06):** setting **`skillOverrides`** `{ "<skill name>": value }`,
  values **`on`** (default for a skill not in the map: name + description listed to Claude, in the `/` menu),
  **`name-only`** (Claude sees only the name), **`user-invocable-only`** (hidden from Claude, still in the `/`
  menu; the CLI's `/skills` menu labels it "user-only"), **`off`** (hidden from Claude and the `/` menu). Valid in
  any settings file; the CLI's own `/skills` menu writes to **`.claude/settings.local.json`** — the extension does
  the same by default (user file as an option). Entries match skill **names** only.
- **Plugin skills are not covered by `skillOverrides`** — they go on/off with their plugin (Installed tab).
- **Bundled skills:** `disableBundledSkills: true` (or env `CLAUDE_CODE_DISABLE_BUNDLED_SKILLS=1`) turns all of
  them off at once — one switch in the Skills tab, no per-skill control.
- The extension reads all settings files to show the effective state and writes only the `skillOverrides` key of
  the chosen file, keeping everything else as it was (read-modify-write right before saving).
- **Discovery (docs):** personal `~/.claude/skills/<name>/SKILL.md`; project `.claude/skills/…` from the start
  folder **and every parent up to the repo root**; nested `<subdir>/.claude/skills/…` (load lazily); plugin
  `<plugin>/skills/<name>/SKILL.md` (invoked as `/plugin:skill`); claude.ai-synced copies in
  `~/.claude/skills/synced/`; legacy `.claude/commands/*.md`. Same name: enterprise > personal > project. Front
  matter: `name` (defaults to the folder), `description`, `when_to_use`, `disable-model-invocation`,
  `user-invocable`, `allowed-tools`, `model`, `effort`, `context`, … (unknown fields ignored). The list the session
  really loaded comes from `system/init` → `skills`; the folder scan adds paths and descriptions.
- **Open / create / delete:** open `SKILL.md` in the VS editor; "New skill" creates the folder + a `SKILL.md`
  template in the user or project skills folder (or `claude plugin init <name>`, which scaffolds a plugin under
  `~/.claude/skills/<name>/` that loads as `<name>@skills-dir` — decide which); delete only user/project skills,
  with a confirmation, never plugin or bundled ones.

## MCP servers (added to the scope 2026-11-06, user decision; verified against CLI 2.1.319 in a sandbox)
- **Commands:** `claude mcp add [-s local|user|project] [-t stdio|sse|http] [-e K=V…] [-H "K: V"…]
  [--client-id …] <name> <commandOrUrl> [args…]`, `add-json [-s …] <name> <json>`, `remove [-s …] <name>`,
  `get <name>`, `list`, `login <name> [--no-browser]` / `logout <name>` (OAuth for HTTP/SSE servers and claude.ai
  connectors), `reset-project-choices`. **No `--json` anywhere** — text output, exit 0/1 (unknown name → exit 1,
  "No MCP server named …").
- **Where the config lives** (measured): user → `~/.claude.json` `mcpServers`; local → `~/.claude.json`
  `projects["<workspace, forward slashes>"].mcpServers`; project → `<workspace>/.mcp.json` `mcpServers`.
  Entry shape `{ type, command, args, env }` / `{ type, url, headers }`.
- **Listing = read the files (structured) + `claude mcp list` for the live status.** `list` health-checks
  every approved server (starts stdio servers briefly — ~4 s here) and prints one line per server:
  `<name>: <command or url> [(HTTP)] - <status>` with status `✔ Connected`, `✘ Failed to connect — <reason>`,
  `⏸ Pending approval (run \`claude\` to approve)`, (and an auth-needed state to capture). Servers from plugins
  appear as `plugin:<plugin>:<server>`, claude.ai connectors as `claude.ai <name>` — shown, but managed by their
  plugin / on claude.ai (read-only rows).
- **Secrets:** `add` echoes headers as `[REDACTED]`, but **`get` prints header values in clear text** and the
  config files hold env values/headers verbatim → the panel shows names only and masks every value; values are
  entered in a password-style field and only passed on the command line.
- **Project `.mcp.json` servers need approval** ("Pending approval") — and our `-p` turns don't connect to
  unapproved ones. The panel offers Approve/Reject by writing the documented settings `enabledMcpjsonServers` /
  `disabledMcpjsonServers` (or `enableAllProjectMcpServers`) into `.claude/settings.local.json` — **verify the key
  names against the settings docs before building**; `reset-project-choices` undoes all choices.
- **Tab "MCP servers"** in the panel: list with scope, transport, status dot, error reason; Add (stdio: command +
  args + env; HTTP/SSE: URL + headers; scope choice, default **local** like the CLI), Remove, Log in / Log out,
  Recheck (re-runs `list`), Approve/Reject for pending project servers.

## UI (WebUI)
A **"Plugins, skills & MCP"** panel (overlay like the history panel), opened from the gear popover and from the
`/` menu. Five tabs (the fifth, **MCP servers**, is described in its own section):

1. **Installed:** each plugin with version, scope, on/off switch, "always-on" token cost (from `details`),
   expandable inventory (skills, agents, hooks, MCP servers), Update, Remove (confirm; option "keep data").
   `plugin_warnings` from the last `system/init` shown on the affected plugin.
2. **Discover:** the marketplace catalog with search (name/description), marketplace filter, sorted by
   `installCount`; Install with a scope choice (default **user**). Installed ones are marked.
3. **Skills:** grouped by source (yours · this project · plugins · built-in), with the effective state
   (on / only when invoked / off) and where it comes from; switch, open, new, delete.
4. **Marketplaces:** list, add (URL / path / GitHub repo), update, remove.

Every action shows its progress on the row (installs clone git repos — can take a while) and the CLI's
`message` on failure. After a change: "Applies from the next prompt"; the slash menu refreshes with the next
`system/init`.

**Warnings shown before acting:**
- Installing a plugin runs third-party code on this machine (hooks, MCP servers, scripts).
- Scope **project** writes into the workspace's `.claude/settings.json` — usually committed and shared with the
  team; **local** stays on this machine.

## Architecture
- **`Core/ClaudePluginClient`** (UI-free, tested): runs the commands, parses the JSON lines (list, available,
  result line incl. `failureCode` / `alreadyInGoalState` / `shownCommand`), parses the `details` text into
  counts + token figures; timeouts per command (list short, install/update long); cancellation.
- **`Core/SkillCatalog`** (UI-free, tested): scans the skill folders, parses front matter, reads
  `skillOverrides` from the three settings files and computes the effective state, writes one override.
- **Bridge:** web→host `plugins.list`, `plugins.available`, `plugin.action { action, id, scope, keepData?,
  acceptCommand? }`, `marketplaces.list`, `marketplace.action { action, source|name }`, `skills.list`,
  `skill.setOverride { name, value, scope }`, `skill.open`, `skill.create`, `skill.delete`; host→web the matching
  `*.result` messages with per-row progress. The catalog (3924 entries) is sent once per panel open and cached
  for the session; the search runs in the page.
- Hidden processes reuse the `ClaudeUsageClient` pattern (no console flash).

## Phases
1. ~~**Verify**~~ — **done 2026-11-06** (see "Phase 1 — verified" above). Still unseen: a real `shownCommand`
   refusal (needs a marketplace with a command-source plugin) — build it from the documented fields.
2. ~~**Core**~~ — **done 2026-11-06:** `Core/CliCommandRunner` (hidden process, stdout/stderr apart — on a
   failure the human line goes to stderr *after* the JSON line —, optional stdin, tree-kill on timeout),
   `Core/ClaudePluginClient` (list/available/marketplaces/details + install/enable/disable/update/uninstall/
   marketplace add/update/remove; `ParseResultLine` takes the last JSON line), `Core/SkillCatalog` (scan,
   front matter, `skillOverrides` + `disableBundledSkills` read/write, create/delete), `Core/ClaudeSettingsFiles`
   (scope → file, read-modify-write, refuses to overwrite unreadable JSON), `Core/ClaudeMcpClient` (files +
   `mcp list` status, add stdio/remote, remove). 26 tests (`ClaudePluginClientTests`, `SkillCatalogTests`,
   `ClaudeMcpClientTests`); read-only smoke test against the real CLI 2.1.319 (5 installed, 3924 available, 4
   marketplaces, `details` → 491 always-on tokens / 7 skills, 3 MCP servers connected). Found on the way:
   - **Through a .NET pipe the CLI prints console fallback symbols** (`√ Connected`, not `✔`) — the MCP status
     is therefore recognised by its word, not its symbol.
   - `plugin list` also reports **`scope: "synced"`** (plugins synced from claude.ai) — show them read-only.
3. ~~**Read-only panel**~~ — **done 2026-11-10:** gear popover → "Plugins, skills & MCP…" opens a sheet over the
   chat (`.manage-sheet` in `#overlay-layer`; Esc closes it unless a popover is open above it) with five tabs —
   **Installed** (scope + on/off badges, row expands to `details`: always-on tokens, inventory, dates; CLI
   errors per plugin in red), **Discover** (search + marketplace filter over the whole catalog, sorted by installs,
   60 rows per "Show more", "Installed" badge), **Skills** (grouped personal / project / plugin / synced / bundled,
   state badge with its scope, "not loaded" once a turn reported `system/init` → `skills`, open `SKILL.md` via the
   existing `editor.openFile`), **MCP servers** (status dot + badge, scope, transport, command/URL, env/header
   **names only**, failure reason; loading says each server is started briefly), **Marketplaces**. Each tab
   loads on first view, ↻ reloads it. Host: `Bridge/ManagementPanel` (one in-flight load per tab, off the UI
   thread). `system/init` → `skills` is now parsed (`SessionInitEvent.Skills`). Mock adapter answers every
   `manage.*` request with fictional SampleShop data. Verified in the browser (mock): all tabs, details, search,
   Esc, status colours; build + 438 tests green. **Not yet seen inside VS.**
4. ~~**Actions**~~ — **done 2026-11-10:** web→host `manage.action { requestId, action, … }`, host→web
   `manage.actionResult { requestId, action, ok, message, confirm?: { command, sha256 }, reload: [tabs], openPath? }`.
   Actions run **one at a time** (`SemaphoreSlim` — they write the same settings files). Plugins: switch on/off in the
   plugin's own scope (synced/managed read-only), Update, Remove… (keep or delete its data), Install… from Discover
   (scope choice, third-party-code + project-scope warning; a `shownCommand` comes back as `confirm` → dialog with
   the command verbatim → re-run with `acceptSha256`). Marketplaces: Add… (source + scope), Update, Remove…. Skills:
   state dropdown (`SkillCatalog.SetState` — `on` removes the entry, written explicitly only when another scope still
   hides it), save scope local (default, like the CLI's `/skills`) or user, built-in-skills switch, New skill…
   (personal/project, opens the new `SKILL.md`), Delete. MCP: Add server… (stdio: command line split like a shell,
   `KEY=value` env lines; HTTP/SSE: URL, `Name: value` headers; scope, default local), Remove…, **Approve/Reject**
   for project servers (`ClaudeMcpClient.SetProjectApproval` → `.claude/settings.local.json`
   `enabledMcpjsonServers`/`disabledMcpjsonServers`, verified in the settings docs). **Docs finding:** `claude -p`
   turns load project servers *without asking*, so for the extension's own turns only Reject changes anything — the
   tab says so. `mcp login` needs an interactive terminal (pasting the redirect URL) → a "needs sign-in" server only
   gets the hint `claude mcp login <name>`.
   **Verified:** browser (mock: every dialog, validation, the command-confirmation flow, busy states, Esc closes a
   dialog before the sheet); **real CLI in a sandbox** (`CLAUDE_CONFIG_DIR` → temp): marketplace add/remove,
   install/disable/enable/"already enabled"/update/uninstall(keep data) in local scope, MCP stdio with env and
   `--` arguments, HTTP with a header in project scope, remove, remove of an unknown name — all with exactly the
   arguments the clients build; the user's real config untouched. 440 tests. **Not yet seen inside VS.**
5. ~~**Docs & release**~~ — **done 2026-11-10:** NOTES section "Plugins, skills & MCP panel" (current state +
   pitfalls) + contract additions + code map, CLAUDE.md pitfall, CHANGELOG 1.3.0, README + marketplace-overview
   (screenshot section, features group, plus the missing 1.2.0 notification/queue bullets), screenshots
   `Screenshots/plugins-panel.png` + `mcp-servers.png` (560×800, mock with SampleShop data). The release itself
   (merge to `main`, GitHub release, Marketplace) is the user's.

## Risks
- **CLI churn:** `plugin` output formats are not a documented API either; capture fixtures and re-test on CLI
  updates (like the other CLI-text parsers).
- **`details` is text:** the token-cost figures come from parsing a table — fragile; show the raw text as a
  fallback.
- **Editing settings files** for `skillOverrides`: other tools (the CLI itself, the user) write the same files —
  read-modify-write right before saving, keep the user's formatting as far as possible, never touch other keys.
- **Long-running installs** and network failures: progress + cancel, no frozen panel.

## Decisions (2026-11-06)
- **Comes before the tabs** (user decision): this is the next feature release; the tabs plan
  (`docs/multi-session-tabs-plan.md`) follows after it.
- **All three scopes** (user / project / local) as the CLI offers them; default **user**, a warning on
  **project** (shared via the repo).
- **MCP servers** (`claude mcp …`) are **in the first version** (user decision, same day) — see "MCP servers".
- **New skill** = a plain `<name>/SKILL.md` folder (the standard layout), not `claude plugin init`.
