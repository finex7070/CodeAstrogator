using System.Linq;
using CodeAstrogator.Core.EditReview;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class EditReviewTests
    {
        // ── LineDiff ────────────────────────────────────────────────────────────

        [Fact]
        public void LineDiff_SingleContiguousChange_IsOneHunk()
        {
            var segs = LineDiff.Compute("a\nb\nc", "a\nB\nc");
            var changed = segs.Where(s => s.Kind == SegmentKind.Changed).ToList();
            Assert.Single(changed);
            Assert.Equal(new[] { "b" }, changed[0].OldLines);
            Assert.Equal(new[] { "B" }, changed[0].NewLines);
        }

        [Fact]
        public void LineDiff_TwoSeparateChanges_AreTwoHunks()
        {
            var segs = LineDiff.Compute("a\nb\nc\nd\ne", "a\nB\nc\nD\ne");
            var changed = segs.Where(s => s.Kind == SegmentKind.Changed).ToList();
            Assert.Equal(2, changed.Count);
            Assert.Equal(new[] { "b" }, changed[0].OldLines);
            Assert.Equal(new[] { "D" }, changed[1].NewLines);
        }

        [Fact]
        public void LineDiff_PureInsertion_HasNoDeletedLines()
        {
            var segs = LineDiff.Compute("a\nc", "a\nb\nc");
            var changed = Assert.Single(segs, s => s.Kind == SegmentKind.Changed);
            Assert.Empty(changed.OldLines);
            Assert.Equal(new[] { "b" }, changed.NewLines);
        }

        [Fact]
        public void LineDiff_PureDeletion_HasNoAddedLines()
        {
            var segs = LineDiff.Compute("a\nb\nc", "a\nc");
            var changed = Assert.Single(segs, s => s.Kind == SegmentKind.Changed);
            Assert.Equal(new[] { "b" }, changed.OldLines);
            Assert.Empty(changed.NewLines);
        }

        [Fact]
        public void LineDiff_CrlfVsLf_TreatedAsEqual()
        {
            var segs = LineDiff.Compute("a\r\nb\r\nc", "a\nb\nc");
            Assert.DoesNotContain(segs, s => s.Kind == SegmentKind.Changed);
        }

        [Fact]
        public void LineDiff_SegmentsPartitionBothTexts()
        {
            const string oldText = "one\ntwo\nthree\nfour\nfive";
            const string newText = "one\nTWO\nthree\nfour\nFIVE!";
            var segs = LineDiff.Compute(oldText, newText);
            var rebuiltOld = string.Join("\n", segs.SelectMany(s => s.OldLines));
            var rebuiltNew = string.Join("\n", segs.SelectMany(s => s.NewLines));
            Assert.Equal(oldText, rebuiltOld);
            Assert.Equal(newText, rebuiltNew);
        }

        [Fact]
        public void LineDiff_NoChange_HasNoHunks()
        {
            var segs = LineDiff.Compute("a\nb", "a\nb");
            Assert.DoesNotContain(segs, s => s.Kind == SegmentKind.Changed);
        }

        // ── FindStartLine ─────────────────────────────────────────────────────────

        [Fact]
        public void FindStartLine_ReturnsOneBasedLineOfFirstOccurrence()
        {
            Assert.Equal(2, EditReviewSession.FindStartLine("x\nold\nz", "old"));
            Assert.Equal(1, EditReviewSession.FindStartLine("old\nz", "old"));
        }

        [Fact]
        public void FindStartLine_MissingOrEmpty_ReturnsOne()
        {
            Assert.Equal(1, EditReviewSession.FindStartLine("x\ny", "absent"));
            Assert.Equal(1, EditReviewSession.FindStartLine("", "x"));
            Assert.Equal(1, EditReviewSession.FindStartLine("x\ny", ""));
        }

        [Fact]
        public void FindStartLine_NormalisesCrlfBeforeSearch()
        {
            Assert.Equal(2, EditReviewSession.FindStartLine("x\r\nold\r\nz", "old"));
        }

        // ── EditReviewSession: build ──────────────────────────────────────────────

        [Fact]
        public void Build_Edit_AnchorsHunkToRealFileLine()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["old_string"] = "old", ["new_string"] = "new" };
            var review = EditReviewSession.Build("Edit", input, () => "x\nold\nz");
            var hunk = Assert.Single(review.Hunks);
            Assert.Equal(2, hunk.AnchorLine);            // "old" sits on line 2
            Assert.Equal(0, hunk.Index);
            Assert.Equal(HunkState.Pending, hunk.State);
        }

        [Fact]
        public void Build_NoOpEdit_HasNoHunks()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["old_string"] = "same", ["new_string"] = "same" };
            var review = EditReviewSession.Build("Edit", input, () => "same");
            Assert.False(review.HasHunks);
            Assert.Null(review.BuildUpdatedInput());
        }

        // ── EditReviewSession: reconstruction ─────────────────────────────────────

        [Fact]
        public void Reconstruct_Edit_AllAccepted_EchoesOriginalNewStringExactly()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["old_string"] = "a\nb\nc", ["new_string"] = "a\nB\nc" };
            var review = EditReviewSession.Build("Edit", input, () => "a\nb\nc");
            review.AcceptPending();
            var updated = review.BuildUpdatedInput();
            Assert.NotNull(updated);
            Assert.Equal("a\nB\nc", updated!.Value<string>("new_string"));
            Assert.Equal("f.cs", updated.Value<string>("file_path")); // siblings preserved
        }

        [Fact]
        public void Reconstruct_Edit_AllRejected_ReturnsNull()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["old_string"] = "a\nb", ["new_string"] = "a\nB" };
            var review = EditReviewSession.Build("Edit", input, () => "a\nb");
            foreach (var h in review.Hunks) h.State = HunkState.Rejected;
            Assert.Null(review.BuildUpdatedInput());
        }

        [Fact]
        public void Reconstruct_Edit_PartialAccept_MergesOnlyAcceptedHunks()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["old_string"] = "a\nb\nc\nd\ne", ["new_string"] = "a\nB\nc\nD\ne" };
            var review = EditReviewSession.Build("Edit", input, () => "a\nb\nc\nd\ne");
            Assert.Equal(2, review.Hunks.Count);
            review.Hunks[0].State = HunkState.Accepted; // b -> B
            review.Hunks[1].State = HunkState.Rejected; // keep d
            var updated = review.BuildUpdatedInput();
            Assert.Equal("a\nB\nc\nd\ne", updated!.Value<string>("new_string"));
        }

        [Fact]
        public void Reconstruct_Write_PartialAccept_RebuildsFullFileContent()
        {
            var input = new JObject { ["file_path"] = "f.cs", ["content"] = "a\nB\nc\nD\ne" };
            var review = EditReviewSession.Build("Write", input, () => "a\nb\nc\nd\ne");
            Assert.Equal(2, review.Hunks.Count);
            review.Hunks[0].State = HunkState.Rejected; // keep b
            review.Hunks[1].State = HunkState.Accepted; // d -> D
            var updated = review.BuildUpdatedInput();
            Assert.Equal("a\nb\nc\nD\ne", updated!.Value<string>("content"));
        }

        [Fact]
        public void Reconstruct_MultiEdit_DropsFullyRejectedEdits()
        {
            var input = new JObject
            {
                ["file_path"] = "f.cs",
                ["edits"] = new JArray
                {
                    new JObject { ["old_string"] = "a", ["new_string"] = "A" },
                    new JObject { ["old_string"] = "b", ["new_string"] = "B" },
                },
            };
            var review = EditReviewSession.Build("MultiEdit", input, () => "a\nb");
            Assert.Equal(2, review.Hunks.Count);
            review.Hunks[0].State = HunkState.Accepted; // edit 0 kept
            review.Hunks[1].State = HunkState.Rejected; // edit 1 dropped
            var updated = review.BuildUpdatedInput();
            var edits = (JArray)updated!["edits"]!;
            Assert.Single(edits);
            Assert.Equal("a", edits[0].Value<string>("old_string"));
            Assert.Equal("A", edits[0].Value<string>("new_string"));
        }

        [Fact]
        public void Reconstruct_MultiEdit_KeepsAcceptedAndDoesNotDropLineEndingOnlyEdits()
        {
            var input = new JObject
            {
                ["file_path"] = "f.cs",
                ["edits"] = new JArray
                {
                    new JObject { ["old_string"] = "a", ["new_string"] = "A" },
                    new JObject { ["old_string"] = "p\r\nq", ["new_string"] = "p\nq" }, // only line endings → zero hunks
                },
            };
            var review = EditReviewSession.Build("MultiEdit", input, () => "a\np\r\nq");
            Assert.Single(review.Hunks); // only a->A yields a reviewable hunk
            review.AcceptPending();
            var edits = (JArray)review.BuildUpdatedInput()!["edits"]!;
            Assert.Equal(2, edits.Count); // the line-ending-only edit must survive, not be silently dropped
            Assert.Equal("p\r\nq", edits[1].Value<string>("old_string"));
            Assert.Equal("p\nq", edits[1].Value<string>("new_string"));
        }

        [Fact]
        public void Reconstruct_MultiEdit_AllRejected_ReturnsNull()
        {
            var input = new JObject
            {
                ["file_path"] = "f.cs",
                ["edits"] = new JArray { new JObject { ["old_string"] = "a", ["new_string"] = "A" } },
            };
            var review = EditReviewSession.Build("MultiEdit", input, () => "a");
            review.Hunks[0].State = HunkState.Rejected;
            Assert.Null(review.BuildUpdatedInput());
        }

        [Fact]
        public void Reconstruct_MultiEdit_PreservesReplaceAllFlagOnKeptEdits()
        {
            var input = new JObject
            {
                ["file_path"] = "f.cs",
                ["edits"] = new JArray { new JObject { ["old_string"] = "a", ["new_string"] = "A", ["replace_all"] = true } },
            };
            var review = EditReviewSession.Build("MultiEdit", input, () => "a");
            review.AcceptPending();
            var edits = (JArray)review.BuildUpdatedInput()!["edits"]!;
            Assert.True(edits[0].Value<bool>("replace_all"));
        }

        // ── Post-turn "Review edits at end of turn": whole-file Write model ────────
        // The feature models each changed file as a Write whose old-text is the pre-turn baseline and
        // whose content is the current on-disk state; BuildUpdatedInput reconstructs the file to write
        // back (accept → current line, reject → baseline line).

        [Fact]
        public void TurnReview_Write_AllAccepted_ContentEqualsCurrentDisk()
        {
            const string baseline = "a\nb\nc";
            const string current = "a\nB\nc\nd"; // Claude changed b→B and appended d
            var review = EditReviewSession.Build("Write", new JObject { ["file_path"] = "f.cs", ["content"] = current }, () => baseline);
            review.AcceptPending();
            Assert.Equal(current, review.BuildUpdatedInput()!.Value<string>("content"));
        }

        [Fact]
        public void TurnReview_Write_AllRejected_ReturnsNull_SoBridgeRevertsToBaseline()
        {
            var review = EditReviewSession.Build("Write", new JObject { ["file_path"] = "f.cs", ["content"] = "a\nB\nc" }, () => "a\nb\nc");
            foreach (var h in review.Hunks) h.State = HunkState.Rejected;
            // null ⇒ the host writes the baseline back (full revert of the turn's changes to this file)
            Assert.Null(review.BuildUpdatedInput());
        }

        [Fact]
        public void TurnReview_Write_NewFile_BaselineEmpty_AcceptKeepsContent_RejectAllReturnsNull()
        {
            // baseline "" models a file created during the turn (host marks it isNew).
            var accepted = EditReviewSession.Build("Write", new JObject { ["file_path"] = "n.cs", ["content"] = "line1\nline2" }, () => "");
            Assert.True(accepted.HasHunks);
            accepted.AcceptPending();
            Assert.Equal("line1\nline2", accepted.BuildUpdatedInput()!.Value<string>("content"));

            var rejected = EditReviewSession.Build("Write", new JObject { ["file_path"] = "n.cs", ["content"] = "line1\nline2" }, () => "");
            foreach (var h in rejected.Hunks) h.State = HunkState.Rejected;
            // null + isNew ⇒ the host deletes the created file rather than leaving it emptied.
            Assert.Null(rejected.BuildUpdatedInput());
        }

        [Fact]
        public void TurnReview_Write_AppliedAnchor_UsesNewFileCoordinates()
        {
            // baseline "a\nb\nc" → current "X\nY\na\nB\nc": prepend X,Y and change b→B.
            // Old-coord anchors differ from new-coord anchors once lines are inserted above.
            var review = EditReviewSession.Build("Write",
                new JObject { ["file_path"] = "f.cs", ["content"] = "X\nY\na\nB\nc" }, () => "a\nb\nc");
            Assert.Equal(2, review.Hunks.Count);

            // hunk 0 = the leading insertion (X,Y): both anchors at line 1
            Assert.Empty(review.Hunks[0].DeletedLines);
            Assert.Equal(new[] { "X", "Y" }, review.Hunks[0].AddedLines);
            Assert.Equal(1, review.Hunks[0].AnchorLine);
            Assert.Equal(1, review.Hunks[0].AppliedAnchorLine);

            // hunk 1 = b→B: old-text line 2, but NEW-text line 4 (after X,Y,a). Applied mode anchors here.
            Assert.Equal(2, review.Hunks[1].AnchorLine);
            Assert.Equal(4, review.Hunks[1].AppliedAnchorLine);
        }

        [Fact]
        public void TurnReview_Write_PreservesCrlfByteExact()
        {
            const string baseline = "a\r\nb\r\nc\r\n";
            const string current = "a\r\nB\r\nc\r\n"; // same CRLF line endings, only b→B
            var review = EditReviewSession.Build("Write", new JObject { ["file_path"] = "f.cs", ["content"] = current }, () => baseline);
            review.AcceptPending();
            Assert.Equal(current, review.BuildUpdatedInput()!.Value<string>("content")); // byte-for-byte, CRLF intact
        }

        [Fact]
        public void IsReviewableTool_OnlyEditWriteMultiEdit()
        {
            Assert.True(EditReviewSession.IsReviewableTool("Edit"));
            Assert.True(EditReviewSession.IsReviewableTool("Write"));
            Assert.True(EditReviewSession.IsReviewableTool("MultiEdit"));
            Assert.False(EditReviewSession.IsReviewableTool("Bash"));
            Assert.False(EditReviewSession.IsReviewableTool("NotebookEdit"));
        }
    }
}
