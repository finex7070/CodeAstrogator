# Plan: Checkpoints / Rewind (Parität zur Claude-Desktop-App)

> **Status 2026-08-28: umgesetzt.** Der Ist-Stand (inkl. der Abweichungen, die beim Bauen
> entstanden sind) steht in `docs/NOTES.md` → „File checkpoints / rewind"; dieses Dokument bleibt
> als Entwurf/Begründung erhalten. Umgesetzt in `Core/GitCheckpointService.cs`,
> `Bridge/WebViewBridge.cs`, `WebUI/app.js` + `app.css`, `Options`/`Services`/`ToolWindows`
> (Settings + Retention), Tests in `CodeAstrogator.Tests/GitCheckpointServiceTests.cs`.
> Abweichungen vom Plan: kein `for-each-ref`/`cat-file`-stdin (arg-basiert), Existenzprüfung via
> `rev-list --ignore-missing`, und die Restore-Reihenfolge nutzt den Safety-Snapshot direkt als
> Diff-Gegenstück.

> Revision 2026-08-28: Recherche der echten Claude-Desktop-/VS-Code-Umsetzung eingearbeitet.
> Der Snapshot-Kern (Schatten-Git) bleibt wie 2026-06-30 beschlossen; **UX und Semantik**
> werden auf das Desktop-Verhalten umgestellt.
> Revision 2026-08-28b: **Pre/Post-Snapshot pro Turn** (damit auch Bash-/Script-Änderungen von
> Claude als „Claudes Änderungen" gelten), **ref-basierte Checkpoint-Ablage** (damit Aufräumen
> billig ist und SHAs stabil bleiben) und eine **einstellbare Aufbewahrungsdauer**.
> Die Delta-Tabelle unten listet, was sich gegenüber der ersten Fassung ändert.

## Ziel
Der User möchte pro Prompt/Turn einen Wiederherstellungspunkt für die **Dateien des Workspaces**
und ein Bedienkonzept, das sich **so anfühlt wie in der Claude-Desktop-App** ("Rewind"). Umsetzung
über **Git** in einem **Schatten-Repo**, das das (vielleicht vorhandene) Projekt-Repo des Users
**niemals** berührt.

## Referenz: so funktioniert es in Claude Desktop / VS Code (recherchiert 2026-08-28)
Quellen: [Checkpointing](https://code.claude.com/docs/en/checkpointing),
[VS Code extension → Rewind with checkpoints](https://code.claude.com/docs/en/vs-code),
[Agent SDK → File checkpointing](https://code.claude.com/docs/en/agent-sdk/file-checkpointing),
[Manage sessions](https://code.claude.com/docs/en/sessions),
[Desktop application](https://code.claude.com/docs/en/desktop).

| Aspekt | Verhalten in Claude Desktop / VS Code |
| --- | --- |
| Auslöser | **Jeder User-Prompt** erzeugt automatisch einen Checkpoint. Kein Setting, immer an. |
| Umfang | **Nur Claude's Datei-Edits** über `Write` / `Edit` / `NotebookEdit`. |
| Nicht erfasst | Änderungen durch **Bash-Befehle**, Edits von **Subagents** (Ausnahme: Foreground-`context: fork`-Skill), Änderungen **außerhalb** der Session, Verzeichnis-Operationen (create/move/delete), **Symlinks/Hardlinks** werden beim Restore übersprungen ("Restored the code, but skipped N files"). |
| GUI-Bedienung | **Maus über eine Nachricht → Rewind-Button**. Menü mit drei Einträgen: **„Fork conversation from here"** (Chat abzweigen, Code bleibt), **„Rewind code to here"** (Dateien zurück, Chat bleibt), **„Fork conversation and rewind code"** (beides). |
| CLI-Menü (`/rewind`, Esc-Esc) | Restore code and conversation · Restore conversation · Restore code · Summarize from here · Summarize up to here · Never mind. |
| Bedingte Optionen | Die **Code**-Optionen erscheinen **nur**, wenn der Checkpoint überhaupt getrackte Datei-Änderungen hat; sonst bleiben nur Conversation-/Summarize-Optionen. |
| Nach Chat-Restore | Der **ursprüngliche Prompt der gewählten Nachricht landet zurück im Eingabefeld** (zum erneuten Senden oder Bearbeiten). |
| Persistenz / Retention | Snapshots der **100 letzten Checkpoints je Session**, mit der Session gespeichert (überlebt `--resume`); Löschung zusammen mit der Session nach **30 Tagen** (`cleanupPeriodDays`). |
| Abgrenzung | Explizit **kein Ersatz für Versionskontrolle** — Session-Level-Recovery. |
| Headless-Weg (für Integrationen) | `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=true claude -p --resume <sid> --rewind-files <user-message-uuid>`; Checkpoint-ID = UUID der User-Message (Stream-UUIDs nur mit `--replay-user-messages`). Flag ist **nicht in `--help`**, aber dokumentiert. Für **Chat**-Rewind gibt es **keinen** headless Weg (Feature-Request [#16976](https://github.com/anthropics/claude-code/issues/16976) → „not planned"); ein Desktop-Rewind-UI-Request ([#43755](https://github.com/anthropics/claude-code/issues/43755)) ist ebenfalls „not planned", das GUI-Verhalten oben ist der Ist-Stand der Docs. |

### Warum wir trotzdem Schatten-Git nehmen (Entscheidung 2026-08-28)
`--rewind-files` wäre 1:1-Parität, ist aber ein undokumentierter Flag (bricht potenziell bei jedem
CLI-Update), erfasst **keine** Bash-Änderungen und liefert uns **keine** Diff-Daten für eine
Vorschau. Das Schatten-Repo ist versionsunabhängig, erfasst *alles*, und der Diff zweier Snapshots
gibt uns Datei-Liste + `+/-`-Statistik gratis.

**Wir gehen bewusst über die Desktop-Parität hinaus:** weil wir **vor und nach** jedem Turn einen
Snapshot machen, ist „was hat Claude in diesem Turn geändert" bei uns ein **Diff**, nicht eine
Tool-Call-Liste. Damit sind Änderungen durch **Bash, Python-Scripts, Build-Steps, Subagents,
Formatter-Hooks** genau so erfasst wie `Edit`/`Write` — die Desktop-App kann das prinzipiell nicht
(siehe „Nicht erfasst" oben).

## Bestätigte Entscheidungen

### Weiterhin gültig (User bestätigt 2026-06-30)
- **Zeitpunkt:** Snapshot **vor jedem Prompt**. Checkpoint an Prompt N = Stand *vor* Turn N →
  Rewind macht Turn N und alles danach rückgängig (deckt auch den letzten Turn ab).
- **Speicherort:** Schatten-Repo **außerhalb** des Projekts unter
  `%LOCALAPPDATA%\CodeAstrogator\Checkpoints\<hash-des-solution-pfads>\`.
- **Nicht-destruktiv:** Ein Rewind legt vorher einen Sicherungs-Snapshot des aktuellen Stands an;
  bestehende Checkpoints verschwinden nie → „Redo" = späteren Checkpoint wiederherstellen.
- **Restore-Kontext für Claude:** Dem nächsten Turn wird ein Hinweis vorangestellt, wohin
  zurückgesetzt wurde, damit Claude nicht auf einem veralteten Dateistand-Modell weiterarbeitet.
- **Schnell-Toggle im Zahnrad-Popover** (zusätzlich zum „Advanced options…"-Fenster).

### Neu / geändert (User bestätigt 2026-08-28)
- **Default = AN, sobald Git verfügbar ist** (Desktop-Parität) — aber **abgefragt im bestehenden
  First-Run-Consent-Modal**, in dem auch Announcements und Update-Benachrichtigungen abgefragt
  werden (`openConsentPopup`, `WebUI/app.js:420`). Dritte Checkbox, vorbelegt **an**.
- **Bedienung = Hover-Menü an der Nachricht** statt eines permanent sichtbaren Buttons.
- **Drei Menü-Optionen** analog Desktop (siehe §Restore-Scopes).
- **Restore-Umfang standardmäßig auf die Änderungen aus Claudes Turns beschränkt** (Pre/Post-Diff,
  **inklusive Bash-/Script-Änderungen**), mit Diff-Vorschau und Per-Datei-Auswahl vor dem
  Ausführen; Umschalter für „alle Änderungen im Workspace".
- **Chat-Rewind = Anzeige + Kontext-Hinweis** (kein Eingriff in die CLI-Transkript-Dateien,
  kein Fork): spätere Turns werden ausgegraut, der Prompt landet zurück im Eingabefeld, dem
  nächsten Turn wird ein Hinweis vorangestellt.
- **Aufbewahrungsdauer einstellbar** (§Retention): Combo „Never / 7 / 14 / 30 / 60 / 90 / 180 /
  365 days" wie bei History und Pasted-Images; **Default 30 Tage** (Desktop-Parität),
  **„Never" = unbegrenzt behalten**.
- **Ref-basierte Ablage** der Snapshots (§Architektur), damit das Aufräumen alter Checkpoints ein
  `update-ref -d` + `gc` ist und persistierte SHAs nie umgeschrieben werden müssen.

### Delta gegenüber der ersten Planfassung
| Thema | Alt (2026-06-30) | Neu |
| --- | --- | --- |
| Default | Opt-in, Setting AUS | AN wenn Git da; Abfrage im Consent-Modal |
| Bedienung | Dauerhaft sichtbarer „↩ Wiederherstellen"-Button pro Turn | Hover-Rewind-Button + Menü mit 3 Optionen |
| Snapshots pro Turn | 1 (vor dem Prompt) | **2** (vor dem Prompt + am Turn-Ende) |
| „Was hat Claude geändert" | — (immer ganzer Work-Tree) | **Pre/Post-Diff** → erfasst auch Bash/Python/Subagent |
| Umfang eines Restore | Immer ganzer Work-Tree | Standard: Claudes Turn-Änderungen; „alles" als Option |
| Vorschau | keine | Diff-Vorschau (Dateiliste + `+/-`) mit Per-Datei-Häkchen |
| Chat | bleibt unverändert, nur Hinweis für Claude | zusätzlich: spätere Turns ausgegraut, Prompt zurück ins Eingabefeld |
| Bedingte UI | — | Code-Optionen nur bei tatsächlich vorhandenen Änderungen |
| Ablage | Linearer Branch mit Commit-Historie | Parentless Commits hinter eigenen Refs |
| Retention | nicht definiert | **Einstellbar (Default 30 Tage, „Never" = unbegrenzt)**; 100 jüngste in der Liste |

## Architektur

### Schatten-Repo (Kern)
Ein Git-Repo, dessen **GIT_DIR außerhalb** des Projekts liegt, dessen **work-tree** aber auf das
Projektverzeichnis zeigt:
```
git --git-dir=<LOCALAPPDATA>\CodeAstrogator\Checkpoints\<hash>\.git --work-tree=<solutionDir> <cmd>
```
- Git ignoriert bei `add -A` ein Verzeichnis namens `.git` → das **echte** Projekt-Repo wird nie
  eingecheckt.
- Die `.gitignore`-Dateien des work-trees greifen automatisch (bin/obj, node_modules, .vs).
- Ohne `.gitignore` im Projekt: Default-Excludes in `<git-dir>\info\exclude`
  (`bin`, `obj`, `.vs`, `node_modules`, `*.user`, …), damit Snapshots nicht riesig werden.
- Init einmalig pro Solution: `git init --bare`-artig (nur GIT_DIR, es gibt keinen `HEAD`-Branch,
  auf dem wir arbeiten), lokal `user.name`/`user.email` setzen (Commits ohne globale Git-Identität),
  `commit.gpgsign=false` (keine Signatur-Prompts), `core.autocrlf=false` (byte-genaue Snapshots),
  `meta.json` mit dem Original-Pfad daneben legen.

### Snapshot-Ablage: parentless Commits hinter eigenen Refs
Statt eines linearen Branches ist **jeder Snapshot ein eigenständiger, elternloser Commit**:
```
GIT_INDEX_FILE=<git-dir>\ca-index  git add -A          # Index für uns allein, HEAD bleibt unberührt
git write-tree                                          # → <tree>
git commit-tree <tree> -m "<label>"                     # ohne -p  → <sha>
git update-ref refs/ca-checkpoints/<turnId>-pre <sha>
```
Vorteile, die genau die zwei Anforderungen erfüllen:
- **Aufräumen ist trivial:** `git update-ref -d <ref>` + `git gc --prune=now` → der Platz ist weg.
  Kein History-Rewrite, keine Rebase-Akrobatik, um „nur die letzten X Tage" zu behalten.
- **SHAs bleiben stabil:** in der Chat-History persistierte `checkpointSha`-Werte zeigen für immer
  auf denselben Snapshot (oder sind weggeräumt — dann meldet die UI „expired", siehe §Retention).
- Objekte sind inhaltsadressiert und werden **dedupliziert**: zwei Snapshots eines unveränderten
  Repos kosten praktisch nichts (nur ein Tree-Objekt + Commit).
- `git diff <shaA> <shaB>` und `git checkout <sha> -- <pfad>` funktionieren mit elternlosen
  Commits unverändert.

Ref-Namensschema: `refs/ca-checkpoints/<sessionId>/<turnSeq>-pre` bzw. `-post`, dazu
`.../<turnSeq>-safety-<zeit>` für die Sicherungs-Snapshots vor einem Rewind.

### Pre/Post pro Turn — „was hat Claude geändert"
| Zeitpunkt | Aktion | Ref |
| --- | --- | --- |
| Vor `RunTurnAsync` | Snapshot | `<turnSeq>-pre` ← **das ist der Checkpoint, auf den „Rewind to here" zurücksetzt** |
| Bei `turn.result` (auch bei Abbruch/Fehler) | Snapshot | `<turnSeq>-post` |

- **Änderungen aus Turn k** = `git diff --numstat <k-pre> <k-post>`. Das enthält *alles*, was
  während des Turns passiert ist: `Edit`/`Write`/`NotebookEdit`, **Bash** (`sed -i`, `rm`, `mv`),
  **von Claude gestartete Scripts** (`python fix_all.py`, `npm run format`), Subagent-Edits,
  Formatter-/Build-Nebenwirkungen.
- **Standard-Restore-Umfang für Checkpoint N** = Vereinigung der Turn-Diffs aller Turns ≥ N.
  Änderungen, die der **User selbst zwischen zwei Turns** gemacht hat, liegen zwischen `post(k)`
  und `pre(k+1)` und sind damit **nicht** in der Vorauswahl — genau das gewünschte Verhalten.
- Damit entfällt das ursprünglich geplante Tool-Call-Tracking über `IsEditTool` komplett; die
  Zuordnung kommt aus dem Diff und ist dadurch vollständig statt heuristisch.
- Fehlt ein `-post`-Ref (VS-Crash, Prozess-Kill), gilt `pre(k+1)` als Ersatz-Post für Turn k
  (dann können User-Änderungen aus der Zwischenzeit mit hineinrutschen — in der Vorschau als
  Hinweis kennzeichnen).
- Kosten: ein zusätzliches `add -A` pro Turn (Stat-Walk über den work-tree, mit `.gitignore`
  gefiltert). Snapshot läuft asynchron, mit Timeout und stillem Skip (§Randfälle).

### Restore-Scopes (Menü-Parität)
| UI-Eintrag (unser Wording) | Desktop-Pendant | Wirkung |
| --- | --- | --- |
| **Rewind code to here** | „Rewind code to here" | Dateien der Auswahl auf den Checkpoint-Stand; Chat unverändert |
| **Rewind conversation to here** | „Restore conversation" / „Fork conversation from here" | Turns nach N ausgegraut + als verworfen markiert, Prompt zurück ins Eingabefeld, Hinweis an Claude; **Dateien unverändert** |
| **Rewind code and conversation** | „Restore code and conversation" | beides |

Die beiden Code-Einträge werden **nur angezeigt**, wenn die Vorschau tatsächlich Änderungen
meldet (Desktop-Verhalten).

`RestoreAsync(sha, paths)` — nicht-destruktiv:
1. Sicherungs-Snapshot des aktuellen Stands (`…-safety-<zeit>`),
2. `git checkout <sha> -- <pfade>` (überschreibt die Auswahl mit dem Ziel-Inhalt),
3. seit dem Ziel **hinzugekommene** Dateien aus der Auswahl löschen
   (`git diff --diff-filter=A --name-only <sha> <safety-sha> -- <pfade>`),
4. leere Verzeichnisse, die dadurch entstehen, bleiben stehen (wie im Desktop).

**Übersprungen wie im Desktop:** Pfade, die Symlink/Hardlink/kein reguläres File sind, sowie
Pfade, deren Elternverzeichnis nicht mehr existiert → werden gezählt und als
„Restored the code, but skipped N files"-Hinweis gemeldet, nicht stillschweigend überschrieben.

## Retention (neu, einstellbar)
- **Setting:** `CheckpointRetentionDays`, Combo mit den bestehenden Presets
  `AstrogatorOptions.RetentionDayChoices` (`0, 7, 14, 30, 60, 90, 180, 365`), Label über
  `RetentionLabel()` → **`0` = „Never" = unbegrenzt behalten**. **Default 30**.
  Fügt sich damit exakt in das Muster von `HistoryRetentionDays` (Default 90) und
  `PastedRetentionDays` (Default 30) ein — dritte `MakeRetentionCombo()` im Settings-Fenster.
- **Wann geräumt wird:** wie bei den anderen beiden — beim VS-Start und bei jedem Speichern der
  Settings (`RetentionService`), zusätzlich beim Init eines Schatten-Repos.
- **Wie geräumt wird:** Commit-Datum jedes Refs über
  `git for-each-ref --format="%(refname) %(committerdate:unix)" refs/ca-checkpoints` →
  alles älter als das Fenster: `git update-ref -d <ref>`; danach einmal
  `git gc --prune=now --quiet`. Bleiben **keine** Refs übrig, wird das Repo-Verzeichnis
  gelöscht (nächster Prompt initialisiert neu).
- **Zusätzliche Grenze:** die UI listet höchstens die **100 jüngsten** Checkpoints je Session
  (Desktop-Parität); ältere bleiben bei „Never" auf Platte, sind aber nicht mehr im Menü.
- **Abgelaufene Checkpoints in der UI:** persistierte `checkpointSha`-Werte werden beim Laden
  eines Transcripts gegen `git cat-file -e <sha>^{commit}` geprüft (ein Batch-Aufruf via
  `git cat-file --batch-check` für alle SHAs). Nicht mehr vorhandene → Rewind-Button wird
  ausgegraut mit Tooltip „Checkpoint expired (retention: <n> days)".
- **Wechselwirkung:** Checkpoints können **vor** der Chat-Session ablaufen (30 vs. 90 Tage
  Default). Das ist gewollt (Snapshots kosten Platz, Chats fast nichts) und wird im
  Settings-Fenster als Hinweistext unter der Combo erklärt.
- Wird das Feature abgeschaltet, bleiben vorhandene Checkpoints bis zum Ablauf nutzbar
  (Rewind funktioniert weiter, es entstehen nur keine neuen). Ein „Delete all checkpoints now"-
  Button im Settings-Fenster räumt auf Wunsch sofort (zeigt den belegten Platz an).

## Nachrichten-Kontrakt (Ergänzung zu Teil B §3)

**host → web**
- `session.init` + `checkpoints: { enabled: bool, gitAvailable: bool, retentionDays: int }`
- `checkpoint.created` `{ messageId, turnSeq, sha, shortSha, createdAt }`
- `checkpoint.expired` `{ shas: [sha…] }` (nach dem Batch-Check beim `transcript.load`)
- `checkpoint.preview` `{ sha, scope, files: [{ path, rel, added, removed, status }], filtered: bool, truncated: bool, postMissing: bool }`
- `checkpoint.restored` `{ sha, scope, ok, restoredCount, skipped: [rel…], error? }`
- `checkpoint.settings` `{ enabled, retentionDays }` (Live-Update nach Toggle/Settings-Save)

**web → host**
- `checkpoint.previewRequest` `{ sha, scope: "turns" | "all" }`
- `checkpoint.restore` `{ sha, scope: "code" | "conversation" | "both", paths?: [rel…], allFiles?: bool }`
- `checkpoints.set` `{ enabled }`
- `consent.set` + Feld `checkpointsEnabled` (bisher `noticeEnabled` / `updateEnabled`)

## Neue/zu ändernde Dateien

### 1. `Core/GitCheckpointService.cs` (neu, UI-frei + testbar)
Kapselt alle Git-Aufrufe via `System.Diagnostics.Process`; Muster und `Quote()` aus
`Core/ClaudeCliProcessHost.cs` wiederverwenden. API:
- `static bool IsGitAvailable()` — `git --version`, Ergebnis cachen.
- `Task EnsureInitializedAsync(solutionDir)` — Schatten-Repo lazy anlegen (+ Retention-Sweep).
- `Task<CheckpointInfo> SnapshotAsync(solutionDir, refName, label)` — `add -A` in den eigenen
  Index, `write-tree`, `commit-tree` (parentless), `update-ref`. Liefert SHA + Kurz-SHA + Zeit.
- `Task<IReadOnlyList<CheckpointFileChange>> DiffAsync(solutionDir, fromSha, toSha, paths?)` —
  `git diff --numstat --diff-filter=ACMRD <from> <to> -- <paths>`; `path/added/removed/status`
  für die Vorschau. `toSha == null` → gegen den aktuellen work-tree.
- `Task<RestoreResult> RestoreAsync(solutionDir, sha, paths?)` — die Schritte oben;
  `RestoreResult { RestoredCount, SkippedPaths, Error }`.
- `Task<IReadOnlyList<CheckpointInfo>> ListAsync(solutionDir, sessionId, max = 100)` —
  `for-each-ref` mit Committer-Datum.
- `Task<ISet<string>> FilterExistingAsync(solutionDir, IEnumerable<string> shas)` —
  `git cat-file --batch-check` für die „expired"-Erkennung.
- `Task<int> PruneAsync(solutionDir, retentionDays)` — Refs älter als das Fenster löschen,
  `gc --prune=now`; `retentionDays <= 0` → no-op (unbegrenzt). Liefert die Anzahl gelöschter Refs.
- `Task<long> GetSizeAsync(solutionDir)` / `void DeleteRepo(solutionDir)` — für Settings-Anzeige
  und „Delete all checkpoints now".
Alles auf Hintergrund-Thread; Git-Fehler werden gefangen und als Text zurückgegeben —
das Feature darf einen Turn **nie** crashen lassen.

### 2. Settings & Consent
- `Options/AstrogatorOptions.cs`:
  - `public bool CheckpointsEnabled { get; set; } = true;`
  - `public bool CheckpointsDecided { get; set; } = false;` (Muster `NoticeFetchDecided`, Z. 43-56)
  - `public int CheckpointRetentionDays { get; set; } = 30;` (direkt neben
    `HistoryRetentionDays` Z. 109 / `PastedRetentionDays` Z. 113; nutzt
    `RetentionDayChoices` Z. 116 und `ClampRetentionDays` Z. 119 unverändert)
- `Services/AstrogatorSettingsStore.cs`: die drei Felder in `Read()`/`Write()`
  (`GetBool`/`SetBoolean`, `GetInt32`/`SetInt32` wie bei den anderen Retention-Werten).
- `ToolWindows/AstrogatorSettingsWindow.cs`:
  - `MakeCheck("Create a git checkpoint before each prompt (rewind anytime)")` — ohne Git
    deaktiviert + Hinweistext „Git not found".
  - dritte `MakeRetentionCombo()` (Feld `_checkpointRetention`) → „Keep checkpoints for",
    plus Hinweiszeile „Never = keep until you delete them. Checkpoints can expire before the
    chat history they belong to." und Button **„Delete all checkpoints now (<größe>)"**.
  - `SelectRetention`/`SelectedRetention` (Z. 356-363) unverändert mitnutzen.
- `WebUI/app.js` — **Consent-Modal** (`evaluateBanners` Z. 408, `openConsentPopup` Z. 420):
  - Modal öffnet künftig auch, wenn `!s.checkpoints.decided` → bestehende Installationen sehen es
    einmal erneut.
  - Dritte `consentRow`: **„Create a checkpoint before each prompt (rewind files anytime)"**,
    vorbelegt an; bei `gitAvailable === false` deaktiviert + Hinweis „Git not found".
  - Titel/Body anpassen (es geht nicht mehr nur um Netzwerk-Abfragen); die Checkpoint-Zeile
    bekommt den Zusatz „local only, no network — kept for 30 days by default".
  - `post("consent.set", { noticeEnabled, updateEnabled, checkpointsEnabled })`.
- `Bridge/WebViewBridge.cs` `case "consent.set"` (Z. 319): drittes Feld lesen,
  `CheckpointsDecided` setzen, persistieren, `checkpoint.settings` zurückposten.
- Zahnrad-Popover: Schnell-Toggle (Muster wie der Ultracode-/Versions-Eintrag) → `checkpoints.set`.

### 3. `Bridge/WebViewBridge.cs` (Orchestrierung)
- **Vor dem Prompt:** in `RunPrompt` (Z. 486, läuft auf `TaskScheduler.Default`) vor
  `RunTurnAsync`: wenn `CheckpointsEnabled` && Git verfügbar && `cwd` vorhanden →
  `EnsureInitializedAsync` + `SnapshotAsync(<turnSeq>-pre)`. Ergebnis via `checkpoint.created`
  posten **und** am User-Message-Objekt persistieren (§5).
- **Am Turn-Ende:** dort, wo `turn.result` verarbeitet wird, `SnapshotAsync(<turnSeq>-post)`
  (fire-and-forget mit Timeout) und den Post-SHA an derselben User-Message ablegen. Auch bei
  Abbruch (`turn.stop`) und Fehler-Ende ausführen.
- **Neue web→host-Cases** im Switch (ab Z. 221):
  - `checkpoint.previewRequest` → Scope `turns`: Pfad-Vereinigung der Turn-Diffs ab N bilden,
    dann `DiffAsync(sha, null, paths)` gegen den aktuellen Stand; Scope `all`: ohne Pfadfilter →
    `checkpoint.preview`.
  - `checkpoint.restore` → `HandleCheckpointRestore`: blockt, wenn ein Turn läuft
    (`status != ready`); Scope `code`/`both` → `RestoreAsync`; Scope `conversation`/`both` →
    `_pendingRestoreNote` setzen + History-Markierung; Antwort `checkpoint.restored`
    (+ `system.note`, bei Fehler `error`).
  - `checkpoints.set` → Setting schreiben + `checkpoint.settings`.
- **Beim `transcript.load`:** alle `checkpointSha` der Session durch `FilterExistingAsync`
  schicken und die fehlenden per `checkpoint.expired` melden.
- **Restore-Hinweis für Claude:** `_pendingRestoreNote` wird in `RunPrompt` **einmalig** vorne an
  den nächsten User-Prompt gehängt und dann geleert, z. B.
  `[System] Der Workspace wurde auf den Stand vor Turn N (Checkpoint <kurz-sha>, <zeit>)`
  `zurückgesetzt; N Dateien wurden zurückgeschrieben. Die Turns nach N gelten als verworfen —`
  `ignoriere sie.`
- `session.init` (Z. 2766) um `checkpoints { enabled, gitAvailable, retentionDays }` erweitern.
- Service-Instanz `_checkpoints` im Bridge-Ctor (analog zu den übrigen Core-Services).

### 4. `WebUI/app.js` + `index.html` + Styles
- **Hover-Rewind-Button** an der User-Bubble (`renderUserMessage`, Z. 1099): kleiner Icon-Button,
  per CSS nur bei `:hover`/`:focus-within` der Bubble sichtbar (Tastatur: fokussierbar), Tooltip
  „Rewind to here". Nur rendern, wenn `session.init.checkpoints.enabled` **und** die Nachricht
  einen `checkpointSha` hat; bei `checkpoint.expired` ausgegraut + Tooltip „Checkpoint expired".
- **Klick → `checkpoint.previewRequest`** (Scope `turns`), Popover/Modal öffnet mit Spinner.
- **Vorschau-Modal** (Muster `modal`/`modal-actions` aus dem Consent-Popup, Dateiliste analog der
  Turn-Review-Liste Z. 3013):
  - Kopf: „Rewind to this point — <zeit>".
  - Dateiliste mit `+n/−m`, Häkchen pro Datei (alle vorausgewählt), Klick auf den Pfad öffnet die
    Datei über den bestehenden `editor.openFile`-Weg.
  - Untertitel erklärt den Umfang: „Changes Claude made in the turns after this point (including
    bash and scripts)". Bei `postMissing: true` Zusatz „one turn ended unexpectedly — the list may
    include your own edits".
  - Umschalter **„Include all workspace changes"** → erneuter `previewRequest` mit Scope `all`
    (zeigt dann auch die eigenen manuellen Änderungen).
  - Drei Aktions-Buttons entsprechend §Restore-Scopes; die Code-Buttons **fehlen**, wenn die
    Vorschau leer ist (dann nur „Rewind conversation to here" + „Never mind").
- **`checkpoint.restored`:** `systemNote` mit Anzahl; bei `skipped.length > 0` zusätzlich
  „Restored the code, but skipped N files" (Liste im Tooltip). Bei Scope `conversation`/`both`:
  alle Nachrichten **nach** der Zielnachricht per CSS-Klasse `rewound` ausgrauen + einmaliger
  Trenner „Rewound to here", und den Prompt-Text der Zielnachricht zurück ins Eingabefeld legen
  (Desktop-Verhalten) — Fokus ins Eingabefeld.
- `loadTranscript` (Z. 691): `checkpointSha` und `rewound`-Flag aus der History wiederherstellen,
  damit Buttons und Ausgrauung einen Reload überleben.
- Nach jeder JS-Änderung `node --check WebUI\app.js`.

### 5. History-Persistenz: `Services/SessionHistoryStore.cs`
Die User-Message-`JObject`s bekommen optionale Felder (das Modell ist bereits generisch
`List<JObject>`, Z. 22 — nur Save/Load durchlassen und in `MaxPersistedMessagesPerSession`
mitdenken):
- `checkpointSha` / `checkpointShortSha` — Pre-Snapshot, Ziel des Rewind-Buttons,
- `checkpointPostSha` — Post-Snapshot des Turns, Grundlage des Turn-Diffs,
- `rewound: true` — für die Ausgrauung nach einem Conversation-Rewind.

### 6. Retention: `Services/RetentionService.cs`
- `CheckpointRetentionDays` in den bestehenden Sweep aufnehmen (VS-Start + Settings-Save):
  `GitCheckpointService.PruneAsync(cwd, days)`; `0` → nichts tun.
- Wird eine Session/ein Workspace endgültig verworfen, auch deren Refs bzw. das ganze
  Schatten-Repo löschen („Checkpoints sterben mit der Session", wie im Desktop).

## Wiederverwendete vorhandene Bausteine
- Prozess/Quoting: `Core/ClaudeCliProcessHost.cs` (ProcessStartInfo-Muster, `Quote()`).
- Solution-Pfad: `CodeAstrogatorPackage.GetSolutionDirectory()` (UI-Thread; vor `await` capturen —
  Muster Z. 1716/2594).
- Retention-UI + -Logik: `AstrogatorOptions.RetentionDayChoices` / `ClampRetentionDays`,
  `AstrogatorSettingsWindow.MakeRetentionCombo` / `SelectRetention` / `SelectedRetention`,
  `Services/RetentionService.cs`.
- Settings: `AstrogatorOptions` / `AstrogatorSettingsStore` / `AstrogatorSettingsWindow.MakeCheck`.
- Modal-/Listen-UI: `openConsentPopup` (Z. 420) und die Turn-Review-Dateiliste (Z. 3013).
- Datei öffnen: `editor.openFile` / `Core/FileOpenRouter.cs`.
- Nachrichten-Kontrakt: `OnWebMessageReceived`-Switch + `Post`/`PostOrQueue`; JS `post()`.

## Randfälle / Hinweise
- Keine Solution offen / kein `cwd` → Feature für diese Session inaktiv (kein Fehler).
- Git nicht installiert → Consent-Zeile und Settings-Checkbox deaktiviert, keine Checkpoints,
  kein Crash. (Die Desktop-App verlangt auf Windows ebenfalls Git for Windows.)
- Restore während laufendem Turn unterbinden (Status prüfen, Fehler-Notify).
- Erster Snapshot eines großen Repos kann dauern → Snapshots blockieren den Turn nie,
  Statuszeile („Creating checkpoint…"), Timeout (z. B. 30 s) mit stillem Skip; ein fehlender
  Pre-Snapshot bedeutet nur: diese Nachricht bekommt keinen Rewind-Button.
- Projekt ohne `.gitignore` → Default-Excludes via `info/exclude`.
- Schatten-`.git` liegt außerhalb des Projekts → `git status` im echten Repo bleibt unverändert.
- Restore schreibt **Dateien**; leere Verzeichnisse und Verzeichnis-Umbenennungen werden nicht
  rückgängig gemacht (gleiche Einschränkung wie im Desktop).
- Symlinks/Hardlinks werden übersprungen und gezählt (siehe §Restore-Scopes).
- Änderungen, die der **User während** eines laufenden Turns macht, landen im Turn-Diff und damit
  in der Vorauswahl — technisch nicht von Claudes Änderungen unterscheidbar; die Per-Datei-Häkchen
  sind der Ausweg.
- Chat-Rewind kürzt den **CLI-Kontext nicht** — die verworfenen Turns bleiben in der Session und
  kosten weiter Tokens; der Hinweis an Claude ist der Ersatz. In der UI klar kommunizieren
  („conversation is marked as discarded, not deleted"). Ein echtes Kürzen wäre nur über das
  interne JSONL-Format möglich (laut Docs instabil) — bewusst **nicht** geplant.
- Retention „Never" kann bei großen Repos Platz kosten → Größenanzeige + „Delete all checkpoints
  now" im Settings-Fenster.

## Verifikation (End-to-End)
1. Build (nur VS-MSBuild):
   `MSBuild.exe CodeAstrogator.slnx /t:Restore,Build /p:Configuration=Release /m /v:m`
2. Unit-Tests für `GitCheckpointService` (vstest) mit Temp-Verzeichnis + echtem Git:
   - init → Datei anlegen → `SnapshotAsync` → ändern → `RestoreAsync` → alter Inhalt zurück;
   - nach Restore neu hinzugekommene Datei entfernt;
   - **gefilterter** Restore lässt nicht ausgewählte Dateien unangetastet;
   - `DiffAsync` liefert korrekte `+/-`-Zahlen und erfasst eine Änderung, die **nicht** über ein
     Edit-Tool kam (Datei per Test direkt geschrieben = Ersatz für Bash/Script);
   - `PruneAsync`: Ref mit altem Committer-Datum (via `GIT_COMMITTER_DATE`) wird gelöscht, junges
     bleibt; `retentionDays = 0` löscht nichts; nach dem Prune meldet `FilterExistingAsync` den
     SHA als weg;
   - Symlink-Pfad wird übersprungen und gezählt.
3. `node --check WebUI\app.js`.
4. Manuell in VS (VSIX neu bauen **und** installieren):
   Consent-Modal zeigt die dritte Checkbox → mehrere Prompts → Hover an einer User-Nachricht zeigt
   den Rewind-Button → Vorschau listet die Änderungen der Turns danach, **inklusive einer Änderung,
   die Claude per Bash/Python gemacht hat** → „Rewind code to here" stellt sie wieder her, Chat
   bleibt → „Rewind conversation to here" graut spätere Turns aus und legt den Prompt ins
   Eingabefeld → nächster Turn enthält den Hinweis. Checkpoint ohne Änderungen zeigt **keine**
   Code-Optionen. Retention-Combo auf „7 days" + Systemzeit-/`GIT_COMMITTER_DATE`-Test → alter
   Checkpoint verschwindet, Button wird als „expired" ausgegraut.
5. Gegentest **mit** und **ohne** Projekt-Git-Repo, dass das echte `.git` unangetastet bleibt
   (`git status` im Projekt unverändert).

## Doku & Versionierung (Projektregeln, CLAUDE.md)
- `docs/NOTES.md` (englisch): Schatten-Repo-Konzept, ref-basierte Ablage, Pre/Post-Snapshots und
  die daraus abgeleitete Änderungszuordnung, Restore-Scopes, Kontrakt-Ergänzungen,
  Desktop-Abweichungen (Bash **wird** erfasst; Chat-Rewind ist nur Markierung) und die
  Retention-Einstellung dokumentieren.
- `CHANGELOG.md` (englisch): neuer `## [x.y.z] – YYYY-MM-DD`-Eintrag (Added), gemeinsam mit dem
  Versionsbump.
- Am Turn-Ende: geplante Commit-Message als Vorschau in den Chat, **danach** per
  `AskUserQuestion` Versionsbump **und** Commit/Push-Frage; Commit auf `develope` mit
  `Co-Authored-By: Claude …`. Nicht eigenmächtig nach `main`.
