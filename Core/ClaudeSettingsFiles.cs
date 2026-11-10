using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// The CLI's settings files per scope — user <c>~/.claude/settings.json</c>, project
    /// <c>&lt;workspace&gt;/.claude/settings.json</c>, local <c>&lt;workspace&gt;/.claude/settings.local.json</c> — and a
    /// careful read-modify-write for the few keys the extension sets itself (<c>skillOverrides</c>,
    /// <c>disableBundledSkills</c>), where the CLI has no command. Plugin and MCP changes never come through
    /// here: they go through <c>claude plugin …</c> / <c>claude mcp …</c>.
    /// </summary>
    public static class ClaudeSettingsFiles
    {
        public const string User = "user";
        public const string Project = "project";
        public const string Local = "local";

        /// <summary>Scopes in precedence order: a value in an earlier one wins.</summary>
        public static readonly string[] ByPrecedence = { Local, Project, User };

        public static string UserClaudeDir(string? userProfile = null) =>
            Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        /// <summary>The settings file of <paramref name="scope"/>; null for project/local without a workspace.</summary>
        public static string? PathOf(string scope, string? workspace, string? userClaudeDir = null)
        {
            switch (scope)
            {
                case User:
                    return Path.Combine(userClaudeDir ?? UserClaudeDir(), "settings.json");
                case Project:
                    return string.IsNullOrEmpty(workspace) ? null : Path.Combine(workspace, ".claude", "settings.json");
                case Local:
                    return string.IsNullOrEmpty(workspace) ? null : Path.Combine(workspace, ".claude", "settings.local.json");
                default:
                    return null;
            }
        }

        /// <summary>The file's JSON object; null when it is missing or not readable as an object.</summary>
        public static JObject? Read(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;
                var text = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Reads the file right before writing (so nothing written meanwhile is lost), lets
        /// <paramref name="change"/> edit it and writes it back atomically. Other keys keep their values and
        /// order. Refuses (returns false) when an existing file is not valid JSON — never overwrite what we can't read.</summary>
        public static bool Update(string path, Action<JObject> change)
        {
            JObject root;
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                root = new JObject();
            }
            change(root);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            WriteAtomic(path, root.ToString(Formatting.Indented) + Environment.NewLine);
            return true;
        }

        private static void WriteAtomic(string path, string content)
        {
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("n").Substring(0, 8);
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
    }
}
