# CodeAstrogator — Claude Code Chat für Visual Studio 2026

VSIX-Extension (VSSDK in-process, net472, ein Projekt): Chat-Tool-Window auf WebView2-Basis,
das die **Claude Code CLI** integriert (`claude -p --output-format stream-json`, ein Prozess
pro Turn, Prompt via stdin). Optional opt-in: **persistenter bidirektionaler Modus**
(`--input-format stream-json`, ein langlebiger Prozess) und **MCP-Permission-Hook**
(`--permission-prompt-tool` → in-process Localhost-MCP-Server für interaktive Diff-Approvals).

## Pflichtlektüre vor Änderungen
- `docs/claude-vs-2026-plan.md` — verbindliche Spec (Teil A Architektur, Teil B UI-Kontrakt).
- `docs/NOTES.md` — **Ist-Stand, alle Abweichungen, Kontrakt-Ergänzungen, offene Punkte,
  Build-/Install-Kommandos.** Bei jeder inhaltlichen Änderung mitpflegen!
- **Beide `docs/`-Dateien werden auf Englisch geschrieben** (wie das Changelog; diese `CLAUDE.md`
  bleibt deutsch).
- `CHANGELOG.md` — **Bei jeder inhaltlichen Änderung einen neuen Eintrag ergänzen** (gleiche
  Version wie die Versionsanpassung im Manifest; Format: `## [x.y.z] – YYYY-MM-DD` mit
  Added/Changed/Fixed-Abschnitten). Kein Eintrag ohne Versionsbump und umgekehrt.
  **Die Changelog-Einträge werden auf Englisch geschrieben** (auch wenn die übrige Projektdoku
  deutsch ist).

## Build & Test (NUR VS-MSBuild — `dotnet build` kann die VSIX-Targets nicht)
```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CodeAstrogator.slnx /t:Restore,Build /p:Configuration=Release /m /v:m
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" CodeAstrogator.Tests\bin\Debug\net472\CodeAstrogator.Tests.dll
node --check WebUI\app.js   # nach jeder JS-Änderung (analog für WebUI\qr.js)
```
Install: VS schließen → `bin\Release\net472\CodeAstrogator.vsix` doppelklicken.
UI isoliert testen: `WebUI\index.html` im Browser (Mock-Adapter simuliert komplette Turns).

## Versionierung (Version lebt in `source.extension.vsixmanifest`, `<Identity … Version="x.y.z">`)
**NICHT automatisch hochzählen.** Wenn Arbeit geleistet wurde, am Ende des Turns den User per
**interaktiver Frage** (AskUserQuestion) fragen, ob/wie die Version steigen soll — Optionen:
- **Dont change** — Version bleibt.
- **Patch** (+0.0.1).
- **Minor** (+0.1.0; Patch → 0) — für größere Änderungen / neue Features.
- (Freitext via „Other"/TUI für eine eigene Eingabe, z. B. Major `x.0.0`.)

Erst nach der Antwort die Version im selben/nächsten Turn editieren und in der Zusammenfassung nennen.
Sparsam bleiben — der User fand häufige Minor-/Patch-Bumps zu viel.

**Vor** den beiden Fragen **immer die geplante Commit-Message in den Chat-Verlauf schreiben**
(als Vorschau, damit der User sie sieht) — **danach** die zwei Fragen stellen.

**Direkt zusammen mit der Versionsfrage** (idealerweise im selben `AskUserQuestion`-Aufruf als
zweite Frage) **immer auch fragen, ob alle aktuellen Änderungen committet und gepusht werden
sollen** — Optionen z. B. **Commit + Push**, **Nur Commit**, **Nein**. Bei „Ja": auf dem
Branch **`develope`** committen (nicht direkt auf `main`; Branch anlegen, falls er noch nicht
existiert), Commit-Message mit der `Co-Authored-By: Claude …`-Zeile abschließen, dann gemäß
Antwort pushen. Erst nach der Antwort ausführen.

**Branch-Modell (verbindlich):** `main` = immer der Stand des **aktuell veröffentlichten Release**;
`develope` = **alle laufenden Änderungen** (Versions-Bumps + Changelog-Einträge sammeln sich hier).
Der **User** mergt `develope → main` und veröffentlicht das Release, **wenn er es für reif hält** —
**niemals eigenmächtig nach `main` mergen oder ein Release veröffentlichen.** Nicht ungefragt einen
PR anlegen; nur auf ausdrücklichen Wunsch.

## Architektur in einem Satz je Schicht
`WebUI/` (single-page, dependency-frei, §3-Nachrichten-Kontrakt) ↔ `Bridge/WebViewBridge.cs`
(Kontrakt host-seitig, UI-Thread-Marshaling) → `Core/` (UI-frei + getestet: NdjsonParser,
ClaudeSessionService, ClaudeCliProcessHost + **ClaudePersistentProcessHost** (opt-in),
**McpPermissionBridge** (`IPermissionBridge`, in-process MCP-Server), UsageClient, FileLister,
RemoteControlHost, CliSessionReader) — daneben `Services/`
(Theme, Session-Persistenz, **`AstrogatorSettingsStore`** = WritableSettingsStore-Wrapper),
`Options/` (`AstrogatorOptions`-Snapshot), `ToolWindows/` (WebView2-Pane +
**`AstrogatorSettingsWindow`** = gehostetes Settings-Fenster via Zahnrad → „Advanced options…").

## Harte Regeln / No-Gos (Sicherheit & ToS)
- **Den OAuth-Token aus `~/.claude/.credentials.json` abzugreifen und für eigene
  HTTP-Requests zu verwenden ist ein absolutes No-Go.** Ebenso **keine** direkten Calls an
  interne Endpunkte wie `GET api.anthropic.com/api/oauth/usage` oder andere `/api/oauth/*`.
  Grund: Verstoß gegen die Anthropic Consumer-ToS (die Tokens sind exklusiv für Claude
  Code / Claude.ai), seit Anfang 2026 serverseitig geblockt, **Account-Ban-Risiko**. Auch
  kein Spoofing des Claude-Code-Harness (User-Agent, Client-ID etc.).
- **Sämtliche Anthropic-Kommunikation läuft ausschließlich über das offizielle `claude`-Binary
  als Subprozess.** Usage-/Limit-Daten kommen aus `claude -p /usage` (s. `ClaudeUsageClient`),
  **nicht** aus dem Token. Das frühere Token-Scraping wurde in 0.3.7 bewusst entfernt — nicht
  wieder einführen. Das Plan-Label darf weiterhin aus `~/.claude.json`
  (`oauthAccount.organizationType`) gelesen werden (kein Secret, keine Netzwerk-Calls).
- **Statusline-Hook als Usage-Quelle ist für dieses VSIX nicht nutzbar** (feuert nicht für
  headless `-p`-Turns — 3× gegen CLI 2.1.178 verifiziert). Nicht erneut untersuchen.

## Stolperfallen
- Nach UI-Änderungen VSIX neu bauen UND neu installieren (WebUI liegt in der VSIX).
- `session.init` (host→web) leert die Transcript-Ansicht — immer VOR `transcript.load` senden.
- `--resume`-Session-IDs nur bei `num_turns > 0` übernehmen (sonst „No conversation found").
- Settings: lesen/schreiben über `AstrogatorSettingsStore` (klassischer **WritableSettingsStore**).
  Die Unified-Settings-In-Proc-API (`VisualStudioExtensibility`) wurde entfernt — der Dienst
  wird in VS 2026 NICHT proffered (s. NOTES „Settings"). Settings-UI = `AstrogatorSettingsWindow`.
- Defender-Fehlalarm auf `~/.claude/projects/**.jsonl` (Konversations-Logs, kein Code) ist
  bekannt/harmlos — ggf. Ausnahme für `%USERPROFILE%\.claude\projects`.
- **MCP-Permission-Protokoll (undokumentiert, beim CLI-Update gegentesten — s. NOTES
  „Permission-Hook"):** tools/call-Arg heißt **`input`** (nicht `tool_input`); allow MUSS
  `updatedInput` = das Input-Objekt zurückgeben (**`null` = deny!**); `protocolVersion` der CLI =
  `2025-11-25` (zurückspiegeln); Transport HTTP via **`TcpListener`** (kein URL-ACL); `GET /mcp`
  darf 405 sein. Read-only-Befehle prompten nicht (CLI-Klassifizierer). Permission-Card **ersetzt**
  die Tool-Card gleicher `tool_use_id` (CLI sendet `tool_use` VOR dem Prompt).
- **MCP-Tool-Timeout (versionsabhängig! 2026-06-17 gegen 2.1.178 geprüft):** Das Config-`timeout`-Feld
  in `--mcp-config` **hat Vorrang** vor der Env-Var `MCP_TOOL_TIMEOUT` (umgekehrt zu 2.1.16x). Der
  User-„Prompt timeout" wird daher ins **Config-`timeout`** geschrieben (`McpPermissionBridge.ToolTimeoutMs`,
  via `UpdateToolTimeout` neu geschrieben); Env-Var bleibt nur Fallback. Beide in **ms**.
- **MCP-Transport-Timeout (Fix 2026-06-30 — eigentliche Ursache für „Prompt läuft nach ~5 min ab + retry"):**
  Das Config-`timeout` ist nur ein **Application-Layer**-Limit; der HTTP-Client der CLI (Node/undici) erzwingt
  zusätzlich ein **Transport-Timeout** (~5 min Header/Body-Inaktivität), das `timeout` NICHT aufhebt. Da der
  Server die `tools/call`-Antwort bislang ohne ein einziges Byte offen hielt, brach der Client nach ~5 min ab
  und **wiederholte** den Call. **Lösung:** `tools/call` wird als **SSE gestreamt** (`WriteToolsCallSseAsync`) —
  Header sofort, alle 25 s ein `: keep-alive`, dann das Ergebnis als `data:`-Event → der Inaktivitäts-Timer
  läuft nie ab. Betrifft Permission-Prompts **und** AskUserQuestion (gleicher `tools/call`-Pfad). Beim
  CLI-Update gegentesten, ob die CLI das SSE-gelieferte Tool-Result akzeptiert.
- Geprüft gegen CLI **2.1.178** (Voll-Re-Verifikation 2026-06-17 — s. NOTES Kopf); `--effort`,
  `--permission-mode`-Werte, MCP-Permission-Protokoll + **-Timeout-Deliverer** (s. o.) und das Format
  des `/usage`-Report-Texts (Usage-Meter via `claude -p /usage --output-format json` — s.
  `ClaudeUsageClient`) beim CLI-Update gegentesten.
