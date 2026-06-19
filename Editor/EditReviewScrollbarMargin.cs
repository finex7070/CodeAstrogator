using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeAstrogator.Core.EditReview;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CodeAstrogator.Editor
{
    /// <summary>
    /// MEF provider for the inline edit-review scrollbar marks. Adds a thin strip to the editor's
    /// overview margin (next to the vertical scrollbar, where git change / error marks live) so the
    /// reviewer can see at a glance where every proposed change sits in the file and jump to it.
    /// Created per view; reads the hunks from that view's <see cref="EditReviewViewAdorner"/>.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(EditReviewScrollbarMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.VerticalScrollBarContainer)]
    [Order(After = PredefinedMarginNames.OverviewChangeTracking)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class EditReviewScrollbarMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            // The scrollbar container hosts the vertical scrollbar; we need it to map buffer
            // positions → y so the marks line up with the scrollbar. No scrollbar → no margin.
            var scrollBar = marginContainer.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar)
                as IVerticalScrollBar;
            return scrollBar == null ? null : new EditReviewScrollbarMargin(wpfTextViewHost.TextView, scrollBar);
        }
    }

    /// <summary>The scrollbar mark strip itself: one coloured tick per hunk, positioned at the hunk's
    /// anchor line via <see cref="IVerticalScrollBar.GetYCoordinateOfBufferPosition"/>. Repaints when
    /// the review changes (attach/clear/decide) or the scrollbar track geometry changes.</summary>
    internal sealed class EditReviewScrollbarMargin : Canvas, IWpfTextViewMargin
    {
        public const string MarginName = "CodeAstrogator.EditReview.ScrollbarMargin";
        private const double MarkWidth = 6.0;
        private const double MarkHeight = 4.0;

        private static readonly Brush AddMark = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));        // green — additions
        private static readonly Brush DelMark = Frozen(Color.FromRgb(0xF8, 0x51, 0x49));        // red — pure deletion
        private static readonly Brush ModMark = Frozen(Color.FromRgb(0x9B, 0x7C, 0xF0));        // purple — changed (add+del)
        private static readonly Brush DoneMark = Frozen(Color.FromArgb(0x66, 0x9A, 0x9A, 0x9A)); // dim grey — decided

        private readonly IWpfTextView _view;
        private readonly IVerticalScrollBar _scrollBar;
        private readonly EditReviewViewAdorner _adorner;
        private bool _disposed;

        public EditReviewScrollbarMargin(IWpfTextView view, IVerticalScrollBar scrollBar)
        {
            _view = view;
            _scrollBar = scrollBar;
            Width = MarkWidth;
            ClipToBounds = true;
            _adorner = EditReviewViewAdorner.GetOrCreate(view);
            _adorner.ReviewChanged += Redraw;
            _scrollBar.TrackSpanChanged += OnTrackSpanChanged;
            _view.Closed += OnViewClosed;
            Redraw();
        }

        private void OnTrackSpanChanged(object sender, EventArgs e) => Redraw();
        private void OnViewClosed(object sender, EventArgs e) => Dispose();

        private void Redraw()
        {
            if (_disposed)
                return;
            Children.Clear();
            IReadOnlyList<ReviewHunk> hunks = _adorner.ReviewHunks;
            if (hunks.Count == 0)
                return;
            var snapshot = _view.TextSnapshot;
            foreach (var hunk in hunks)
            {
                try
                {
                    int line0 = hunk.AnchorLine - 1;
                    if (line0 < 0 || line0 >= snapshot.LineCount)
                        continue;
                    var point = snapshot.GetLineFromLineNumber(line0).Start;
                    double y = _scrollBar.GetYCoordinateOfBufferPosition(point);
                    var mark = new System.Windows.Shapes.Rectangle
                    {
                        Width = MarkWidth,
                        Height = MarkHeight,
                        Fill = hunk.State != HunkState.Pending ? DoneMark : MarkBrush(hunk),
                    };
                    SetLeft(mark, 0);
                    SetTop(mark, y - MarkHeight / 2);
                    Children.Add(mark);
                }
                catch { /* a single bad mark must not break the margin */ }
            }
        }

        private static Brush MarkBrush(ReviewHunk h)
        {
            bool hasAdd = h.AddedLines.Count > 0;
            bool hasDel = h.DeletedLines.Count > 0;
            if (hasAdd && hasDel) return ModMark;
            return hasDel ? DelMark : AddMark;
        }

        // ── IWpfTextViewMargin / ITextViewMargin ─────────────────────────────
        public FrameworkElement VisualElement { get { ThrowIfDisposed(); return this; } }
        public double MarginSize { get { ThrowIfDisposed(); return MarkWidth; } }
        public bool Enabled { get { ThrowIfDisposed(); return true; } }

        public ITextViewMargin? GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _adorner.ReviewChanged -= Redraw;
            _scrollBar.TrackSpanChanged -= OnTrackSpanChanged;
            _view.Closed -= OnViewClosed;
            Children.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(MarginName);
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
