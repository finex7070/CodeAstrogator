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
        // Stronger fills used for the "winning" side once a decision is made (accepted-add / reverted-del),
        // so the chosen outcome reads clearly against the dimmed losing side.
        private static readonly Brush DeleteFillStrong = Frozen(Color.FromArgb(0x52, 0xF8, 0x51, 0x49)); // red ~.32
        private static readonly Brush AddFillStrong = Frozen(Color.FromArgb(0x52, 0x3F, 0xB9, 0x50));    // green ~.32
        private static readonly Brush AddText = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly Brush DeleteText = Frozen(Color.FromRgb(0xF8, 0x51, 0x49));       // red — removed (ghost)
        private static readonly Brush DimText = Frozen(Color.FromArgb(0x99, 0x9A, 0x9A, 0x9A));

        // Accept/Keep = green, Reject/Revert = red. Outline when not chosen, solid fill + white text when chosen.
        private static readonly Color AcceptColor = Color.FromRgb(0x3F, 0xB9, 0x50);
        private static readonly Color RejectColor = Color.FromRgb(0xF8, 0x51, 0x49);
        private static readonly Brush AcceptBrush = Frozen(AcceptColor);
        private static readonly Brush RejectBrush = Frozen(RejectColor);
        private static readonly Brush AcceptFillSolid = Frozen(Color.FromArgb(0xF0, 0x2E, 0x7D, 0x39)); // green chosen bg
        private static readonly Brush RejectFillSolid = Frozen(Color.FromArgb(0xF0, 0xB3, 0x3A, 0x33)); // red chosen bg
        private static readonly Brush BtnChosenText = Frozen(Colors.White);
        private static readonly Brush BtnInactiveBg = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)); // faint tint
        // Neutral toolbar buttons (Prev/Next); Reset gets an amber "undo" tint.
        private static readonly Brush ToolbarBtnText = Frozen(Color.FromRgb(0xCE, 0xD2, 0xD8));
        private static readonly Brush ToolbarBtnBorder = Frozen(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF));
        private static readonly Brush ResetBtnText = Frozen(Color.FromRgb(0xE0, 0xA8, 0x4E)); // amber
        private static readonly ControlTemplate ButtonTemplate = BuildButtonTemplate();

        private readonly IWpfTextView _view;
        private IAdornmentLayer? _below;
        private IAdornmentLayer? _above;

        private EditReviewSession? _review;
        private Action? _onCompleted;
        private Action? _onStateChanged; // fired on every decide/reset so the chat chip can update
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
        public void SetReview(EditReviewSession review, Action onCompleted, bool applied = false,
            Action? onStateChanged = null)
        {
            _review = review ?? throw new ArgumentNullException(nameof(review));
            _onCompleted = onCompleted;
            _onStateChanged = onStateChanged;
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

            // Applied (post-turn) review: the file no longer auto-closes on the last decision — the user
            // completes it explicitly. Reset clears every decision; Finish (enabled once all are decided)
            // commits the file and drops it from the list.
            if (_applied)
            {
                var reset = MakeNavButton("↺ Reset", "Reset all decisions in this file", () => ResetDecisions());
                reset.Foreground = ResetBtnText; // amber "undo" tint
                reset.IsEnabled = _review.AnyDecided; // template dims it when disabled
                bar.Children.Add(reset);
                var finish = MakeNavButton("✓ Finish", "Complete the review for this file", () => FinishReview());
                finish.IsEnabled = _review.AllDecided; // template dims it when disabled
                if (_review.AllDecided)
                {
                    // solid green fill + white bold text so it's clearly readable and reads as the primary action
                    finish.Background = AcceptFillSolid;
                    finish.Foreground = BtnChosenText;
                    finish.BorderBrush = AcceptBrush;
                    finish.FontWeight = FontWeights.Bold;
                }
                bar.Children.Add(finish);
            }

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
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ButtonTemplate,
                Background = BtnInactiveBg,
                Foreground = ToolbarBtnText,
                BorderBrush = ToolbarBtnBorder,
                BorderThickness = new Thickness(1),
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

        /// <summary>Jumps to the next (dir &gt; 0) or previous (dir &lt; 0) review hunk relative to the CURRENT
        /// viewport — always the first hunk below the last visible line (next) or the last hunk above the first
        /// visible line (prev), so it works from wherever the user has scrolled and skips hunks already on
        /// screen. Wraps around at the ends. Reading the live visible RANGE (not a tracked index) is what makes
        /// manual scrolling between presses behave.</summary>
        private void Navigate(int dir, ITextSnapshot snapshot)
        {
            if (_review == null || _review.Hunks.Count == 0)
                return;
            var lines = new List<int>();
            foreach (var h in _review.Hunks)
                lines.Add(Clamp(EffectiveAnchor(h) - 1, 0, snapshot.LineCount - 1));
            lines.Sort();

            GetVisibleLineRange(out int firstVisible, out int lastVisible);
            int target = -1;
            if (dir > 0)
            {
                foreach (var l in lines) if (l > lastVisible) { target = l; break; } // first below the viewport
                if (target < 0) target = lines[0];                                    // none below → wrap to first
            }
            else
            {
                for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] < firstVisible) { target = lines[i]; break; } // last above
                if (target < 0) target = lines[lines.Count - 1];                       // none above → wrap to last
            }
            try
            {
                var line = snapshot.GetLineFromLineNumber(Clamp(target, 0, snapshot.LineCount - 1));
                _view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(line.Start, line.End),
                    EnsureSpanVisibleOptions.AlwaysCenter);
            }
            catch { /* best effort */ }
        }

        /// <summary>The 0-based line numbers of the first and last fully-or-partly visible lines (0/0 on error).</summary>
        private void GetVisibleLineRange(out int firstVisible, out int lastVisible)
        {
            firstVisible = 0;
            lastVisible = 0;
            try
            {
                var lines = _view.TextViewLines;
                if (lines != null && lines.Count > 0)
                {
                    firstVisible = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
                    lastVisible = lines.LastVisibleLine.Start.GetContainingLine().LineNumber;
                }
            }
            catch { }
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
                // to-be-deleted: rejected → faint (edit won't apply), accepted → strong, undecided → normal
                AddRect(_below!, tvl.Extent, tvl.TextTop, tvl.Height,
                    rejected ? DeleteFillDim : accepted ? DeleteFillStrong : DeleteFill);
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
                    // to-be-added: rejected → faint (edit won't apply), accepted → strong, undecided → normal
                    AddRect(_above!, anchorSpan, y, _view.LineHeight,
                        rejected ? AddFillDim : accepted ? AddFillStrong : AddFill);
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
                // new text: reverted → faint (it'll go), kept → strong, undecided → normal
                AddRect(_below!, tvl.Extent, tvl.TextTop, tvl.Height,
                    rejected ? AddFillDim : accepted ? AddFillStrong : AddFill);
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
                    // old text (ghost): kept → faint (discarded), reverted → strong (it'll come back), undecided → normal
                    AddRect(_above!, anchorSpan, y, _view.LineHeight,
                        accepted ? DeleteFillDim : rejected ? DeleteFillStrong : DeleteFill);
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
            bar.Children.Add(MakeButton(acceptLabel, hunk.State == HunkState.Accepted, accept: true, () => Decide(hunk, HunkState.Accepted)));
            bar.Children.Add(MakeButton(rejectLabel, hunk.State == HunkState.Rejected, accept: false, () => Decide(hunk, HunkState.Rejected)));

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

        private Button MakeButton(string text, bool active, bool accept, Action onClick)
        {
            var accent = accept ? AcceptBrush : RejectBrush;
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(2, 1, 2, 1),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ButtonTemplate,
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                // Chosen: solid accent fill + white bold text. Not chosen: faint tint, accent-colored text.
                Background = active ? (accept ? AcceptFillSolid : RejectFillSolid) : BtnInactiveBg,
                Foreground = active ? BtnChosenText : accent,
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                Opacity = active ? 1.0 : 0.9,
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>A minimal button template (rounded border + centered content) so our Background/Border/
        /// Foreground render exactly as set, without the OS theme's default gray chrome or hover repaint.</summary>
        private static ControlTemplate BuildButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            // Hover brightens slightly; disabled dims. Setters without a TargetName apply to the button itself.
            var hover = new System.Windows.Trigger { Property = System.Windows.UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new System.Windows.Setter(System.Windows.UIElement.OpacityProperty, 0.82));
            template.Triggers.Add(hover);
            var disabled = new System.Windows.Trigger { Property = System.Windows.UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new System.Windows.Setter(System.Windows.UIElement.OpacityProperty, 0.4));
            template.Triggers.Add(disabled);
            template.Seal();
            return template;
        }

        private void Decide(ReviewHunk hunk, HunkState state)
        {
            if (_review == null || _completed)
                return;
            hunk.State = state;
            Draw(); // refresh button labels / dimming
            ReviewChanged?.Invoke(); // repaint the scrollbar marks (decided hunks dim)
            _onStateChanged?.Invoke(); // let the chat chip enable/disable its Finish button
            // Proposal mode (a blocking permission prompt) auto-resolves once every hunk is decided.
            // Applied mode (post-turn review) waits for an explicit Finish so the user controls when the
            // file leaves the list — see FinishReview.
            if (!_applied && _review.AllDecided && !_completed)
            {
                _completed = true;
                var cb = _onCompleted;
                cb?.Invoke(); // host reconstructs updatedInput + resolves the CLI call, then Close()s us
            }
        }

        /// <summary>Explicitly completes an applied-mode (post-turn) review — the user pressed Finish once
        /// every change was decided. Commits via <see cref="_onCompleted"/> (bridge → CommitPath). No-op
        /// unless every hunk is decided.</summary>
        public void FinishReview()
        {
            if (_review == null || _completed || !_review.AllDecided)
                return;
            _completed = true;
            _onCompleted?.Invoke();
        }

        /// <summary>Resets every hunk back to undecided (the "reset all decisions" toolbar action) and
        /// redraws. Notifies the host so the chip's Finish button disables again.</summary>
        public void ResetDecisions()
        {
            if (_review == null || _completed || !_review.AnyDecided)
                return;
            _review.ResetDecisions();
            Draw();
            ReviewChanged?.Invoke();
            _onStateChanged?.Invoke();
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
