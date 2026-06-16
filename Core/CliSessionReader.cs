using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>A CLI-side session transcript imported into the extension's history.</summary>
    public sealed class ImportedCliSession
    {
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "Untitled";
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>Context size after the last turn (from the last assistant usage), 0 if unknown.</summary>
        public long ContextTokens { get; set; }

        /// <summary>Messages in the transcript.load schema (role/id/text/toolName/input/status/ts).</summary>
        public List<JObject> Messages { get; } = new List<JObject>();
    }

    /// <summary>
    /// Reads the CLI's own session store (<c>~/.claude/projects/&lt;munged-cwd&gt;/*.jsonl</c>)
    /// — used to pick up sessions a `claude remote-control` server created so they can
    /// be loaded into the chat afterwards. The JSONL lines closely resemble the
    /// stream-json output (user/assistant lines plus metadata lines we skip).
    /// </summary>
    public static class CliSessionReader
    {
        private const int MaxImportedMessages = 400;   // matches the history store cap
        private const int MaxTitleLength = 48;         // matches HandlePromptSend

        /// <summary>%USERPROFILE%\.claude\projects unless overridden (tests).</summary>
        public static string DefaultProjectsRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        /// <summary>
        /// Maps a working directory onto the CLI's project folder. Munging: every
        /// non-alphanumeric character becomes '-' ("C:\Users\Jan\Repo" →
        /// "C--Users-Jan-Repo"). The drive-letter case varies in the wild, so an
        /// existing directory is matched case-insensitively first.
        /// </summary>
        public static string? GetProjectDirectory(string cwd, string? projectsRoot = null)
        {
            if (string.IsNullOrEmpty(cwd))
                return null;
            var root = projectsRoot ?? DefaultProjectsRoot;
            var munged = MungePath(cwd);

            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (string.Equals(Path.GetFileName(dir), munged, StringComparison.OrdinalIgnoreCase))
                            return dir;
                    }
                }
            }
            catch
            {
                // fall through to the literal path
            }

            var literal = Path.Combine(root, munged);
            return Directory.Exists(literal) ? literal : null;
        }

        internal static string MungePath(string path)
        {
            var sb = new StringBuilder(path.Length);
            foreach (var c in path.TrimEnd('\\', '/'))
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        /// <summary>Session files touched since <paramref name="sinceUtc"/>, newest first.</summary>
        public static IReadOnlyList<string> FindSessionsSince(string projectDir, DateTime sinceUtc)
        {
            try
            {
                return Directory.GetFiles(projectDir, "*.jsonl")
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTimeUtc >= sinceUtc)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Imports a session transcript. Returns null when the file holds no
        /// displayable conversation (e.g. the pre-created but unused session).
        /// </summary>
        public static ImportedCliSession? ImportTranscript(string filePath)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch
            {
                return null;
            }

            var session = new ImportedCliSession
            {
                SessionId = Path.GetFileNameWithoutExtension(filePath),
                UpdatedAtUtc = SafeMtime(filePath),
            };

            var toolResults = new Dictionary<string, bool>(); // tool_use_id → isError
            JObject? lastAssistant = null;                    // for merging split text blocks
            string? lastAssistantId = null;

            foreach (var line in lines)
            {
                JObject obj;
                try
                {
                    obj = JObject.Parse(line);
                }
                catch
                {
                    continue;
                }

                if (obj.Value<bool?>("isSidechain") == true || obj.Value<bool?>("isMeta") == true)
                    continue;

                var type = obj.Value<string>("type");
                var message = obj["message"] as JObject;
                var ts = obj.Value<string>("timestamp") ?? "";

                if (type == "user" && message != null)
                {
                    if (message["content"] is JValue text)
                    {
                        var t = (text.Value<string>() ?? "").Trim();
                        // command tags / local-command echoes are CLI-internal
                        if (t.Length == 0 || t.StartsWith("<", StringComparison.Ordinal))
                            continue;
                        session.Messages.Add(new JObject
                        {
                            ["role"] = "user",
                            ["id"] = obj.Value<string>("uuid") ?? Guid.NewGuid().ToString("n"),
                            ["text"] = t,
                            ["ts"] = ts,
                        });
                        lastAssistant = null;
                    }
                    else if (message["content"] is JArray blocks)
                    {
                        foreach (var block in blocks.OfType<JObject>())
                        {
                            if (block.Value<string>("type") == "tool_result")
                            {
                                var id = block.Value<string>("tool_use_id");
                                if (!string.IsNullOrEmpty(id))
                                    toolResults[id!] = block.Value<bool?>("is_error") ?? false;
                            }
                        }
                    }
                }
                else if (type == "assistant" && message?["content"] is JArray content)
                {
                    if (message["usage"] is JObject usage)
                    {
                        session.ContextTokens =
                            (usage.Value<long?>("input_tokens") ?? 0)
                            + (usage.Value<long?>("cache_read_input_tokens") ?? 0)
                            + (usage.Value<long?>("cache_creation_input_tokens") ?? 0)
                            + (usage.Value<long?>("output_tokens") ?? 0);
                    }

                    var messageId = message.Value<string>("id") ?? obj.Value<string>("uuid") ?? "";
                    foreach (var block in content.OfType<JObject>())
                    {
                        switch (block.Value<string>("type"))
                        {
                            case "text":
                            {
                                var t = block.Value<string>("text") ?? "";
                                if (t.Length == 0)
                                    break;
                                if (lastAssistant != null && lastAssistantId == messageId)
                                {
                                    lastAssistant["text"] = (lastAssistant.Value<string>("text") ?? "") + t;
                                }
                                else
                                {
                                    lastAssistant = new JObject
                                    {
                                        ["role"] = "assistant",
                                        ["id"] = messageId + "-" + session.Messages.Count,
                                        ["text"] = t,
                                        ["ts"] = ts,
                                    };
                                    lastAssistantId = messageId;
                                    session.Messages.Add(lastAssistant);
                                }
                                break;
                            }
                            case "tool_use":
                                session.Messages.Add(new JObject
                                {
                                    ["role"] = "tool",
                                    ["id"] = block.Value<string>("id") ?? Guid.NewGuid().ToString("n"),
                                    ["toolName"] = block.Value<string>("name") ?? "Tool",
                                    ["input"] = block["input"]?.DeepClone() ?? new JObject(),
                                    ["status"] = "ok", // patched from tool_result below
                                    ["ts"] = ts,
                                });
                                lastAssistant = null;
                                break;
                            // thinking / signature blocks: not imported
                        }
                    }
                }
            }

            foreach (var m in session.Messages)
            {
                if (m.Value<string>("role") == "tool"
                    && toolResults.TryGetValue(m.Value<string>("id") ?? "", out var isError) && isError)
                {
                    m["status"] = "error";
                }
            }

            if (!session.Messages.Any(m => m.Value<string>("role") == "user"))
                return null; // nothing a user typed — e.g. the pre-created idle session

            var firstUser = session.Messages.First(m => m.Value<string>("role") == "user").Value<string>("text") ?? "";
            session.Title = firstUser.Length > MaxTitleLength
                ? firstUser.Substring(0, MaxTitleLength) + "…"
                : firstUser;

            if (session.Messages.Count > MaxImportedMessages)
                session.Messages.RemoveRange(0, session.Messages.Count - MaxImportedMessages);

            return session;
        }

        private static DateTime SafeMtime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.UtcNow; }
        }
    }
}
