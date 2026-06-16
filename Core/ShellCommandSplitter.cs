using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Splits a shell command line into its top-level sub-commands so each can be suggested
    /// as a separate auto-approve pattern. Splits on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and a
    /// whitespace-surrounded bare <c>&amp;</c> (sequential). Pipes (<c>|</c>) are NOT split — they
    /// belong to one pipeline. Operators inside single/double quotes are ignored, and a bare
    /// <c>&amp;</c> without surrounding whitespace (e.g. the <c>2&gt;&amp;1</c> redirect) is kept.
    /// Heuristic — the result only pre-fills an editable popover.
    /// </summary>
    public static class ShellCommandSplitter
    {
        public static List<string> Split(string command)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(command))
                return parts;

            var buf = new StringBuilder();
            char quote = '\0';

            void Flush()
            {
                var s = buf.ToString().Trim();
                if (s.Length > 0) parts.Add(s);
                buf.Clear();
            }

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (quote != '\0')
                {
                    buf.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    buf.Append(c);
                    continue;
                }
                // && or ||  → split, skip the second operator char
                if ((c == '&' || c == '|') && i + 1 < command.Length && command[i + 1] == c)
                {
                    Flush();
                    i++;
                    continue;
                }
                if (c == ';')
                {
                    Flush();
                    continue;
                }
                // bare & as a sequential separator: only when whitespace-surrounded (so 2>&1 stays)
                if (c == '&'
                    && i > 0 && char.IsWhiteSpace(command[i - 1])
                    && i + 1 < command.Length && char.IsWhiteSpace(command[i + 1]))
                {
                    Flush();
                    continue;
                }
                buf.Append(c);
            }
            Flush();
            return parts;
        }

        // ── Auto-approve pattern extraction ──────────────────────────────────────
        // $x = …   /   VAR=…   (assignment, but not ==) — dropped from suggestions/matching
        private static readonly Regex AssignmentRx =
            new Regex(@"^\s*\$?[A-Za-z_][\w:.]*\s*(\[[^\]]*\])?\s*(\+|-|\*|/)?=(?!=)", RegexOptions.Compiled);
        private static readonly Regex BareVariableRx =
            new Regex(@"^\s*\$[\w:.]+\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Extracts the meaningful sub-commands from a (possibly multi-line, multi-statement)
        /// command line for auto-approve matching/suggestions. Splits on newlines, <c>;</c>,
        /// <c>&amp;&amp;</c>, <c>||</c>, a whitespace-surrounded bare <c>&amp;</c> and pipeline
        /// stages (<c>|</c>); respects single/double quotes and here-strings (<c>@'…'@</c> /
        /// <c>@"…"@</c>). Variable assignments (<c>$x = …</c>, <c>VAR=…</c>) and bare variable
        /// references (<c>$x</c>) are dropped so only real commands remain.
        /// </summary>
        public static List<string> ExtractCommands(string command)
        {
            var result = new List<string>();
            foreach (var seg in SplitSegments(command))
            {
                var s = seg.Trim();
                if (s.Length == 0) continue;
                if (AssignmentRx.IsMatch(s) || BareVariableRx.IsMatch(s)) continue; // ignore var assignments
                result.Add(s);
            }
            return result;
        }

        /// <summary>Turns a command into a reusable glob pattern by replacing quoted argument
        /// values with <c>*</c> (e.g. <c>Out-File -FilePath "x.txt"</c> → <c>Out-File -FilePath "*"</c>).</summary>
        public static string Wildcardize(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;
            var s = Regex.Replace(command, "\"[^\"]*\"", "\"*\"");
            s = Regex.Replace(s, "'[^']*'", "'*'");
            return s;
        }

        // Splits on top-level statement/pipeline separators, treating quotes + here-strings as atomic.
        private static List<string> SplitSegments(string command)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return parts;

            var buf = new StringBuilder();
            const int Normal = 0, HereSingle = 1, HereDouble = 2; // plus '\'' / '"' for quotes
            int state = Normal;

            void Flush()
            {
                var s = buf.ToString().Trim();
                if (s.Length > 0) parts.Add(s);
                buf.Clear();
            }

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                char next = i + 1 < command.Length ? command[i + 1] : '\0';

                if (state == HereSingle) { buf.Append(c); if (c == '\'' && next == '@') { buf.Append('@'); i++; state = Normal; } continue; }
                if (state == HereDouble) { buf.Append(c); if (c == '"' && next == '@') { buf.Append('@'); i++; state = Normal; } continue; }
                if (state == '\'' || state == '"') { buf.Append(c); if (c == state) state = Normal; continue; }

                // Normal state
                if (c == '@' && (next == '\'' || next == '"')) { state = next == '\'' ? HereSingle : HereDouble; buf.Append(c); buf.Append(next); i++; continue; }
                if (c == '\'' || c == '"') { state = c; buf.Append(c); continue; }
                if (c == '\n' || c == '\r') { Flush(); continue; }
                if ((c == '&' || c == '|') && next == c) { Flush(); i++; continue; }       // && ||
                if (c == ';' || c == '|') { Flush(); continue; }                            // ; or pipeline stage
                if (c == '&' && i > 0 && char.IsWhiteSpace(command[i - 1]) && char.IsWhiteSpace(next)) { Flush(); continue; } // bare &
                buf.Append(c);
            }
            Flush();
            return parts;
        }
    }
}
