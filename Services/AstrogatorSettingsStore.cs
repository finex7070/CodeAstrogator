using System;
using System.Collections.Generic;
using System.Linq;
using CodeAstrogator.Options;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Newtonsoft.Json;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// Persists <see cref="AstrogatorOptions"/> via the classic VS <see cref="WritableSettingsStore"/>
    /// (always available in-process, no preview API). Replaces the Unified Settings in-proc
    /// Extensibility API, whose <c>VisualStudioExtensibility</c> service is not proffered in
    /// VS 2026 (see docs/NOTES.md). The settings UI is the host-side AstrogatorSettingsWindow.
    /// </summary>
    internal sealed class AstrogatorSettingsStore
    {
        private const string Collection = "CodeAstrogator";
        private readonly WritableSettingsStore _store;

        private AstrogatorSettingsStore(WritableSettingsStore store) => _store = store;

        /// <summary>Creates the store. Must be called on the UI thread.</summary>
        public static AstrogatorSettingsStore Create(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = new ShellSettingsManager(serviceProvider);
            var store = manager.GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(Collection))
                store.CreateCollection(Collection);
            return new AstrogatorSettingsStore(store);
        }

        /// <summary>Reads all values into a fresh snapshot, falling back to the defaults.</summary>
        public AstrogatorOptions Read()
        {
            var d = new AstrogatorOptions(); // property initializers define the defaults
            return new AstrogatorOptions
            {
                ClaudeExecutablePath = GetString(nameof(AstrogatorOptions.ClaudeExecutablePath), d.ClaudeExecutablePath),
                DefaultModel = GetString(nameof(AstrogatorOptions.DefaultModel), d.DefaultModel),
                DefaultEffortString = GetString(nameof(AstrogatorOptions.DefaultEffortString), d.DefaultEffortString),
                UltracodeEnabled = GetBool(nameof(AstrogatorOptions.UltracodeEnabled), d.UltracodeEnabled),
                PermissionModeString = GetString(nameof(AstrogatorOptions.PermissionModeString), d.PermissionModeString),
                AutoAcceptCommands = GetBool(nameof(AstrogatorOptions.AutoAcceptCommands), d.AutoAcceptCommands),
                AutoApprovePatterns = GetPatterns(nameof(AstrogatorOptions.AutoApprovePatterns), d.AutoApprovePatterns),
                AccentColor = GetString(nameof(AstrogatorOptions.AccentColor), d.AccentColor),
                NoticeFetchEnabled = GetBool(nameof(AstrogatorOptions.NoticeFetchEnabled), d.NoticeFetchEnabled),
                NoticeFetchDecided = GetBool(nameof(AstrogatorOptions.NoticeFetchDecided), d.NoticeFetchDecided),
                UpdateCheckEnabled = GetBool(nameof(AstrogatorOptions.UpdateCheckEnabled), d.UpdateCheckEnabled),
                UpdateCheckDecided = GetBool(nameof(AstrogatorOptions.UpdateCheckDecided), d.UpdateCheckDecided),
                PromptTimeoutMinutes = AstrogatorOptions.ClampPromptTimeoutMinutes(
                    GetInt(nameof(AstrogatorOptions.PromptTimeoutMinutes), d.PromptTimeoutMinutes)),
                RestoreLastSession = GetBool(nameof(AstrogatorOptions.RestoreLastSession), d.RestoreLastSession),
                AutoAddActiveFile = GetBool(nameof(AstrogatorOptions.AutoAddActiveFile), d.AutoAddActiveFile),
                IncludeSelectedLines = GetBool(nameof(AstrogatorOptions.IncludeSelectedLines), d.IncludeSelectedLines),
                ThemeModeString = GetString(nameof(AstrogatorOptions.ThemeModeString), d.ThemeModeString),
                VerbosityString = GetString(nameof(AstrogatorOptions.VerbosityString), d.VerbosityString),
                UsePersistentCli = GetBool(nameof(AstrogatorOptions.UsePersistentCli), d.UsePersistentCli),
            };
        }

        /// <summary>Writes the whole snapshot. Must be called on the UI thread.</summary>
        public void Write(AstrogatorOptions o)
        {
            if (!_store.CollectionExists(Collection))
                _store.CreateCollection(Collection);
            _store.SetString(Collection, nameof(AstrogatorOptions.ClaudeExecutablePath), o.ClaudeExecutablePath ?? "");
            _store.SetString(Collection, nameof(AstrogatorOptions.DefaultModel), o.DefaultModel ?? "");
            _store.SetString(Collection, nameof(AstrogatorOptions.DefaultEffortString), o.DefaultEffortString ?? "high");
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.UltracodeEnabled), o.UltracodeEnabled);
            _store.SetString(Collection, nameof(AstrogatorOptions.PermissionModeString), o.PermissionModeString ?? "ask");
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.AutoAcceptCommands), o.AutoAcceptCommands);
            _store.SetString(Collection, nameof(AstrogatorOptions.AutoApprovePatterns),
                JsonConvert.SerializeObject(Normalize(o.AutoApprovePatterns)));
            _store.SetString(Collection, nameof(AstrogatorOptions.AccentColor), o.AccentColor ?? "");
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.NoticeFetchEnabled), o.NoticeFetchEnabled);
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.NoticeFetchDecided), o.NoticeFetchDecided);
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.UpdateCheckEnabled), o.UpdateCheckEnabled);
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.UpdateCheckDecided), o.UpdateCheckDecided);
            _store.SetInt32(Collection, nameof(AstrogatorOptions.PromptTimeoutMinutes),
                AstrogatorOptions.ClampPromptTimeoutMinutes(o.PromptTimeoutMinutes));
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.RestoreLastSession), o.RestoreLastSession);
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.AutoAddActiveFile), o.AutoAddActiveFile);
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.IncludeSelectedLines), o.IncludeSelectedLines);
            _store.SetString(Collection, nameof(AstrogatorOptions.ThemeModeString), o.ThemeModeString ?? "auto");
            _store.SetString(Collection, nameof(AstrogatorOptions.VerbosityString), o.VerbosityString ?? "normal");
            _store.SetBoolean(Collection, nameof(AstrogatorOptions.UsePersistentCli), o.UsePersistentCli);
        }

        private string GetString(string name, string fallback) =>
            _store.PropertyExists(Collection, name) ? _store.GetString(Collection, name, fallback) : fallback;

        private bool GetBool(string name, bool fallback) =>
            _store.PropertyExists(Collection, name) ? _store.GetBoolean(Collection, name, fallback) : fallback;

        private int GetInt(string name, int fallback) =>
            _store.PropertyExists(Collection, name) ? _store.GetInt32(Collection, name, fallback) : fallback;

        private List<string> GetPatterns(string name, List<string> fallback) =>
            _store.PropertyExists(Collection, name)
                ? ParsePatterns(_store.GetString(Collection, name, ""))
                : fallback;

        /// <summary>Parses the stored pattern value. New format = JSON array; the legacy
        /// newline-separated string is still accepted so existing settings keep working.</summary>
        internal static List<string> ParsePatterns(string? raw)
        {
            var s = raw?.Trim() ?? "";
            if (s.Length == 0)
                return new List<string>();
            if (s[0] == '[') // JSON array (current format)
            {
                try
                {
                    var list = JsonConvert.DeserializeObject<List<string>>(s);
                    if (list != null)
                        return Normalize(list);
                }
                catch { /* malformed JSON → fall back to legacy parse below */ }
            }
            return Normalize(s.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Trims, drops blanks and removes case-insensitive duplicates (order preserved).</summary>
        internal static List<string> Normalize(IEnumerable<string> items) =>
            (items ?? Enumerable.Empty<string>())
                .Select(p => (p ?? "").Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
