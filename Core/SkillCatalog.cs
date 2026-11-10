using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>Where a skill comes from.</summary>
    public enum SkillSource
    {
        /// <summary><c>~/.claude/skills/&lt;name&gt;/SKILL.md</c></summary>
        Personal,
        /// <summary><c>.claude/skills/&lt;name&gt;/SKILL.md</c> in the workspace or a parent up to the repo root.</summary>
        Project,
        /// <summary><c>&lt;plugin&gt;/skills/&lt;name&gt;/SKILL.md</c> — goes on/off with its plugin.</summary>
        Plugin,
        /// <summary>Synced from claude.ai into <c>~/.claude/skills/synced/</c>.</summary>
        Synced,
        /// <summary>Shipped with the CLI — known only by name (from <c>system/init</c>).</summary>
        Bundled,
    }

    public sealed class SkillInfo
    {
        /// <summary>The name the skill is invoked by (<c>plugin:skill</c> for plugin skills).</summary>
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public SkillSource Source { get; set; }
        /// <summary>Path of <c>SKILL.md</c>; null for bundled skills.</summary>
        public string? SkillFile { get; set; }
        public string? PluginId { get; set; }
        /// <summary>The <c>skillOverrides</c> value that applies (null = none set = on).</summary>
        public string? Override { get; set; }
        /// <summary>The settings scope that value comes from.</summary>
        public string? OverrideScope { get; set; }
        /// <summary>The state that results: <c>on</c> | <c>name-only</c> | <c>user-invocable-only</c> | <c>off</c>.</summary>
        public string EffectiveState => Override ?? SkillCatalog.On;
        /// <summary>The session listed it in <c>system/init</c>; null when that list was not available.</summary>
        public bool? Loaded { get; set; }
        /// <summary><c>skillOverrides</c> applies (not to plugin skills — they follow their plugin).</summary>
        public bool CanOverride => Source != SkillSource.Plugin;
        /// <summary>The extension may open/delete it (personal or project skill on disk).</summary>
        public bool CanEdit => Source == SkillSource.Personal || Source == SkillSource.Project;
    }

    /// <summary>The fields of a <c>SKILL.md</c> front matter the panel shows.</summary>
    public sealed class SkillFrontMatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool? UserInvocable { get; set; }
        public bool? DisableModelInvocation { get; set; }
    }

    /// <summary>What to scan.</summary>
    public sealed class SkillScanContext
    {
        /// <summary><c>~/.claude</c> (tests point it elsewhere).</summary>
        public string UserClaudeDir { get; set; } = ClaudeSettingsFiles.UserClaudeDir();
        public string? Workspace { get; set; }
        /// <summary>Installed plugins; the enabled ones contribute their skills.</summary>
        public IReadOnlyList<InstalledPlugin> Plugins { get; set; } = Array.Empty<InstalledPlugin>();
        /// <summary>Skill names the session reported in <c>system/init</c>; unknown ones are listed as bundled.</summary>
        public IReadOnlyCollection<string>? LoadedNames { get; set; }
    }

    /// <summary>
    /// Skills on disk and their visibility (<c>skillOverrides</c>; values verified in the Claude Code docs,
    /// 2026-11-06 — see docs/plugins-skills-plan.md). There is no CLI command for skills, so this class scans the
    /// documented folders and edits that one settings key; everything else about plugins goes through the CLI.
    /// </summary>
    public static class SkillCatalog
    {
        public const string On = "on";
        public const string NameOnly = "name-only";
        public const string UserInvocableOnly = "user-invocable-only";
        public const string Off = "off";
        public static readonly string[] States = { On, NameOnly, UserInvocableOnly, Off };

        private static readonly Regex ValidName = new Regex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

        /// <summary>Lowercase letters, digits and hyphens, at most 64 characters (the documented skill-name rule).</summary>
        public static bool IsValidName(string? name) => name != null && ValidName.IsMatch(name);

        public static IReadOnlyList<SkillInfo> Scan(SkillScanContext ctx)
        {
            var skills = new List<SkillInfo>();
            var userSkills = Path.Combine(ctx.UserClaudeDir, "skills");
            skills.AddRange(ScanFolder(userSkills, SkillSource.Personal, null, skip: "synced"));
            skills.AddRange(ScanFolder(Path.Combine(userSkills, "synced"), SkillSource.Synced, null));
            foreach (var dir in ProjectSkillDirs(ctx.Workspace))
                skills.AddRange(ScanFolder(dir, SkillSource.Project, null));
            foreach (var plugin in ctx.Plugins.Where(p => p.Enabled && !string.IsNullOrEmpty(p.InstallPath)))
            {
                foreach (var s in ScanFolder(Path.Combine(plugin.InstallPath!, "skills"), SkillSource.Plugin, plugin.Id))
                {
                    s.Name = plugin.Name + ":" + s.Name; // invoked as /plugin:skill
                    skills.Add(s);
                }
            }

            // Same name: personal beats project (enterprise beats both, not scanned here).
            skills = skills
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(s => s.Source == SkillSource.Personal ? 0 : s.Source == SkillSource.Project ? 1 : 2).First())
                .ToList();

            if (ctx.LoadedNames != null)
            {
                var loaded = new HashSet<string>(ctx.LoadedNames, StringComparer.OrdinalIgnoreCase);
                foreach (var s in skills)
                    s.Loaded = loaded.Contains(s.Name);
                foreach (var name in loaded.Where(n => !skills.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))))
                    skills.Add(new SkillInfo { Name = name, Source = SkillSource.Bundled, Loaded = true });
            }

            var overrides = ReadOverrides(ctx.Workspace, ctx.UserClaudeDir);
            foreach (var s in skills.Where(s => s.CanOverride))
            {
                if (overrides.TryGetValue(s.Name, out var o))
                {
                    s.Override = o.Value;
                    s.OverrideScope = o.Scope;
                }
            }
            return skills.OrderBy(s => s.Source).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary><c>.claude/skills</c> in the workspace and every parent up to the repository root (a folder
        /// holding <c>.git</c>); only the workspace itself when it is not inside a repository.</summary>
        public static IReadOnlyList<string> ProjectSkillDirs(string? workspace)
        {
            var dirs = new List<string>();
            if (string.IsNullOrEmpty(workspace))
                return dirs;
            var chain = new List<string>();
            string? repoRoot = null;
            for (var d = new DirectoryInfo(workspace); d != null; d = d.Parent)
            {
                chain.Add(d.FullName);
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                {
                    repoRoot = d.FullName;
                    break;
                }
            }
            foreach (var d in repoRoot == null ? chain.Take(1) : chain)
                dirs.Add(Path.Combine(d, ".claude", "skills"));
            return dirs;
        }

        private static IEnumerable<SkillInfo> ScanFolder(string dir, SkillSource source, string? pluginId, string? skip = null)
        {
            if (!Directory.Exists(dir))
                yield break;
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { yield break; }
            foreach (var sub in subdirs.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(sub);
                if (skip != null && string.Equals(folder, skip, StringComparison.OrdinalIgnoreCase))
                    continue;
                var file = Path.Combine(sub, "SKILL.md");
                if (!File.Exists(file))
                    continue;
                SkillFrontMatter fm;
                try { fm = ParseFrontMatter(File.ReadAllText(file)); }
                catch { fm = new SkillFrontMatter(); }
                yield return new SkillInfo
                {
                    Name = string.IsNullOrWhiteSpace(fm.Name) ? folder : fm.Name!.Trim(),
                    Description = fm.Description,
                    Source = source,
                    SkillFile = file,
                    PluginId = pluginId,
                };
            }
        }

        // ── front matter ────────────────────────────────────────────────────

        /// <summary>The YAML front matter between the leading <c>---</c> lines (the opening one must be the first
        /// line). A small subset: <c>key: value</c>, quoted values, <c>&gt;</c>/<c>|</c> block scalars and indented
        /// continuation lines — enough for name/description; unknown keys are ignored like the CLI does.</summary>
        public static SkillFrontMatter ParseFrontMatter(string text)
        {
            var fm = new SkillFrontMatter();
            var lines = (text ?? "").Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
                return fm;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? key = null;
            var block = (string?)null; // ">" folded, "|" literal, "" plain continuation
            var sb = new StringBuilder();
            void Flush()
            {
                if (key != null)
                    values[key] = sb.ToString().Trim();
                key = null;
                sb.Clear();
            }
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim() == "---")
                    break;
                var indented = line.Length > 0 && (line[0] == ' ' || line[0] == '\t');
                if (indented && key != null)
                {
                    if (sb.Length > 0)
                        sb.Append(block == "|" ? "\n" : " ");
                    sb.Append(line.Trim());
                    continue;
                }
                var colon = line.IndexOf(':');
                if (indented || colon <= 0)
                    continue;
                Flush();
                key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (value.StartsWith(">", StringComparison.Ordinal) || value.StartsWith("|", StringComparison.Ordinal))
                {
                    block = value.Substring(0, 1);
                    continue;
                }
                block = "";
                sb.Append(Unquote(value));
            }
            Flush();
            fm.Name = Get(values, "name");
            fm.Description = Get(values, "description");
            fm.UserInvocable = Bool(Get(values, "user-invocable"));
            fm.DisableModelInvocation = Bool(Get(values, "disable-model-invocation"));
            return fm;
        }

        private static string? Get(Dictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v.Length < 2 || v[0] != '"' || v[v.Length - 1] != '"')
                return v;
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length - 1; i++)
            {
                var c = v[i];
                if (c == '\\' && i + 1 < v.Length - 1)
                {
                    var next = v[++i];
                    sb.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>The CLI accepts true/false/yes/no/on/off/1/0.</summary>
        private static bool? Bool(string? v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "1": return true;
                case "false": case "no": case "off": case "0": return false;
                default: return null;
            }
        }

        // ── visibility (skillOverrides / disableBundledSkills) ──────────────

        /// <summary>Skill name → the value that applies and its scope (local beats project beats user).</summary>
        public static Dictionary<string, (string Value, string Scope)> ReadOverrides(string? workspace, string? userClaudeDir = null)
        {
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in ClaudeSettingsFiles.ByPrecedence)
            {
                if (!(ClaudeSettingsFiles.Read(ClaudeSettingsFiles.PathOf(scope, workspace, userClaudeDir))?["skillOverrides"] is JObject map))
                    continue;
                foreach (var p in map.Properties())
                {
                    if (!result.ContainsKey(p.Name) && p.Value.Type == JTokenType.String && States.Contains(p.Value.ToString()))
                        result[p.Name] = (p.Value.ToString(), scope);
                }
            }
            return result;
        }

        /// <summary>Sets one skill's value in the given scope's file; <paramref name="value"/> null removes the entry
        /// (back to whatever a lower scope says, else on). False when the scope has no file (no workspace) or the
        /// existing file is not valid JSON.</summary>
        public static bool WriteOverride(string scope, string? workspace, string skillName, string? value, string? userClaudeDir = null)
        {
            if (value != null && !States.Contains(value))
                throw new ArgumentException("Unknown skill state: " + value, nameof(value));
            var path = ClaudeSettingsFiles.PathOf(scope, workspace, userClaudeDir);
            if (path == null)
                return false;
            return ClaudeSettingsFiles.Update(path, root =>
            {
                var map = root["skillOverrides"] as JObject;
                if (value == null)
                {
                    map?.Remove(skillName);
                    if (map != null && !map.HasValues)
                        root.Remove("skillOverrides");
                    return;
                }
                if (map == null)
                {
                    map = new JObject();
                    root["skillOverrides"] = map;
                }
                map[skillName] = value;
            });
        }

        /// <summary>The panel's "set this skill to …" in one scope. <c>on</c> removes that scope's entry (absent =
        /// on) and writes <c>on</c> explicitly only when another scope would otherwise still hide the skill.</summary>
        public static bool SetState(string scope, string? workspace, string skillName, string value, string? userClaudeDir = null)
        {
            if (value != On)
                return WriteOverride(scope, workspace, skillName, value, userClaudeDir);
            if (!WriteOverride(scope, workspace, skillName, null, userClaudeDir))
                return false;
            return !ReadOverrides(workspace, userClaudeDir).TryGetValue(skillName, out var still) || still.Value == On
                || WriteOverride(scope, workspace, skillName, On, userClaudeDir);
        }

        /// <summary>Whether bundled skills are switched off, and by which scope (local beats project beats user).</summary>
        public static (bool Disabled, string? Scope) ReadBundledDisabled(string? workspace, string? userClaudeDir = null)
        {
            foreach (var scope in ClaudeSettingsFiles.ByPrecedence)
            {
                var token = ClaudeSettingsFiles.Read(ClaudeSettingsFiles.PathOf(scope, workspace, userClaudeDir))?["disableBundledSkills"];
                if (token != null && token.Type == JTokenType.Boolean)
                    return (token.Value<bool>(), scope);
            }
            return (false, null);
        }

        public static bool WriteBundledDisabled(string scope, string? workspace, bool? disabled, string? userClaudeDir = null)
        {
            var path = ClaudeSettingsFiles.PathOf(scope, workspace, userClaudeDir);
            if (path == null)
                return false;
            return ClaudeSettingsFiles.Update(path, root =>
            {
                if (disabled == null)
                    root.Remove("disableBundledSkills");
                else
                    root["disableBundledSkills"] = disabled.Value;
            });
        }

        // ── create / delete ─────────────────────────────────────────────────

        /// <summary>Creates <c>&lt;skillsDir&gt;/&lt;name&gt;/SKILL.md</c> from a template and returns its path.
        /// Throws when the name is invalid or the skill already exists.</summary>
        public static string CreateSkill(string skillsDir, string name, string description)
        {
            if (!IsValidName(name))
                throw new ArgumentException("A skill name uses lowercase letters, digits and hyphens (at most 64).", nameof(name));
            var folder = Path.Combine(skillsDir, name);
            var file = Path.Combine(folder, "SKILL.md");
            if (Directory.Exists(folder))
                throw new IOException("A skill named \"" + name + "\" already exists.");
            Directory.CreateDirectory(folder);
            var desc = (description ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (desc.Length == 0)
                desc = "Describe when Claude should use this skill.";
            var content = "---\n"
                + "name: " + name + "\n"
                + "description: " + YamlValue(desc) + "\n"
                + "---\n\n"
                + "# " + name + "\n\n"
                + "Write the instructions Claude should follow when this skill is used.\n";
            File.WriteAllText(file, content.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            return file;
        }

        /// <summary>Deletes a personal or project skill's folder. Refuses anything else, and anything that does not
        /// look like <c>…/skills/&lt;name&gt;/SKILL.md</c>.</summary>
        public static void DeleteSkill(SkillInfo skill)
        {
            if (!skill.CanEdit || string.IsNullOrEmpty(skill.SkillFile))
                throw new InvalidOperationException("Only your own and project skills can be deleted here.");
            var folder = Path.GetDirectoryName(skill.SkillFile!);
            var parent = folder == null ? null : Path.GetFileName(Path.GetDirectoryName(folder));
            if (folder == null || !string.Equals(Path.GetFileName(skill.SkillFile), "SKILL.md", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parent, "skills", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected skill location: " + skill.SkillFile);
            Directory.Delete(folder, recursive: true);
        }

        /// <summary>Quotes a YAML scalar when it would otherwise be read differently.</summary>
        private static string YamlValue(string v) =>
            v.IndexOfAny(new[] { ':', '#', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0
                ? "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : v;
    }
}
