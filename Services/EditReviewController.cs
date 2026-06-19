using System;
using System.Collections.Generic;
using CodeAstrogator.Core.EditReview;
using CodeAstrogator.Editor;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Host side of the inline edit review (opt-in "Review edits in the editor"). Opens the edited
    /// file in the VS editor, hands the per-file review to that view's <see cref="EditReviewViewAdorner"/>
    /// (red/green diff + per-hunk Accept/Reject), and reports back to the bridge once every hunk is
    /// decided so it can reconstruct <c>updatedInput</c> from the accepted hunks. UI-thread only.
    /// </summary>
    internal sealed class EditReviewController : IDisposable
    {
        private readonly CodeAstrogatorPackage _package;
        private readonly Action<string> _onReviewCompleted; // requestId → bridge.FinalizeEditReview
        private readonly Dictionary<string, IWpfTextView> _views = new Dictionary<string, IWpfTextView>();

        public EditReviewController(CodeAstrogatorPackage package, Action<string> onReviewCompleted)
        {
            _package = package;
            _onReviewCompleted = onReviewCompleted;
        }

        /// <summary>Opens the file for <paramref name="review"/> and shows the inline diff adornments.
        /// Throws if the editor view can't be obtained (the bridge surfaces it as an error).</summary>
        public void Open(string requestId, EditReviewSession review)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(review.FilePath))
                throw new InvalidOperationException("The edit has no file path to open.");

            var view = OpenWpfTextView(review.FilePath);
            if (view == null)
                throw new InvalidOperationException("Could not open an editor view for " + review.FilePath);

            // One active review per view. If a different request already owns this file's view (parallel
            // edits to the same path), keep the new one on the chat card rather than overwriting.
            if (EditReviewViewAdorner.TryGet(view, out var existing) && existing != null && existing.HasReview
                && (!_views.TryGetValue(requestId, out var mapped) || mapped != view))
                throw new InvalidOperationException("Another edit for this file is already being reviewed — decide it first.");

            _views[requestId] = view;
            var adorner = EditReviewViewAdorner.GetOrCreate(view);
            adorner.SetReview(review, () => _onReviewCompleted(requestId));
        }

        /// <summary>Removes the review's adornments (on resolve, turn end, window close). Safe to call
        /// from any thread — turn-end / abandoned-prompt teardown comes in on a background thread, but
        /// editor access must be on the UI thread, so off-thread calls are marshalled.</summary>
        public void Close(string requestId)
        {
            if (ThreadHelper.CheckAccess())
            {
                CloseCore(requestId);
                return;
            }
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                CloseCore(requestId);
            }).Task.Forget();
        }

        private void CloseCore(string requestId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_views.TryGetValue(requestId, out var view))
                return;
            _views.Remove(requestId);
            try
            {
                if (EditReviewViewAdorner.TryGet(view, out var adorner))
                    adorner?.ClearReview();
            }
            catch { /* view may already be closed */ }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var view in _views.Values)
            {
                try
                {
                    if (EditReviewViewAdorner.TryGet(view, out var adorner))
                        adorner?.ClearReview();
                }
                catch { }
            }
            _views.Clear();
        }

        /// <summary>Opens (or focuses) the document and converts its shell text view to an IWpfTextView.</summary>
        private IWpfTextView? OpenWpfTextView(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(_package, path, VSConstants.LOGVIEWID_TextView,
                out _, out _, out IVsWindowFrame frame);
            frame?.Show();
            var vsTextView = VsShellUtilities.GetTextView(frame);
            if (vsTextView == null)
                return null;
            var adapters = _package.GetComponentModel()?.GetService<IVsEditorAdaptersFactoryService>();
            return adapters?.GetWpfTextView(vsTextView);
        }
    }
}
