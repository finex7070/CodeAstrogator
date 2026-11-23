using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// One chat session as shown in the history panel. Messages follow the
    /// transcript.load Message schema of Teil B §3.1.
    /// </summary>
    internal sealed class SessionRecord
    {
        /// <summary>Local id; replaced by the CLI session_id once known (used for --resume).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("n");

        public string Title { get; set; } = "Untitled";
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<JObject> Messages { get; } = new List<JObject>();

        /// <summary>True once the id is a real CLI session id (resumable).</summary>
        public bool HasCliSession { get; set; }

        /// <summary>Context size after the last turn (input incl. cache + output) — the "Context n%"
        /// statusbar meter falls back to it (with <see cref="ContextWindow"/>) without a breakdown.</summary>
        public long ContextTokens { get; set; }

        /// <summary>The model's context-window size as last reported by the CLI; 0 = unknown.</summary>
        public long ContextWindow { get; set; }

        /// <summary>The CLI's context breakdown after the last turn (the <c>context.usage</c> payload,
        /// see <c>WebViewBridge.BuildContextUsage</c>); null until the first turn reported one.</summary>
        public JObject? ContextUsage { get; set; }

        /// <summary>The CLI's session cost total (<c>result.total_cost_usd</c>) after the last turn. That
        /// value is cumulative over the whole session — even across <c>--resume</c> — so a turn's own cost
        /// is the difference to this. Null = not known yet (record older than this field, imported).</summary>
        public double? CostTotalUsd { get; set; }

        /// <summary>The CLI's prediction of the next prompt (ghost text in the empty composer), kept so it survives a
        /// VS restart / solution reload; cleared when a prompt is sent or the suggestion is dismissed.</summary>
        public string? PromptSuggestion { get; set; }

        public string Preview =>
            Messages.LastOrDefault(m => m.Value<string>("text") is { Length: > 0 })
                ?.Value<string>("text") is string t
                ? (t.Length > 80 ? t.Substring(0, 80) + "…" : t)
                : "";
    }

    /// <summary>
    /// Session store with JSON persistence: one file per workspace under
    /// %LocalAppData%\CodeAstrogator\history. The CLI keeps the actual
    /// conversations, so persisted ids stay resumable across VS restarts.
    /// <para>Several chats can be <b>open</b> at once (one per chat tab): an open record is held by its tab
    /// and lives in the same list as every other record. Records without messages are never listed or
    /// persisted, so an empty "new chat" leaves no trace. Not thread-safe — callers lock on the store.</para>
    /// </summary>
    internal sealed class SessionHistoryStore
    {
        private const int MaxPersistedSessions = 50;
        private const int MaxPersistedMessagesPerSession = 400;

        private readonly List<SessionRecord> _sessions = new List<SessionRecord>();
        private readonly List<SessionRecord> _open = new List<SessionRecord>(); // shown in a tab, in tab order
        private string? _path;

        /// <summary>The records currently shown in a tab (in the order they were opened).</summary>
        public IReadOnlyList<SessionRecord> OpenRecords => _open;

        /// <summary>The chat of the active tab (set by the panel) — persisted so a restart selects that tab again.</summary>
        public string? ActiveOpenId { get; set; }

        /// <summary>The chats that were open in tabs when the file was written (tab order), and the active one —
        /// what the next start restores. Empty for files from before tabs existed.</summary>
        public IReadOnlyList<string> SavedOpenIds { get; private set; } = Array.Empty<string>();
        public string? SavedActiveId { get; private set; }

        // ── persistence ───────────────────────────────────────────────────────

        /// <summary>History file for a workspace (solution dir) — stable hash key.</summary>
        public static string GetHistoryPath(string? solutionDir)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "history");
            var key = string.IsNullOrEmpty(solutionDir)
                ? "global"
                : HashKey(solutionDir!.TrimEnd('\\', '/').ToLowerInvariant());
            return Path.Combine(baseDir, key + ".json");
        }

        /// <summary>Loads the store; missing/corrupt files yield an empty store. When
        /// <paramref name="retentionDays"/> &gt; 0, sessions whose last activity is older than that many
        /// days are dropped at load time (mirrors <see cref="RetentionService"/> so the in-memory view
        /// and the next <see cref="Save"/> reflect the retention window). <c>0</c> = keep everything.</summary>
        public static SessionHistoryStore LoadFrom(string path, int retentionDays = 0)
        {
            var store = new SessionHistoryStore { _path = path };
            try
            {
                if (!File.Exists(path))
                    return store;

                var cutoff = retentionDays > 0 ? DateTime.UtcNow.AddDays(-retentionDays) : (DateTime?)null;
                var root = JObject.Parse(File.ReadAllText(path));
                store.SavedOpenIds = (root["openTabs"] as JArray ?? new JArray())
                    .Select(x => x.Type == JTokenType.String ? x.Value<string>() : null)
                    .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
                store.SavedActiveId = root.Value<string>("activeTab");
                foreach (var token in root["sessions"] as JArray ?? new JArray())
                {
                    if (token is not JObject s)
                        continue;
                    if (cutoff is { } c && RetentionService.IsExpired(s["updatedAt"], c))
                        continue; // older than the retention window → skip
                    var record = new SessionRecord
                    {
                        Id = s.Value<string>("id") ?? Guid.NewGuid().ToString("n"),
                        Title = s.Value<string>("title") ?? "Untitled",
                        UpdatedAtUtc = ParseTimestamp(s["updatedAt"]),
                        HasCliSession = s.Value<bool?>("hasCliSession") ?? false,
                        ContextTokens = s.Value<long?>("contextTokens") ?? 0,
                        ContextWindow = s.Value<long?>("contextWindow") ?? 0,
                        ContextUsage = s["contextUsage"] as JObject,
                        CostTotalUsd = s.Value<double?>("costTotalUsd"),
                        PromptSuggestion = s.Value<string>("promptSuggestion"),
                    };
                    foreach (var m in s["messages"] as JArray ?? new JArray())
                    {
                        if (m is JObject message)
                            record.Messages.Add(message);
                    }
                    store._sessions.Add(record);
                }
            }
            catch
            {
                // corrupt history file — start fresh rather than break the panel
            }
            return store;
        }

        /// <summary>The history file this store reads/writes (one per workspace, see <see cref="GetHistoryPath"/>).</summary>
        public string? FilePath => _path;

        /// <summary>Switches this store to another workspace's history file (the panel followed a solution/folder
        /// being opened or closed): drops the in-memory sessions and loads that file's, with nothing open — the
        /// caller opens the chats its tabs show next. The instance stays the same, so everything that locks on it
        /// keeps working. Save first.</summary>
        public void ReloadFrom(string path, int retentionDays = 0)
        {
            var other = LoadFrom(path, retentionDays);
            _sessions.Clear();
            _sessions.AddRange(other._sessions);
            _open.Clear();
            _path = other._path;
            SavedOpenIds = other.SavedOpenIds;
            SavedActiveId = other.SavedActiveId;
            ActiveOpenId = null;
        }

        /// <summary>Writes all sessions with messages (open ones first, so they are never cut by the cap). Call
        /// under the store lock.</summary>
        public void Save()
        {
            if (_path == null)
                return;
            try
            {
                var sessions = new JArray();
                foreach (var s in Enumerate())
                {
                    var messages = new JArray();
                    foreach (var m in s.Messages.Skip(Math.Max(0, s.Messages.Count - MaxPersistedMessagesPerSession)))
                        messages.Add(m.DeepClone());

                    sessions.Add(new JObject
                    {
                        ["id"] = s.Id,
                        ["title"] = s.Title,
                        ["updatedAt"] = s.UpdatedAtUtc.ToString("o"),
                        ["hasCliSession"] = s.HasCliSession,
                        ["contextTokens"] = s.ContextTokens,
                        ["contextWindow"] = s.ContextWindow,
                        ["contextUsage"] = s.ContextUsage?.DeepClone(),
                        ["costTotalUsd"] = s.CostTotalUsd,
                        ["promptSuggestion"] = s.PromptSuggestion,
                        ["messages"] = messages,
                    });
                    if (sessions.Count >= MaxPersistedSessions)
                        break;
                }

                // the open tabs (only chats with messages — an empty tab has nothing to restore)
                var open = _open.Where(s => s.Messages.Count > 0).Select(s => s.Id).ToList();
                var root = new JObject { ["sessions"] = sessions, ["openTabs"] = new JArray(open) };
                if (ActiveOpenId != null && open.Contains(ActiveOpenId))
                    root["activeTab"] = ActiveOpenId;

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, root.ToString());
            }
            catch
            {
                // persistence is best-effort
            }
        }

        private static DateTime ParseTimestamp(JToken? token)
        {
            switch (token?.Type)
            {
                case JTokenType.Date:
                    return token.Value<DateTime>().ToUniversalTime();
                case JTokenType.String when DateTime.TryParse(
                    token.Value<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d):
                    return d;
                default:
                    return DateTime.UtcNow;
            }
        }

        private static string HashKey(string value)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ── session management ───────────────────────────────────────────────

        /// <summary>Opens a fresh, empty chat (a new tab, or "new chat" after <see cref="Close"/> of the old one).
        /// It shows up in the list once it has a message.</summary>
        public SessionRecord StartNew()
        {
            var record = new SessionRecord();
            _sessions.Add(record);
            _open.Add(record);
            return record;
        }

        /// <summary>The record with this id (open or not), or null.</summary>
        public SessionRecord? Find(string id) =>
            string.IsNullOrEmpty(id) ? null : _sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Marks the chat with this id as open (shown in a tab) and returns it; null when unknown. Opening
        /// an already open chat returns it unchanged — keeping one chat in one tab is the caller's job.</summary>
        public SessionRecord? Open(string id)
        {
            var found = Find(id);
            if (found != null && !_open.Contains(found))
                _open.Add(found);
            return found;
        }

        /// <summary>The tab stops showing this chat. An empty one (never got a message) is dropped entirely.</summary>
        public void Close(SessionRecord record)
        {
            _open.Remove(record);
            if (record.Messages.Count == 0)
                _sessions.Remove(record);
        }

        /// <summary>True when this chat is shown in a tab.</summary>
        public bool IsOpen(string id) =>
            _open.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the history holds a session with this id (open or not).</summary>
        public bool Contains(string id) => Find(id) != null;

        /// <summary>
        /// Inserts or replaces a session imported from the CLI's own store (remote
        /// control). The transcript is authoritative — existing messages are replaced.
        /// An existing session keeps its title (it may have been renamed). Does not open it.
        /// </summary>
        public SessionRecord Import(string id, string title, IEnumerable<JObject> messages, DateTime updatedAtUtc, long contextTokens = 0)
        {
            var record = Find(id);
            if (record == null)
            {
                record = new SessionRecord { Id = id, Title = title };
                _sessions.Add(record);
            }
            else if (string.IsNullOrWhiteSpace(record.Title) || record.Title == "Untitled")
            {
                record.Title = title;
            }
            record.HasCliSession = true;
            record.UpdatedAtUtc = updatedAtUtc;
            record.ContextTokens = contextTokens;
            record.Messages.Clear();
            record.Messages.AddRange(messages);
            return record;
        }

        /// <summary>
        /// Deletes a session (open or not). Returns whether anything was removed and whether it was open — in
        /// which case the tab showing it should start a fresh chat and re-init its UI (like "new chat"). The CLI
        /// keeps its own conversation store, so a deleted session may still be resumable via --resume.
        /// </summary>
        public (bool deleted, bool wasOpen) Delete(string id)
        {
            var found = Find(id);
            if (found == null)
                return (false, false);
            _sessions.Remove(found);
            return (true, _open.Remove(found));
        }

        /// <summary>Renames a session (open or not) without touching its recency.</summary>
        public bool Rename(string id, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;
            var record = Find(id);
            if (record == null)
                return false;
            record.Title = title.Trim();
            return true;
        }

        /// <summary>Payload for session.list (open chats first, then most recent).</summary>
        public JArray ToSessionList()
        {
            var arr = new JArray();
            foreach (var s in Enumerate())
            {
                arr.Add(new JObject
                {
                    ["id"] = s.Id,
                    ["title"] = s.Title,
                    ["updatedAt"] = s.UpdatedAtUtc.ToString("o"),
                    ["preview"] = s.Preview,
                });
            }
            return arr;
        }

        /// <summary>Every record with messages: the open ones first (most recent first), then the rest.</summary>
        private IEnumerable<SessionRecord> Enumerate() =>
            _open.Where(s => s.Messages.Count > 0).OrderByDescending(s => s.UpdatedAtUtc)
                .Concat(_sessions.Where(s => s.Messages.Count > 0 && !_open.Contains(s)).OrderByDescending(s => s.UpdatedAtUtc));
    }
}
