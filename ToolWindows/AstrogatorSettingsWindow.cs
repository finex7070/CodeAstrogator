using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CodeAstrogator.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

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

        public AstrogatorSettingsWindow(CodeAstrogatorPackage package, AstrogatorOptions current)
        {
            _package = package;
            _current = current;

            Title = "Code Astrogator — Settings";
            Width = 540;
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
            _autoApprove = MakePatternGrid();
            _removePattern = MakeButton("Remove", minWidth: 90);
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

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Header("Claude CLI"));
            panel.Children.Add(Labeled("Claude executable path (optional override; empty = resolve automatically):", WithBrowse(_exePath)));
            // Model & effort moved to the in-chat Model·Mode popover (sticky/persisted there).
            panel.Children.Add(Header("Appearance & transcript"));
            panel.Children.Add(Labeled("Theme:", _theme));
            panel.Children.Add(Labeled("Transcript verbosity:", _verbosity));
            panel.Children.Add(Header("Behavior"));
            panel.Children.Add(_restore);
            panel.Children.Add(_autoAdd);
            panel.Children.Add(_includeLines);
            panel.Children.Add(_activeFileDefault);
            panel.Children.Add(Header("Announcements & updates"));
            panel.Children.Add(_noticeFetch);
            panel.Children.Add(_updateCheck);
            panel.Children.Add(Header("Permissions"));
            panel.Children.Add(Labeled(
                "Auto-approve patterns (* = wildcard) — matching Bash/PowerShell commands and MCP tools "
                + "skip the permission prompt. The \"Always\" button on a prompt adds the command/tool here.",
                _autoApprove));
            panel.Children.Add(PatternButtons());
            panel.Children.Add(Labeled(
                $"Prompt timeout — how long a permission prompt / question waits for your answer "
                + $"before it expires (minutes, {AstrogatorOptions.MinPromptTimeoutMinutes}–{AstrogatorOptions.MaxPromptTimeoutMinutes}):",
                _promptTimeout));
            panel.Children.Add(Header("Advanced"));
            panel.Children.Add(_persistentCli);

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
        }

        private void ApplyAndPersist()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _autoApprove.CommitEdit(DataGridEditingUnit.Row, true); // flush any in-progress cell edit
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
                // Popover-managed state (Model·Mode + accent color) — carry it over untouched
                DefaultModel = _current.DefaultModel,
                DefaultEffortString = _current.DefaultEffortString,
                UltracodeEnabled = _current.UltracodeEnabled,
                PermissionModeString = _current.PermissionModeString,
                AutoAcceptCommands = _current.AutoAcceptCommands,
                ReviewEditsInEditor = _current.ReviewEditsInEditor,
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

        /// <summary>Editable single-column list of auto-approve patterns (theme-tinted DataGrid).</summary>
        private DataGrid MakePatternGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _patterns,
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
                Header = "Command / tool pattern",
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

            grid.SelectionChanged += (_, __) => _removePattern.IsEnabled = grid.SelectedItems.Count > 0;
            return grid;
        }

        /// <summary>"Add" / "Remove" buttons under the pattern grid (mirrors the screenshot's layout).</summary>
        private FrameworkElement PatternButtons()
        {
            var add = MakeButton("Add", minWidth: 90);
            add.Click += (_, __) => AddPattern();

            _removePattern.Click += (_, __) => RemovePatterns();

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            add.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(add);
            sp.Children.Add(_removePattern);
            return sp;
        }

        private void AddPattern()
        {
            var item = new PatternItem();
            _patterns.Add(item);
            _autoApprove.SelectedItem = item;
            _autoApprove.ScrollIntoView(item);
            if (_autoApprove.Columns.Count > 0)
            {
                _autoApprove.CurrentCell = new DataGridCellInfo(item, _autoApprove.Columns[0]);
                _autoApprove.BeginEdit();
            }
        }

        private void RemovePatterns()
        {
            foreach (var item in _autoApprove.SelectedItems.OfType<PatternItem>().ToList())
                _patterns.Remove(item);
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
