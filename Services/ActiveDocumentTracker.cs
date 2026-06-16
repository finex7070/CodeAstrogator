using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Tracks the file in the active editor tab (Teil-B-Ergänzung: auto-reference the
    /// active document). Uses <see cref="IVsMonitorSelection"/> and listens for changes
    /// of the active <c>SEID_DocumentFrame</c> — focusing a tool window (e.g. our own
    /// chat) does not change that frame, so the last active code file stays put.
    /// </summary>
    internal sealed class ActiveDocumentTracker : IVsSelectionEvents, IDisposable
    {
        private readonly IVsMonitorSelection? _monitor;
        private uint _cookie;

        /// <summary>Raised on the UI thread with the new active document path (null = none).</summary>
        public event Action<string?>? ActiveDocumentChanged;

        /// <summary>Full path of the file in the active editor tab, or null.</summary>
        public string? CurrentPath { get; private set; }

        public ActiveDocumentTracker(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _monitor = serviceProvider.GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_monitor == null)
                return;

            _monitor.AdviseSelectionEvents(this, out _cookie);

            // Initial value: the document frame that is active right now.
            if (ErrorHandler.Succeeded(_monitor.GetCurrentElementValue(
                    (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var current)))
            {
                CurrentPath = FrameToPath(current as IVsWindowFrame);
            }
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame)
            {
                var path = FrameToPath(varValueNew as IVsWindowFrame);
                if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentPath = path;
                    ActiveDocumentChanged?.Invoke(path);
                }
            }
            return VSConstants.S_OK;
        }

        public int OnSelectionChanged(
            IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
            => VSConstants.S_OK;

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        private static string? FrameToPath(IVsWindowFrame? frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null)
                return null;
            if (ErrorHandler.Succeeded(frame.GetProperty(
                    (int)__VSFPROPID.VSFPROPID_pszMkDocument, out var value))
                && value is string path && path.Length > 0)
            {
                return path;
            }
            return null;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_monitor != null && _cookie != 0)
            {
                _monitor.UnadviseSelectionEvents(_cookie);
                _cookie = 0;
            }
        }
    }
}
