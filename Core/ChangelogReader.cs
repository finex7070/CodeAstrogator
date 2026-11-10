using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAstrogator.Core
{
    /// <summary>One version's section of CHANGELOG.md.</summary>
    public sealed class ChangelogEntry
    {
        public string Version { get; set; } = "";
        public string? Date { get; set; }
        /// <summary>The section's Markdown without its "## [x.y.z]" heading.</summary>
        public string Markdown { get; set; } = "";
    }

    /// <summary>
    /// Reads the CHANGELOG.md shipped in the VSIX for the "What's new" popup that opens the first time the panel
    /// runs on a new version. Headings look like <c>## [1.3.0] – 2026-11-10</c> (en dash or hyphen).
    /// </summary>
    public static class ChangelogReader
    {
        private static readonly Regex Heading = new Regex(@"^##\s+\[(?<v>\d+(?:\.\d+){1,3})\]\s*(?:[–—-]\s*(?<d>\S+))?", RegexOptions.Compiled);

        public static IReadOnlyList<ChangelogEntry> Parse(string text)
        {
            var entries = new List<ChangelogEntry>();
            ChangelogEntry? current = null;
            var body = new StringBuilder();
            void Flush()
            {
                if (current == null)
                    return;
                current.Markdown = body.ToString().Trim();
                entries.Add(current);
                body.Clear();
            }
            foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Heading.Match(line);
                if (m.Success)
                {
                    Flush();
                    current = new ChangelogEntry { Version = m.Groups["v"].Value, Date = m.Groups["d"].Success ? m.Groups["d"].Value : null };
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal) || line.Trim() == "---")
                {
                    // another level-2 heading or a separator ends the version section
                    Flush();
                    current = null;
                    continue;
                }
                if (current != null)
                    body.AppendLine(line);
            }
            Flush();
            return entries;
        }

        /// <summary>The sections newer than <paramref name="lastSeen"/> up to <paramref name="current"/>, newest first,
        /// at most <paramref name="max"/>. <paramref name="lastSeen"/> null = only <paramref name="current"/>'s section.</summary>
        public static IReadOnlyList<ChangelogEntry> Since(IReadOnlyList<ChangelogEntry> entries, string? lastSeen, string current, int max = 5)
        {
            if (!TryVersion(current, out var cur))
                return Array.Empty<ChangelogEntry>();
            Version? last = null;
            if (lastSeen != null && !TryVersion(lastSeen, out last))
                last = null;
            return entries
                .Select(e => (e, ok: TryVersion(e.Version, out var v), v))
                .Where(x => x.ok && x.v <= cur && (lastSeen == null ? x.v == cur : last == null || x.v > last))
                .OrderByDescending(x => x.v)
                .Take(max)
                .Select(x => x.e)
                .ToList();
        }

        /// <summary>True when <paramref name="current"/> is newer than <paramref name="lastSeen"/>.</summary>
        public static bool IsNewer(string? current, string? lastSeen) =>
            TryVersion(current, out var c) && (!TryVersion(lastSeen, out var l) || c > l);

        private static bool TryVersion(string? s, out Version v)
        {
            v = new Version(0, 0);
            return !string.IsNullOrWhiteSpace(s) && Version.TryParse(s!.Trim(), out v!);
        }
    }
}
