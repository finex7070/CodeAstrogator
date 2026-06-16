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
            // instead of spawning a bare WebView2 popup window.
            core.NewWindowRequested += OnNewWindowRequested;

            _bridge = new WebViewBridge(core, package);
            _bridgeReady.TrySetResult(_bridge);

            // Drag & drop: let the host (not Chromium) handle external drops so we get the real
            // file paths the CLI needs (HTML5 File objects don't expose the filesystem path).
            // AllowExternalDrop=false makes the WebView2 control forward OS drops as WPF routed
            // events — but it raises the BUBBLING DragOver/Drop events, NOT the tunneling Preview*
            // ones, so we must subscribe to those (PreviewDragOver/PreviewDrop never fire here).
            _webView.AllowExternalDrop = false;
            _webView.AllowDrop = true;
            _webView.DragOver += OnWebViewDragOver;
            _webView.Drop += OnWebViewDrop;

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        /// <summary>Opens new-window links (target="_blank") in the user's default browser.</summary>
        private static void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            var uri = e.Uri;
            if (string.IsNullOrEmpty(uri))
                return;
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

        private static void OnWebViewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnWebViewDrop(object sender, DragEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // WPF drop events are raised on the UI thread
            if (_bridge != null
                && e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                && paths.Length > 0)
            {
                _bridge.AddFileAttachments(paths); // → attach.added chips (CLI reads files by path)
            }
            e.Handled = true;
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
