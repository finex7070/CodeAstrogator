using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;

namespace CodeAstrogator.ToolWindows
{
    /// <summary>Dockable chat tool window hosting the WebView2 UI (Teil A §A2).</summary>
    [Guid("3f1c2d8a-94b7-4f5e-8a6d-7c0b9e21f3a4")]
    public sealed class ClaudeChatWindow : ToolWindowPane
    {
        // Matches the moniker in Resources\CodeAstrogator.imagemanifest (and the .vsct guidCodeAstrogatorIcons).
        private static readonly Guid AstrogatorIconsGuid = new Guid("854bf90d-a9a9-4ae4-b6ce-8546aaae07c7");

        private readonly ClaudeChatWindowControl _control;

        public ClaudeChatWindow() : base(null)
        {
            Caption = "Code Astrogator";
            BitmapImageMoniker = new ImageMoniker { Guid = AstrogatorIconsGuid, Id = 1 };
            _control = new ClaudeChatWindowControl();
            Content = _control;
        }

        protected override void Initialize()
        {
            base.Initialize();
            if (Package is CodeAstrogatorPackage package)
                _control.Initialize(package);
        }

        /// <summary>Resolves with the chat bridge once the WebView2 has initialized — used by the
        /// editor right-click commands to add files/selections to the prompt after opening.</summary>
        // VSTHRD003: the task is our own TCS (ClaudeChatWindowControl), not foreign work.
#pragma warning disable VSTHRD003
        internal System.Threading.Tasks.Task<Bridge.WebViewBridge> GetBridgeAsync() => _control.BridgeReady;
#pragma warning restore VSTHRD003

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // tool window teardown is on the UI thread
            if (disposing)
                _control.Dispose();
            base.Dispose(disposing);
        }
    }
}
