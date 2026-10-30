using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeAstrogator.Services
{
    /// <summary>
    /// The user's own chat/code fonts: copies under <c>%LocalAppData%\CodeAstrogator\fonts</c>, so moving or
    /// deleting the original file doesn't matter. Added/removed in the settings window
    /// (<c>AstrogatorOptions.CustomFonts</c> holds the file names); the bridge hands the active ones to the
    /// WebUI as data URIs.
    /// </summary>
    internal static class CustomFontStore
    {
        public const long MaxFontFileBytes = 10L * 1024 * 1024;

        public const string FileFilter = "Font files (*.ttf;*.otf;*.woff;*.woff2)|*.ttf;*.otf;*.woff;*.woff2";

        public static string FontsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAstrogator", "fonts");

        /// <summary>Copies a font into the store and returns its file name there (an existing file of the same
        /// name is replaced). Throws on a missing, oversized or unsupported file.</summary>
        public static string Import(string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                throw new FileNotFoundException("The font file was not found.", sourcePath);
            if (info.Length > MaxFontFileBytes)
                throw new InvalidOperationException("The font file is larger than 10 MB.");
            var ext = info.Extension.ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf" && ext != ".woff" && ext != ".woff2")
                throw new InvalidOperationException("Only .ttf, .otf, .woff and .woff2 fonts can be used.");

            Directory.CreateDirectory(FontsDir);
            var target = Path.Combine(FontsDir, info.Name);
            if (!string.Equals(Path.GetFullPath(sourcePath), target, StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, target, overwrite: true);
            return info.Name;
        }

        public static bool Exists(string fileName) =>
            !string.IsNullOrEmpty(fileName) && File.Exists(Path.Combine(FontsDir, fileName));

        /// <summary>What the dropdowns show: the file name without extension.</summary>
        public static string DisplayName(string fileName) => Path.GetFileNameWithoutExtension(fileName ?? "");

        /// <summary>The font as a <c>data:font/&lt;ext&gt;;base64,…</c> URI for a WebUI <c>FontFace</c>; null if unreadable.</summary>
        public static string? ReadDataUri(string fileName)
        {
            try
            {
                var path = Path.Combine(FontsDir, fileName);
                var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                return "data:font/" + (ext.Length > 0 ? ext : "ttf") + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Deletes stored fonts that are no longer in the list (removed in the settings window, or added
        /// and then cancelled). Best-effort.</summary>
        public static void DeleteUnused(IEnumerable<string> keep)
        {
            try
            {
                if (!Directory.Exists(FontsDir))
                    return;
                var keepSet = new HashSet<string>(keep ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(FontsDir))
                {
                    if (keepSet.Contains(Path.GetFileName(file)))
                        continue;
                    try { File.Delete(file); } catch { /* in use — next time */ }
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
