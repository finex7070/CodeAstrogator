using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CodeAstrogator.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace CodeAstrogator.ToolWindows
{
    /// <summary>
    /// Host-side settings window opened from the gear popover's "Advanced options…"
    /// (web → host <c>options.open</c>). Edits all <see cref="AstrogatorOptions"/>; "Reset to
    /// defaults" restores the defaults; "Save" applies and persists the values via the
    /// package (WritableSettingsStore) and raises OptionsChanged so the chat updates,
    /// while "Cancel" (and the window's X) discards the edits. All controls are tinted
    /// with the active VS theme brushes so text/background follow Dark/Light correctly.
    /// </summary>
    internal sealed class AstrogatorSettingsWindow : DialogWindow
    {
        private readonly CodeAstrogatorPackage _package;
        private readonly AstrogatorOptions _current; // carries over Model·Mode popover state on Save

        private readonly TextBox _exePath;
        private readonly ObservableCollection<PatternItem> _patterns = new ObservableCollection<PatternItem>();
        private readonly DataGrid _autoApprove;
        private readonly Button _removePattern;
        private readonly ComboBox _theme;
        private readonly ComboBox _verbosity;
        private readonly CheckBox _restore;
        private readonly CheckBox _autoAdd;
        private readonly CheckBox _includeLines;
        private readonly CheckBox _activeFileDefault;
        private readonly CheckBox _noticeFetch;
        private readonly CheckBox _updateCheck;
        private readonly TextBox _promptTimeout;
        private readonly CheckBox _persistentCli;
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

            Title = "Code Astrogator — Settings";
            Width = 980; // two columns — wide instead of a single very long list
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
            _theme = MakeCombo("auto", "dark", "light");
            _verbosity = MakeCombo("compact", "normal", "detailed");
            _restore = MakeCheck("Restore the last session when the chat window opens", new Thickness(0, 8, 0, 0));
            _autoAdd = MakeCheck("Reference the active editor file in each prompt", new Thickness(0, 8, 0, 0));
            _includeLines = MakeCheck("Include the selected line range in the file reference", new Thickness(20, 6, 0, 0));
            _activeFileDefault = MakeCheck("Reference it by default in new chats (otherwise a new chat starts with the reference off; toggle it on via the chip)", new Thickness(20, 6, 0, 0));
            _autoAdd.Checked += (_, __) => { _includeLines.IsEnabled = true; _activeFileDefault.IsEnabled = true; };
            _autoAdd.Unchecked += (_, __) => { _includeLines.IsEnabled = false; _activeFileDefault.IsEnabled = false; };
            _noticeFetch = MakeCheck("Periodically check the project's GitHub for announcements and show them as a banner (makes a network request)", new Thickness(0, 8, 0, 0));
            _updateCheck = MakeCheck("Notify me about new versions (checks the project's GitHub for updates and shows a banner)", new Thickness(0, 8, 0, 0));
            _promptTimeout = MakeTextBox();
            _promptTimeout.HorizontalAlignment = HorizontalAlignment.Left;
            _promptTimeout.MinWidth = 80;
            _persistentCli = MakeCheck("Use a persistent CLI session (lower latency; experimental)", new Thickness(0, 8, 0, 0));
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

            // Two columns instead of one very long list — the window is wide rather than tall.
            var left = new StackPanel();
            left.Children.Add(Header("Claude CLI"));
            left.Children.Add(Labeled("Claude executable path (optional override; empty = resolve automatically):", WithBrowse(_exePath)));
            // Model & effort moved to the in-chat Model·Mode popover (sticky/persisted there).
            left.Children.Add(Header("Appearance & transcript"));
            left.Children.Add(Labeled("Theme:", _theme));
            left.Children.Add(Labeled("Transcript verbosity:", _verbosity));
            left.Children.Add(Header("Behavior"));
            left.Children.Add(_restore);
            left.Children.Add(_autoAdd);
            left.Children.Add(_includeLines);
            left.Children.Add(_activeFileDefault);
            left.Children.Add(Header("Announcements & updates"));
            left.Children.Add(_noticeFetch);
            left.Children.Add(_updateCheck);
            left.Children.Add(Header("Permissions"));
            left.Children.Add(Labeled(
                "Auto-approve patterns (* = wildcard) — matching Bash/PowerShell commands and MCP tools "
                + "skip the permission prompt. The \"Always\" button on a prompt adds the command/tool here.",
                _autoApprove));
            left.Children.Add(GridButtons(_autoApprove, _patterns, _removePattern));
            left.Children.Add(Labeled(
                $"Prompt timeout — how long a permission prompt / question waits for your answer "
                + $"before it expires (minutes, {AstrogatorOptions.MinPromptTimeoutMinutes}–{AstrogatorOptions.MaxPromptTimeoutMinutes}):",
                _promptTimeout));
            left.Children.Add(Header("Advanced"));
            left.Children.Add(_persistentCli);

            var right = new StackPanel();
            right.Children.Add(Header("History & storage"));
            right.Children.Add(Labeled(
                "Automatically delete chat history older than (by last activity; \"Never\" keeps it forever):",
                _historyRetention));
            right.Children.Add(Labeled(
                "Automatically delete pasted images older than (files under …\\CodeAstrogator\\pasted):",
                _pastedRetention));
            right.Children.Add(Header("Checkpoints (rewind)"));
            right.Children.Add(_checkpoints);
            if (!gitAvailable)
                right.Children.Add(Hint("Git was not found on PATH — checkpoints are unavailable. "
                    + "Install Git for Windows and restart Visual Studio."));
            right.Children.Add(Labeled(
                $"Skip files larger than (MB, 0 = no limit, max {AstrogatorOptions.MaxCheckpointFileMb}):",
                _checkpointMaxMb));
            right.Children.Add(Labeled("Extension filter:", _checkpointFilterMode));
            right.Children.Add(Labeled("File extensions (empty list = no extension filter):",
                _checkpointExtensions));
            right.Children.Add(GridButtons(_checkpointExtensions, _extensions, _removeExtension));
            right.Children.Add(Hint("Large binaries are what makes a snapshot expensive — a rewind is "
                + "meant for source code. Files excluded here are never restored by a rewind."));
            right.Children.Add(Labeled("Keep checkpoints for:", _checkpointRetention));
            right.Children.Add(Hint("\"Never\" keeps them until you delete them. Checkpoints take disk space, "
                + "so they can expire before the chat history they belong to."));
            right.Children.Add(_deleteCheckpoints);

            var columns = new Grid { Margin = new Thickness(16) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(right);
            var panel = columns;

            var reset = MakeButton("Reset to defaults", minWidth: 130);
            reset.Click += (_, __) => Load(new AstrogatorOptions());

            var cancel = MakeButton("Cancel", minWidth: 90);
            cancel.IsCancel = true; // Esc / window X → discard
            cancel.Click += (_, __) => Close();

            var save = MakeButton("Save", minWidth: 90);
            save.IsDefault = true; // Enter → apply + persist
            save.Click += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); ApplyAndPersist(); Close(); };

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
            cancel.Margin = new Thickness(0, 0, 8, 0);
            rightButtons.Children.Add(cancel);
            rightButtons.Children.Add(save);

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            DockPanel.SetDock(reset, Dock.Left);
            DockPanel.SetDock(rightButtons, Dock.Right);
            buttons.Children.Add(reset);
            buttons.Children.Add(rightButtons);
            Grid.SetRow(buttons, 1);
            Grid.SetColumnSpan(buttons, 3); // the button row spans both columns
            panel.Children.Add(buttons);

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Load(current);
        }

        private void Load(AstrogatorOptions o)
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
            SelectCombo(_theme, o.ThemeModeString, "auto");
            SelectCombo(_verbosity, o.VerbosityString, "normal");
            _restore.IsChecked = o.RestoreLastSession;
            _autoAdd.IsChecked = o.AutoAddActiveFile;
            _includeLines.IsChecked = o.IncludeSelectedLines;
            _includeLines.IsEnabled = o.AutoAddActiveFile;
            _activeFileDefault.IsChecked = o.ActiveFileOnByDefault;
            _activeFileDefault.IsEnabled = o.AutoAddActiveFile;
            _noticeFetch.IsChecked = o.NoticeFetchEnabled;
            _updateCheck.IsChecked = o.UpdateCheckEnabled;
            _promptTimeout.Text = AstrogatorOptions.ClampPromptTimeoutMinutes(o.PromptTimeoutMinutes).ToString();
            _persistentCli.IsChecked = o.UsePersistentCli;
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

        private void ApplyAndPersist()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _autoApprove.CommitEdit(DataGridEditingUnit.Row, true);          // flush in-progress cell edits
            _checkpointExtensions.CommitEdit(DataGridEditingUnit.Row, true);
            var patterns = _patterns
                .Select(p => (p.Pattern ?? "").Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var updated = new AstrogatorOptions
            {
                ClaudeExecutablePath = _exePath.Text?.Trim() ?? "",
                AutoApprovePatterns = patterns,
                ThemeModeString = Selected(_theme, "auto"),
                VerbosityString = Selected(_verbosity, "normal"),
                RestoreLastSession = _restore.IsChecked == true,
                AutoAddActiveFile = _autoAdd.IsChecked == true,
                IncludeSelectedLines = _includeLines.IsChecked == true,
                ActiveFileOnByDefault = _activeFileDefault.IsChecked == true,
                NoticeFetchEnabled = _noticeFetch.IsChecked == true,
                NoticeFetchDecided = true, // setting it here counts as having decided → no consent popup
                UpdateCheckEnabled = _updateCheck.IsChecked == true,
                UpdateCheckDecided = true,
                PromptTimeoutMinutes = ParsePromptTimeout(_promptTimeout.Text),
                UsePersistentCli = _persistentCli.IsChecked == true,
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
            };
            _package.UpdateOptions(updated);
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
                if (ofd.ShowDialog(this) == true)
                    tb.Text = ofd.FileName;
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
