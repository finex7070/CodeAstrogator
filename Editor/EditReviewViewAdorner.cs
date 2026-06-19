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
        private static readonly Brush DimText = Frozen(Color.FromArgb(0x99, 0x9A, 0x9A, 0x9A));

        private readonly IWpfTextView _view;
        private IAdornmentLayer? _below;
        private IAdornmentLayer? _above;

        private EditReviewSession? _review;
        private Action? _onCompleted;
        private bool _completed;
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

        /// <summary>Attaches a review to this view and (re)draws it. Replaces any prior review.</summary>
        public void SetReview(EditReviewSession review, Action onCompleted)
        {
            _review = review ?? throw new ArgumentNullException(nameof(review));
            _onCompleted = onCompleted;
            _completed = false;
            _reviewSnapshot = _view.TextSnapshot;
            ComputeReservedSpace();
            // The buffer must not change underneath the review (line numbers would drift); cancel if it does.
            // Idempotent: SetReview may run twice on the same view (re-open / two edits to the same file).
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            ForceRelayout();   // re-query the line transform so the reserved space appears, then draw
            Draw();
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
                try { DrawHunk(hunk, snapshot); }
                catch { /* never let a draw error break the editor */ }
            }
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

        private void AddButtonBar(ReviewHunk hunk, SnapshotSpan span, double top)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x25, 0x29, 0x33)),
            };
            bar.Children.Add(MakeButton(hunk.State == HunkState.Accepted ? "✓ Accepted" : "✓ Accept",
                hunk.State == HunkState.Accepted, () => Decide(hunk, HunkState.Accepted)));
            bar.Children.Add(MakeButton(hunk.State == HunkState.Rejected ? "✕ Rejected" : "✕ Reject",
                hunk.State == HunkState.Rejected, () => Decide(hunk, HunkState.Rejected)));

            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = bar.DesiredSize.Width;
            Canvas.SetLeft(bar, Math.Max(_view.ViewportLeft, _view.ViewportLeft + _view.ViewportWidth - w - 18));
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

        private void AddAddedText(SnapshotSpan span, string text, double left, double top, bool dim)
        {
            var tb = new TextBlock
            {
                Text = text.Length == 0 ? " " : text,
                Foreground = dim ? DimText : AddText,
                FontFamily = GetEditorFontFamily(),
                FontSize = GetEditorFontSize(),
                Height = _view.LineHeight,
                IsHitTestVisible = false,
                TextDecorations = dim ? TextDecorations.Strikethrough : null,
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
