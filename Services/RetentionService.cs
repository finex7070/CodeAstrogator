using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Best-effort housekeeping for the extension's on-disk data under
    /// <c>%LocalAppData%\CodeAstrogator</c>: deletes chat-history sessions and pasted-image files
    /// older than the user-configured retention windows (see <c>AstrogatorOptions.HistoryRetentionDays</c>
    /// / <c>PastedRetentionDays</c>; <c>0</c> = keep forever). Runs off the UI thread on VS startup and
    /// whenever the settings are saved. All I/O is wrapped in try/catch — cleanup never breaks the app.
    /// The current workspace's in-memory history is kept consistent independently via
    /// <see cref="SessionHistoryStore.LoadFrom"/>, which applies the same cutoff at load time.
    /// </summary>
    internal static class RetentionService
    {
        private static string BaseDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAstrogator");

        /// <summary>Runs both sweeps. Safe to call from a background thread; each part is independent
        /// and best-effort. A non-positive day count skips that sweep entirely.</summary>
        public static void Cleanup(int historyRetentionDays, int pastedRetentionDays)
        {
            try { PrunePastedFiles(pastedRetentionDays); } catch { /* best-effort */ }
            try { PruneHistorySessions(historyRetentionDays); } catch { /* best-effort */ }
        }

        /// <summary>Deletes pasted-image files whose last write time is older than the cutoff.</summary>
        private static void PrunePastedFiles(int days)
        {
            if (days <= 0)
                return;
            var dir = Path.Combine(BaseDir, "pasted");
            if (!Directory.Exists(dir))
                return;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* file locked / already gone — skip it */ }
            }
        }

        /// <summary>Rewrites every workspace history file, dropping sessions whose <c>updatedAt</c> is
        /// older than the cutoff; a file left with no sessions is deleted. Sessions with an
        /// unparseable timestamp are kept (never delete on ambiguity).</summary>
        private static void PruneHistorySessions(int days)
        {
            if (days <= 0)
                return;
            var dir = Path.Combine(BaseDir, "history");
            if (!Directory.Exists(dir))
                return;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try { PruneOneHistoryFile(path, cutoff); }
                catch { /* corrupt / locked file — leave it as-is */ }
            }
        }

        private static void PruneOneHistoryFile(string path, DateTime cutoffUtc)
        {
            var root = JObject.Parse(File.ReadAllText(path));
            if (!(root["sessions"] is JArray sessions) || sessions.Count == 0)
                return;

            var kept = new JArray();
            foreach (var token in sessions)
            {
                if (!(token is JObject s))
                    continue;
                if (IsExpired(s["updatedAt"], cutoffUtc))
                    continue;
                kept.Add(s);
            }

            if (kept.Count == sessions.Count)
                return; // nothing removed → don't touch the file
            if (kept.Count == 0)
            {
                File.Delete(path);
                return;
            }
            root["sessions"] = kept;
            File.WriteAllText(path, root.ToString());
        }

        /// <summary>True when the timestamp parses and lies strictly before the cutoff. Unparseable /
        /// missing timestamps return false (keep the session).</summary>
        internal static bool IsExpired(JToken? updatedAt, DateTime cutoffUtc)
        {
            switch (updatedAt?.Type)
            {
                case JTokenType.Date:
                    return updatedAt.Value<DateTime>().ToUniversalTime() < cutoffUtc;
                case JTokenType.String when DateTime.TryParse(
                    updatedAt.Value<string>(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var d):
                    return d < cutoffUtc;
                default:
                    return false;
            }
        }
    }
}
