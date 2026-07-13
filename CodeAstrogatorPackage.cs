using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using CodeAstrogator.Bridge;
using CodeAstrogator.Options;
using CodeAstrogator.Services;
using CodeAstrogator.ToolWindows;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CodeAstrogator
{
    /// <summary>
    /// AsyncPackage of the Code Astrogator extension (Teil A §A2/§A6):
    /// registers the chat tool window and its menu command, and mirrors the persisted
    /// settings (WritableSettingsStore) into a AstrogatorOptions snapshot. The settings UI is
    /// the host-side <see cref="AstrogatorSettingsWindow"/> (opened via the gear popover).
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(CodeAstrogatorPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ClaudeChatWindow),
        Style = VsDockStyle.Tabbed,
        Orientation = ToolWindowOrientation.Right,
        Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
    public sealed class CodeAstrogatorPackage : AsyncPackage
    {
        public const string PackageGuidString = "35791cdf-0a50-4728-8e97-8e29c521696b";
        public static readonly Guid CommandSetGuid = new Guid("8b8ce5a8-7f6a-4b21-9f0e-2d3c1a9e44d1");
        public const int CmdIdShowChatWindow = 0x0100;
        public const int CmdIdAddFileToPrompt = 0x0101;
        public const int CmdIdAddSelectionToPrompt = 0x0102;

        private readonly AstrogatorOptions _options = new AstrogatorOptions();
        private AstrogatorSettingsStore? _store;

        /// <summary>Raised on the UI thread whenever a persisted setting changes.</summary>
        internal event Action? OptionsChanged;

        /// <summary>Non-null when loading/saving settings failed (shown in the chat for diagnosis).</summary>
        internal string? SettingsLoadError { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(new MenuCommand(
                    ShowChatWindow, new CommandID(CommandSetGuid, CmdIdShowChatWindow)));

                // Editor right-click → add the current file / the selection to the chat prompt.
                var addFile = new OleMenuCommand(
                    AddFileToPrompt, new CommandID(CommandSetGuid, CmdIdAddFileToPrompt));
                addFile.BeforeQueryStatus += (s, _) =>
                {
                    if (s is OleMenuCommand c) c.Visible = c.Enabled = HasActiveDocument();
                };
                commandService.AddCommand(addFile);

                var addSelection = new OleMenuCommand(
                    AddSelectionToPrompt, new CommandID(CommandSetGuid, CmdIdAddSelectionToPrompt));
                addSelection.BeforeQueryStatus += (s, _) =>
                {
                    if (s is OleMenuCommand c) c.Visible = c.Enabled = HasNonEmptySelection();
                };
                commandService.AddCommand(addSelection);
            }

            LoadSettings();
            RunRetentionCleanup(); // prune old history / pasted files per the retention settings
        }

        /// <summary>Kicks off the best-effort on-disk cleanup (old chat history + pasted images) on a
        /// background thread, using the current retention settings. No-op when both are "keep forever".</summary>
        private void RunRetentionCleanup()
        {
            var historyDays = _options.HistoryRetentionDays;
            var pastedDays = _options.PastedRetentionDays;
            if (historyDays <= 0 && pastedDays <= 0)
                return;
            _ = Task.Run(() => RetentionService.Cleanup(historyDays, pastedDays));
        }

        private void ShowChatWindow(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await ShowToolWindowAsync(typeof(ClaudeChatWindow), 0, create: true, DisposalToken);
                if (window?.Frame == null)
                    throw new NotSupportedException("Cannot create Code Astrogator tool window.");
            }).Task.Forget();
        }

        // ── Editor context-menu commands ───────────────────────────────────────

        private bool HasActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetDte()?.ActiveDocument != null;
        }

        private bool HasNonEmptySelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetDte()?.ActiveDocument?.Selection is EnvDTE.TextSelection sel && !sel.IsEmpty;
        }

        /// <summary>"Add file to Claude prompt" — attaches the active document as an @-reference chip.</summary>
        private void AddFileToPrompt(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var path = GetDte()?.ActiveDocument?.FullName;
                if (string.IsNullOrEmpty(path))
                    return;
                var bridge = await ShowAndGetBridgeAsync();
                bridge?.AddFileAttachments(new[] { path! });
            }).Task.Forget();
        }

        /// <summary>"Add selection to Claude prompt" — appends the selected code to the composer.</summary>
        private void AddSelectionToPrompt(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!(GetDte()?.ActiveDocument is EnvDTE.Document doc)
                    || !(doc.Selection is EnvDTE.TextSelection sel) || sel.IsEmpty)
                    return;
                var text = sel.Text ?? "";
                if (string.IsNullOrEmpty(text))
                    return;
                int top = sel.TopPoint.Line, bottom = sel.BottomPoint.Line;
                if (bottom > top && sel.BottomPoint.LineCharOffset == 1) // ends at col 1 → last line not covered
                    bottom--;
                var path = doc.FullName;
                var bridge = await ShowAndGetBridgeAsync();
                bridge?.AddSelectionToPrompt(path, top, bottom < top ? top : bottom);
            }).Task.Forget();
        }

        /// <summary>Opens (or focuses) the chat tool window and returns its bridge once ready.</summary>
        private async System.Threading.Tasks.Task<WebViewBridge?> ShowAndGetBridgeAsync()
        {
            var pane = await ShowToolWindowAsync(typeof(ClaudeChatWindow), 0, create: true, DisposalToken);
            if (pane is ClaudeChatWindow window)
            {
                try { return await window.GetBridgeAsync(); }
                catch { return null; } // WebView2 failed to initialize
            }
            return null;
        }

        /// <summary>Current settings snapshot (kept fresh by settings writes).</summary>
        internal AstrogatorOptions GetOptions() => _options;

        // ── Settings (classic WritableSettingsStore) ───────────────────────────

        /// <summary>Creates the settings store and reads the persisted values. UI thread.</summary>
        private void LoadSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _store = AstrogatorSettingsStore.Create(this);
                Copy(from: _store.Read(), to: _options);
            }
            catch (Exception ex)
            {
                RecordSettingsError("Reading settings failed: " + ex);
                // _options keeps its defaults
            }
        }

        /// <summary>
        /// Applies edited values (from the settings window), persists them, and notifies
        /// listeners so the chat updates live. UI thread.
        /// </summary>
        internal void UpdateOptions(AstrogatorOptions updated)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Copy(from: updated, to: _options);
            SaveOptions();
            OptionsChanged?.Invoke();
            RunRetentionCleanup(); // a shortened retention window should take effect immediately
        }

        /// <summary>Persists the current snapshot. UI thread. Used by the gear popover writes.</summary>
        internal void SaveOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _store?.Write(_options);
            }
            catch (Exception ex)
            {
                RecordSettingsError("Saving settings failed: " + ex);
            }
        }

        private static void Copy(AstrogatorOptions from, AstrogatorOptions to)
        {
            to.ClaudeExecutablePath = from.ClaudeExecutablePath ?? "";
            to.DefaultModel = from.DefaultModel ?? "";
            to.DefaultEffortString = from.DefaultEffortString ?? "high";
            to.UltracodeEnabled = from.UltracodeEnabled;
            to.PermissionModeString = from.PermissionModeString ?? "ask";
            to.AutoAcceptCommands = from.AutoAcceptCommands;
            to.AutoApprovePatterns = from.AutoApprovePatterns != null
                ? new System.Collections.Generic.List<string>(from.AutoApprovePatterns)
                : new System.Collections.Generic.List<string>();
            to.AccentColor = from.AccentColor ?? "";
            to.NoticeFetchEnabled = from.NoticeFetchEnabled;
            to.NoticeFetchDecided = from.NoticeFetchDecided;
            to.UpdateCheckEnabled = from.UpdateCheckEnabled;
            to.UpdateCheckDecided = from.UpdateCheckDecided;
            to.PromptTimeoutMinutes = AstrogatorOptions.ClampPromptTimeoutMinutes(from.PromptTimeoutMinutes);
            to.RestoreLastSession = from.RestoreLastSession;
            to.AutoAddActiveFile = from.AutoAddActiveFile;
            to.ActiveFileOnByDefault = from.ActiveFileOnByDefault;
            to.IncludeSelectedLines = from.IncludeSelectedLines;
            to.ThemeModeString = from.ThemeModeString ?? "auto";
            to.VerbosityString = from.VerbosityString ?? "normal";
            to.UsePersistentCli = from.UsePersistentCli;
            to.ReviewEditsInEditor = from.ReviewEditsInEditor;
            to.ReviewEditsAtTurnEnd = from.ReviewEditsAtTurnEnd;
            to.HistoryRetentionDays = AstrogatorOptions.ClampRetentionDays(from.HistoryRetentionDays);
            to.PastedRetentionDays = AstrogatorOptions.ClampRetentionDays(from.PastedRetentionDays);
        }

        private void RecordSettingsError(string message)
        {
            SettingsLoadError = message;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodeAstrogator");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "settings-error.log"),
                    DateTime.Now.ToString("o") + "  " + message + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // logging is best-effort
            }
        }

        /// <summary>Opens the settings window ("Advanced options…" in the gear popover). UI thread.</summary>
        internal void OpenOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = new AstrogatorSettingsWindow(this, _options);
            window.ShowModal(); // Save applies + persists; Cancel/X discards (AstrogatorSettingsWindow)
        }

        internal DTE2? GetDte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(typeof(SDTE)) as DTE2;
        }

        /// <summary>MEF composition container — used to resolve editor services
        /// (e.g. <c>IVsEditorAdaptersFactoryService</c> to turn an <c>IVsTextView</c> into an
        /// <c>IWpfTextView</c> for the inline edit-review adornments). UI thread.</summary>
        internal Microsoft.VisualStudio.ComponentModelHost.IComponentModel? GetComponentModel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
                as Microsoft.VisualStudio.ComponentModelHost.IComponentModel;
        }

        /// <summary>Working directory for the CLI child process = open solution/folder (Teil A §A3).</summary>
        internal string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsSolution)) is IVsSolution solution
                && ErrorHandler.Succeeded(solution.GetSolutionInfo(out var dir, out _, out _))
                && !string.IsNullOrEmpty(dir))
            {
                return dir;
            }
            return null;
        }
    }
}
