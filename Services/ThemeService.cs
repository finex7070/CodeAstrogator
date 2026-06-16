using System.Drawing;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Resolves the theme message for the web UI (Teil B §8). For mode "auto" the
    /// active VS theme is mapped onto the UI's CSS variables; for explicit
    /// dark/light the UI falls back to its built-in palettes (vars stays empty).
    /// The purple brand accent is never overridden (brand color).
    /// </summary>
    internal static class ThemeService
    {
        /// <summary>Builds the host→web "theme" message for the given mode (dark|light|auto).</summary>
        public static JObject BuildThemeMessage(string mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var vars = new JObject();
            string resolved;

            if (mode == "auto")
            {
                var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                resolved = IsDark(bg) ? "dark" : "light";

                vars["--bg"] = ToCssHex(bg);
                vars["--bg-chrome"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.EnvironmentBackgroundColorKey));
                vars["--bg-elevated"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.DropDownPopupBackgroundBeginColorKey));
                vars["--border"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey));
                vars["--border-subtle"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.CommandBarToolBarBorderColorKey));
                vars["--text"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey));
                vars["--text-dim"] = ToCssHex(VSColorTheme.GetThemedColor(EnvironmentColors.SystemGrayTextColorKey));
                // --text-faint / --comment / --accent / status colors: UI palette defaults
            }
            else
            {
                resolved = mode == "light" ? "light" : "dark";
            }

            return new JObject
            {
                ["type"] = "theme",
                ["mode"] = mode,
                ["resolved"] = resolved,
                ["vars"] = vars,
            };
        }

        private static bool IsDark(Color c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;

        private static string ToCssHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    }
}
