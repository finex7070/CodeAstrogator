using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeAstrogator.Core
{
    /// <summary>Where the open button of a card/attachment chip should send a path.</summary>
    public enum FileOpenTarget
    {
        /// <summary>Open as a document inside Visual Studio (code, project, config, plain text …).</summary>
        VisualStudio,
        /// <summary>Hand to the shell so Windows uses the file type's default program (images, PDFs, media …).</summary>
        DefaultProgram,
        /// <summary>Select the item in Windows Explorer (folders, and as a last-resort fallback).</summary>
        Explorer,
    }

    /// <summary>
    /// Decides how a path from the UI's open button is opened. Only file types Visual Studio
    /// actually owns (source, project/build, config, plain text) are opened as VS documents;
    /// everything else Windows has a default program for — images, PDFs, Office documents,
    /// archives, media — is handed to that program instead of being forced into a VS editor,
    /// which used to show a PNG in VS's image editor rather than the user's image viewer.
    /// </summary>
    public static class FileOpenRouter
    {
        /// <summary>
        /// Extensions (without dot) treated as "belongs to Visual Studio". Deliberately broad on
        /// text-ish/dev files: a false "VS" only means the file opens in the IDE, whereas a false
        /// "default program" could hand a source file to some unrelated registered app.
        /// </summary>
        private static readonly HashSet<string> VsExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // .NET / native source
            "cs", "csx", "vb", "fs", "fsx", "fsi", "c", "cc", "cpp", "cxx", "h", "hh", "hpp", "hxx",
            "inl", "ixx", "idl", "asm", "s", "def", "rc", "rc2",
            // markup / web / UI
            "xaml", "axaml", "razor", "cshtml", "vbhtml", "aspx", "ascx", "asax", "asmx", "master",
            "html", "htm", "xhtml", "css", "scss", "sass", "less", "vue", "svelte",
            // scripts / other languages
            "js", "mjs", "cjs", "jsx", "ts", "tsx", "mts", "cts", "py", "pyi", "rb", "go", "rs",
            "java", "kt", "kts", "swift", "php", "pl", "lua", "dart", "scala", "groovy", "gradle",
            "r", "m", "mm", "sql", "ipynb", "tt", "ttinclude", "cmake", "sh", "bash", "zsh",
            "ps1", "psm1", "psd1", "bat", "cmd",
            // project / build / solution
            "sln", "slnx", "slnf", "csproj", "vbproj", "fsproj", "vcxproj", "vcxitems", "shproj",
            "esproj", "sqlproj", "dcproj", "njsproj", "pyproj", "proj", "props", "targets", "filters",
            "user", "pubxml", "nuspec", "vsixmanifest", "runsettings", "testsettings", "vsct",
            // config / data / docs read as text
            "json", "jsonc", "json5", "jsonl", "ndjson", "xml", "xsd", "xsl", "xslt", "config",
            "resx", "resw", "settings", "manifest", "yml", "yaml", "toml", "ini", "cfg", "conf",
            "properties", "env", "editorconfig", "gitignore", "gitattributes", "gitmodules",
            "dockerignore", "npmrc", "nvmrc", "babelrc", "eslintrc", "prettierrc",
            "md", "markdown", "mdx", "txt", "text", "log", "csv", "tsv", "diff", "patch", "snippet",
        };

        /// <summary>
        /// Files whose <em>name</em> has no extension but which are plain text project files
        /// (Makefile, Dockerfile, LICENSE …). Extensionless files fall back to VS anyway; this
        /// list only exists to keep the intent explicit and to skip the shell lookup for them.
        /// </summary>
        private static readonly HashSet<string> VsFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "makefile", "dockerfile", "cmakelists.txt", "license", "licence", "readme", "notice",
            "authors", "changelog", "copying", "codeowners", "procfile", "rakefile", "gemfile",
            "pipfile", "brewfile", "vagrantfile", ".gitignore", ".gitattributes", ".editorconfig",
            ".env", ".npmrc", ".dockerignore",
        };

        /// <summary>Extension of <paramref name="path"/> without the dot ("" when there is none).</summary>
        public static string ExtensionOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            var ext = Path.GetExtension(path!.Trim());
            return string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();
        }

        /// <summary>True when the path's type is one Visual Studio owns (see <see cref="VsExtensions"/>).</summary>
        public static bool IsVisualStudioFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var name = SafeFileName(path!);
            if (VsFileNames.Contains(name))
                return true;
            var ext = ExtensionOf(path);
            return ext.Length == 0 || VsExtensions.Contains(ext);
        }

        /// <summary>
        /// Types that must never be handed to the shell, because "open with the default program"
        /// would <b>run</b> them — an executable, installer or script that a chat turn produced or
        /// downloaded is exactly what a click in the transcript must not launch. They are revealed in
        /// Explorer instead. Script types VS edits as text (<c>bat</c>, <c>cmd</c>, <c>ps1</c>,
        /// <c>js</c>, …) are in <see cref="VsExtensions"/> and open in the IDE, which is safe.
        /// </summary>
        private static readonly HashSet<string> RunnableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "com", "scr", "pif", "cpl", "msc", "dll", "sys", "ocx", "drv",
            "msi", "msix", "msixbundle", "appx", "appxbundle", "msp", "mst",
            "vbs", "vbe", "jse", "wsf", "wsh", "hta", "jar", "gadget", "lnk", "url", "reg", "inf",
        };

        /// <summary>True when handing the path to the shell would execute it (see <see cref="RunnableExtensions"/>).</summary>
        public static bool IsRunnableFile(string? path) => RunnableExtensions.Contains(ExtensionOf(path));

        /// <summary>
        /// Routes a path: directories → Explorer, VS-owned types → the IDE, executables/installers →
        /// Explorer (never launched), and anything else that Windows has a real default program for →
        /// that program. Types with no association (an unknown extension, or one whose handler is just
        /// the "Open with…" picker) fall back to the IDE, which at least shows the bytes instead of
        /// popping a chooser dialog.
        /// <paramref name="hasDefaultProgram"/> is injectable for tests; null uses the shell lookup.
        /// </summary>
        public static FileOpenTarget Decide(string? path, Func<string, bool>? hasDefaultProgram = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return FileOpenTarget.Explorer;
            if (DirectoryExistsSafe(path!))
                return FileOpenTarget.Explorer;
            if (IsVisualStudioFile(path))
                return FileOpenTarget.VisualStudio;
            if (IsRunnableFile(path))
                return FileOpenTarget.Explorer;

            var ext = ExtensionOf(path);
            var probe = hasDefaultProgram ?? HasDefaultProgram;
            return probe(ext) ? FileOpenTarget.DefaultProgram : FileOpenTarget.VisualStudio;
        }

        /// <summary>
        /// Asks the shell whether <paramref name="extension"/> (with or without dot) has a default
        /// program. Three strings are consulted, because a **packaged (UWP/MSIX) default app has no
        /// executable path at all**: `ASSOCSTR_EXECUTABLE`/`ASSOCSTR_COMMAND` come back empty for
        /// `.png`/`.jpg`/`.mp4` when the Photos / Media Player app owns them (measured on Win10 22H2:
        /// `AssocQueryString` returns `0x80070483 ERROR_NO_ASSOCIATION`), which made images fall through
        /// to the IDE — the bug this method exists to avoid. So: a real command/executable that is not
        /// Windows' own picker counts as associated; if both are empty we fall back to
        /// `ASSOCSTR_FRIENDLYAPPNAME`, which a packaged handler does report ("Photos", "Media Player").
        /// A handler resolving to `OpenWith.exe`/`rundll32.exe` counts as none — better the IDE than
        /// Windows' "How do you want to open this file?" chooser.
        /// </summary>
        public static bool HasDefaultProgram(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return false;
            var assoc = extension!.StartsWith(".", StringComparison.Ordinal) ? extension! : "." + extension;
            try
            {
                var exe = QueryAssoc(assoc, ASSOCSTR_EXECUTABLE);
                var cmd = QueryAssoc(assoc, ASSOCSTR_COMMAND);
                if (exe.Length > 0 || cmd.Length > 0)
                    return !IsPickerCommand(exe) && !IsPickerCommand(cmd);
                // no exe/command → likely a packaged app; it still has a friendly name
                var friendly = QueryAssoc(assoc, ASSOCSTR_FRIENDLYAPPNAME);
                return friendly.Length > 0 && !IsPickerCommand(friendly);
            }
            catch
            {
                return false; // no shlwapi / odd registry state → treat as unassociated
            }
        }

        /// <summary>One <c>AssocQueryString</c> lookup; "" when the string is not present.</summary>
        private static string QueryAssoc(string assoc, int stringId)
        {
            uint length = 0;
            // The sizing call reports S_FALSE and fills pcchOut; a missing string leaves it at 0.
            AssocQueryString(0, stringId, assoc, null, null, ref length);
            if (length == 0)
                return "";
            var sb = new StringBuilder((int)length);
            return AssocQueryString(0, stringId, assoc, null, sb, ref length) == 0 ? sb.ToString().Trim() : "";
        }

        /// <summary>True when a command/exe/app-name string is Windows' own "Open with" picker.</summary>
        private static bool IsPickerCommand(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var s = value.Trim();
            // "C:\path\app.exe" "%1" → take the quoted or first whitespace-delimited token
            string first;
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = s.IndexOf('"', 1);
                first = end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
            }
            else
            {
                var sp = s.IndexOf(' ');
                first = sp > 0 ? s.Substring(0, sp) : s;
            }
            var name = SafeFileName(first);
            return name.Equals("openwith.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path.Trim()) ?? ""; }
            catch { return ""; }
        }

        private static bool DirectoryExistsSafe(string path)
        {
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        private const int ASSOCSTR_COMMAND = 1;
        private const int ASSOCSTR_EXECUTABLE = 2;
        private const int ASSOCSTR_FRIENDLYAPPNAME = 4;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = false, SetLastError = false)]
        private static extern int AssocQueryString(
            int flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
    }
}
