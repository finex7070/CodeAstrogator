# Current Plan — Permission-Hook & Inline-Diff-Review (Roadmap #1 + #3)

> Resume-Anker: Sag „**mach den currentplan weiter**". Diese Datei ist die Single Source of
> Truth für den aktuellen Arbeitsstrang. Volldetails + alle Lerneffekte stehen in
> `docs/NOTES.md` → Abschnitt **„Permission hook & inline diff review (Roadmap #1+#3)"**
> (NOTES.md ist englisch; zwingend zuerst lesen). Protokoll-/CLI-Stolperfallen außerdem in `CLAUDE.md`.
>
> **Stand des Plans (2026-06-19):** Extension geprüft gegen CLI **2.1.178** (Voll-Re-Verifikation
> 2026-06-17). **Alle Phasen (0-A, 1, 0-B, 2, 3) implementiert.** Der Inline-Editor-Diff
> (`ReviewEditsInEditor`-Setting + Popover-Toggle, `EditReviewController`, `Editor/`-Adornments,
> `Core/EditReview/`-Hunk-Diff+Rekonstruktion mit Unit-Tests, `editReview.open`/`reviewEditsInEditor.set`/
> `permission.finalize`-Kontrakt) ist gebaut und kompiliert; die reine Diff-/Rekonstruktions-Logik ist
> unit-getestet. **Offen: manuelle VS-Verifikation des WPF-Adornment-Renderings** (s. NOTES → „Inline edit
> review in the editor"). Details + Fallbacks: `docs/NOTES.md`.

## Ziel
Die CLI bei genehmigungspflichtigen Tool-Calls (Edit/Write/Bash) anhalten, der User entscheidet
in der Chat-UI, die Extension meldet allow/deny zurück. Zwei UX-Stufen:
1. **Standard:** interaktive Permission/Diff-Card im Chat (allow/deny). — **fertig.**
2. **Erweitert (ein-/ausschaltbar, Toggle im Appearance-Popover):** nur für File-Edits eine
   Datei-Card im Chat, Klick öffnet die Datei im Editor, dort **Inline-Diff** (rot/grün wie
   VS-Copilot) mit **Accept/Reject pro Hunk**; Teil-Annahme via `updatedInput`. — **implementiert
   (Adornment-Rendering noch manuell in VS zu verifizieren).**

## Phasen-Status
- ✅ **Phase 0-A — MCP-Protokoll-Spike** (gegen CLI 2.1.162, re-verifiziert 2.1.178): erledigt.
- ✅ **Phase 1 — MCP-Bridge + Standard-Chat-Card**: **implementiert, adversarial reviewt,
  in VS verifiziert (erstmals 2026-06-04) — und seither produktiv ausgebaut.** Testsuite
  inzwischen ~109 Testmethoden grün (war 97 zum Phase-1-Abschluss).
  - `Core/McpPermissionBridge` (TcpListener-HTTP + JSON-RPC, X-Auth, `--mcp-config`-Datei).
  - `ClaudeSessionService` injiziert `--mcp-config` + `--permission-prompt-tool` (außer Bypass).
  - `WebViewBridge`: `permission.request`/`permission.decision`, Diff (Edit/Write) mit
    dateirelativen Zeilennummern, Status Applied/Failed/Rejected, Persistenz; Permission-Card
    **ersetzt** die redundante Tool-Card; Read-only-Befehle prompten nicht.
  - Tests: `PermissionBridgeTests`.
  - **Seit Plan-Erstellung in Phase 1 nachgezogen (alles in NOTES „Permission hook & inline
    diff review" dokumentiert, NICHT Teil von 0-B/2/3):**
    - Card-UX: nach Approve/Reject **kollabiert** die Card (Status-Badge + Tint), Header re-expandiert
      den read-only Diff; Status Approved/Rejected → Applied/Failed via `permission.result`.
    - **Auto-approved Edits als vor-entschiedene Card** (`acceptEdits`/`bypass`): Bridge sendet
      `permission.request` mit `autoApproved:true`, UI rendert direkt grün/kollabiert ohne Buttons.
    - **Parallel-Prompt-Fix** (mehrere offene Cards gleichzeitig): `PostStatusAfterDecision()` bleibt
      `waiting-permission`, solange `_pendingPermissions` nicht leer; UI trackt `pendingPermissions`-Set.
    - **AskUserQuestion** läuft ebenfalls **über den Permission-Hook** (echte In-Turn-Card) — gleicher
      Blockier-Mechanismus, eigene Card-UI.
    - **MCP-Tool-Timeout-Fix (CLI 2.1.178):** Config-`timeout` hat jetzt Vorrang vor `MCP_TOOL_TIMEOUT`
      → User-„Prompt timeout" wird ins Config-`timeout` geschrieben (`ToolTimeoutMs`/`UpdateToolTimeout`).
- ✅ **Phase 0-B — Editor-Adornments (implementiert 2026-06-19, Rendering noch manuell zu prüfen):**
  Statt eines Wegwerf-Spikes direkt die Vollausführung gebaut. **Phantom-Additions** über
  `ILineTransformSource` (reserviert Bottom-Space, **additiv zu `line.DefaultLineTransform`**) + `TextRelative`-
  Adornment; **rote Deletions** auf realen Buffer-Zeilen; **WPF-Buttons pro Hunk** (`Editor/EditReviewMef.cs`
  + `Editor/EditReviewViewAdorner.cs`, MEF via `MefComponent`-Asset im Manifest). Buffer wird **nie** verändert.
  Eine unabhängige API-Reflektions-Panel (gegen die ausgelieferten 17.14.249-Ref-DLLs) hat alle genutzten
  Editor-APIs bestätigt; einziger nicht offline verifizierbarer Punkt: ob `GetLineTransform` beim Anhängen/nach
  einer Hunk-Entscheidung neu feuert → **in VS testen.** Fallbacks dokumentiert (NOTES).
- ✅ **Phase 2 — Dateiliste + Öffnen + Anzeige (implementiert):**
  - Setting `AstrogatorOptions.ReviewEditsInEditor` (Default aus) durch Store/Copy + **Toggle im
    Appearance-Popover** (app.js). Kontrakt: web→host `reviewEditsInEditor.set {enabled}`, host→web Initialwert
    in `session.init` (`reviewEditsInEditor`).
  - Edit-Review-Card: host→web `permission.request` mit `editInEditor:true` + `hunkCount` (rendert die
    Datei-Card statt des Inline-Diffs); host→web `permission.finalize {requestId,status}`; web→host
    `editReview.open {requestId}`. (Statt der ursprünglich geplanten `permission.fileEdit*`-Nachrichten wird die
    bestehende Permission-Card-Infrastruktur wiederverwendet — weniger neue Fläche.)
  - `Services/EditReviewController`: Dokument öffnen (`VsShellUtilities.OpenDocument` → `IVsTextView` →
    `IWpfTextView` via `IVsEditorAdaptersFactoryService`), Review an den View-Adorner übergeben.
- ✅ **Phase 3 — Per-Hunk Accept/Reject + updatedInput (implementiert):** Pro-Hunk-Buttons im Editor mutieren
  `ReviewHunk.State`; bei „alle entschieden" rekonstruiert `EditReviewSession.BuildUpdatedInput()`
  `new_string`/`content`/MultiEdit aus **nur akzeptierten Hunks** → `allow + UpdatedInput` (alle verworfen →
  deny). Reine Merge-Logik in `Core/EditReview/` → **unit-getestet** (`EditReviewTests`).

## Verifizierte Protokoll-Fakten (nicht erneut spiken — CLI 2.1.162, re-verifiziert 2.1.178 am 2026-06-17)
- tools/call-Arg heißt **`input`** (nicht `tool_input`); **allow MUSS `updatedInput` = Input-Objekt
  zurückgeben (`null` = deny!)**; `protocolVersion` der CLI = **`2025-11-25`** (zurückspiegeln).
- Transport HTTP via **`TcpListener`** (kein URL-ACL nötig); `GET /mcp` darf 405 sein; `X-Auth`-Header.
- CLI sendet `tool_use` **vor** dem Permission-Prompt → Card ersetzt Tool-Card (nicht vorab
  unterdrücken). Read-only-Befehle (Listing, cat, git status …) prompten nicht (CLI-Klassifizierer).
- **Tool-Timeout (versionsabhängig!):** ab 2.1.178 hat das Config-`timeout`-Feld **Vorrang** vor der
  Env-Var `MCP_TOOL_TIMEOUT` (umgekehrt zu 2.1.16x) — beim nächsten CLI-Update erneut gegentesten.

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
