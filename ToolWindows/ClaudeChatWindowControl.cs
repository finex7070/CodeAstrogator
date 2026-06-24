using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CodeAstrogator.Bridge;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodeAstrogator.ToolWindows
{
    /// <summary>
    /// WPF content of the chat tool window: a WebView2 that serves the embedded
    /// web UI (Teil B) via a virtual-host mapping and connects it to the bridge.
    /// </summary>
    public sealed class ClaudeChatWindowControl : Grid, IDisposable
    {
        private const string VirtualHost = "codeastrogator.local";

        private readonly WebView2 _webView;
        private WebViewBridge? _bridge;
        private bool _initialized;

        // Completes once the bridge exists (WebView2 initialized) so editor commands that open the
        // window can await it before adding files/selections to the prompt.
        private readonly TaskCompletionSource<WebViewBridge> _bridgeReady
            = new TaskCompletionSource<WebViewBridge>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolves with the bridge once WebView2 has initialized (faults if init failed).</summary>
        internal Task<WebViewBridge> BridgeReady => _bridgeReady.Task;

        public ClaudeChatWindowControl()
        {
            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                // matches --bg dark default; avoids a white flash before the page paints
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0f, 0x0f, 0x10),
            };
            Children.Add(_webView);
        }

        /// <summary>Called by the tool window pane once the package is available.</summary>
        public void Initialize(CodeAstrogatorPackage package)
        {
            if (_initialized)
                return;
            _initialized = true;

            package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await InitializeWebViewAsync(package);
                }
                catch (Exception ex)
                {
                    _bridgeReady.TrySetException(ex); // unblock awaiting editor commands
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Children.Clear();
                    Children.Add(new TextBlock
                    {
                        Text = "Failed to initialize WebView2:\n" + ex.Message,
                        Margin = new Thickness(12),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }).Task.Forget();
        }

        private async Task InitializeWebViewAsync(CodeAstrogatorPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            TrySetLoaderDllFolderPath();

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true; // handy while the extension matures

            var webUiDir = Path.Combine(
                Path.GetDirectoryName(typeof(ClaudeChatWindowControl).Assembly.Location) ?? "",
                "WebUI");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webUiDir, CoreWebView2HostResourceAccessKind.Allow);

            // target="_blank" links (banner / rendered markdown) open in the system browser
            // instead of spawning a bare WebView2 popup window. File drops that open in a new
            // window (multi-file drops) are caught here too — see OnNewWindowRequested.
            core.NewWindowRequested += OnNewWindowRequested;

            _bridge = new WebViewBridge(core, package);
            _bridgeReady.TrySetResult(_bridge);

            // Drag & drop of files/folders → attachment chips (the CLI reads them by path).
            //
            // History: we used to set AllowExternalDrop=false and try to handle the OS drop via WPF
            // routed events (DragOver/Drop). That forwarding is NOT contractual and simply does not
            // happen in the current WebView2 runtime — AllowExternalDrop=false just makes Chromium
            // REJECT the drop (the "no-drop" cursor the user saw everywhere). No WPF route, tunneling
            // or bubbling, ever fired.
            //
            // Fix: let Chromium ACCEPT the drop (AllowExternalDrop=true). The page has no JS drop
            // handler, so Chromium's default action for a dropped file is to NAVIGATE to its
            // file:// URL. We intercept that navigation (OnNavigationStarting / OnNewWindowRequested),
            // CANCEL it (never let the SPA navigate away), and recover the real local path from the
            // URL → AddFileAttachments. This keeps real paths AND folder support, and relies only on
            // documented CoreWebView2 navigation events instead of fragile drag-event forwarding.
            _webView.AllowExternalDrop = true;
            core.NavigationStarting += OnNavigationStarting;

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        /// <summary>Cancels file:// navigations caused by dropping a file onto the page and turns
        /// them into attachments instead (keeps the SPA from navigating away to the file).</summary>
        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // CoreWebView2 events are raised on the UI thread
            if (IsFileUri(e.Uri))
            {
                e.Cancel = true; // do not navigate the app to the dropped file
                AttachDroppedFileUri(e.Uri);
            }
        }

        /// <summary>Opens real web links (target="_blank") in the system browser; turns file://
        /// "new window" requests (e.g. a multi-file drop) into attachments.</summary>
        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // CoreWebView2 events are raised on the UI thread
            e.Handled = true; // never spawn a bare WebView2 popup
            var uri = e.Uri;
            if (string.IsNullOrEmpty(uri))
                return;
            if (IsFileUri(uri))
            {
                AttachDroppedFileUri(uri);
                return;
            }
            // Only follow real web links — never let the page launch arbitrary local schemes.
            if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // no browser / blocked — best-effort, nothing to surface
            }
        }

        private static bool IsFileUri(string? uri) =>
            !string.IsNullOrEmpty(uri) && uri!.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

        /// <summary>Converts a dropped file:// URL to a local path and attaches it (UI thread).</summary>
        private void AttachDroppedFileUri(string uri)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // called from UI-thread navigation events
            string? path = null;
            try { path = new Uri(uri).LocalPath; } // file:///C:/a%20b/f.png → C:\a b\f.png (decodes %20)
            catch { /* malformed URL — ignore */ }
            if (!string.IsNullOrEmpty(path))
                _bridge?.AddFileAttachments(new[] { path! });
        }

        /// <summary>
        /// The VSIX carries WebView2Loader.dll under runtimes\win-{arch}\native;
        /// point the SDK there explicitly so probing never depends on the host process dir.
        /// </summary>
        private static void TrySetLoaderDllFolderPath()
        {
            try
            {
                var baseDir = Path.GetDirectoryName(typeof(ClaudeChatWindowControl).Assembly.Location) ?? "";
                var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    .ToString().ToLowerInvariant();
                var loaderDir = Path.Combine(baseDir, "runtimes", "win-" + arch, "native");
                if (File.Exists(Path.Combine(loaderDir, "WebView2Loader.dll")))
                    CoreWebView2Environment.SetLoaderDllFolderPath(loaderDir);
            }
            catch
            {
                // loader already resolved elsewhere — fine
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // disposed by the tool window on the UI thread
            _bridge?.Dispose();
            _bridge = null;
            _webView.Dispose();
        }
    }
}
