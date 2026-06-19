using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace CodeAstrogator.Editor
{
    /// <summary>
    /// MEF exports for the inline edit-review feature. These are discovered because the VSIX
    /// manifest declares a <c>Microsoft.VisualStudio.MefComponent</c> asset. Two adornment layers
    /// (deletion highlights below the text, phantom additions + buttons above it) and a line-transform
    /// source that reserves vertical space for the phantom "added" lines so they don't overlap real
    /// code. The actual drawing lives in <see cref="EditReviewViewAdorner"/>, which the host-side
    /// <c>EditReviewController</c> drives per view (MEF parts can't reach the package/bridge directly,
    /// so they only coordinate through the per-view adorner registry).
    /// </summary>
    internal static class EditReviewLayers
    {
        public const string BelowTextLayer = "CodeAstrogator.EditReview.Below";
        public const string AboveTextLayer = "CodeAstrogator.EditReview.Above";

        // Behind the text: red highlight on lines that will be deleted.
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(BelowTextLayer)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        public static AdornmentLayerDefinition? BelowText;

        // Above the text: green phantom "added" lines (drawn in reserved empty space) + the
        // per-hunk Accept/Reject buttons (must be on top to stay clickable).
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(AboveTextLayer)]
        [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
        public static AdornmentLayerDefinition? AboveText;
    }

    /// <summary>Per-view provider for the line transform that reserves room for phantom added lines.</summary>
    [Export(typeof(ILineTransformSourceProvider))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class EditReviewLineTransformSourceProvider : ILineTransformSourceProvider
    {
        public ILineTransformSource Create(IWpfTextView textView) =>
            new EditReviewLineTransformSource(textView);
    }

    /// <summary>Adds bottom space below a buffer line equal to the number of phantom "added" lines
    /// that attach there (× the current line height). Returns the identity transform when no review
    /// is active for the view, so it never affects normal editing.</summary>
    internal sealed class EditReviewLineTransformSource : ILineTransformSource
    {
        private readonly IWpfTextView _view;

        public EditReviewLineTransformSource(IWpfTextView view) => _view = view;

        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
        {
            // Compose with the line's existing transform (other providers / default spacing) by
            // ADDING our reserved space to it, rather than returning an absolute transform.
            var prev = line.DefaultLineTransform;
            if (EditReviewViewAdorner.TryGet(_view, out var adorner) && adorner != null)
            {
                var (top, bottom) = adorner.GetReservedSpace(line);
                if (top != 0 || bottom != 0)
                    return new LineTransform(prev.TopSpace + top, prev.BottomSpace + bottom, prev.VerticalScale);
            }
            return prev;
        }
    }
}
