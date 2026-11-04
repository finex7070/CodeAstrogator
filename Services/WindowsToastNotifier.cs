using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Microsoft.Win32;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Shows Windows notifications (toasts) for the "Notify me" option. Uses the WinRT toast API
    /// (<c>Windows.UI.Notifications</c>) through reflection — .NET Framework resolves WinRT types by name
    /// (<c>ContentType=WindowsRuntime</c>), so no SDK-contracts package ships in the VSIX.
    /// <para>An unpackaged process needs an AppUserModelID to show toasts. Instead of a Start-menu shortcut
    /// the ID is registered under <c>HKCU\Software\Classes\AppUserModelId\CodeAstrogator.Chat</c>
    /// (display name + icon) — the same thing the Windows Community Toolkit does for desktop apps. The key
    /// is rewritten once per VS session, because the icon path changes with every extension update.</para>
    /// <para>Every toast has a tag (a request id, or "turn"): a new toast with the same tag replaces the old
    /// one, and <see cref="Remove"/> takes it out of the Action Center once it is no longer current. Tags are
    /// scoped to this process (<see cref="ToastContent.ScopedTag"/>) — all Visual Studio instances share the
    /// AppUserModelID, and an unscoped "turn" would let one instance replace or remove another's toast.</para>
    /// </summary>
    internal sealed class WindowsToastNotifier
    {
        public const string AppId = "CodeAstrogator.Chat";
        private const string Group = "codeastrogator";
        private const string WinRt = ", Windows.UI.Notifications, ContentType=WindowsRuntime";

        private readonly Action _onActivated;
        private readonly Action<string> _onError;
        private readonly object _gate = new object();
        // Every WinRT call runs here, one after the other, on thread-pool (MTA) threads. Show/Remove are called
        // from the UI thread (STA: permission prompts) and from the CLI stream thread (MTA: turn end); a WinRT
        // object created in the STA and then used from the MTA fails with RPC_E_WRONG_THREAD (0x8001010E) —
        // seen in VS 1.2.0 when a prompt's toast came first and the turn-end toast next. Serial also keeps a
        // Remove behind the Show of the same tag.
        private Task _queue = Task.CompletedTask; // guarded by _gate
        private bool _initialized;
        private bool _errorReported;
        private Type? _toastType;
        private Type? _xmlType;
        private Type? _notifierType;
        private Type? _historyType;
        private Type? _managerType;
        // the toasts still on screen / in the Action Center, by tag: holding the WinRT object keeps its
        // Activated handler alive, so a click still reaches us. Only touched inside the queue.
        private readonly Dictionary<string, object> _live = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <param name="onActivated">A toast was clicked (thread pool).</param>
        /// <param name="onError">Showing failed — called once, with the reason.</param>
        public WindowsToastNotifier(Action onActivated, Action<string> onError)
        {
            _onActivated = onActivated;
            _onError = onError;
        }

        /// <summary>Shows a toast; one with the same <paramref name="tag"/> is replaced. Any thread; returns at once.</summary>
        public void Show(string tag, ToastText text, string? attribution)
        {
            tag = ShortTag(tag);
            var xmlText = ToastContent.BuildXml(text, attribution, tag);
            Enqueue(() =>
            {
                try
                {
                    EnsureInitialized();
                    var xml = Activator.CreateInstance(_xmlType!);
                    _xmlType!.GetMethod("LoadXml", new[] { typeof(string) })!.Invoke(xml, new object[] { xmlText });
                    var toast = Activator.CreateInstance(_toastType!, xml);
                    _toastType!.GetProperty("Tag")!.SetValue(toast, tag);
                    _toastType.GetProperty("Group")!.SetValue(toast, Group);
                    HookActivated(toast);
                    // a fresh notifier each time — cheap, and never one created in another apartment
                    var notifier = _managerType!.GetMethod("CreateToastNotifier", new[] { typeof(string) })!
                        .Invoke(null, new object[] { AppId });
                    _notifierType!.GetMethod("Show")!.Invoke(notifier, new[] { toast });
                    _live[tag] = toast;
                }
                catch (Exception ex)
                {
                    ReportOnce(ex);
                }
            });
        }

        /// <summary>Takes the toast with this tag off the screen and out of the Action Center (the prompt it
        /// announced was answered, or a new turn started). Any thread; a no-op for unknown tags.</summary>
        public void Remove(string tag)
        {
            tag = ShortTag(tag);
            Enqueue(() =>
            {
                if (!_live.Remove(tag))
                    return;
                try
                {
                    var history = _managerType!.GetProperty("History")!.GetValue(null);
                    _historyType!.GetMethod("Remove", new[] { typeof(string), typeof(string), typeof(string) })!
                        .Invoke(history, new object[] { tag, Group, AppId });
                }
                catch
                {
                    // best effort — the toast just stays in the Action Center
                }
            });
        }

        private void Enqueue(Action work)
        {
            lock (_gate)
            {
                _queue = _queue.ContinueWith(_ => work(), CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _managerType = Type.GetType("Windows.UI.Notifications.ToastNotificationManager" + WinRt, throwOnError: true);
            _toastType = Type.GetType("Windows.UI.Notifications.ToastNotification" + WinRt, throwOnError: true);
            _notifierType = Type.GetType("Windows.UI.Notifications.ToastNotifier" + WinRt, throwOnError: true);
            _historyType = Type.GetType("Windows.UI.Notifications.ToastNotificationHistory" + WinRt, throwOnError: true);
            _xmlType = Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime",
                throwOnError: true);
            RegisterAppId();
            _initialized = true;
        }

        /// <summary>The AppUserModelID's name and icon in the notification (and in Windows' notification settings).</summary>
        private static void RegisterAppId()
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            key.SetValue("DisplayName", "Code Astrogator");
            var dir = Path.GetDirectoryName(typeof(WindowsToastNotifier).Assembly.Location) ?? "";
            var icon = Path.Combine(dir, "Resources", "icon-90.png");
            if (!File.Exists(icon))
                icon = Path.Combine(dir, "WebUI", "logo.png");
            if (File.Exists(icon))
                key.SetValue("IconUri", icon);
        }

        /// <summary>Activated is a WinRT event (<c>TypedEventHandler&lt;ToastNotification, object&gt;</c>).
        /// <c>EventInfo.AddEventHandler</c> refuses WinRT events ("not supported for WinRT events"), so the
        /// projected <c>add_Activated</c> is called directly; the returned registration token is not needed —
        /// the handler lives as long as the toast. Without it a click just dismisses the toast.</summary>
        private void HookActivated(object toast)
        {
            try
            {
                var ev = _toastType!.GetEvent("Activated");
                var handler = Delegate.CreateDelegate(ev!.EventHandlerType!, this,
                    typeof(WindowsToastNotifier).GetMethod(nameof(OnToastActivated), BindingFlags.NonPublic | BindingFlags.Instance)!);
                ev.GetAddMethod()!.Invoke(toast, new object[] { handler });
            }
            catch
            {
                // the toast still shows; a click only dismisses it
            }
        }

        private void OnToastActivated(object sender, object args)
        {
            try { _onActivated(); }
            catch { /* never let an exception escape into the WinRT callback */ }
        }

        private void ReportOnce(Exception ex)
        {
            if (_errorReported)
                return;
            _errorReported = true;
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            try { _onError(inner.Message); } catch { }
        }

        private static string ShortTag(string tag) => ToastContent.ScopedTag(tag, (int)CurrentProcessId);

        // ── foreground ───────────────────────────────────────────────────────

        private static readonly uint CurrentProcessId = (uint)Process.GetCurrentProcess().Id;

        /// <summary>True when a Visual Studio window (main window, floating tool window, dialog) is the active
        /// window — the user is looking at it, so a notification would only be noise.</summary>
        public static bool IsVisualStudioForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == CurrentProcessId;
        }

        /// <summary>Restores <paramref name="hwnd"/> if it is minimized (to its previous size — maximized
        /// stays maximized) and asks Windows to bring it to the front.</summary>
        public static void BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
