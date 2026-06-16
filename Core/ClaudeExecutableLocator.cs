using System;
using System.Collections.Generic;
using System.IO;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Resolves the <c>claude</c> executable (Teil A §A3): configured override →
    /// PATH (claude.exe / claude.cmd) → npm global → native installer location.
    /// </summary>
    public static class ClaudeExecutableLocator
    {
        private static readonly string[] WindowsNames = { "claude.exe", "claude.cmd", "claude.bat" };

        public static string? Locate(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(overridePath!.Trim());
                return File.Exists(expanded) ? expanded : null;
            }

            foreach (var dir in EnumerateCandidateDirectories())
            {
                foreach (var name in WindowsNames)
                {
                    try
                    {
                        var candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch
                    {
                        // invalid PATH entry — skip
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCandidateDirectories()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var entry in path.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    yield return entry.Trim();
            }

            // npm global install (npm i -g @anthropic-ai/claude-code)
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "npm");

            // native installer
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                yield return Path.Combine(userProfile, ".local", "bin");
                yield return Path.Combine(userProfile, ".claude", "local");
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "Programs", "claude");
        }

        /// <summary>
        /// Best-effort check whether CLI credentials exist (OAuth login or API key).
        /// The CLI remains the source of truth; this only drives the auth.state hint.
        /// </summary>
        public static (bool loggedIn, string mode) ProbeAuthState()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
                return (true, "apiKey");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")))
                return (true, "oauth");

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                try
                {
                    if (File.Exists(Path.Combine(userProfile, ".claude", ".credentials.json")))
                        return (true, "oauth");

                    // Recent CLIs keep an oauthAccount marker in ~/.claude.json
                    var configPath = Path.Combine(userProfile, ".claude.json");
                    if (File.Exists(configPath))
                    {
                        var text = File.ReadAllText(configPath);
                        if (text.Contains("\"oauthAccount\""))
                            return (true, "oauth");
                    }
                }
                catch
                {
                    // unreadable — fall through
                }
            }

            return (false, "none");
        }
    }
}
