using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>Title + body of one Windows notification.</summary>
    internal sealed class ToastText
    {
        public string Title = "";
        public string Body = "";
    }

    /// <summary>
    /// Builds the text and the toast XML of the Windows notifications ("Notify me" option): one at the end
    /// of a turn and one whenever Claude waits for the user (permission prompt, question, plan approval).
    /// UI-free so it can be tested; showing the toast is <c>Services/WindowsToastNotifier</c>.
    /// </summary>
    internal static class ToastContent
    {
        /// <summary>Longest body text; Windows shows about four lines of it anyway.</summary>
        public const int MaxBodyLength = 200;

        public static ToastText ForTurnEnd(string? resultText, string? error)
        {
            if (!string.IsNullOrWhiteSpace(error))
                return new ToastText { Title = "Claude stopped with an error", Body = Snippet(error) };
            var body = Snippet(resultText, markdown: true);
            return new ToastText { Title = "Claude finished", Body = body.Length > 0 ? body : "Your turn." };
        }

        /// <summary>A permission prompt: what Claude wants to run or change. ExitPlanMode is the plan to approve.</summary>
        public static ToastText ForPermission(string toolName, JObject? input)
        {
            if (toolName == "ExitPlanMode" || toolName == "exit_plan_mode")
                return new ToastText { Title = "Claude's plan is ready for review", Body = Snippet(input?.Value<string>("plan"), markdown: true) };
            var command = input?.Value<string>("command") ?? input?.Value<string>("cmd") ?? input?.Value<string>("script");
            var path = input?.Value<string>("file_path") ?? input?.Value<string>("notebook_path") ?? input?.Value<string>("path");
            var detail = !string.IsNullOrWhiteSpace(command) ? command
                : !string.IsNullOrWhiteSpace(path) ? FileName(path!)
                : null;
            var tool = DisplayToolName(toolName);
            return new ToastText
            {
                Title = "Claude needs your permission",
                Body = detail != null ? Snippet(tool + ": " + detail) : tool,
            };
        }

        /// <summary>An AskUserQuestion prompt: the first question (and how many more follow).</summary>
        public static ToastText ForQuestion(JObject? input)
        {
            var questions = (input?["questions"] as JArray)?.OfType<JObject>().ToList();
            var first = questions?.FirstOrDefault()?.Value<string>("question");
            var body = Snippet(first);
            if (questions != null && questions.Count > 1)
                body = (body.Length > 0 ? body + " " : "") + "(+" + (questions.Count - 1) + " more)";
            return new ToastText { Title = "Claude has a question", Body = body };
        }

        /// <summary>Longest tag Windows accepts.</summary>
        public const int MaxTagLength = 64;

        /// <summary>The tag a toast is shown with. Tags are per AppUserModelID, and every Visual Studio instance
        /// shares ours — unscoped, a second instance's turn-end toast would replace the first one's, and its next
        /// prompt would take the other's toast away. So each process gets its own prefix; a long id is cut from
        /// the front (its end is the unique part of a tool_use_id).</summary>
        public static string ScopedTag(string tag, int processId)
        {
            var prefix = "p" + processId + "-";
            var rest = string.IsNullOrEmpty(tag) ? "toast" : tag;
            var room = MaxTagLength - prefix.Length;
            return prefix + (rest.Length <= room ? rest : rest.Substring(rest.Length - room));
        }

        /// <summary>The small grey line at the bottom: the workspace (which Visual Studio — there may be several
        /// open) and the chat's title. Either may be missing.</summary>
        public static string? Attribution(string? workspaceDirectory, string? chatTitle)
        {
            string? workspace = null;
            if (!string.IsNullOrWhiteSpace(workspaceDirectory))
            {
                try { workspace = Path.GetFileName(workspaceDirectory!.TrimEnd('\\', '/')); }
                catch (ArgumentException) { }
            }
            var title = string.IsNullOrWhiteSpace(chatTitle) || chatTitle == "Untitled" ? null : chatTitle;
            if (string.IsNullOrEmpty(workspace))
                return title;
            return title == null ? workspace : workspace + " · " + title;
        }

        /// <summary>The toast XML (ToastGeneric). <paramref name="attribution"/> = the small grey line at the
        /// bottom (the chat's title); <paramref name="launch"/> comes back on a click.</summary>
        public static string BuildXml(ToastText text, string? attribution, string launch)
        {
            var sb = new StringBuilder();
            sb.Append("<toast launch=\"").Append(Escape(launch)).Append("\">");
            sb.Append("<visual><binding template=\"ToastGeneric\">");
            sb.Append("<text>").Append(Escape(Clean(text.Title))).Append("</text>");
            var body = Clean(text.Body);
            if (body.Length > 0)
                sb.Append("<text>").Append(Escape(body)).Append("</text>");
            var attr = Snippet(attribution, 80);
            if (attr.Length > 0)
                sb.Append("<text placement=\"attribution\">").Append(Escape(attr)).Append("</text>");
            sb.Append("</binding></visual></toast>");
            return sb.ToString();
        }

        /// <summary>One line of plain text: whitespace collapsed, cut at <paramref name="max"/> characters with
        /// an ellipsis. <paramref name="markdown"/> = Claude's answer: Markdown markers are dropped too (not
        /// for commands — <c>**/*.cs</c> must stay as it is).</summary>
        public static string Snippet(string? text, int max = MaxBodyLength, bool markdown = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var s = text!;
            if (markdown)
            {
                s = Regex.Replace(s, @"```[^\n]*", " ");                                // code fences (the code itself stays)
                s = Regex.Replace(s, @"(?m)^[ \t]{0,3}(#{1,6}|>|[-*+]|\d+\.)[ \t]+", ""); // headings, quotes, list markers
                s = s.Replace("**", "").Replace("__", "").Replace("`", "");
                s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");                    // [text](link) → text
            }
            s = Clean(Regex.Replace(s, @"\s+", " ")).Trim();
            if (s.Length <= max)
                return s;
            var cut = max - 1;
            if (char.IsHighSurrogate(s[cut - 1]))
                cut--; // never split an emoji — a lone surrogate makes the toast XML invalid
            return s.Substring(0, cut).TrimEnd() + "…";
        }

        private static string DisplayToolName(string toolName)
        {
            // mcp__server__tool → "server: tool"
            if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = toolName.Substring(5).Split(new[] { "__" }, 2, StringSplitOptions.None);
                return parts.Length == 2 ? parts[0] + ": " + parts[1] : parts[0];
            }
            return string.IsNullOrEmpty(toolName) ? "Tool" : toolName;
        }

        private static string FileName(string path)
        {
            try { return Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path; }
            catch (ArgumentException) { return path; }
        }

        /// <summary>Drops characters XML 1.0 cannot carry (control characters other than tab/newline).</summary>
        private static string Clean(string? s) =>
            new string((s ?? "").Where(c => c >= 0x20 || c == '\t' || c == '\n' || c == '\r').ToArray());

        private static string Escape(string s) => SecurityElement.Escape(s) ?? "";
    }
}
