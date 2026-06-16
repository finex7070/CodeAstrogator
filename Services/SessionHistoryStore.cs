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

        /// <summary>Context size after the last turn (input incl. cache + output) —
        /// drives the "Ctx n%" statusbar meter.</summary>
        public long ContextTokens { get; set; }

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
    /// </summary>
    internal sealed class SessionHistoryStore
    {
        private const int MaxPersistedSessions = 50;
        private const int MaxPersistedMessagesPerSession = 400;

        private readonly List<SessionRecord> _sessions = new List<SessionRecord>();
        private string? _path;

        public SessionRecord Current { get; private set; } = new SessionRecord();

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

        /// <summary>Loads the store; missing/corrupt files yield an empty store.</summary>
        public static SessionHistoryStore LoadFrom(string path)
        {
            var store = new SessionHistoryStore { _path = path };
            try
            {
                if (!File.Exists(path))
                    return store;

                var root = JObject.Parse(File.ReadAllText(path));
                foreach (var token in root["sessions"] as JArray ?? new JArray())
                {
                    if (token is not JObject s)
                        continue;
                    var record = new SessionRecord
                    {
                        Id = s.Value<string>("id") ?? Guid.NewGuid().ToString("n"),
                        Title = s.Value<string>("title") ?? "Untitled",
                        UpdatedAtUtc = ParseTimestamp(s["updatedAt"]),
                        HasCliSession = s.Value<bool?>("hasCliSession") ?? false,
                        ContextTokens = s.Value<long?>("contextTokens") ?? 0,
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

        /// <summary>Writes all sessions (incl. the active one). Call under the store lock.</summary>
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
                        ["messages"] = messages,
                    });
                    if (sessions.Count >= MaxPersistedSessions)
                        break;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, new JObject { ["sessions"] = sessions }.ToString());
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

        public SessionRecord StartNew()
        {
            ArchiveCurrent();
            Current = new SessionRecord();
            return Current;
        }

        public SessionRecord? Load(string id)
        {
            var found = _sessions.FirstOrDefault(s => s.Id == id);
            if (found == null)
                return Current.Id == id ? Current : null;

            ArchiveCurrent();
            _sessions.Remove(found);
            Current = found;
            return found;
        }

        private void ArchiveCurrent()
        {
            if (Current.Messages.Count > 0)
                _sessions.Insert(0, Current);
        }

        /// <summary>
        /// Inserts or replaces a session imported from the CLI's own store (remote
        /// control). The transcript is authoritative — existing messages are replaced.
        /// </summary>
        public SessionRecord Import(string id, string title, IEnumerable<JObject> messages, DateTime updatedAtUtc, long contextTokens = 0)
        {
            var record = Current.Id == id ? Current : _sessions.FirstOrDefault(s => s.Id == id);
            if (record == null)
            {
                record = new SessionRecord { Id = id };
                _sessions.Insert(0, record);
            }
            record.Title = title;
            record.HasCliSession = true;
            record.UpdatedAtUtc = updatedAtUtc;
            record.ContextTokens = contextTokens;
            record.Messages.Clear();
            record.Messages.AddRange(messages);
            return record;
        }

        /// <summary>Renames a session (current or archived) without touching its recency.</summary>
        public bool Rename(string id, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;
            var record = Current.Id == id ? Current : _sessions.FirstOrDefault(s => s.Id == id);
            if (record == null)
                return false;
            record.Title = title.Trim();
            return true;
        }

        /// <summary>Payload for session.list (current first, then most recent).</summary>
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

        private IEnumerable<SessionRecord> Enumerate()
        {
            if (Current.Messages.Count > 0)
                yield return Current;
            foreach (var s in _sessions.OrderByDescending(s => s.UpdatedAtUtc))
                yield return s;
        }
    }
}
