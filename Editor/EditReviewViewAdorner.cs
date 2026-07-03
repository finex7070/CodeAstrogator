using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeAstrogator.Core.EditReview;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace CodeAstrogator.Editor
{
    /// <summary>
    /// Per-<see cref="IWpfTextView"/> renderer for the inline edit review: red highlight on the lines
    /// that will be deleted, green "phantom" lines for the additions (drawn in space reserved by
    /// <see cref="EditReviewLineTransformSource"/>, never written into the buffer), and a per-hunk
    /// Accept/Reject button bar. The host-side EditReviewController attaches a review via
    /// <see cref="SetReview"/>; when every hunk is decided the supplied callback fires and the host
    /// reconstructs the tool input from the accepted hunks. Buffer stays untouched (preview only).
    ///
    /// NOTE: the visual rendering can only be verified by running it in Visual Studio — the geometry,
    /// adornment-layer ordering and line-transform reservation are not exercised by the unit tests
    /// (only the diff/reconstruction core in CodeAstrogator.Core.EditReview is).
    /// </summary>
    internal sealed class EditReviewViewAdorner
    {
        // One adorner per view, created on demand and kept alive as long as the view is.
        private static readonly ConditionalWeakTable<IWpfTextView, EditReviewViewAdorner> ByView
            = new ConditionalWeakTable<IWpfTextView, EditReviewViewAdorner>();

        public static EditReviewViewAdorner GetOrCreate(IWpfTextView view) =>
            ByView.GetValue(view, v => new EditReviewViewAdorner(v));

        public static bool TryGet(IWpfTextView view, out EditReviewViewAdorner? adorner) =>
            ByView.TryGetValue(view, out adorner);

        private static readonly Brush DeleteFill = Frozen(Color.FromArgb(0x33, 0xF8, 0x51, 0x49));      // red ~.20
        private static readonly Brush DeleteFillDim = Frozen(Color.FromArgb(0x14, 0xF8, 0x51, 0x49));   // red faint
        private static readonly Brush AddFill = Frozen(Color.FromArgb(0x33, 0x3F, 0xB9, 0x50));         // green ~.20
        private static readonly Brush AddFillDim = Frozen(Color.FromArgb(0x12, 0x3F, 0xB9, 0x50));      // green faint
        private static readonly Brush AddText = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly Brush DeleteText = Frozen(Color.FromRgb(0xF8, 0x51, 0x49));       // red — removed (ghost)
        private static readonly Brush DimText = Frozen(Color.FromArgb(0x99, 0x9A, 0x9A, 0x9A));

        private readonly IWpfTextView _view;
        private IAdornmentLayer? _below;
        private IAdornmentLayer? _above;

        private EditReviewSession? _review;
        private Action? _onCompleted;
        private bool _completed;
        // "applied" mode (post-turn "Review edits at end of turn"): the edit is ALREADY in the buffer, so
        // the ADDED lines are real buffer lines highlighted green in place, and the removed OLD lines are
        // shown as red ghost text above them. Default (false) = proposal mode: buffer holds the old text,
        // red highlights the to-be-deleted lines, green phantoms preview the additions.
        private bool _applied;
        // bufferLine (0-based) → count of phantom added lines reserved below / above that line
        private readonly Dictionary<int, int> _spaceBelow = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _spaceAbove = new Dictionary<int, int>();
        private ITextSnapshot? _reviewSnapshot;

        private EditReviewViewAdorner(IWpfTextView view)
        {
            _view = view;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += (s, e) => Detach();
        }

        public bool HasReview => _review != null;

        /// <summary>True while this view shows an "applied" review (buffer holds the new text; added lines
        /// highlighted green in place, removed lines as red ghosts). Read by the scrollbar margin so its
        /// marks use the new-text anchor.</summary>
        public bool Applied => _applied;

        /// <summary>The 1-based buffer line a hunk anchors to in the current mode.</summary>
        public int EffectiveAnchor(ReviewHunk hunk) => _applied ? hunk.AppliedAnchorLine : hunk.AnchorLine;

        /// <summary>The review's hunks (empty when no review is attached). Read by the scrollbar
        /// overview margin to draw a mark per hunk.</summary>
        public IReadOnlyList<ReviewHunk> ReviewHunks => _review?.Hunks ?? Array.Empty<ReviewHunk>();

        /// <summary>Raised whenever the set of hunks or their state changes (attach / clear / decide),
        /// so the scrollbar overview margin can repaint its marks.</summary>
        public event Action? ReviewChanged;

        /// <summary>Attaches a review to this view and (re)draws it. Replaces any prior review.
        /// <paramref name="applied"/> selects the "applied" rendering (see <see cref="_applied"/>).</summary>
        public void SetReview(EditReviewSession review, Action onCompleted, bool applied = false)
        {
            _review = review ?? throw new ArgumentNullException(nameof(review));
            _onCompleted = onCompleted;
            _applied = applied;
            _completed = false;
            _reviewSnapshot = _view.TextSnapshot;
            ComputeReservedSpace();
            // The buffer must not change underneath the review (line numbers would drift); cancel if it does.
            // Idempotent: SetReview may run twice on the same view (re-open / two edits to the same file).
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            ForceRelayout();   // re-query the line transform so the reserved space appears, then draw
            Draw();
            ReviewChanged?.Invoke();
        }

        /// <summary>Removes the review, its adornments and the reserved space.</summary>
        public void ClearReview()
        {
            if (_review == null)
                return;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _review = null;
            _onCompleted = null;
            _spaceBelow.Clear();
            _spaceAbove.Clear();
            _below?.RemoveAllAdornments();
            _above?.RemoveAllAdornments();
            ForceRelayout();   // give the reclaimed vertical space back
            ReviewChanged?.Invoke();
        }

        private void Detach()
        {
            _view.LayoutChanged -= OnLayoutChanged;
            try { _view.TextBuffer.Changed -= OnBufferChanged; } catch { }
            _review = null;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // The user (or anything) edited the file mid-review → the diff anchors are stale.
            // Drop the review's adornments; the chat card stays open so the user can still Reject all.
            ClearReview();
        }

        /// <summary>Top/bottom space (in pixels) to reserve for the given text view line.</summary>
        public (double top, double bottom) GetReservedSpace(ITextViewLine line)
        {
            if (_review == null || line == null)
                return (0, 0);
            try
            {
                var containing = line.Start.GetContainingLine();
                if (line.Start.Position != containing.Start.Position)
                    return (0, 0); // only reserve on the first visual segment of a wrapped line
                int n = containing.LineNumber;
                double h = _view.LineHeight;
                double top = _spaceAbove.TryGetValue(n, out var a) ? a * h : 0;
                double bottom = _spaceBelow.TryGetValue(n, out var b) ? b * h : 0;
                return (top, bottom);
            }
            catch { return (0, 0); }
        }

        private void ComputeReservedSpace()
        {
            _spaceBelow.Clear();
            _spaceAbove.Clear();
            if (_review == null)
                return;
            int lineCount = _view.TextSnapshot.LineCount;
            if (_applied)
            {
                // Applied mode: the added lines are real (in the buffer); reserve room ABOVE the added
                // block for the red ghost "deleted" lines (where the removed content used to sit).
                foreach (var hunk in _review.Hunks)
                {
                    int delCount = hunk.DeletedLines.Count;
                    if (delCount == 0)
                        continue; // pure insertion → nothing removed → no ghosts
                    int newAnchor0 = Clamp(hunk.AppliedAnchorLine - 1, 0, lineCount - 1);
                    Add(_spaceAbove, newAnchor0, delCount);
                }
                return;
            }
            foreach (var hunk in _review.Hunks)
            {
                int addCount = hunk.AddedLines.Count;
                if (addCount == 0)
                    continue; // pure deletion → no phantom lines
                int anchor0 = hunk.AnchorLine - 1;
                int delCount = hunk.DeletedLines.Count;
                if (delCount == 0 && anchor0 <= 0)
                {
                    // insertion at the very top → reserve above line 0
                    Add(_spaceAbove, 0, addCount);
                }
                else
                {
                    int belowLine = delCount > 0 ? anchor0 + delCount - 1 : anchor0 - 1;
                    belowLine = Clamp(belowLine, 0, lineCount - 1);
                    Add(_spaceBelow, belowLine, addCount);
                }
            }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Draw();

        private void Draw()
        {
            if (_below == null) _below = TryGetLayer(EditReviewLayers.BelowTextLayer);
            if (_above == null) _above = TryGetLayer(EditReviewLayers.AboveTextLayer);
            _below?.RemoveAllAdornments();
            _above?.RemoveAllAdornments();
            if (_review == null || _below == null || _above == null)
                return;
            var lines = _view.TextViewLines;
            if (lines == null)
                return;
            var snapshot = _view.TextSnapshot;
            foreach (var hunk in _review.Hunks)
            {
                try { if (_applied) DrawHunkApplied(hunk, snapshot); else DrawHunk(hunk, snapshot); }
                catch { /* never let a draw error break the editor */ }
            }
            try { DrawNavToolbar(snapshot); }
            catch { /* the floating toolbar must never break the editor */ }
        }

        /// <summary>A small floating toolbar (fixed at the top-right of the viewport) with Prev/Next
        /// buttons that jump the caret/scroll to the previous/next review hunk, plus a remaining-count
        /// label. Redrawn each layout pass; OwnerControlled so it stays pinned regardless of scroll.</summary>
        private void DrawNavToolbar(ITextSnapshot snapshot)
        {
            if (_above == null || _review == null || _review.Hunks.Count == 0)
                return;
            int pending = 0;
            foreach (var h in _review.Hunks)
                if (h.State == HunkState.Pending) pending++;

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x25, 0x29, 0x33)),
            };
            bar.Children.Add(MakeNavButton("▲", "Go to the previous change", () => Navigate(-1, snapshot)));
            bar.Children.Add(MakeNavButton("▼", "Go to the next change", () => Navigate(+1, snapshot)));
            bar.Children.Add(new TextBlock
            {
                Text = pending > 0 ? pending + " to review" : "all reviewed",
                Foreground = DimText,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0),
            });

            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = bar.DesiredSize.Width;
            Canvas.SetLeft(bar, Math.Max(_view.ViewportLeft, _view.ViewportRight - w - 16));
            Canvas.SetTop(bar, _view.ViewportTop + 6);
            _above.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, "nav", bar, null);
        }

        private Button MakeNavButton(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = glyph,
                ToolTip = tip,
                Margin = new Thickness(2, 1, 2, 1),
                Padding = new Thickness(7, 1, 7, 1),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>Scrolls the view to the topmost change of the current review (called right after a
        /// review is attached, so opening a file lands on its first change instead of wherever the caret
        /// was). Works in both modes via <see cref="EffectiveAnchor"/>. Best-effort.</summary>
        public void ScrollToFirstHunk()
        {
            if (_review == null || _review.Hunks.Count == 0)
                return;
            try
            {
                var snapshot = _view.TextSnapshot;
                int minLine = int.MaxValue;
                foreach (var h in _review.Hunks)
                    minLine = Math.Min(minLine, EffectiveAnchor(h));
                int line0 = Clamp(minLine - 1, 0, snapshot.LineCount - 1);
                var line = snapshot.GetLineFromLineNumber(line0);
                _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(line.Start, line.End),
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }
            catch { /* best effort */ }
        }

        /// <summary>Scrolls to the previous (dir &lt; 0) or next (dir &gt; 0) hunk relative to the first
        /// visible line, wrapping around at the ends.</summary>
        private void Navigate(int dir, ITextSnapshot snapshot)
        {
            if (_review == null || _review.Hunks.Count == 0)
                return;
            var lines = new List<int>();
            foreach (var h in _review.Hunks)
                lines.Add(Clamp(EffectiveAnchor(h) - 1, 0, snapshot.LineCount - 1));
            lines.Sort();

            int curr = CurrentTopLine();
            int target = -1;
            if (dir > 0)
            {
                foreach (var l in lines) if (l > curr) { target = l; break; }
                if (target < 0) target = lines[0]; // past the last → wrap to the first
            }
            else
            {
                for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] < curr) { target = lines[i]; break; }
                if (target < 0) target = lines[lines.Count - 1]; // before the first → wrap to the last
            }
            try
            {
                var line = snapshot.GetLineFromLineNumber(Clamp(target, 0, snapshot.LineCount - 1));
                _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(line.Start, line.End),
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }
            catch { /* best effort */ }
        }

        private int CurrentTopLine()
        {
            try
            {
                var lines = _view.TextViewLines;
                if (lines != null && lines.Count > 0)
                    return lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            }
            catch { }
            return 0;
        }

        private void DrawHunk(ReviewHunk hunk, ITextSnapshot snapshot)
        {
            int anchor0 = hunk.AnchorLine - 1;
            int delCount = hunk.DeletedLines.Count;
            int addCount = hunk.AddedLines.Count;
            bool rejected = hunk.State == HunkState.Rejected;
            bool accepted = hunk.State == HunkState.Accepted;

            // 1) red highlight on the to-be-deleted buffer lines
            ITextViewLine? firstDelLine = null;
            for (int d = 0; d < delCount; d++)
            {
                var tvl = VisibleLine(anchor0 + d, snapshot);
                if (tvl == null) continue;
                if (firstDelLine == null) firstDelLine = tvl;
                AddRect(_below!, tvl.Extent, tvl.TextTop, tvl.Height, rejected ? DeleteFillDim : DeleteFill);
            }

            // 2) green phantom "added" lines in the reserved space
            double phantomTop;
            SnapshotSpan anchorSpan;
            bool topInsert = delCount == 0 && anchor0 <= 0;
            ITextViewLine? attachLine;
            if (topInsert)
            {
                attachLine = VisibleLine(0, snapshot);
                phantomTop = attachLine != null ? attachLine.TextTop - addCount * _view.LineHeight : double.NaN;
                anchorSpan = attachLine?.Extent ?? default;
            }
            else
            {
                int belowLine = delCount > 0 ? anchor0 + delCount - 1 : Math.Max(0, anchor0 - 1);
                attachLine = VisibleLine(belowLine, snapshot);
                phantomTop = attachLine != null ? attachLine.TextBottom : double.NaN;
                anchorSpan = attachLine?.Extent ?? default;
            }

            double left = (firstDelLine ?? attachLine)?.TextLeft ?? _view.ViewportLeft;
            if (attachLine != null && !double.IsNaN(phantomTop))
            {
                for (int i = 0; i < addCount; i++)
                {
                    double y = phantomTop + i * _view.LineHeight;
                    AddRect(_above!, anchorSpan, y, _view.LineHeight, rejected ? AddFillDim : AddFill);
                    AddAddedText(anchorSpan, hunk.AddedLines[i], left, y, rejected);
                }
            }

            // 3) per-hunk Accept/Reject buttons, anchored at the top of the hunk
            var barAnchor = firstDelLine ?? attachLine;
            if (barAnchor != null)
            {
                double barTop = topInsert && !double.IsNaN(phantomTop) ? phantomTop : barAnchor.TextTop;
                AddButtonBar(hunk, barAnchor.Extent, barTop);
            }
        }

        /// <summary>Applied mode: the edit is already in the buffer. Highlight the added lines GREEN in
        /// place, and show the removed OLD lines as RED ghost text in the reserved space above them.
        /// Reject dims the green (it will be reverted) and shows the red as solid (it will be restored);
        /// accept dims the red (the old is discarded).</summary>
        private void DrawHunkApplied(ReviewHunk hunk, ITextSnapshot snapshot)
        {
            int newAnchor0 = hunk.AppliedAnchorLine - 1;
            int addCount = hunk.AddedLines.Count;
            int delCount = hunk.DeletedLines.Count;
            bool rejected = hunk.State == HunkState.Rejected;
            bool accepted = hunk.State == HunkState.Accepted;

            // 1) green highlight over the real (buffer) added lines
            ITextViewLine? firstAddLine = null;
            for (int a = 0; a < addCount; a++)
            {
                var tvl = VisibleLine(newAnchor0 + a, snapshot);
                if (tvl == null) continue;
                if (firstAddLine == null) firstAddLine = tvl;
                AddRect(_below!, tvl.Extent, tvl.TextTop, tvl.Height, rejected ? AddFillDim : AddFill);
            }

            // 2) red ghost "deleted" lines in the reserved space above the added block (or, for a pure
            //    deletion, above the line the removed block now sits before)
            ITextViewLine? attachLine = firstAddLine ?? VisibleLine(newAnchor0, snapshot);
            double ghostTop = attachLine != null ? attachLine.TextTop - delCount * _view.LineHeight : double.NaN;
            SnapshotSpan anchorSpan = attachLine?.Extent ?? default;
            double left = attachLine?.TextLeft ?? _view.ViewportLeft;
            if (delCount > 0 && attachLine != null && !double.IsNaN(ghostTop))
            {
                for (int i = 0; i < delCount; i++)
                {
                    double y = ghostTop + i * _view.LineHeight;
                    AddRect(_above!, anchorSpan, y, _view.LineHeight, accepted ? DeleteFillDim : DeleteFill);
                    AddPhantomText(anchorSpan, hunk.DeletedLines[i], left, y, accepted ? DimText : DeleteText, strike: true);
                }
            }

            // 3) per-hunk Keep/Revert buttons, anchored at the top of the hunk (the ghost block if any)
            var barAnchor = attachLine ?? firstAddLine;
            if (barAnchor != null)
            {
                double barTop = delCount > 0 && !double.IsNaN(ghostTop) ? ghostTop : barAnchor.TextTop;
                AddButtonBar(hunk, barAnchor.Extent, barTop);
            }
        }

        private void AddButtonBar(ReviewHunk hunk, SnapshotSpan span, double top)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x25, 0x29, 0x33)),
            };
            // Applied mode reads as "keep / revert" (the edit is already on disk); proposal mode as
            // "accept / reject" (the edit hasn't been applied yet).
            var acceptLabel = _applied
                ? (hunk.State == HunkState.Accepted ? "✓ Kept" : "✓ Keep")
                : (hunk.State == HunkState.Accepted ? "✓ Accepted" : "✓ Accept");
            var rejectLabel = _applied
                ? (hunk.State == HunkState.Rejected ? "↩ Reverted" : "↩ Revert")
                : (hunk.State == HunkState.Rejected ? "✕ Rejected" : "✕ Reject");
            bar.Children.Add(MakeButton(acceptLabel, hunk.State == HunkState.Accepted, () => Decide(hunk, HunkState.Accepted)));
            bar.Children.Add(MakeButton(rejectLabel, hunk.State == HunkState.Rejected, () => Decide(hunk, HunkState.Rejected)));

            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = bar.DesiredSize.Width;
            Canvas.SetLeft(bar, Math.Max(_view.ViewportLeft, _view.ViewportLeft + _view.ViewportWidth - w - 18));
            // The floating Prev/Next toolbar sits at the top-right of the viewport (ViewportTop + 6); a hunk
            // anchored to the first visible line would put its (also right-aligned) button bar right on top of
            // it. Push the bar just below that band so the two never overlap.
            double navClearance = _view.ViewportTop + 32;
            if (top < navClearance) top = navClearance;
            Canvas.SetTop(bar, top);
            _above!.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, "btn:" + hunk.Index, bar, null);
        }

        private Button MakeButton(string text, bool active, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(2, 1, 2, 1),
                Padding = new Thickness(6, 1, 6, 1),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void Decide(ReviewHunk hunk, HunkState state)
        {
            if (_review == null || _completed)
                return;
            hunk.State = state;
            Draw(); // refresh button labels / dimming
            ReviewChanged?.Invoke(); // repaint the scrollbar marks (decided hunks dim)
            if (_review.AllDecided && !_completed)
            {
                _completed = true;
                var cb = _onCompleted;
                cb?.Invoke(); // host reconstructs updatedInput + resolves the CLI call, then Close()s us
            }
        }

        private void AddRect(IAdornmentLayer layer, SnapshotSpan span, double top, double height, Brush fill)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(0, _view.ViewportWidth),
                Height = Math.Max(0, height),
                Fill = fill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, _view.ViewportLeft);
            Canvas.SetTop(rect, top);
            layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, rect, null);
        }

        private void AddAddedText(SnapshotSpan span, string text, double left, double top, bool dim) =>
            AddPhantomText(span, text, left, top, dim ? DimText : AddText, strike: dim);

        private void AddPhantomText(SnapshotSpan span, string text, double left, double top, Brush foreground, bool strike)
        {
            var tb = new TextBlock
            {
                Text = text.Length == 0 ? " " : text,
                Foreground = foreground,
                FontFamily = GetEditorFontFamily(),
                FontSize = GetEditorFontSize(),
                Height = _view.LineHeight,
                IsHitTestVisible = false,
                TextDecorations = strike ? TextDecorations.Strikethrough : null,
            };
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, top);
            _above!.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, tb, null);
        }

        private FontFamily GetEditorFontFamily()
        {
            try
            {
                var tf = _view.FormattedLineSource?.DefaultTextProperties?.Typeface;
                if (tf?.FontFamily != null) return tf.FontFamily;
            }
            catch { }
            return new FontFamily("Consolas");
        }

        private double GetEditorFontSize()
        {
            try
            {
                var sz = _view.FormattedLineSource?.DefaultTextProperties?.FontRenderingEmSize;
                if (sz.HasValue && sz.Value > 0) return sz.Value;
            }
            catch { }
            return 12.0;
        }

        private ITextViewLine? VisibleLine(int bufferLine0, ITextSnapshot snapshot)
        {
            if (bufferLine0 < 0 || bufferLine0 >= snapshot.LineCount)
                return null;
            var start = snapshot.GetLineFromLineNumber(bufferLine0).Start;
            var lines = _view.TextViewLines;
            if (lines == null || !lines.ContainsBufferPosition(start))
                return null;
            return lines.GetTextViewLineContainingBufferPosition(start);
        }

        private IAdornmentLayer? TryGetLayer(string name)
        {
            try { return _view.GetAdornmentLayer(name); }
            catch { return null; }
        }

        private void ForceRelayout()
        {
            // Re-run a layout pass at the current scroll position (no visible scroll) so the
            // line-transform reservation is re-queried. DisplayTextLineContainingBufferPosition is
            // the standard way to force a relayout without an edit.
            try
            {
                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0)
                    return;
                var first = lines.FirstVisibleLine;
                // Keep the first visible line exactly where it is (it usually sits slightly above the
                // viewport top, so this distance is small and negative) — re-lay-out without scrolling.
                _view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - _view.ViewportTop, ViewRelativePosition.Top);
            }
            catch { /* best effort */ }
        }

        private static void Add(Dictionary<int, int> map, int key, int delta) =>
            map[key] = (map.TryGetValue(key, out var v) ? v : 0) + delta;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
