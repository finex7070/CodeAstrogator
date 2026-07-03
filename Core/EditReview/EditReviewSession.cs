using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core.EditReview
{
    public enum HunkState { Pending, Accepted, Rejected }

    /// <summary>One reviewable change region the editor shows (= one <see cref="SegmentKind.Changed"/>
    /// diff segment). Carries the lines to delete (red) and add (green) plus where it sits in the file.</summary>
    public sealed class ReviewHunk
    {
        /// <summary>Stable, 0-based ordinal across the whole review (the editor button id).</summary>
        public int Index { get; }

        /// <summary>Which edit unit this hunk belongs to (0 for Edit/Write; the <c>edits[]</c> slot for MultiEdit).</summary>
        public int UnitIndex { get; }

        /// <summary>1-based line in the OLD-text coordinate space where the hunk's first line sits — the
        /// anchor for the default (proposal) review, where the editor buffer still holds the old text.</summary>
        public int AnchorLine { get; }

        /// <summary>1-based line in the NEW-text coordinate space where the hunk's ADDED lines begin (for a
        /// pure deletion, the line the removed block sat before). Used by the "applied" review, where the
        /// buffer already holds the new text and the added lines are highlighted in place.</summary>
        public int AppliedAnchorLine { get; }

        public IReadOnlyList<string> DeletedLines { get; }
        public IReadOnlyList<string> AddedLines { get; }

        public HunkState State { get; set; } = HunkState.Pending;

        public ReviewHunk(int index, int unitIndex, int anchorLine, int appliedAnchorLine,
            IReadOnlyList<string> deletedLines, IReadOnlyList<string> addedLines)
        {
            Index = index;
            UnitIndex = unitIndex;
            AnchorLine = anchorLine;
            AppliedAnchorLine = appliedAnchorLine;
            DeletedLines = deletedLines;
            AddedLines = addedLines;
        }
    }

    /// <summary>
    /// Models an in-flight inline edit review for ONE blocking tool call (Edit/Write/MultiEdit).
    /// Computes the per-unit line diffs + the ordered <see cref="Hunks"/> the editor renders, and
    /// reconstructs the tool's <c>updatedInput</c> from only the accepted hunks. Pure and UI-free —
    /// file content is supplied by the caller (<c>readFile</c>) so this is fully unit-tested
    /// (see EditReviewTests). The editor/adornment layer and the host bridge sit on top of it.
    /// </summary>
    public sealed class EditReviewSession
    {
        private sealed class Unit
        {
            public int Index;
            public IReadOnlyList<LineSegment> Segments = Array.Empty<LineSegment>();
            // 1-based OLD-text line each segment starts at (only meaningful for Changed segments).
            public int[] SegmentAnchors = Array.Empty<int>();
            // 1-based NEW-text line each segment starts at (the "applied" anchor).
            public int[] SegmentNewAnchors = Array.Empty<int>();
            // The hunk attached to each Changed segment, in segment order (null entries = Unchanged).
            public ReviewHunk?[] SegmentHunks = Array.Empty<ReviewHunk?>();
            public JObject? OriginalEdit; // the original edits[] entry for MultiEdit (null for Edit/Write)
        }

        public string ToolName { get; }
        public string FilePath { get; }
        public JObject OriginalInput { get; }
        public IReadOnlyList<ReviewHunk> Hunks { get; }

        private readonly List<Unit> _units;

        private EditReviewSession(string toolName, string filePath, JObject originalInput,
            List<Unit> units, List<ReviewHunk> hunks)
        {
            ToolName = toolName;
            FilePath = filePath;
            OriginalInput = originalInput;
            _units = units;
            Hunks = hunks;
        }

        public bool HasHunks => Hunks.Count > 0;
        public bool AnyAccepted => Hunks.Any(h => h.State == HunkState.Accepted);
        public bool AllDecided => Hunks.All(h => h.State != HunkState.Pending);

        /// <summary>Marks every still-pending hunk as accepted (used as the "default = keep" finalize).</summary>
        public void AcceptPending()
        {
            foreach (var h in Hunks)
                if (h.State == HunkState.Pending) h.State = HunkState.Accepted;
        }

        /// <summary>True for the tools this feature reviews in the editor.</summary>
        public static bool IsReviewableTool(string toolName) =>
            toolName == "Edit" || toolName == "Write" || toolName == "MultiEdit";

        /// <summary>
        /// Builds a review from the raw tool input. <paramref name="readFile"/> returns the file's
        /// current text (or null/"" if unreadable) and is used both as the Write old-text and to anchor
        /// Edit/MultiEdit hunks to real file lines. Default hunk state is <see cref="HunkState.Pending"/>.
        /// Returns a session whose <see cref="HasHunks"/> may be false (nothing to review → caller approves as-is).
        /// </summary>
        public static EditReviewSession Build(string toolName, JObject input, Func<string?> readFile)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var fileContent = (readFile?.Invoke()) ?? "";
            var filePath = input.Value<string>("file_path") ?? input.Value<string>("notebook_path") ?? "";

            var units = new List<Unit>();
            if (toolName == "Write")
            {
                units.Add(BuildUnit(0, oldText: fileContent, newText: input.Value<string>("content") ?? "",
                    baseStartLine: 1, originalEdit: null));
            }
            else if (toolName == "Edit")
            {
                var oldStr = input.Value<string>("old_string") ?? "";
                units.Add(BuildUnit(0, oldText: oldStr, newText: input.Value<string>("new_string") ?? "",
                    baseStartLine: FindStartLine(fileContent, oldStr), originalEdit: null));
            }
            else if (toolName == "MultiEdit")
            {
                var edits = input["edits"] as JArray ?? new JArray();
                for (int i = 0; i < edits.Count; i++)
                {
                    if (!(edits[i] is JObject edit)) continue;
                    var oldStr = edit.Value<string>("old_string") ?? "";
                    units.Add(BuildUnit(i, oldText: oldStr, newText: edit.Value<string>("new_string") ?? "",
                        baseStartLine: FindStartLine(fileContent, oldStr), originalEdit: edit));
                }
            }

            // Assign global, stable hunk indices in unit/segment order.
            var hunks = new List<ReviewHunk>();
            foreach (var unit in units)
            {
                for (int s = 0; s < unit.Segments.Count; s++)
                {
                    if (unit.Segments[s].Kind != SegmentKind.Changed)
                        continue;
                    var seg = unit.Segments[s];
                    var hunk = new ReviewHunk(
                        index: hunks.Count, unitIndex: unit.Index, anchorLine: unit.SegmentAnchors[s],
                        appliedAnchorLine: unit.SegmentNewAnchors[s],
                        deletedLines: seg.OldLines, addedLines: seg.NewLines);
                    unit.SegmentHunks[s] = hunk;
                    hunks.Add(hunk);
                }
            }
            return new EditReviewSession(toolName, filePath, input, units, hunks);
        }

        private static Unit BuildUnit(int unitIndex, string oldText, string newText, int baseStartLine, JObject? originalEdit)
        {
            var segments = LineDiff.Compute(oldText, newText);
            var anchors = new int[segments.Count];
            var newAnchors = new int[segments.Count];
            int oldLineCursor = baseStartLine; // 1-based old-text line of the next old line
            int newLineCursor = baseStartLine; // 1-based new-text line of the next new line
            for (int i = 0; i < segments.Count; i++)
            {
                anchors[i] = oldLineCursor;
                newAnchors[i] = newLineCursor;
                oldLineCursor += segments[i].OldLines.Count;
                newLineCursor += segments[i].NewLines.Count;
            }
            return new Unit
            {
                Index = unitIndex,
                Segments = segments,
                SegmentAnchors = anchors,
                SegmentNewAnchors = newAnchors,
                SegmentHunks = new ReviewHunk?[segments.Count],
                OriginalEdit = originalEdit,
            };
        }

        /// <summary>
        /// Reconstructs the tool input from accepted hunks. Returns null when NOTHING is accepted
        /// (the caller must then deny — Edit/MultiEdit cannot carry a no-op old==new). The returned
        /// object is a full, valid tool input (cloned from the original, with only the changed text
        /// replaced) so it is safe to hand back to the CLI as <c>updatedInput</c> (a null/partial
        /// updatedInput is treated as a denial by the CLI).
        /// </summary>
        public JObject? BuildUpdatedInput()
        {
            if (!AnyAccepted)
                return null;

            var clone = (JObject)OriginalInput.DeepClone();

            if (ToolName == "Write")
            {
                clone["content"] = ReconstructUnit(_units[0]);
                return clone;
            }
            if (ToolName == "Edit")
            {
                clone["new_string"] = ReconstructUnit(_units[0]);
                return clone;
            }
            if (ToolName == "MultiEdit")
            {
                var keptEdits = new JArray();
                foreach (var unit in _units)
                {
                    // Drop an edit ONLY when it had reviewable hunks and the user rejected them all.
                    // A unit with zero hunks (e.g. an edit whose only difference is line endings, which
                    // LineDiff treats as equal) has nothing to reject → carry it through unchanged, or
                    // it would silently vanish from an otherwise-accepted MultiEdit.
                    bool hasHunks = UnitHasHunks(unit);
                    if (hasHunks && !UnitHasAccepted(unit))
                        continue;
                    var editClone = unit.OriginalEdit != null
                        ? (JObject)unit.OriginalEdit.DeepClone()
                        : new JObject();
                    editClone["new_string"] = ReconstructUnit(unit);
                    keptEdits.Add(editClone);
                }
                if (keptEdits.Count == 0)
                    return null;
                clone["edits"] = keptEdits;
                return clone;
            }
            return null;
        }

        private static bool UnitHasAccepted(Unit unit) =>
            unit.SegmentHunks.Any(h => h != null && h.State == HunkState.Accepted);

        private static bool UnitHasHunks(Unit unit) =>
            unit.SegmentHunks.Any(h => h != null);

        /// <summary>Rebuilds one unit's new text: Unchanged → keep; Changed → accepted picks the
        /// added lines, rejected keeps the original deleted lines.</summary>
        private static string ReconstructUnit(Unit unit)
        {
            var outLines = new List<string>();
            for (int i = 0; i < unit.Segments.Count; i++)
            {
                var seg = unit.Segments[i];
                if (seg.Kind == SegmentKind.Unchanged)
                {
                    outLines.AddRange(seg.NewLines);
                }
                else
                {
                    var hunk = unit.SegmentHunks[i];
                    bool accepted = hunk != null && hunk.State == HunkState.Accepted;
                    outLines.AddRange(accepted ? seg.NewLines : seg.OldLines);
                }
            }
            return LineDiff.JoinLines(outLines);
        }

        /// <summary>1-based line where <paramref name="oldString"/> first occurs in the file
        /// (1 if missing/empty/unreadable). CRLF/CR are normalised to LF for the search only —
        /// identical to the host's existing FileStartLine, so the chat preview and editor agree.</summary>
        public static int FindStartLine(string fileContent, string oldString)
        {
            if (string.IsNullOrEmpty(oldString) || string.IsNullOrEmpty(fileContent))
                return 1;
            var content = fileContent.Replace("\r\n", "\n").Replace("\r", "\n");
            var needle = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
            var idx = content.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return 1;
            int line = 1;
            for (int i = 0; i < idx; i++)
                if (content[i] == '\n') line++;
            return line;
        }
    }
}
