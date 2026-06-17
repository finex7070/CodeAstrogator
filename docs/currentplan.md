# Current Plan — Permission-Hook & Inline-Diff-Review (Roadmap #1 + #3)

> Resume-Anker: Sag „**mach den currentplan weiter**". Diese Datei ist die Single Source of
> Truth für den aktuellen Arbeitsstrang. Volldetails + alle Lerneffekte stehen in
> `docs/NOTES.md` → Abschnitt **„Permission-Hook & Inline-Diff-Review"** (zwingend zuerst lesen).
> Protokoll-/CLI-Stolperfallen außerdem in `CLAUDE.md`.

## Ziel
Die CLI bei genehmigungspflichtigen Tool-Calls (Edit/Write/Bash) anhalten, der User entscheidet
in der Chat-UI, die Extension meldet allow/deny zurück. Zwei UX-Stufen:
1. **Standard:** interaktive Permission/Diff-Card im Chat (allow/deny). — **fertig.**
2. **Erweitert (ein-/ausschaltbar, Toggle im Appearance-Popover):** nur für File-Edits eine
   Dateiliste im Chat, Klick öffnet die Datei im Editor, dort **Inline-Diff** (rot/grün wie
   VS-Copilot) mit **Accept/Reject pro Hunk**; Teil-Annahme via `updatedInput`. — **offen.**

## Phasen-Status
- ✅ **Phase 0-A — MCP-Protokoll-Spike** (gegen CLI 2.1.162): erledigt.
- ✅ **Phase 1 — MCP-Bridge + Standard-Chat-Card**: **implementiert, adversarial reviewt
  (3 Fixes), in VS verifiziert (2026-06-04), 97/97 Tests grün.**
  - `Core/McpPermissionBridge` (TcpListener-HTTP + JSON-RPC, X-Auth, `--mcp-config`-Datei).
  - `ClaudeSessionService` injiziert `--mcp-config` + `--permission-prompt-tool` (außer Bypass).
  - `WebViewBridge`: `permission.request`/`permission.decision`, Diff (Edit/Write) mit
    dateirelativen Zeilennummern, Status Applied/Failed/Rejected, Persistenz; Permission-Card
    **ersetzt** die redundante Tool-Card; Read-only-Befehle prompten nicht.
  - Tests: `PermissionBridgeTests`.
- ⏭️ **Phase 0-B — Editor-Adornment-Machbarkeits-Spike (NÄCHSTER SCHRITT, riskantester Teil):**
  kleinster VS-MEF-Prototyp, der in einem offenen Dokument **Additions als „Phantom"-Zeilen**
  (nicht im Buffer) + **Deletions rot** + **WPF-Buttons pro Hunk** rendert
  (`IWpfTextViewCreationListener` + AdornmentLayer + ggf. `ILineTransformSource`, alternativ
  „temporär in Buffer einfügen + dekorieren + revert"). Entscheidet: Inline tragfähig **oder**
  Fallback (VS-Diff-Viewer + Buttons in der Chat-Card). Nur per Build + manuellem VS-Test prüfbar.
- ⏭️ **Phase 2 — Erweitert: Dateiliste + Öffnen + Inline-Anzeige:**
  - Setting `AstrogatorOptions.ReviewEditsInEditor` (Default aus) durch Store/Copy/Settings-Fenster +
    **Toggle im Appearance-Popover** (app.js, bei Dark/Light/Auto). Kontrakt: web→host
    `editReview.setEnabled {enabled}`, host→web `editReview.state {enabled}`.
  - Edit-Review-Card host→web `permission.fileEdit {requestId,path,status}` +
    `permission.fileEdit.update {requestId,status}`; web→host `editReview.open {requestId}`.
  - NEU `Services/EditReviewController`: Dokument öffnen (`VsShellUtilities.OpenDocument`/DTE),
    Hunks berechnen (line-based, wie `buildDiff` host-portiert), Inline-Adornments zeigen;
    zunächst Accept/Reject **pro Datei** als Zwischenstufe.
- ⏭️ **Phase 3 — Per-Hunk Accept/Reject + updatedInput:** Pro-Hunk-Buttons im Editor;
  beim Abschluss `new_string`/`content`/MultiEdit aus **nur akzeptierten Hunks** rekonstruieren →
  `allow + UpdatedInput` (alle verworfen → deny). Reine Merge-Logik → unit-testbar (löst #3 ein).

## Verifizierte Protokoll-Fakten (nicht erneut spiken — CLI 2.1.162)
- tools/call-Arg heißt **`input`** (nicht `tool_input`); **allow MUSS `updatedInput` = Input-Objekt
  zurückgeben (`null` = deny!)**; `protocolVersion` der CLI = **`2025-11-25`** (zurückspiegeln).
- Transport HTTP via **`TcpListener`** (kein URL-ACL nötig); `GET /mcp` darf 405 sein; `X-Auth`-Header.
- CLI sendet `tool_use` **vor** dem Permission-Prompt → Card ersetzt Tool-Card (nicht vorab
  unterdrücken). Read-only-Befehle (Listing, cat, git status …) prompten nicht (CLI-Klassifizierer).

## Out of Scope (v1)
Diff für Nicht-Edit-Tools (JSON in Standard-Card); Multi-Edit-Batch-Review über mehrere blockende
Calls; zusätzliches Side-by-Side. (Spätere Roadmap-Punkte: Multi-Tab, Kontext-Injektion aktiver
Editor; Remote-Control-Ausbau.)

## Arbeitsweise / Befehle
- Build NUR via VS-MSBuild (`…\18\Community\MSBuild\Current\Bin\MSBuild.exe`,
  `CodeAstrogator.slnx /t:Restore,Build /p:Configuration=Release`).
- Tests: `vstest.console.exe …\CodeAstrogator.Tests.dll`.
- Nach JS-Änderung: `node --check WebUI\app.js`. Nach UI-Änderung VSIX neu bauen UND installieren.
- Empirische CLI-Proben sind erlaubt (Wegwerf-Skript `probe*.js`, via `.gitignore` ausgeschlossen,
  danach löschen). Ultracode/Workflow für adversariales Review nutzen, wenn substantiell.
