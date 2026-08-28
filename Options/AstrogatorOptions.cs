using System.Collections.Generic;
using System.Linq;

namespace CodeAstrogator.Options
{
    /// <summary>
    /// In-memory snapshot of the persisted settings. Loaded by the package from the
    /// WritableSettingsStore on startup and updated when the settings window (or the gear
    /// popover) writes a value. The property initializers below define the defaults
    /// (also used by the settings window's "Reset to defaults").
    /// </summary>
    internal sealed class AstrogatorOptions
    {
        public string ClaudeExecutablePath { get; set; } = "";

        // ── Model·Mode popover state (persisted across new chats + VS restarts) ──
        // These are driven by the in-chat Model·Mode popover (model.set / effort.set /
        // ultracode.set / permission.set), NOT by the settings window. They are the sticky
        // defaults a new session starts with.
        public string DefaultModel { get; set; } = "";
        public string DefaultEffortString { get; set; } = "high";
        public bool UltracodeEnabled { get; set; } = false;
        public string PermissionModeString { get; set; } = "ask";

        /// <summary>When on, "Auto-accept edits" mode also auto-approves every non-question tool
        /// (Bash/PowerShell/MCP …) without a prompt — edits are already auto-accepted by the CLI
        /// in that mode, so this extends the same trust to commands. AskUserQuestion still prompts.
        /// No effect outside acceptEdits. Off by default; toggled in the Model·Mode popover.</summary>
        public bool AutoAcceptCommands { get; set; } = false;

        /// <summary>Glob patterns (<c>*</c> = wildcard). A Bash/PowerShell command or MCP tool name
        /// matching any pattern is auto-approved without a permission prompt. Persisted as a JSON
        /// array (legacy newline-separated strings are still read for backward compatibility).
        /// Grown by the "Always" button on permission cards; editable in the settings window.</summary>
        public List<string> AutoApprovePatterns { get; set; } = new List<string>();

        /// <summary>Custom brand/accent color as a CSS hex string (e.g. <c>#8d5fc7</c>). Empty =
        /// use the built-in per-theme default. Set from the gear popover's color picker.</summary>
        public string AccentColor { get; set; } = "";

        /// <summary>Whether the WebUI may periodically fetch the announcement/notice file from the
        /// project's GitHub (a network request on each window open) and show it as a banner.
        /// Off by default; the user opts in via the first-run consent popup or the settings window.</summary>
        public bool NoticeFetchEnabled { get; set; } = false;

        /// <summary>Whether the user has already answered the first-run notice-fetch consent popup
        /// (or set the value in the settings window). While false, the popup is shown once.</summary>
        public bool NoticeFetchDecided { get; set; } = false;

        /// <summary>Whether the WebUI may periodically check the project's GitHub (version.json) for a
        /// newer release and show an update banner. Off by default; opted in via the consent popup or
        /// the settings window. Shares the first-run consent popup with <see cref="NoticeFetchEnabled"/>.</summary>
        public bool UpdateCheckEnabled { get; set; } = false;

        /// <summary>Whether the user has decided the update-check opt-in (consent popup or settings).
        /// While false (for either this or <see cref="NoticeFetchDecided"/>), the popup is shown.</summary>
        public bool UpdateCheckDecided { get; set; } = false;

        /// <summary>How long (minutes) the CLI waits on a permission/AskUserQuestion prompt before
        /// it times out (applied via MCP_TOOL_TIMEOUT). Clamped to [<see cref="MinPromptTimeoutMinutes"/>,
        /// <see cref="MaxPromptTimeoutMinutes"/>]. Default 60 (1 h).</summary>
        public int PromptTimeoutMinutes { get; set; } = 60;
        public const int MinPromptTimeoutMinutes = 1;
        public const int MaxPromptTimeoutMinutes = 240; // 4 h

        /// <summary>Clamps an arbitrary minutes value into the allowed range.</summary>
        public static int ClampPromptTimeoutMinutes(int minutes) =>
            minutes < MinPromptTimeoutMinutes ? MinPromptTimeoutMinutes
            : minutes > MaxPromptTimeoutMinutes ? MaxPromptTimeoutMinutes
            : minutes;

        public bool RestoreLastSession { get; set; } = true;
        public bool AutoAddActiveFile { get; set; } = true;
        public bool IncludeSelectedLines { get; set; } = true;
        /// <summary>Default state of the per-chat active-file reference: true = a new chat starts
        /// with the file referenced (chip on); false = it starts off and the user toggles it on via
        /// the composer chip. Only meaningful when <see cref="AutoAddActiveFile"/> is enabled.</summary>
        public bool ActiveFileOnByDefault { get; set; } = true;
        public string ThemeModeString { get; set; } = "auto";
        public string VerbosityString { get; set; } = "normal";

        /// <summary>
        /// When on, drive the CLI as one long-lived bidirectional stream-json process
        /// (lower per-turn latency + in-place interrupt) instead of one process per turn.
        /// Opt-in; the per-turn host stays the default. See docs/NOTES.md ("Persistent CLI").
        /// </summary>
        public bool UsePersistentCli { get; set; } = false;

        /// <summary>When on, file-edit permission prompts (Edit/Write/MultiEdit) are reviewed
        /// <em>in the code editor</em> instead of via the inline diff card: the chat shows a file
        /// card, clicking it opens the file with an inline red/green diff and per-hunk Accept/Reject
        /// (partial acceptance is returned to the CLI via <c>updatedInput</c>). Off by default;
        /// toggled in the gear/appearance popover. Only takes effect in the modes that actually
        /// prompt for edits (Ask/Plan) — in Auto-accept/Bypass the CLI applies edits without a prompt.</summary>
        public bool ReviewEditsInEditor { get; set; } = false;

        /// <summary>When on, "Auto-accept edits" mode still applies every edit live during the turn,
        /// but at the <em>end of the turn</em> a list of all changed files appears above the composer.
        /// Clicking a file opens it in the same in-editor red/green per-hunk review; rejecting a hunk
        /// reverts the already-applied change on disk. The next prompt is blocked until the list is
        /// cleared (every file reviewed, or "Keep all"). Off by default; toggled in the Model·Mode
        /// popover under Auto-accept edits. Only meaningful in <c>acceptEdits</c> mode — to guarantee a
        /// pre-edit baseline it launches the CLI so edits pass through the permission hook (auto-approved
        /// there) instead of the CLI's own auto-accept.</summary>
        public bool ReviewEditsAtTurnEnd { get; set; } = false;

        /// <summary>Auto-delete chat-history sessions whose last activity is older than this many days.
        /// Runs on VS startup and whenever the settings are saved, and is also enforced when a
        /// workspace's history is loaded. <c>0</c> = keep forever (no time-based cleanup). Default 90.</summary>
        public int HistoryRetentionDays { get; set; } = 90;

        /// <summary>Auto-delete pasted-image files (…\CodeAstrogator\pasted) older than this many days.
        /// Runs on VS startup and whenever the settings are saved. <c>0</c> = keep forever. Default 30.</summary>
        public int PastedRetentionDays { get; set; } = 30;

        /// <summary>When on, a file snapshot ("checkpoint") is taken before every prompt and at the end
        /// of every turn, so any message can be rewound to — Claude's edits <em>and</em> whatever its bash
        /// commands and scripts changed. Snapshots live in a shadow git repo outside the project (see
        /// <c>Core/GitCheckpointService</c>); the user's own repo is never touched. Requires git on PATH.
        /// On by default (like the Claude desktop app); asked in the first-run consent popup and
        /// toggleable in the gear popover.</summary>
        public bool CheckpointsEnabled { get; set; } = true;

        /// <summary>Whether the user has answered the checkpoint question (consent popup or settings
        /// window). While false — as for the notice/update opt-ins — the consent popup is shown.</summary>
        public bool CheckpointsDecided { get; set; } = false;

        /// <summary>Auto-delete file checkpoints older than this many days (swept on VS startup and on
        /// every settings save). <c>0</c> = keep forever. Default 30 — same window the CLI uses, and
        /// deliberately shorter than <see cref="HistoryRetentionDays"/>: snapshots cost disk space,
        /// chat history barely any, so a checkpoint may expire before the chat it belongs to.</summary>
        public int CheckpointRetentionDays { get; set; } = 30;

        /// <summary>Largest file (MB) a checkpoint snapshot includes. Big binaries are what makes a
        /// snapshot expensive, and a rewind is meant for source recovery. <c>0</c> = no limit.
        /// Default 10.</summary>
        public int CheckpointMaxFileMb { get; set; } = 10;
        public const int MaxCheckpointFileMb = 4096;

        /// <summary>How <see cref="CheckpointExtensions"/> is read: <c>false</c> (default) = blacklist,
        /// those extensions are skipped; <c>true</c> = whitelist, only those are snapshotted.</summary>
        public bool CheckpointExtensionsAreWhitelist { get; set; } = false;

        /// <summary>File extensions for the checkpoint filter (normalized to lowercase with a leading
        /// dot). Persisted as a JSON array, like <see cref="AutoApprovePatterns"/>.</summary>
        public List<string> CheckpointExtensions { get; set; } = new List<string>(DefaultCheckpointExtensions);

        /// <summary>Default blacklist: build output, binaries, archives, media, captures and databases —
        /// the things that blow up a snapshot without ever being worth rewinding.</summary>
        public static readonly string[] DefaultCheckpointExtensions =
        {
            ".exe", ".dll", ".lib", ".so", ".bin", ".dat", ".zip", ".7z", ".rar", ".gz", ".tar", ".iso", ".msi",
            ".cache", ".apk", ".aab", ".pak", ".pdb", ".dmp", ".log", ".trace"
        };

        /// <summary>Clamps the file-size limit to a sane range (negative → 0 = no limit).</summary>
        public static int ClampCheckpointMaxFileMb(int mb) =>
            mb < 0 ? 0 : mb > MaxCheckpointFileMb ? MaxCheckpointFileMb : mb;

        /// <summary>Normalizes user input ("EXE", " .Bin ") to lowercase extensions with a leading dot.</summary>
        public static List<string> NormalizeExtensions(IEnumerable<string>? items)
        {
            var result = new List<string>();
            foreach (var raw in items ?? Enumerable.Empty<string>())
            {
                var e = (raw ?? "").Trim().Trim(',', ';').ToLowerInvariant();
                if (e.Length == 0)
                    continue;
                if (e[0] != '.')
                    e = "." + e;
                if (e.Length > 1 && !result.Contains(e))
                    result.Add(e);
            }
            return result;
        }

        /// <summary>Retention-days presets offered in the settings window (0 = "Never").</summary>
        public static readonly int[] RetentionDayChoices = { 0, 7, 14, 30, 60, 90, 180, 365 };

        /// <summary>Snaps an arbitrary retention value to a non-negative day count (negatives → 0).</summary>
        public static int ClampRetentionDays(int days) => days < 0 ? 0 : days;
    }
}
