# Plan: Git-basierte Checkpoints / Savepoints pro Turn

## Context
Der User möchte zwischen jedem Prompt/Turn einen Wiederherstellungspunkt ("Checkpoint")
für die **Dateien des Workspaces**, zu dem er jederzeit zurückkehren kann (Cursor-artiges
"Restore"). Umsetzung über **Git**, daher nur verfügbar, wenn Git installiert ist. Wichtig:
Es muss **unabhängig von einem evtl. schon vorhandenen Git-Repo** des Projekts funktionieren —
also ein eigenes kleines **Schatten-Repo**, das das (vielleicht schon vorhandene) Projekt-Repo
des Users **niemals** berührt.

Ergebnis: Vor jedem Prompt wird der Dateistand committet; im Transcript bekommt jeder
User-Prompt einen kleinen "↩ Wiederherstellen"-Button, der den Workspace auf den Stand
*vor* diesem Turn zurücksetzt.

## Bestätigte Entscheidungen (vom User bestätigt 2026-06-30)
- **Zeitpunkt:** ✅ Commit **vor jedem Prompt** (Cursor-Modell). Der Checkpoint an Prompt N =
  Stand *vor* Turn N → "Wiederherstellen" macht Turn N (und alles danach) rückgängig. Deckt
  auch das Rückgängigmachen des **letzten** Turns ab.
- **Revert-UI:** **Button pro Turn** inline am User-Prompt im Transcript.
- **Default:** ✅ **Opt-in** (Setting standardmäßig AUS). **NEU:** Der Toggle soll **direkt im
  Zahnrad-Menü oben rechts** sitzen (nicht nur im „Advanced options…"-Fenster) — schnell
  erreichbarer Schalter im Gear-Popover.
- **Speicherort:** Schatten-Repo **außerhalb** des Projekts unter
  `%LOCALAPPDATA%\CodeAstrogator\Checkpoints\<hash-des-solution-pfads>\` → verschmutzt das
  Projekt-Repo des Users nicht und wird nicht von dessen Git erfasst.
- **Restore-Semantik:** ✅ stellt **nur Dateien** wieder her, **nicht** den Chat-/CLI-Verlauf.
  Forward-only (siehe unten), damit alle Checkpoints in der Liste bleiben und "Redo" =
  einen späteren Checkpoint wiederherstellen.
  **NEU:** Nach einem Restore muss beim **nächsten Prompt für Claude klar sein, wohin
  zurückgesetzt wurde** — d. h. dem nächsten Turn wird ein Kontext-Hinweis vorangestellt
  (z. B. „Hinweis: Der Workspace wurde auf den Stand vor Turn N (Checkpoint <kurz-sha>,
  <zeit>) zurückgesetzt; spätere Dateiänderungen sind verworfen."). So bleibt der Chat zwar
  bestehen, aber Claude arbeitet nicht auf einer falschen Annahme über den Dateistand weiter.

## Architektur / Schatten-Repo-Kern (das technische Herzstück)
Ein Schatten-Repo = ein Git-Repo, dessen **GIT_DIR außerhalb** des Projekts liegt, dessen
**work-tree aber auf das Projektverzeichnis** zeigt:
```
git --git-dir=<LOCALAPPDATA>\CodeAstrogator\Checkpoints\<hash>\.git --work-tree=<solutionDir> <cmd>
```
- Git ignoriert beim `add -A` immer ein Verzeichnis namens `.git` → das **echte** Projekt-Repo
  wird nie miteingecheckt.
- Git liest die `.gitignore`-Dateien im work-tree automatisch → Build-Artefakte (bin/obj,
  node_modules, .vs) werden ausgeschlossen, *wenn* das Projekt eine `.gitignore` hat.
- Hat das Projekt **keine** `.gitignore` (z. B. kein Repo), legen wir eine
  Default-Excludeliste in `<git-dir>\info\exclude` an (bin, obj, .vs, node_modules, *.user, …),
  damit Snapshots nicht riesig werden.
- Init einmalig pro Solution: `git init`, dann lokal `user.name`/`user.email` setzen (damit
  Commits ohne globale Git-Identität funktionieren) und `commit.gpgsign=false` (keine
  Signatur-Prompts). Eine `meta.json` (Original-Pfad) zur Nachvollziehbarkeit ablegen.

## Neue/zu ändernde Dateien

### 1. Core (UI-frei + testbar): `Core/GitCheckpointService.cs` (neu)
Kapselt alle Git-Aufrufe via `System.Diagnostics.Process` — Muster aus
`Core/ClaudeCliProcessHost.cs` (ProcessStartInfo, `RedirectStandardOutput`, async read) und
Arg-Quoting via `ClaudeCliProcessHost.Quote()` wiederverwenden. Öffentliche API:
- `static bool IsGitAvailable()` — `git --version`, Ergebnis cachen.
- `Task EnsureInitializedAsync(solutionDir)` — Schatten-Repo lazy anlegen (siehe oben).
- `Task<CheckpointInfo> CommitAsync(solutionDir, label)` — `git add -A` dann
  `git commit --allow-empty -m <label>` (allow-empty → **jeder** Prompt bekommt einen
  Restore-Punkt, auch wenn der Vorgänger-Turn nichts änderte). Liefert Commit-SHA + Kurz-SHA.
- `Task RestoreAsync(solutionDir, sha)` — **forward-only**:
  1. Safety-Commit des aktuellen Stands (`add -A` + commit "auto: vor Wiederherstellung"),
  2. `git checkout <sha> -- .` (überschreibt getrackte Dateien mit Ziel-Inhalt),
  3. seit dem Ziel **hinzugekommene** Dateien löschen
     (`git diff --diff-filter=A --name-only <sha> HEAD` → aus work-tree entfernen),
  4. `git add -A` + commit "Wiederhergestellt auf <kurz-sha>".
  → linearer Verlauf, alle Checkpoints bleiben sichtbar, nichts geht verloren.
- `Task<IReadOnlyList<CheckpointInfo>> ListAsync(solutionDir)` — optional, für spätere Liste.

Alle Aufrufe laufen auf Hintergrund-Thread; Git-Fehler werden gefangen und als Fehlertext
zurückgegeben (Feature darf einen Turn nie crashen lassen).

### 2. Settings
- `Options/AstrogatorOptions.cs`: neues `public bool CheckpointsEnabled { get; set; } = false;`
  (Muster wie `AutoAcceptCommands`).
- `Services/AstrogatorSettingsStore.cs`: in `Read()`/`Write()` via `GetBool`/`SetBoolean`
  ergänzen (Muster wie `UltracodeEnabled`).
- `ToolWindows/AstrogatorSettingsWindow.cs`: `MakeCheck("Create a git checkpoint before each
  prompt (revert anytime)")` + Laden/Persistieren (Muster wie vorhandene Checkboxen).
  Wenn `!GitCheckpointService.IsGitAvailable()`: Checkbox deaktivieren + Hinweistext
  "Git nicht gefunden".
- **NEU — Toggle im Zahnrad-Menü oben rechts:** Zusätzlich zum Advanced-Fenster ein
  Schnell-Toggle direkt im Gear-Popover (Muster wie der bestehende Versions-/Ultracode-Eintrag
  im Zahnrad-Popover, vgl. Release 0.5.1 „version in gear popover"). Klick schaltet
  `CheckpointsEnabled` sofort um (persistiert via `AstrogatorSettingsStore`) und meldet den
  neuen Stand an die UI (`session.init.checkpointsEnabled` bzw. ein dediziertes Update). Bei
  fehlendem Git: Eintrag deaktiviert/ausgegraut mit Tooltip „Git nicht gefunden".

### 3. Bridge: `Bridge/WebViewBridge.cs` (Orchestrierung)
- **Vor dem Prompt:** In `RunPrompt` (Z. 413) — läuft bereits auf `TaskScheduler.Default` —
  *vor* `await _session.RunTurnAsync(...)` (Z. 428): wenn `options.CheckpointsEnabled` &&
  Git verfügbar && `cwd` (`GetSolutionDirectory()`) vorhanden →
  `await _checkpoints.EnsureInitializedAsync(cwd)` + `CommitAsync(cwd, label)`. Label enthält
  Turn-Index, Zeit, Prompt-Auszug. SHA via neuer Host→Web-Nachricht `checkpoint.created`
  senden **und** am User-Message-Objekt in der History persistieren (siehe 5).
- **Neue web→host-Cases** in `OnWebMessageReceived` (Switch ab Z. 188):
  `checkpoint.restore` { sha } → `HandleCheckpointRestore` (auf Hintergrund-Thread,
  blockiert wenn ein Turn läuft / `status != ready`; bei Erfolg `checkpoint.restored` +
  `system.note`, sonst `error`).
- **NEU — Restore-Kontext für Claude:** Nach erfolgreichem Restore einen Hinweis vormerken,
  der dem **nächsten** Turn-Prompt vorangestellt wird (Bridge hält z. B. einen
  `_pendingRestoreNote`-String; in `RunPrompt` vor `RunTurnAsync` einmalig vorne an den
  User-Prompt hängen und danach leeren). Text z. B.: „[System] Der Workspace wurde auf den
  Stand vor Turn N (Checkpoint <kurz-sha>, <zeit>) zurückgesetzt; alle danach gemachten
  Dateiänderungen wurden verworfen." → Claude arbeitet so nicht auf einem veralteten
  Dateistand-Modell weiter.
- **Neue host→web-Nachrichten** (via `Post`/`PostOrQueue`, Muster Z. 2195/2218):
  - `checkpoint.created` { turnSeq, sha, shortSha, label, timestamp }
  - `checkpoint.restored` { sha, ok, error? }
  - In `session.init` ein Flag `checkpointsEnabled` mitsenden, damit die UI weiß, ob
    Restore-Buttons gerendert werden.
- Service-Instanz `_checkpoints` analog zu den anderen Core-Services im Bridge-Ctor anlegen.

### 4. WebUI: `WebUI/app.js` + `WebUI/index.html` + Styles
- Neue Nachrichten im Dispatcher behandeln (Muster wie `turn.result`/`permission.result`):
  - `checkpoint.created`: SHA am DOM-Knoten des zuletzt gerenderten **User-Prompts** ablegen
    und einen kleinen "↩ Wiederherstellen"-Button an der User-Bubble / am `hr.turn-divider`
    rendern (Stil an `turn-footer`/Permission-Buttons anlehnen).
  - Klick → kurze Inline-Bestätigung ("Dateien auf diesen Stand zurücksetzen? Chat bleibt.")
    → `post("checkpoint.restore", { sha })` (Helper `post()` wie bei `permission.decision`).
  - `checkpoint.restored`: `systemNote` "Workspace wiederhergestellt" bzw. Fehler.
- Restore-Buttons nur rendern, wenn `session.init.checkpointsEnabled === true`.
- Nach jeder JS-Änderung `node --check WebUI\app.js`.

### 5. History-Persistenz (Buttons überleben Reload): `Services/SessionHistoryStore.cs`
- User-Message-Modell um optionales Feld `checkpointSha` erweitern; beim Speichern setzen
  (aus `checkpoint.created`), beim `transcript.load` (`loadTranscript` in app.js) wieder als
  Restore-Button rendern. Ohne dies verschwinden die Buttons nach Reload/Session-Wechsel.

## Wiederverwendete vorhandene Bausteine
- Prozess/Quoting: `Core/ClaudeCliProcessHost.cs` (ProcessStartInfo-Muster, `Quote()`).
- Solution-Pfad: `CodeAstrogatorPackage.GetSolutionDirectory()` (Z. 267, UI-Thread).
- Settings-Muster: `AstrogatorOptions` / `AstrogatorSettingsStore` (`GetBool`/`SetBoolean`) /
  `AstrogatorSettingsWindow` (`MakeCheck`).
- Nachrichten-Kontrakt: `WebViewBridge.OnWebMessageReceived` Switch + `Post`/`PostOrQueue`;
  JS `post()` + Dispatcher; Turn-Grenze `turnResult` (`hr.turn-divider`, `turn-footer`).

## Randfälle / Hinweise
- Keine Solution offen / kein `cwd` → Feature für diese Session inaktiv (kein Fehler).
- Git nicht installiert → Setting deaktiviert, keine Checkpoints, kein Crash.
- Restore während laufendem Turn unterbinden (busy/Status prüfen).
- Restore betrifft **nur Dateien**, nicht den Chat-/CLI-Verlauf (in UI klar kommunizieren).
- Projekt ohne `.gitignore`: Default-Excludes via `info/exclude` gegen riesige Snapshots.
- Schatten-`.git` liegt außerhalb des Projekts → echtes Repo des Users bleibt unberührt.

## Doku & Versionierung (Projektregeln, CLAUDE.md)
- `docs/NOTES.md` (englisch): neues Schatten-Repo-Konzept, Speicherort, Restore-Semantik,
  Nachrichten-Kontrakt-Ergänzungen, offene Punkte dokumentieren.
- `CHANGELOG.md` (englisch): neuer `## [x.y.z] – YYYY-MM-DD`-Eintrag (Added) — gemeinsam mit
  dem Versionsbump.
- Am Turn-Ende: geplante Commit-Message als Vorschau in den Chat schreiben, **danach** per
  `AskUserQuestion` Versionsbump (Minor, da neues Feature) **und** Commit/Push-Frage stellen;
  Commit auf Branch `develope`, mit `Co-Authored-By: Claude …`. Nicht eigenmächtig nach `main`.

## Verifikation (End-to-End)
1. Build (nur VS-MSBuild):
   `MSBuild.exe CodeAstrogator.slnx /t:Restore,Build /p:Configuration=Release /m /v:m`
2. Unit-Tests für `GitCheckpointService` (vstest) mit Temp-Verzeichnis + echtem Git:
   init → Datei anlegen → `CommitAsync` → Datei ändern → `RestoreAsync` → Inhalt = alter Stand;
   und: nach Restore neu hinzugefügte Datei ist entfernt; Verlauf bleibt linear.
3. `node --check WebUI\app.js`.
4. Manuell in VS (VSIX neu bauen **und** installieren): Setting aktivieren → mehrere Prompts →
   Restore-Button pro Turn sichtbar → Datei ändern lassen → Wiederherstellen → Dateien zurück,
   Chat unverändert. Gegentest **mit** und **ohne** vorhandenem Projekt-Git-Repo, dass das
   echte `.git` des Users unangetastet bleibt (`git status` im Projekt unverändert).
