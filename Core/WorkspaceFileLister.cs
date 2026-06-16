using System;
using System.Collections.Generic;
using System.IO;

namespace CodeAstrogator.Core
{
    public sealed class WorkspaceEntry
    {
        public string Path { get; set; } = ""; // relative, forward slashes; dirs without trailing slash
        public bool IsDir { get; set; }
    }

    /// <summary>
    /// Enumerates the open solution/folder for the @-mention file picker
    /// (breadth-first, shallow entries first — matches how the VS Code picker reads).
    /// </summary>
    public static class WorkspaceFileLister
    {
        private static readonly HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "out", "TestResults",
        };

        public static IReadOnlyList<WorkspaceEntry> List(string? root, int maxEntries = 2000)
        {
            var result = new List<WorkspaceEntry>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return result;

            var queue = new Queue<(string abs, string rel)>();
            queue.Enqueue((root!, ""));

            while (queue.Count > 0 && result.Count < maxEntries)
            {
                var (abs, rel) = queue.Dequeue();
                string[] dirs, files;
                try
                {
                    dirs = Directory.GetDirectories(abs);
                    files = Directory.GetFiles(abs);
                }
                catch
                {
                    continue; // access denied etc.
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    if (result.Count >= maxEntries)
                        break;
                    var name = Path.GetFileName(file);
                    result.Add(new WorkspaceEntry { Path = Combine(rel, name), IsDir = false });
                }

                foreach (var dir in dirs)
                {
                    if (result.Count >= maxEntries)
                        break;
                    var name = Path.GetFileName(dir);
                    if (ExcludedDirs.Contains(name))
                        continue;
                    result.Add(new WorkspaceEntry { Path = Combine(rel, name), IsDir = true });
                    queue.Enqueue((dir, Combine(rel, name)));
                }
            }

            return result;
        }

        private static string Combine(string prefix, string name) =>
            prefix.Length == 0 ? name : prefix + "/" + name;
    }
}
