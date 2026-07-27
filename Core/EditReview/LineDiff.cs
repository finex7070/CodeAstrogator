using System;
using System.Collections.Generic;

namespace CodeAstrogator.Core.EditReview
{
    public enum SegmentKind { Unchanged, Changed }

    /// <summary>One contiguous run of a line-based diff between an old and a new text.</summary>
    public sealed class LineSegment
    {
        public SegmentKind Kind { get; }

        /// <summary>Old-side lines (verbatim; the trailing <c>\n</c> is excluded). Empty for a pure insertion.</summary>
        public IReadOnlyList<string> OldLines { get; }

        /// <summary>New-side lines (verbatim). Empty for a pure deletion.</summary>
        public IReadOnlyList<string> NewLines { get; }

        public LineSegment(SegmentKind kind, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
        {
            Kind = kind;
            OldLines = oldLines;
            NewLines = newLines;
        }
    }

    /// <summary>
    /// UI-free, line-based diff used by the inline edit-review feature. Splits both texts on
    /// <c>'\n'</c> (a trailing <c>'\r'</c> stays on the line content but is ignored for equality, so
    /// CRLF and LF lines match), trims the common prefix/suffix, then runs an LCS diff over the middle
    /// so the result can contain MULTIPLE changed hunks — unlike the single-block WebUI <c>buildDiff</c>.
    /// For a genuinely single contiguous change the result reduces to one <see cref="SegmentKind.Changed"/>
    /// segment, so the chat preview and the editor agree.
    ///
    /// Invariant: concatenating every segment's <see cref="LineSegment.NewLines"/> in order reproduces
    /// <c>newText.Split('\n')</c> exactly, and likewise <see cref="LineSegment.OldLines"/> reproduces
    /// <c>oldText.Split('\n')</c>. This is what lets the reconstruction echo the original text byte-for-byte
    /// when every hunk is accepted (or rejected).
    /// </summary>
    public static class LineDiff
    {
        // Beyond this many lines on either side of a differing region, the O(n*m) LCS table gets too
        // expensive (a 1500x1500 int table is already ~9 MB, and it is allocated inside devenv), so the
        // region is first split at unique common lines (patience diff) and only the smaller gaps go
        // through the LCS. Edits are normally far below this.
        private const int LcsLineCap = 1500;

        public static IReadOnlyList<LineSegment> Compute(string oldText, string newText)
        {
            var oldLines = SplitLines(oldText ?? "");
            var newLines = SplitLines(newText ?? "");
            int n = oldLines.Length, m = newLines.Length;

            // Common prefix.
            int prefix = 0;
            while (prefix < n && prefix < m && LineEquals(oldLines[prefix], newLines[prefix]))
                prefix++;
            // Common suffix (never overlapping the prefix).
            int suffix = 0;
            while (suffix < (n - prefix) && suffix < (m - prefix)
                   && LineEquals(oldLines[n - 1 - suffix], newLines[m - 1 - suffix]))
                suffix++;

            var raw = new List<LineSegment>();
            if (prefix > 0)
                raw.Add(new LineSegment(SegmentKind.Unchanged, Slice(oldLines, 0, prefix), Slice(newLines, 0, prefix)));

            DiffMiddle(oldLines, prefix, n - suffix, newLines, prefix, m - suffix, raw);

            if (suffix > 0)
                raw.Add(new LineSegment(SegmentKind.Unchanged, Slice(oldLines, n - suffix, n), Slice(newLines, m - suffix, m)));

            return Coalesce(raw);
        }

        private static void DiffMiddle(
            string[] oldLines, int oStart, int oEnd,
            string[] newLines, int nStart, int nEnd,
            List<LineSegment> outSegments)
        {
            int oLen = oEnd - oStart, nLen = nEnd - nStart;
            if (oLen == 0 && nLen == 0)
                return;
            if (oLen == 0 || nLen == 0)
            {
                // Pure insertion or pure deletion → one Changed segment.
                outSegments.Add(new LineSegment(SegmentKind.Changed,
                    Slice(oldLines, oStart, oEnd), Slice(newLines, nStart, nEnd)));
                return;
            }
            if (oLen > LcsLineCap || nLen > LcsLineCap)
            {
                // Too big for an O(n*m) LCS table. Do NOT give up and call the whole region changed —
                // that used to turn a ~60-line spelling pass over a 6.5k-line JSON language file into
                // "+5108 -5108" (every line between the first and last edit), which then rendered
                // thousands of phantom lines and made the editor unscrollable. Instead split the region
                // at lines occurring exactly once on both sides (patience diff) and recurse into the
                // gaps; each gap is strictly smaller, so this terminates and normally lands well under
                // the cap on the first pass.
                if (TrySplitOnUniqueAnchors(oldLines, oStart, oEnd, newLines, nStart, nEnd, outSegments))
                    return;
                // Degenerate region with no line unique to both sides (e.g. thousands of repeats of the
                // same line) → last resort: one big hunk.
                outSegments.Add(new LineSegment(SegmentKind.Changed,
                    Slice(oldLines, oStart, oEnd), Slice(newLines, nStart, nEnd)));
                return;
            }

            // LCS length table over the middle region.
            var lcs = new int[oLen + 1, nLen + 1];
            for (int i = oLen - 1; i >= 0; i--)
                for (int j = nLen - 1; j >= 0; j--)
                    lcs[i, j] = LineEquals(oldLines[oStart + i], newLines[nStart + j])
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            // Backtrack into an edit script, grouping into Unchanged / Changed runs.
            int oi = 0, nj = 0;
            var delBuf = new List<string>();
            var insBuf = new List<string>();
            var eqOld = new List<string>();
            var eqNew = new List<string>();

            void FlushChange()
            {
                if (delBuf.Count > 0 || insBuf.Count > 0)
                {
                    outSegments.Add(new LineSegment(SegmentKind.Changed, delBuf.ToArray(), insBuf.ToArray()));
                    delBuf = new List<string>();
                    insBuf = new List<string>();
                }
            }
            void FlushEqual()
            {
                if (eqOld.Count > 0)
                {
                    outSegments.Add(new LineSegment(SegmentKind.Unchanged, eqOld.ToArray(), eqNew.ToArray()));
                    eqOld = new List<string>();
                    eqNew = new List<string>();
                }
            }

            while (oi < oLen && nj < nLen)
            {
                if (LineEquals(oldLines[oStart + oi], newLines[nStart + nj]))
                {
                    FlushChange();
                    eqOld.Add(oldLines[oStart + oi]);
                    eqNew.Add(newLines[nStart + nj]);
                    oi++; nj++;
                }
                else if (lcs[oi + 1, nj] >= lcs[oi, nj + 1])
                {
                    FlushEqual();
                    delBuf.Add(oldLines[oStart + oi]);
                    oi++;
                }
                else
                {
                    FlushEqual();
                    insBuf.Add(newLines[nStart + nj]);
                    nj++;
                }
            }
            while (oi < oLen) { FlushEqual(); delBuf.Add(oldLines[oStart + oi]); oi++; }
            while (nj < nLen) { FlushEqual(); insBuf.Add(newLines[nStart + nj]); nj++; }
            FlushChange();
            FlushEqual();
        }

        /// <summary>
        /// Patience-diff split for regions too large for the LCS table: pairs up lines that occur
        /// EXACTLY ONCE on both sides, keeps the longest non-crossing chain of those pairs as fixed
        /// anchors, and recurses into the gaps between them. Real-world large regions (source files,
        /// JSON language files) are full of such lines, so this yields precise small hunks instead of
        /// one file-sized hunk. Returns false when no anchor exists, leaving the caller to fall back.
        /// </summary>
        private static bool TrySplitOnUniqueAnchors(
            string[] oldLines, int oStart, int oEnd,
            string[] newLines, int nStart, int nEnd,
            List<LineSegment> outSegments)
        {
            // Line → its single index, or -1 once seen more than once. Keyed on the \r-trimmed text so
            // anchoring agrees with LineEquals.
            var oldOnce = new Dictionary<string, int>(oEnd - oStart, StringComparer.Ordinal);
            for (int i = oStart; i < oEnd; i++)
            {
                var key = TrimCr(oldLines[i]);
                oldOnce[key] = oldOnce.ContainsKey(key) ? -1 : i;
            }
            var newOnce = new Dictionary<string, int>(nEnd - nStart, StringComparer.Ordinal);
            for (int j = nStart; j < nEnd; j++)
            {
                var key = TrimCr(newLines[j]);
                newOnce[key] = newOnce.ContainsKey(key) ? -1 : j;
            }

            // Candidate anchors: unique on BOTH sides, ordered by old-side position.
            var pairs = new List<KeyValuePair<int, int>>();
            foreach (var kv in oldOnce)
            {
                if (kv.Value < 0) continue;
                if (!newOnce.TryGetValue(kv.Key, out var nj) || nj < 0) continue;
                pairs.Add(new KeyValuePair<int, int>(kv.Value, nj));
            }
            if (pairs.Count == 0)
                return false;
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));

            var anchors = LongestIncreasingChain(pairs);
            if (anchors.Count == 0)
                return false;

            int oi = oStart, njCursor = nStart;
            foreach (var anchor in anchors)
            {
                DiffMiddle(oldLines, oi, anchor.Key, newLines, njCursor, anchor.Value, outSegments);
                // The anchor line itself is common to both sides. Coalesce() merges runs of these.
                outSegments.Add(new LineSegment(SegmentKind.Unchanged,
                    Slice(oldLines, anchor.Key, anchor.Key + 1),
                    Slice(newLines, anchor.Value, anchor.Value + 1)));
                oi = anchor.Key + 1;
                njCursor = anchor.Value + 1;
            }
            DiffMiddle(oldLines, oi, oEnd, newLines, njCursor, nEnd, outSegments);
            return true;
        }

        /// <summary>Longest chain of pairs with strictly increasing values, given input already sorted
        /// by key. Patience sort (O(k log k)) with back-pointers so the chain itself is recovered, not
        /// just its length. Anchors must not cross or the recursion would emit overlapping ranges.</summary>
        private static List<KeyValuePair<int, int>> LongestIncreasingChain(List<KeyValuePair<int, int>> pairs)
        {
            var tails = new List<int>();          // per chain length: index of the smallest possible tail
            var prev = new int[pairs.Count];      // predecessor index in the chain, -1 for a chain start
            for (int i = 0; i < pairs.Count; i++)
            {
                int v = pairs[i].Value;
                int lo = 0, hi = tails.Count;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (pairs[tails[mid]].Value < v) lo = mid + 1; else hi = mid;
                }
                prev[i] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
            }

            var chain = new List<KeyValuePair<int, int>>(tails.Count);
            for (int k = tails.Count > 0 ? tails[tails.Count - 1] : -1; k >= 0; k = prev[k])
                chain.Add(pairs[k]);
            chain.Reverse();
            return chain;
        }

        /// <summary>Merges adjacent same-kind segments (e.g. the prefix Unchanged meeting the
        /// first middle Unchanged) so each hunk is one contiguous block.</summary>
        private static IReadOnlyList<LineSegment> Coalesce(List<LineSegment> segments)
        {
            var result = new List<LineSegment>(segments.Count);
            foreach (var seg in segments)
            {
                if (result.Count > 0 && result[result.Count - 1].Kind == seg.Kind)
                {
                    var prev = result[result.Count - 1];
                    var mergedOld = new List<string>(prev.OldLines); mergedOld.AddRange(seg.OldLines);
                    var mergedNew = new List<string>(prev.NewLines); mergedNew.AddRange(seg.NewLines);
                    result[result.Count - 1] = new LineSegment(seg.Kind, mergedOld, mergedNew);
                }
                else
                {
                    result.Add(seg);
                }
            }
            return result;
        }

        /// <summary>Splits on <c>'\n'</c>, keeping any trailing <c>'\r'</c> as part of the line
        /// (matches <c>WebUI buildDiff</c>: <c>"".Split('\n')</c> → one empty line).</summary>
        public static string[] SplitLines(string text) => (text ?? "").Split('\n');

        /// <summary>Joins lines back with <c>'\n'</c> (inverse of <see cref="SplitLines"/>).</summary>
        public static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);

        /// <summary>Equality used for matching: a single trailing <c>'\r'</c> is ignored so CRLF
        /// and LF versions of the same line are treated as equal.</summary>
        public static bool LineEquals(string a, string b) =>
            string.Equals(TrimCr(a), TrimCr(b), StringComparison.Ordinal);

        private static string TrimCr(string s) =>
            (s.Length > 0 && s[s.Length - 1] == '\r') ? s.Substring(0, s.Length - 1) : s;

        private static string[] Slice(string[] src, int start, int end)
        {
            var len = end - start;
            var dst = new string[len];
            Array.Copy(src, start, dst, 0, len);
            return dst;
        }
    }
}
