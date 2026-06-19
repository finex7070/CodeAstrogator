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
        // Beyond this many lines on either side of the differing middle, skip the O(n*m) LCS and
        // emit one big Changed hunk (still correct, just not granular). Edits are normally tiny.
        private const int LcsLineCap = 4000;

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
                // Too big for an O(n*m) LCS — treat the whole middle as a single hunk.
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
