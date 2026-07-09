using System;
using System.Collections.Generic;
using CodeAstrogator.Core.EditReview;
using CodeAstrogator.Editor;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
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

        // ── post-turn "Review edits at end of turn" (path-keyed, already-applied edits) ──────────
        // Unlike the requestId-keyed flow above (edit is a pending proposal, buffer holds the OLD text),
        // here the edit is ALREADY on disk. The buffer keeps the new content and the adorner renders in
        // "applied" mode (added lines highlighted green in place, removed lines as red ghosts). The buffer
        // is read-only for the review's duration; on commit only a reverted file is written back to disk.
        // See docs/NOTES.md ("Review edits at end of turn").
        private sealed class TurnFileReview
        {
            public IWpfTextView View = null!;
            public IVsWindowFrame? Frame;
            public readonly List<IReadOnlyRegion> ReadOnly = new List<IReadOnlyRegion>();
            // The frame's editor-caption suffix before we appended the "+N -M" review counts, so we can
            // restore it when the review ends (usually null → clears back to just the file name).
            public string? PriorCaption;
            public bool CaptionSet;
        }
        private readonly Dictionary<string, TurnFileReview> _turnViews =
            new Dictionary<string, TurnFileReview>(StringComparer.OrdinalIgnoreCase);

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
            adorner.ScrollToFirstHunk(); // land on the first change instead of the caret's old position
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
            foreach (var path in new List<string>(_turnViews.Keys))
            {
                try { CancelPathCore(path); } catch { }
            }
            _turnViews.Clear();
        }

        /// <summary>Opens <paramref name="path"/> for the post-turn review. The edit is ALREADY applied, so
        /// the buffer keeps the new content: the added lines are highlighted green in place and the removed
        /// old lines are shown as red ghost text (applied mode). The buffer is made read-only for the
        /// review's duration so stray typing / Ctrl+S can't disturb the anchors. When every hunk is decided,
        /// <paramref name="onDecided"/> fires (bridge → CommitPath). Throws if the view can't be obtained.
        /// UI thread.</summary>
        public void OpenForPath(string path, EditReviewSession review, Action onDecided, Action onStateChanged)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("The changed file has no path to open.");
            if (_turnViews.ContainsKey(path))
            {
                // already open for review → just focus it
                _turnViews[path].Frame?.Show();
                return;
            }

            VsShellUtilities.OpenDocument(_package, path, VSConstants.LOGVIEWID_TextView,
                out _, out _, out IVsWindowFrame frame);
            frame?.Show();
            var vsTextView = VsShellUtilities.GetTextView(frame);
            var view = vsTextView == null ? null : _package.GetComponentModel()
                ?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsTextView);
            if (view == null)
                throw new InvalidOperationException("Could not open an editor view for " + path);

            AttachReview(path, view, frame, review, onDecided, onStateChanged);
        }

        /// <summary>If <paramref name="path"/> is ALREADY open in an editor, attach the inline review to it
        /// right away so the green/red diff shows without the user having to click the chip first. No-op when
        /// the file isn't open (it gets opened + attached on demand via <see cref="OpenForPath"/>) or already
        /// under review. Marshals to the UI thread; safe to call from a session-event background thread.</summary>
        public void AttachIfOpen(string path, EditReviewSession review, Action onDecided, Action onStateChanged)
        {
#pragma warning disable VSTHRD010
            RunOnUi(() => AttachIfOpenCore(path, review, onDecided, onStateChanged));
#pragma warning restore VSTHRD010
        }

        private void AttachIfOpenCore(string path, EditReviewSession review, Action onDecided, Action onStateChanged)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || _turnViews.ContainsKey(path))
                return;
            if (!VsShellUtilities.IsDocumentOpen(_package, path, Guid.Empty,
                    out _, out _, out IVsWindowFrame frame) || frame == null)
                return; // not open → leave it for the chip click
            var vsTextView = VsShellUtilities.GetTextView(frame);
            var view = vsTextView == null ? null : _package.GetComponentModel()
                ?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsTextView);
            if (view == null)
                return; // open but not a text view (e.g. a designer) → skip auto-attach
            AttachReview(path, view, frame, review, onDecided, onStateChanged);
        }

        /// <summary>Attaches the applied-mode review to an open <paramref name="view"/>: locks the buffer
        /// read-only (so stray typing / Ctrl+S can't shift the diff anchors), records the view, wires the
        /// tab-close cleanup, draws the review and scrolls to the first change. UI thread.</summary>
        private void AttachReview(string path, IWpfTextView view, IVsWindowFrame? frame,
            EditReviewSession review, Action onDecided, Action onStateChanged)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rec = new TurnFileReview { View = view, Frame = frame };
            // The buffer already holds the applied (new) content — no swap. Lock it read-only, then attach the
            // review in applied mode.
            AddReadOnly(rec);
            _turnViews[path] = rec;
            // If the user closes the editor tab without deciding, drop the entry (like a cancel) so the chip
            // can reopen the file — otherwise OpenForPath would short-circuit to Show() on a closed frame and
            // nothing would happen. The review stays pending host-side, so the chip remains in the list.
            EventHandler? onClosed = null;
            onClosed = (s, e) =>
            {
                view.Closed -= onClosed;
                if (_turnViews.TryGetValue(path, out var cur) && ReferenceEquals(cur, rec))
                    CancelPathCore(path);
            };
            view.Closed += onClosed;
            var adorner = EditReviewViewAdorner.GetOrCreate(view);
            adorner.SetReview(review, onDecided, applied: true, onStateChanged: onStateChanged);
            adorner.ScrollToFirstHunk(); // land on the first change instead of the caret's old position
            ApplyReviewCaption(rec, review);
        }

        /// <summary>Appends the review's "+N -M" line counts to the editor tab caption (so the tab shows e.g.
        /// "GraphicPixelExplosion.cs +24 -3" while it's under review), remembering the prior caption to
        /// restore later. Best-effort — a failure just leaves the plain file-name tab.</summary>
        private static void ApplyReviewCaption(TurnFileReview rec, EditReviewSession review)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var frame = rec.Frame;
            if (frame == null)
                return;
            int added = 0, removed = 0;
            foreach (var h in review.Hunks)
            {
                added += h.AddedLines.Count;
                removed += h.DeletedLines.Count;
            }
            try
            {
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, out var prior) == VSConstants.S_OK)
                    rec.PriorCaption = prior as string;
                // VS appends the caption straight onto the file name, so lead with a space and wrap the
                // counts in parentheses, e.g. "GraphicPixelExplosion.cs (+24 -3)" (VS keeps a leading space).
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, " (+" + added + " -" + removed + ")");
                rec.CaptionSet = true;
            }
            catch { /* best effort — leave the plain tab caption */ }
        }

        /// <summary>Restores the editor tab caption we changed in <see cref="ApplyReviewCaption"/> (no-op if we
        /// never set it or the frame is gone).</summary>
        private static void RestoreCaption(TurnFileReview rec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!rec.CaptionSet || rec.Frame == null)
                return;
            try { rec.Frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, rec.PriorCaption); } catch { }
            rec.CaptionSet = false;
        }

        /// <summary>Finalizes a post-turn review: removes the read-only lock and the adornments, then either
        /// deletes the file (a new file whose creation was fully rejected) or, when something was reverted,
        /// writes <paramref name="finalContent"/> (accepted kept, rejected reverted) to the buffer and saves
        /// it. Accept-all writes nothing (the buffer already holds the final content). No-op if the path
        /// isn't open. Safe to call off the UI thread (marshals).</summary>
        public void CommitPath(string path, string finalContent, bool deleteFile)
        {
            // RunOnUi marshals to the UI thread; the analyzer can't see that CommitPathCore runs there.
#pragma warning disable VSTHRD010
            RunOnUi(() => CommitPathCore(path, finalContent, deleteFile));
#pragma warning restore VSTHRD010
        }

        private void CommitPathCore(string path, string finalContent, bool deleteFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_turnViews.TryGetValue(path, out var rec))
                return;
            _turnViews.Remove(path);
            try
            {
                RestoreCaption(rec);
                if (EditReviewViewAdorner.TryGet(rec.View, out var adorner))
                    adorner?.ClearReview();
                RemoveReadOnly(rec);
                if (deleteFile)
                {
                    rec.Frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
                }
                else if (finalContent != rec.View.TextBuffer.CurrentSnapshot.GetText())
                {
                    // something was reverted → write the reconstruction back; accept-all falls through (the
                    // buffer already equals the final content, so no write / no EOL churn).
                    ReplaceBufferText(rec.View.TextBuffer, finalContent);
                    SaveDocument(rec);
                }
            }
            catch { /* leave the file as-is on any editor failure; the chip is already cleared host-side */ }
        }

        /// <summary>Cancels an open post-turn review without deciding it (window closed / "Keep all" /
        /// teardown): drops the read-only lock. The buffer was never changed, so disk stays as applied.</summary>
        public void CancelPath(string path)
        {
#pragma warning disable VSTHRD010
            RunOnUi(() => CancelPathCore(path));
#pragma warning restore VSTHRD010
        }

        private void CancelPathCore(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_turnViews.TryGetValue(path, out var rec))
                return;
            _turnViews.Remove(path);
            try
            {
                RestoreCaption(rec);
                if (EditReviewViewAdorner.TryGet(rec.View, out var adorner))
                    adorner?.ClearReview();
                RemoveReadOnly(rec);
            }
            catch { }
        }

        /// <summary>True when <paramref name="path"/> is currently open in a post-turn review (so the caller
        /// routes a revert through <see cref="CommitPath"/> instead of writing to disk under an open buffer).</summary>
        public bool IsReviewing(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return path != null && _turnViews.ContainsKey(path);
        }

        /// <summary>Cancels every open post-turn review (Keep all / session change / turn abandon).</summary>
        public void CloseAllTurnReviews()
        {
#pragma warning disable VSTHRD010
            RunOnUi(() =>
            {
                foreach (var path in new List<string>(_turnViews.Keys))
                    CancelPathCore(path);
            });
#pragma warning restore VSTHRD010
        }

        private static void ReplaceBufferText(ITextBuffer buffer, string text)
        {
            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(0, buffer.CurrentSnapshot.Length, text ?? "");
                edit.Apply();
            }
        }

        private static void AddReadOnly(TurnFileReview rec)
        {
            var buffer = rec.View.TextBuffer;
            var roEdit = buffer.CreateReadOnlyRegionEdit();
            try
            {
                var region = roEdit.CreateReadOnlyRegion(
                    new Span(0, buffer.CurrentSnapshot.Length),
                    SpanTrackingMode.EdgeInclusive, EdgeInsertionMode.Deny);
                roEdit.Apply();
                rec.ReadOnly.Add(region);
            }
            catch { roEdit.Cancel(); }
        }

        private static void RemoveReadOnly(TurnFileReview rec)
        {
            if (rec.ReadOnly.Count == 0)
                return;
            var roEdit = rec.View.TextBuffer.CreateReadOnlyRegionEdit();
            try
            {
                foreach (var region in rec.ReadOnly)
                    roEdit.RemoveReadOnlyRegion(region);
                roEdit.Apply();
            }
            catch { roEdit.Cancel(); }
            rec.ReadOnly.Clear();
        }

        /// <summary>Saves the (already-edited) document via its window frame's doc data.</summary>
        private static void SaveDocument(TurnFileReview rec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (rec.Frame != null
                    && rec.Frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData) == VSConstants.S_OK)
                    (docData as IVsPersistDocData)?.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out _);
            }
            catch { /* best effort — the buffer still shows the result */ }
        }

        private void RunOnUi(Action action)
        {
            if (ThreadHelper.CheckAccess())
            {
                action();
                return;
            }
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                action();
            }).Task.Forget();
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
