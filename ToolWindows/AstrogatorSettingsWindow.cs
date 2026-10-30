using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CodeAstrogator.Options;
using CodeAstrogator.Services;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace CodeAstrogator.ToolWindows
{
    /// <summary>
    /// Host-side settings window opened from the gear popover's "Advanced options…"
    /// (web → host <c>options.open</c>). Edits the <see cref="AstrogatorOptions"/> that are not in the gear
    /// popover (theme, verbosity, model·mode, accent and syntax theme live there). Every change applies
    /// <b>live</b> (short debounce; text fields on Enter / leaving the field) via the package — persisted and
    /// OptionsChanged raised, so the chat restyles while the window is open. What cannot be undone waits for
    /// the close: retention cleanup and deleting removed fonts. "Done" (and the window's X) keeps the
    /// changes; "Cancel" (Esc) restores the options as they were when the window opened; "Reset to defaults"
    /// loads the defaults (applied live as well). All controls are tinted with the active VS theme brushes
    /// so text/background follow Dark/Light correctly.
    /// </summary>
    internal sealed class AstrogatorSettingsWindow : DialogWindow
    {
        private readonly CodeAstrogatorPackage _package;
        private readonly AstrogatorOptions _current;  // the package's live options — popover-managed values are carried over
        private readonly AstrogatorOptions _original; // as they were when the window opened (Cancel restores it)

        // live apply: control changes restart the timer; it applies once things settle
        private readonly System.Windows.Threading.DispatcherTimer _applyTimer;
        private bool _loading;   // Load() fills the controls — not a user change
        private bool _reverted;  // Cancel already restored the original options
        private bool _dirty;     // something was changed — only then does closing apply (and mark the consents decided)

        private readonly TextBox _exePath;
        private readonly ObservableCollection<PatternItem> _patterns = new ObservableCollection<PatternItem>();
        private readonly DataGrid _autoApprove;
        private readonly Button _removePattern;
        private readonly ComboBox _userColor;      // preset name or a typed #hex
        private readonly ComboBox _bubbleIntensity;
        private readonly CheckBox _colorBlindDiffs;
        private readonly ObservableCollection<string> _customFonts = new ObservableCollection<string>(); // file names
        private readonly ComboBox _uiFont;    // items: FontChoice
        private readonly ComboBox _uiScale;   // items: "0.7×" … "1.5×"
        private readonly ComboBox _codeFont;
        private readonly ComboBox _codeScale;

        /// <summary>One entry of a font list: the option id (<c>default</c>, <c>roboto</c>, <c>custom:&lt;file&gt;</c>, …)
        /// and what the list shows.</summary>
        private sealed class FontChoice
        {
            public FontChoice(string id, string label) { Id = id; Label = label; }
            public string Id { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }

        private static readonly (string Id, string Label)[] BuiltInFontLabels =
        {
            ("default", "Default"), ("cascadia", "Cascadia Code"), ("roboto", "Roboto"),
            ("arial", "Arial"), ("times", "Times New Roman"),
        };

        /// <summary>Presets of the "your messages" color (label → CSS hex; "" = the built-in blue).</summary>
        private static readonly (string Label, string Hex)[] UserColorPresets =
        {
            ("Default (blue)", ""), ("Gray", "#8a8f98"), ("Purple", "#8d5fc7"), ("Teal", "#2bb3a3"),
            ("Green", "#5aa469"), ("Amber", "#d9a23f"), ("Clay", "#d97757"), ("Magenta", "#c45fa0"),
        };
        private static readonly string[] BubbleLabels = { "Off", "Subtle", "Normal", "Strong" };
        private readonly ListBox _fontList;
        private readonly Button _removeFont;
        private readonly CheckBox _restore;
        private readonly CheckBox _promptSuggestions;
        private readonly CheckBox _autoAdd;
        private readonly CheckBox _includeLines;
        private readonly CheckBox _activeFileDefault;
        private readonly CheckBox _noticeFetch;
        private readonly CheckBox _updateCheck;
        private readonly CheckBox _modelCatalogFetch;
        private readonly CheckBox _autoUpdate;

        /// <summary>"Check now" button + result line per check kind ("notice", "update", "models").</summary>
        private readonly Dictionary<string, CheckNowUi> _checkUi = new Dictionary<string, CheckNowUi>();
        private readonly TextBox _promptTimeout;
        private readonly ComboBox _historyRetention;
        private readonly ComboBox _pastedRetention;
        private readonly CheckBox _checkpoints;
        private readonly ComboBox _checkpointRetention;
        private readonly TextBox _checkpointMaxMb;
        private readonly ComboBox _checkpointFilterMode;
        private readonly ObservableCollection<PatternItem> _extensions = new ObservableCollection<PatternItem>();
        private readonly DataGrid _checkpointExtensions;
        private readonly Button _removeExtension;
        private readonly Button _deleteCheckpoints;

        private const string FilterModeBlacklist = "Blacklist — skip these extensions";
        private const string FilterModeWhitelist = "Whitelist — snapshot only these extensions";

        public AstrogatorSettingsWindow(CodeAstrogatorPackage package, AstrogatorOptions current)
        {
            _package = package;
            _current = current;
            _original = package.SnapshotOptions();
            _applyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _applyTimer.Tick += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _applyTimer.Stop();
                ApplyAndPersist(preview: true);
            };
            _package.ManualCheckCompleted += OnManualCheckCompleted;
            Closed += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _package.ManualCheckCompleted -= OnManualCheckCompleted;
                foreach (var ui in _checkUi.Values)
                    ui.Timeout?.Stop();
                _applyTimer.Stop();
                // Done / X: the final apply — also flushes a pending change or an open cell edit, and now
                // runs what was held back while previewing (retention cleanup, deleting removed fonts)
                if (_dirty && !_reverted)
                    ApplyAndPersist(preview: false);
            };

            Title = "Code Astrogator - Settings";
            // three columns — wide instead of tall, so the window fits without scrolling
            Width = Math.Min(1340, Math.Max(900, SystemParameters.WorkArea.Width - 40));
            SizeToContent = SizeToContent.Height;
            // Cap the height to the screen so the window never grows taller than the display on
            // small screens; past the cap the content ScrollViewer (below) scrolls instead.
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMaximizeButton = false;
            HasMinimizeButton = false;

            // Match the active VS theme (chrome is themed by DialogWindow; tint the content too).
            SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
            SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);

            _exePath = MakeTextBox();
            _removePattern = MakeButton("Remove", minWidth: 90);
            _autoApprove = MakeItemGrid(_patterns, "Command / tool pattern", _removePattern);
            _removeExtension = MakeButton("Remove", minWidth: 90);
            _checkpointExtensions = MakeItemGrid(_extensions, "File extension (e.g. .exe)", _removeExtension);
            _removeFont = MakeButton("Remove", minWidth: 90);
            _fontList = MakeFontList(_customFonts, _removeFont);
            _uiFont = MakeFontCombo();
            _codeFont = MakeFontCombo();
            _uiScale = MakeCombo(AstrogatorOptions.FontScales.Select(ScaleLabel).ToArray());
            _codeScale = MakeCombo(AstrogatorOptions.FontScales.Select(ScaleLabel).ToArray());
            _uiScale.MinWidth = _codeScale.MinWidth = 80;
            // a font added/removed below shows up in (or disappears from) both font lists right away
            _customFonts.CollectionChanged += (_, __) => RefreshFontCombos();
            _userColor = MakeCombo(UserColorPresets.Select(p => p.Label).ToArray());
            _userColor.IsEditable = true; // a typed #rrggbb is a custom color
            _bubbleIntensity = MakeCombo(BubbleLabels);
            _colorBlindDiffs = MakeCheck("Color-blind friendly diffs: blue for added and orange for removed lines, in the panel "
                + "and in the inline editor review (instead of green and red)", new Thickness(0, 8, 0, 0));
            _restore = MakeCheck("Restore the last session when the chat window opens", new Thickness(0, 8, 0, 0));
            _promptSuggestions = MakeCheck("Suggest the next prompt after each turn (shown as grey text in the empty input; Tab takes it over). Costs one extra, mostly cached request per turn", new Thickness(0, 8, 0, 0));
            _autoAdd = MakeCheck("Reference the active editor file in each prompt", new Thickness(0, 8, 0, 0));
            _includeLines = MakeCheck("Include the selected line range in the file reference", new Thickness(20, 6, 0, 0));
            _activeFileDefault = MakeCheck("Reference it by default in new chats (otherwise a new chat starts with the reference off; toggle it on via the chip)", new Thickness(20, 6, 0, 0));
            _autoAdd.Checked += (_, __) => { _includeLines.IsEnabled = true; _activeFileDefault.IsEnabled = true; };
            _autoAdd.Unchecked += (_, __) => { _includeLines.IsEnabled = false; _activeFileDefault.IsEnabled = false; };
            _noticeFetch = MakeCheck("Periodically check the project's GitHub for announcements and show them as a banner (makes a network request)", new Thickness(0, 8, 0, 0));
            _updateCheck = MakeCheck("Notify me about new versions (checks the project's GitHub for updates and shows a banner)", new Thickness(0, 8, 0, 0));
            _autoUpdate = MakeCheck("Install updates automatically: download new releases from the project's GitHub and install them when Visual Studio closes (checked at most every 3 h)", new Thickness(20, 6, 0, 0));
            _modelCatalogFetch = MakeCheck("Keep the model list up to date from the project's GitHub, so newly released Claude models appear in the picker without an extension update (checked at most every 12 h; off = the list shipped with this version is used)", new Thickness(0, 8, 0, 0));
            _promptTimeout = MakeTextBox();
            _promptTimeout.HorizontalAlignment = HorizontalAlignment.Left;
            _promptTimeout.MinWidth = 80;
            _historyRetention = MakeRetentionCombo();
            _pastedRetention = MakeRetentionCombo();
            var gitAvailable = Core.GitCheckpointService.IsGitAvailable();
            _checkpoints = MakeCheck(
                "Create a file checkpoint before each prompt and at the end of each turn (rewind any message "
                + "from its hover menu). Snapshots live outside the project — your own git repo is never touched.",
                new Thickness(0, 8, 0, 0));
            _checkpointRetention = MakeRetentionCombo();
            _checkpointMaxMb = MakeTextBox();
            _checkpointMaxMb.HorizontalAlignment = HorizontalAlignment.Left;
            _checkpointMaxMb.MinWidth = 80;
            _checkpointFilterMode = MakeCombo(FilterModeBlacklist, FilterModeWhitelist);
            _checkpointFilterMode.MinWidth = 300;
            _checkpoints.IsEnabled = gitAvailable;
            _checkpointRetention.IsEnabled = gitAvailable;
            _checkpointMaxMb.IsEnabled = gitAvailable;
            _checkpointFilterMode.IsEnabled = gitAvailable;
            _checkpointExtensions.IsEnabled = gitAvailable;
            _deleteCheckpoints = MakeButton("Delete all checkpoints now", minWidth: 190);
            _deleteCheckpoints.HorizontalAlignment = HorizontalAlignment.Left;
            _deleteCheckpoints.Margin = new Thickness(0, 8, 0, 0);
            _deleteCheckpoints.Click += (_, __) => DeleteAllCheckpoints();
            UpdateCheckpointSizeLabel();

            // Three columns instead of one very long list — the window is wide rather than tall, so it fits
            // the screen without scrolling. Every block is its own card (Section) so the groups read apart
            // at a glance. Order chosen by the user: banners/look/fonts left, behavior/storage/permissions in the
            // middle, the tall Checkpoints block with Claude CLI below it on the right.
            var left = new StackPanel();
            left.Children.Add(Section("Announcements & updates",
                WithCheckNow(_noticeFetch, "notice"),
                WithCheckNow(_updateCheck, "update"),
                _autoUpdate,
                WithCheckNow(_modelCatalogFetch, "models")));
            // Model & effort live in the in-chat Model·Mode popover, theme + verbosity in the gear popover.
            left.Children.Add(Section("Appearance",
                Labeled("Color of your messages (a preset, or type a hex color like #4f8cf0):", _userColor),
                Labeled("Message bubble tint:", _bubbleIntensity),
                _colorBlindDiffs));
            left.Children.Add(Section("Fonts",
                FontRow("UI", _uiFont, _uiScale),
                FontRow("Code", _codeFont, _codeScale),
                Hint("UI = the whole panel (conversation, prompt box, menus, status bar); Code = code blocks, diffs "
                    + "and command output. The size multiplies every text's normal size."),
                Labeled("Your own fonts (.ttf, .otf, .woff, .woff2):", _fontList),
                FontButtons(),
                Hint("Fonts added here appear in the UI and Code lists above. They are copied into the extension's "
                    + "folder, so the original file can be moved.")));

            var middle = new StackPanel();
            middle.Children.Add(Section("Behavior",
                _restore, _promptSuggestions, _autoAdd, _includeLines, _activeFileDefault));
            middle.Children.Add(Section("History & storage",
                Labeled(
                    "Automatically delete chat history older than (by last activity; \"Never\" keeps it forever):",
                    _historyRetention),
                Labeled(
                    "Automatically delete pasted images older than (files under …\\CodeAstrogator\\pasted):",
                    _pastedRetention)));
            middle.Children.Add(Section("Permissions",
                Labeled(
                    "Auto-approve patterns (* = wildcard) — matching Bash/PowerShell commands and MCP tools "
                    + "skip the permission prompt. The \"Always\" button on a prompt adds the command/tool here.",
                    _autoApprove),
                GridButtons(_autoApprove, _patterns, _removePattern),
                Labeled(
                    $"Prompt timeout — how long a permission prompt / question waits for your answer "
                    + $"before it expires (minutes, {AstrogatorOptions.MinPromptTimeoutMinutes}–{AstrogatorOptions.MaxPromptTimeoutMinutes}):",
                    _promptTimeout)));

            var right = new StackPanel();
            var checkpointItems = new List<UIElement> { _checkpoints };
            if (!gitAvailable)
                checkpointItems.Add(Hint("Git was not found on PATH — checkpoints are unavailable. "
                    + "Install Git for Windows and restart Visual Studio."));
            checkpointItems.Add(Labeled(
                $"Skip files larger than (MB, 0 = no limit, max {AstrogatorOptions.MaxCheckpointFileMb}):",
                _checkpointMaxMb));
            checkpointItems.Add(Labeled("Extension filter:", _checkpointFilterMode));
            checkpointItems.Add(Labeled("File extensions (empty list = no extension filter):",
                _checkpointExtensions));
            checkpointItems.Add(GridButtons(_checkpointExtensions, _extensions, _removeExtension));
            checkpointItems.Add(Hint("Large binaries are what makes a snapshot expensive — a rewind is "
                + "meant for source code. Files excluded here are never restored by a rewind."));
            checkpointItems.Add(Labeled("Keep checkpoints for:", _checkpointRetention));
            checkpointItems.Add(Hint("\"Never\" keeps them until you delete them. Checkpoints take disk space, "
                + "so they can expire before the chat history they belong to."));
            checkpointItems.Add(_deleteCheckpoints);
            right.Children.Add(Section("Checkpoints (rewind)", checkpointItems.ToArray()));
            right.Children.Add(Section("Claude CLI",
                Labeled("Claude executable path (optional override; empty = resolve automatically):", WithBrowse(_exePath))));

            var columns = new Grid { Margin = new Thickness(16) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(middle, 2);
            Grid.SetColumn(right, 4);
            columns.Children.Add(left);
            columns.Children.Add(middle);
            columns.Children.Add(right);
            var panel = columns;

            var reset = MakeButton("Reset to defaults", minWidth: 130);
            reset.Click += (_, __) =>
            {
                Load(new AstrogatorOptions());
                ScheduleApply(); // applied live like any other change (Cancel still goes back)
            };

            var cancel = MakeButton("Cancel", minWidth: 90);
            cancel.IsCancel = true; // Esc → back to how it was when the window opened
            cancel.ToolTip = "Undo all changes made since this window was opened";
            cancel.Click += (_, __) => RevertAndClose();

            var done = MakeButton("Done", minWidth: 90);
            done.IsDefault = true; // Enter; the window's X keeps the changes too (they are already applied)
            done.Click += (_, __) => Close();

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
            cancel.Margin = new Thickness(0, 0, 8, 0);
            rightButtons.Children.Add(cancel);
            rightButtons.Children.Add(done);

            var liveHint = new TextBlock
            {
                Text = "Changes apply right away.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Opacity = 0.7,
            };

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            DockPanel.SetDock(reset, Dock.Left);
            DockPanel.SetDock(liveHint, Dock.Left);
            DockPanel.SetDock(rightButtons, Dock.Right);
            buttons.Children.Add(reset);
            buttons.Children.Add(liveHint);
            buttons.Children.Add(rightButtons);
            Grid.SetRow(buttons, 1);
            Grid.SetColumnSpan(buttons, 5); // the button row spans all columns
            panel.Children.Add(buttons);

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Load(current);
            WireLiveApply();
        }

        private void Load(AstrogatorOptions o)
        {
            _loading = true;
            try
            {
                LoadControls(o);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadControls(AstrogatorOptions o)
        {
            _exePath.Text = o.ClaudeExecutablePath ?? "";
            _patterns.Clear();
            foreach (var line in o.AutoApprovePatterns ?? new System.Collections.Generic.List<string>())
            {
                var p = (line ?? "").Trim();
                if (p.Length > 0)
                    _patterns.Add(new PatternItem { Pattern = p });
            }
            _removePattern.IsEnabled = false;
            var userHex = (o.UserMessageColor ?? "").Trim().ToLowerInvariant();
            var preset = UserColorPresets.FirstOrDefault(p => p.Hex == userHex);
            if (preset.Label != null)
                _userColor.SelectedItem = preset.Label;
            else
                _userColor.Text = userHex;
            var bubble = AstrogatorOptions.NormalizeBubbleIntensity(o.BubbleIntensity);
            _bubbleIntensity.SelectedItem = BubbleLabels[Array.IndexOf(AstrogatorOptions.BubbleIntensities, bubble)];
            _colorBlindDiffs.IsChecked = o.ColorBlindDiffs;
            _customFonts.Clear();
            foreach (var font in o.CustomFonts ?? new List<string>())
            {
                if (CustomFontStore.Exists(font))
                    _customFonts.Add(font);
            }
            _removeFont.IsEnabled = false;
            RefreshFontCombos(AstrogatorOptions.NormalizeFont(o.ChatFont, _customFonts),
                AstrogatorOptions.NormalizeFont(o.CodeFont, _customFonts));
            _uiScale.SelectedItem = ScaleLabel(AstrogatorOptions.ClampFontScale(o.ChatFontScale));
            _codeScale.SelectedItem = ScaleLabel(AstrogatorOptions.ClampFontScale(o.CodeFontScale));
            _restore.IsChecked = o.RestoreLastSession;
            _promptSuggestions.IsChecked = o.PromptSuggestions;
            _autoAdd.IsChecked = o.AutoAddActiveFile;
            _includeLines.IsChecked = o.IncludeSelectedLines;
            _includeLines.IsEnabled = o.AutoAddActiveFile;
            _activeFileDefault.IsChecked = o.ActiveFileOnByDefault;
            _activeFileDefault.IsEnabled = o.AutoAddActiveFile;
            _noticeFetch.IsChecked = o.NoticeFetchEnabled;
            _updateCheck.IsChecked = o.UpdateCheckEnabled;
            _modelCatalogFetch.IsChecked = o.ModelCatalogFetchEnabled;
            _autoUpdate.IsChecked = o.AutoUpdateEnabled;
            _promptTimeout.Text = AstrogatorOptions.ClampPromptTimeoutMinutes(o.PromptTimeoutMinutes).ToString();
            SelectRetention(_historyRetention, o.HistoryRetentionDays);
            SelectRetention(_pastedRetention, o.PastedRetentionDays);
            _checkpoints.IsChecked = o.CheckpointsEnabled;
            SelectRetention(_checkpointRetention, o.CheckpointRetentionDays);
            _checkpointMaxMb.Text = AstrogatorOptions.ClampCheckpointMaxFileMb(o.CheckpointMaxFileMb).ToString();
            SelectCombo(_checkpointFilterMode,
                o.CheckpointExtensionsAreWhitelist ? FilterModeWhitelist : FilterModeBlacklist, FilterModeBlacklist);
            _extensions.Clear();
            foreach (var ext in AstrogatorOptions.NormalizeExtensions(o.CheckpointExtensions))
                _extensions.Add(new PatternItem { Pattern = ext });
            _removeExtension.IsEnabled = false;
        }

        /// <summary>
        /// Shows how much disk the checkpoint snapshots use, on the delete button itself. Measured on a
        /// background thread: a snapshot repo can hold tens of thousands of loose objects, and stat-ing
        /// them on the UI thread visibly delayed the window opening.
        /// </summary>
        private void UpdateCheckpointSizeLabel()
        {
            _deleteCheckpoints.Content = "Delete all checkpoints now (measuring…)";
            _deleteCheckpoints.IsEnabled = false;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var bytes = Core.GitCheckpointService.GetAllReposSize();
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _deleteCheckpoints.Content = "Delete all checkpoints now (" + FormatSize(bytes) + ")";
                _deleteCheckpoints.IsEnabled = bytes > 0;
            }).Task.Forget();
        }

        private void DeleteAllCheckpoints()
        {
            var answer = MessageBox.Show(this,
                "Delete every file checkpoint of every workspace?\n\n"
                + "Rewinding to an earlier message will no longer be possible for existing chats. "
                + "Your project files and your own git repository are not affected.",
                "Delete checkpoints", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
                return;
            // Also off the UI thread: clearing read-only git objects can be tens of thousands of files.
            _deleteCheckpoints.Content = "Deleting…";
            _deleteCheckpoints.IsEnabled = false;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                Core.GitCheckpointService.DeleteAllRepos();
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateCheckpointSizeLabel();
            }).Task.Forget();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "empty";
            if (bytes < 1024L * 1024)
                return Math.Max(1, bytes / 1024) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        /// <summary>Builds the options from the controls and applies + persists them. <paramref name="preview"/> =
        /// a live change while the window is open (see <see cref="CodeAstrogatorPackage.OptionsPreview"/>).</summary>
        private void ApplyAndPersist(bool preview)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!preview)
            {
                // flush in-progress cell edits — not on a live apply, that would end the edit under the cursor
                _autoApprove.CommitEdit(DataGridEditingUnit.Row, true);
                _checkpointExtensions.CommitEdit(DataGridEditingUnit.Row, true);
            }
            var patterns = _patterns
                .Select(p => (p.Pattern ?? "").Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var updated = new AstrogatorOptions
            {
                ClaudeExecutablePath = _exePath.Text?.Trim() ?? "",
                AutoApprovePatterns = patterns,
                ThemeModeString = _current.ThemeModeString, // set in the gear popover only
                VerbosityString = _current.VerbosityString,
                RestoreLastSession = _restore.IsChecked == true,
                PromptSuggestions = _promptSuggestions.IsChecked == true,
                AutoAddActiveFile = _autoAdd.IsChecked == true,
                IncludeSelectedLines = _includeLines.IsChecked == true,
                ActiveFileOnByDefault = _activeFileDefault.IsChecked == true,
                NoticeFetchEnabled = _noticeFetch.IsChecked == true,
                NoticeFetchDecided = true, // setting it here counts as having decided → no consent popup
                UpdateCheckEnabled = _updateCheck.IsChecked == true,
                UpdateCheckDecided = true,
                ModelCatalogFetchEnabled = _modelCatalogFetch.IsChecked == true,
                ModelCatalogFetchDecided = true,
                AutoUpdateEnabled = _autoUpdate.IsChecked == true,
                AutoUpdateDecided = true,
                PromptTimeoutMinutes = ParsePromptTimeout(_promptTimeout.Text),
                HistoryRetentionDays = SelectedRetention(_historyRetention),
                PastedRetentionDays = SelectedRetention(_pastedRetention),
                CheckpointsEnabled = _checkpoints.IsChecked == true,
                CheckpointsDecided = true, // deciding it here also silences the consent popup
                CheckpointRetentionDays = SelectedRetention(_checkpointRetention),
                CheckpointMaxFileMb = ParseMaxFileMb(_checkpointMaxMb.Text),
                CheckpointExtensionsAreWhitelist = Selected(_checkpointFilterMode, FilterModeBlacklist) == FilterModeWhitelist,
                CheckpointExtensions = AstrogatorOptions.NormalizeExtensions(
                    _extensions.Select(e => e.Pattern ?? "")),
                // Popover-managed state (Model·Mode + accent color) — carry it over untouched
                DefaultModel = _current.DefaultModel,
                DefaultEffortString = _current.DefaultEffortString,
                UltracodeEnabled = _current.UltracodeEnabled,
                PermissionModeString = _current.PermissionModeString,
                AutoAcceptCommands = _current.AutoAcceptCommands,
                ReviewEditsInEditor = _current.ReviewEditsInEditor,
                ReviewEditsAtTurnEnd = _current.ReviewEditsAtTurnEnd,
                AccentColor = _current.AccentColor,
                SyntaxTheme = _current.SyntaxTheme,     // chosen in the gear popover
                UserMessageColor = ParseUserColor(_userColor.Text),
                BubbleIntensity = AstrogatorOptions.BubbleIntensities[
                    Math.Max(0, Array.IndexOf(BubbleLabels, _bubbleIntensity.SelectedItem as string ?? "Normal"))],
                ColorBlindDiffs = _colorBlindDiffs.IsChecked == true,
                ChatFont = SelectedFontId(_uiFont),
                ChatFontScale = SelectedScale(_uiScale),
                CodeFont = SelectedFontId(_codeFont),
                CodeFontScale = SelectedScale(_codeScale),
                CustomFonts = _customFonts.ToList(),    // added/removed here (Fonts section)
            };
            _package.UpdateOptions(updated, preview);
        }

        /// <summary>A control changed: apply once the user pauses (a dropdown scrolled with the arrow keys or
        /// a burst of checkbox clicks applies once, not per step).</summary>
        private void ScheduleApply()
        {
            if (_loading || _reverted)
                return;
            _dirty = true;
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        /// <summary>Hooks every control to <see cref="ScheduleApply"/>. Text fields apply on Enter or when the
        /// field is left — not per keystroke (a half-typed path or hex color would apply on the way).</summary>
        private void WireLiveApply()
        {
            foreach (var combo in new[] { _userColor, _bubbleIntensity, _uiFont, _uiScale, _codeFont, _codeScale,
                         _historyRetention, _pastedRetention, _checkpointRetention, _checkpointFilterMode })
                combo.SelectionChanged += (_, __) => ScheduleApply();
            foreach (var check in new[] { _colorBlindDiffs, _restore, _promptSuggestions, _autoAdd, _includeLines,
                         _activeFileDefault, _noticeFetch, _updateCheck, _autoUpdate, _modelCatalogFetch, _checkpoints })
            {
                check.Checked += (_, __) => ScheduleApply();
                check.Unchecked += (_, __) => ScheduleApply();
            }
            foreach (var field in new Control[] { _exePath, _promptTimeout, _checkpointMaxMb, _userColor })
            {
                var edited = false; // only typed text counts — merely tabbing through a field changes nothing
                field.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                    new TextChangedEventHandler((_, __) => { if (!_loading) edited = true; }));
                void Commit()
                {
                    if (!edited)
                        return;
                    edited = false;
                    ScheduleApply();
                }
                field.LostKeyboardFocus += (_, __) => Commit();
                field.KeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter)
                        Commit();
                };
            }
            _patterns.CollectionChanged += (_, __) => ScheduleApply();
            _extensions.CollectionChanged += (_, __) => ScheduleApply();
            _customFonts.CollectionChanged += (_, __) => ScheduleApply();
            // an edited row: CellEditEnding fires before the value is committed — the debounce applies after it
            _autoApprove.CellEditEnding += (_, __) => ScheduleApply();
            _checkpointExtensions.CellEditEnding += (_, __) => ScheduleApply();
        }

        /// <summary>Cancel: back to the options as they were when the window opened (also undoes what was
        /// applied live), then close.</summary>
        private void RevertAndClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _applyTimer.Stop();
            _reverted = true;
            _package.UpdateOptions(_original);
            Close();
        }

        // ── small UI helpers ───────────────────────────────────────────────────

        private static TextBox MakeTextBox()
        {
            var t = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
            t.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
            return t;
        }

        /// <summary>Editable single-column list (theme-tinted DataGrid) — used for the auto-approve
        /// patterns and for the checkpoint extension filter. <paramref name="removeButton"/> is enabled
        /// while rows are selected.</summary>
        private DataGrid MakeItemGrid(ObservableCollection<PatternItem> items, string header, Button removeButton)
        {
            var grid = new DataGrid
            {
                ItemsSource = items,
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                CanUserAddRows = false,   // rows are added via the "Add" button
                CanUserResizeRows = false,
                CanUserReorderColumns = false,
                RowHeaderWidth = 0,
                SelectionMode = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                MinHeight = 120,
                MaxHeight = 200,
                Margin = new Thickness(0, 6, 0, 0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
            };
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(nameof(PatternItem.Pattern))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });

            // Tint with the active VS theme so the grid reads correctly in Dark & Light.
            grid.SetResourceReference(Control.BackgroundProperty, VsBrushes.WindowKey);
            grid.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
            grid.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ComboBoxBorderKey);
            grid.SetResourceReference(DataGrid.RowBackgroundProperty, VsBrushes.WindowKey);
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, VsBrushes.PanelBorderKey);

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(VsBrushes.ToolWindowBackgroundKey)));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(VsBrushes.WindowTextKey)));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(VsBrushes.PanelBorderKey)));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            grid.ColumnHeaderStyle = headerStyle;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(VsBrushes.WindowTextKey)));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            grid.CellStyle = cellStyle;

            grid.SelectionChanged += (_, __) => removeButton.IsEnabled = grid.SelectedItems.Count > 0;
            return grid;
        }

        // ── fonts ───────────────────────────────────────────────────────────

        private static string ScaleLabel(int percent) =>
            (percent / 100.0).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "×";

        private static int SelectedScale(ComboBox c)
        {
            var label = c.SelectedItem as string;
            foreach (var s in AstrogatorOptions.FontScales)
                if (ScaleLabel(s) == label)
                    return s;
            return 100;
        }

        private static string SelectedFontId(ComboBox c) => (c.SelectedItem as FontChoice)?.Id ?? AstrogatorOptions.DefaultFont;

        private static ComboBox MakeFontCombo()
        {
            var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200, Padding = new Thickness(6, 3, 6, 3) };
            c.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            return c;
        }

        /// <summary>Refills both font lists (built-ins + the fonts currently in the "own fonts" list), keeping the
        /// selection — or <paramref name="uiId"/>/<paramref name="codeId"/> when given. A selected own font that was
        /// removed falls back to Default.</summary>
        private void RefreshFontCombos(string? uiId = null, string? codeId = null)
        {
            uiId ??= SelectedFontId(_uiFont);
            codeId ??= SelectedFontId(_codeFont);
            var choices = BuiltInFontLabels.Select(f => new FontChoice(f.Id, f.Label))
                .Concat(_customFonts.Select(f => new FontChoice(AstrogatorOptions.CustomFontPrefix + f, CustomFontStore.DisplayName(f))))
                .ToList();
            foreach (var (combo, id) in new[] { (_uiFont, uiId), (_codeFont, codeId) })
            {
                combo.Items.Clear();
                foreach (var choice in choices)
                    combo.Items.Add(choice);
                combo.SelectedItem = choices.FirstOrDefault(c => c.Id == id) ?? choices[0];
            }
        }

        /// <summary>"UI" / "Code" label + font list + size list in one line.</summary>
        private static FrameworkElement FontRow(string label, ComboBox font, ComboBox scale)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label + ":", Width = 44, VerticalAlignment = VerticalAlignment.Center });
            font.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(font);
            sp.Children.Add(scale);
            return sp;
        }

        /// <summary>A preset label → its hex; a typed <c>#rgb</c>/<c>#rrggbb</c> → itself; anything else → "" (default).</summary>
        private static string ParseUserColor(string? text)
        {
            var t = (text ?? "").Trim();
            var preset = UserColorPresets.FirstOrDefault(p => string.Equals(p.Label, t, StringComparison.OrdinalIgnoreCase));
            if (preset.Label != null)
                return preset.Hex;
            if (!t.StartsWith("#", StringComparison.Ordinal))
                t = "#" + t;
            return System.Text.RegularExpressions.Regex.IsMatch(t, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
                ? t.ToLowerInvariant() : "";
        }

        /// <summary>The list of the user's own fonts (shown by name, stored as file names).</summary>
        private static ListBox MakeFontList(ObservableCollection<string> items, Button removeButton)
        {
            var list = new ListBox
            {
                ItemsSource = items,
                MinHeight = 60,
                MaxHeight = 140,
                Margin = new Thickness(0, 6, 0, 0),
                SelectionMode = SelectionMode.Extended,
                DisplayMemberPath = null,
            };
            list.ItemTemplate = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
            };
            list.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty,
                new Binding { Converter = new FontNameConverter() });
            list.SetResourceReference(Control.BackgroundProperty, VsBrushes.WindowKey);
            list.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
            list.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ComboBoxBorderKey);
            list.SelectionChanged += (_, __) => removeButton.IsEnabled = list.SelectedItems.Count > 0;
            return list;
        }

        /// <summary>"Add font file…" copies the picked font into the store right away (a cancelled window leaves
        /// it unreferenced; the bridge deletes unreferenced fonts) — "Remove" only drops it from the list.</summary>
        private FrameworkElement FontButtons()
        {
            var add = MakeButton("Add font file…", minWidth: 120);
            add.Click += (_, __) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add a font",
                    Filter = CustomFontStore.FileFilter,
                    Multiselect = true,
                };
                if (dialog.ShowDialog(this) != true)
                    return;
                foreach (var path in dialog.FileNames)
                {
                    try
                    {
                        var name = CustomFontStore.Import(path);
                        if (!_customFonts.Contains(name, StringComparer.OrdinalIgnoreCase))
                            _customFonts.Add(name);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, System.IO.Path.GetFileName(path) + ": " + ex.Message, "Code Astrogator",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            };
            _removeFont.Click += (_, __) =>
            {
                foreach (var name in _fontList.SelectedItems.OfType<string>().ToList())
                    _customFonts.Remove(name);
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            add.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(add);
            sp.Children.Add(_removeFont);
            return sp;
        }

        /// <summary>Shows a stored font file name as its display name.</summary>
        private sealed class FontNameConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                CustomFontStore.DisplayName(value as string ?? "");

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                Binding.DoNothing;
        }

        /// <summary>"Add" / "Remove" buttons under an item grid. Add appends a row and starts editing it.</summary>
        private FrameworkElement GridButtons(DataGrid grid, ObservableCollection<PatternItem> items, Button removeButton)
        {
            var add = MakeButton("Add", minWidth: 90);
            add.Click += (_, __) =>
            {
                var item = new PatternItem();
                items.Add(item);
                grid.SelectedItem = item;
                grid.ScrollIntoView(item);
                if (grid.Columns.Count > 0)
                {
                    grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[0]);
                    grid.BeginEdit();
                }
            };
            removeButton.Click += (_, __) =>
            {
                foreach (var item in grid.SelectedItems.OfType<PatternItem>().ToList())
                    items.Remove(item);
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            add.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(add);
            sp.Children.Add(removeButton);
            return sp;
        }

        private static ComboBox MakeCombo(params string[] items)
        {
            var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 170, Padding = new Thickness(6, 3, 6, 3) };
            foreach (var i in items)
                c.Items.Add(i);
            c.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            return c;
        }

        private static CheckBox MakeCheck(string content, Thickness margin)
        {
            // Wrap the label so long descriptions don't get clipped at the window's right edge.
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                Margin = margin,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            cb.SetResourceReference(StyleProperty, VsResourceKeys.CheckBoxStyleKey);
            return cb;
        }

        private static Button MakeButton(string content, double minWidth)
        {
            var b = new Button { Content = content, Padding = new Thickness(12, 4, 12, 4), MinWidth = minWidth };
            b.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            return b;
        }

        private static void SelectCombo(ComboBox c, string? value, string fallback)
        {
            var v = value ?? fallback;
            if (!c.Items.Contains(v))
                v = fallback;
            c.SelectedItem = v;
        }

        private static string Selected(ComboBox c, string fallback) => c.SelectedItem as string ?? fallback;

        // ── retention combos (day presets ↔ friendly labels; 0 = "Never") ────────
        private static string RetentionLabel(int days) => days <= 0 ? "Never" : days + " days";

        private static ComboBox MakeRetentionCombo() =>
            MakeCombo(AstrogatorOptions.RetentionDayChoices.Select(RetentionLabel).ToArray());

        private static void SelectRetention(ComboBox c, int days) =>
            SelectCombo(c, RetentionLabel(AstrogatorOptions.ClampRetentionDays(days)), "Never");

        private static int SelectedRetention(ComboBox c)
        {
            var label = c.SelectedItem as string ?? "Never";
            foreach (var d in AstrogatorOptions.RetentionDayChoices)
                if (RetentionLabel(d) == label)
                    return d;
            return 0;
        }

        /// <summary>Parses the checkpoint size limit (MB); non-numeric → the default, then clamped.</summary>
        private static int ParseMaxFileMb(string? text) =>
            AstrogatorOptions.ClampCheckpointMaxFileMb(
                int.TryParse((text ?? "").Trim(), out var mb) ? mb : new AstrogatorOptions().CheckpointMaxFileMb);

        /// <summary>Parses the prompt-timeout minutes field; non-numeric → 60, then clamped to range.</summary>
        private static int ParsePromptTimeout(string? text) =>
            AstrogatorOptions.ClampPromptTimeoutMinutes(
                int.TryParse((text ?? "").Trim(), out var m) ? m : 60);

        private static TextBlock Header(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 2),
        };

        /// <summary>
        /// One settings block as a card: bold title, thin rounded border in the theme's tool-window
        /// border colour, own padding, and space below — so the groups stand apart instead of running
        /// into each other as one long list.
        /// </summary>
        private static FrameworkElement Section(string title, params UIElement[] content)
        {
            var panel = new StackPanel();
            var header = Header(title);
            header.Margin = new Thickness(0, 0, 0, 2); // the card's padding provides the space above
            panel.Children.Add(header);
            foreach (var item in content)
            {
                if (item != null)
                    panel.Children.Add(item);
            }

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = panel,
            };
            card.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
            return card;
        }

        /// <summary>Dim explanatory line under a control (no own label, wraps).</summary>
        private static TextBlock Hint(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 4, 0, 0),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.WindowTextKey);
            return tb;
        }

        private static FrameworkElement Labeled(string label, FrameworkElement field)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(field);
            return sp;
        }

        // ── manual "Check now" ──────────────────────────────────────────────

        /// <summary>The check takes at most this long; past it the button frees up again.
        /// (A model check re-probes new models against the CLI, ~4 s each.)</summary>
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(90);

        private sealed class CheckNowUi
        {
            public CheckNowUi(Button button, TextBlock status)
            {
                Button = button;
                Status = status;
            }

            public Button Button { get; }
            public TextBlock Status { get; }
            public System.Windows.Threading.DispatcherTimer? Timeout { get; set; }
        }

        /// <summary>A checkbox with a "Check now" button on its right and a result line below it. The
        /// check runs regardless of the checkbox: clicking it is the consent for that one request.</summary>
        private FrameworkElement WithCheckNow(CheckBox cb, string kind)
        {
            var outer = new StackPanel { Margin = cb.Margin };
            cb.Margin = new Thickness(0);

            var row = new DockPanel();
            var button = MakeButton("Check now", minWidth: 0);
            button.Margin = new Thickness(10, 0, 0, 0);
            button.Padding = new Thickness(8, 2, 8, 2);
            button.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(cb);
            outer.Children.Add(row);

            var status = Hint("");
            status.Margin = new Thickness(20, 3, 0, 0); // aligned with the checkbox text
            status.Visibility = Visibility.Collapsed;
            outer.Children.Add(status);

            var ui = new CheckNowUi(button, status);
            _checkUi[kind] = ui;
            button.Click += (_, __) => StartCheck(kind);
            return outer;
        }

        private void StartCheck(string kind)
        {
            if (!_checkUi.TryGetValue(kind, out var ui))
                return;

            ui.Button.IsEnabled = false;
            ShowCheckStatus(ui, kind == "models"
                ? "Checking… (fetching the list and asking your Claude CLI which models it can run)"
                : "Checking…", ok: true);

            if (!_package.RequestManualCheck(kind))
            {
                ui.Button.IsEnabled = true;
                ShowCheckStatus(ui, "Open the Code Astrogator chat window first — it runs the check.", ok: false);
                return;
            }

            ui.Timeout?.Stop();
            ui.Timeout = new System.Windows.Threading.DispatcherTimer { Interval = CheckTimeout };
            ui.Timeout.Tick += (_, __) =>
            {
                ui.Timeout?.Stop();
                if (ui.Button.IsEnabled)
                    return;
                ui.Button.IsEnabled = true;
                ShowCheckStatus(ui, "No answer from the chat window — try again.", ok: false);
            };
            ui.Timeout.Start();
        }

        private void OnManualCheckCompleted(string kind, bool ok, string message)
        {
            // Raised from the bridge (any thread) → back onto this window's dispatcher.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_checkUi.TryGetValue(kind, out var ui))
                    return;
                ui.Timeout?.Stop();
                ui.Button.IsEnabled = true;
                ShowCheckStatus(ui, (ok ? "✓ " : "⚠ ") + message, ok);
            }));
        }

        private static void ShowCheckStatus(CheckNowUi ui, string text, bool ok)
        {
            ui.Status.Text = text;
            ui.Status.Opacity = ok ? 0.75 : 1.0; // a failure reads a little louder than routine output
            ui.Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private FrameworkElement WithBrowse(TextBox tb)
        {
            var dp = new DockPanel();
            var browse = MakeButton("Browse…", minWidth: 0);
            browse.Margin = new Thickness(6, 0, 0, 0);
            browse.Padding = new Thickness(8, 2, 8, 2);
            DockPanel.SetDock(browse, Dock.Right);
            browse.Click += (_, __) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Locate the claude executable",
                    Filter = "Executables (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
                };
                if (ofd.ShowDialog(this) != true)
                    return;
                tb.Text = ofd.FileName;
                ScheduleApply(); // picked, not typed — no Enter / focus change to wait for
            };
            dp.Children.Add(browse);
            dp.Children.Add(tb);
            return dp;
        }

        /// <summary>One editable row in the auto-approve pattern grid.</summary>
        private sealed class PatternItem
        {
            public string Pattern { get; set; } = "";
        }
    }
}
