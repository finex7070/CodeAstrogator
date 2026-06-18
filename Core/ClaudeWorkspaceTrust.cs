using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Ensures a workspace is marked "trusted" in the CLI's <c>~/.claude.json</c> so that
    /// <c>claude remote-control</c> starts without the interactive trust dialog.
    ///
    /// Background: headless <c>claude -p</c> (the normal chat turn) bypasses the workspace-trust
    /// check entirely, but <c>remote-control</c> refuses to start in an untrusted directory
    /// (<c>"Error: Workspace not trusted. Please run `claude` … first"</c>). Opening a project for
    /// the first time and immediately starting a remote session therefore failed. We pre-set
    /// <c>projects[dir].hasTrustDialogAccepted = true</c> — the same flag the interactive dialog
    /// writes — for the directory the user explicitly opened in Visual Studio. Verified against
    /// CLI 2.1.178; the project key is the working directory with forward slashes
    /// (e.g. <c>C:/Users/Jan/Repo</c>). Best-effort: any failure is swallowed, leaving the CLI to
    /// surface its own trust error.
    /// </summary>
    public static class ClaudeWorkspaceTrust
    {
        /// <summary>
        /// Marks <paramref name="workingDirectory"/> trusted in the CLI config. Returns true if the
        /// config now records the directory as trusted (already-trusted or freshly written), false
        /// if nothing could be done (no directory, or an I/O/parse failure).
        /// </summary>
        /// <param name="configPath">Override for <c>~/.claude.json</c> (tests only).</param>
        public static bool EnsureTrusted(string? workingDirectory, string? configPath = null)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
                return false;

            if (configPath == null)
                configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

            try
            {
                var key = NormalizeKey(workingDirectory!);

                var root = File.Exists(configPath)
                    ? JObject.Parse(File.ReadAllText(configPath))
                    : new JObject();

                if (!(root["projects"] is JObject projects))
                {
                    projects = new JObject();
                    root["projects"] = projects;
                }

                // Reuse an existing entry whose path matches (case-insensitive, slash-normalized)
                // so we don't leave a stale duplicate in a different key format.
                JObject? entry = null;
                foreach (var prop in projects.Properties())
                {
                    if (string.Equals(NormalizeKey(prop.Name), key, StringComparison.OrdinalIgnoreCase)
                        && prop.Value is JObject obj)
                    {
                        if (obj.Value<bool?>("hasTrustDialogAccepted") == true)
                            return true; // already trusted — leave the file untouched
                        entry = obj;
                        break;
                    }
                }

                if (entry == null)
                {
                    entry = new JObject();
                    projects[key] = entry;
                }

                entry["hasTrustDialogAccepted"] = true;

                WriteAtomic(configPath, root.ToString(Formatting.Indented));
                return true;
            }
            catch
            {
                return false; // best-effort; the CLI will surface its own trust error
            }
        }

        /// <summary>Working dir → CLI project key: forward slashes, no trailing separator.</summary>
        private static string NormalizeKey(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        /// <summary>Write via a temp file + replace so a crash never truncates the config.</summary>
        private static void WriteAtomic(string path, string content)
        {
            var tmp = path + ".castr.tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
    }
}
