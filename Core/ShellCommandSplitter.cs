using System.Collections.Generic;
using System.Linq;
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

        /// <summary>Turns a command into a reusable glob pattern by replacing each quoted argument
        /// (<b>including the surrounding quotes</b>) with a bare <c>*</c> — e.g.
        /// <c>Out-File -FilePath "x.txt"</c> → <c>Out-File -FilePath *</c>. Dropping the quotes makes
        /// the pattern quote-agnostic: it then matches the same command whether the next invocation
        /// uses double quotes, single quotes or no quotes at all (the literal quote chars used to have
        /// to match exactly, which is why <c>*</c> often appeared not to work). Runs of resulting
        /// wildcards are collapsed so <c>* *</c> can't bloat. Matching is always per sub-command, so
        /// a wildcard can never span a real command separator.</summary>
        public static string Wildcardize(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;
            var s = Regex.Replace(command, "\"[^\"]*\"", "*");
            s = Regex.Replace(s, "'[^']*'", "*");
            s = Regex.Replace(s, @"\*(\s+\*)+", "*"); // collapse "* *" → "*"
            return s;
        }

        /// <summary>Glob match over the <b>whole</b> string: <c>*</c> = any run (incl. none),
        /// everything else literal, case-insensitive, newlines included. The single source of truth
        /// for auto-approve pattern matching (host + tests).</summary>
        public static bool MatchesGlob(string value, string pattern)
        {
            if (value == null || pattern == null) return false;
            try
            {
                var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            catch { return false; }
        }

        /// <summary>True when <b>every</b> meaningful sub-command of <paramref name="command"/>
        /// (variable assignments dropped, see <see cref="ExtractCommands"/>) is covered by at least
        /// one of <paramref name="patterns"/>. "All sub-commands" — never approve a chain just because
        /// one &amp;&amp;/pipe stage happens to match. Blank patterns are ignored.</summary>
        public static bool IsCommandCovered(string command, IEnumerable<string> patterns)
        {
            var pats = (patterns ?? Enumerable.Empty<string>())
                .Select(p => (p ?? "").Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (pats.Count == 0) return false;

            var commands = ExtractCommands(command);
            if (commands.Count == 0)
                commands = new List<string> { (command ?? "").Trim() };
            return commands.All(sub => pats.Any(p => MatchesGlob(sub, p)));
        }

        // Splits on top-level statement/pipeline separators, treating quotes + here-strings as
        // atomic. Hardened for the two shells this extension drives (PowerShell + bash):
        //   • Escapes: PowerShell backtick (`) and bash backslash (\") are honoured so an escaped
        //     quote or operator never flips the parser's state. (Bare backslash stays literal so
        //     Windows paths like WebUI\app.js are untouched — PowerShell has no \ escape.)
        //   • Doubled quotes ("" / '') inside a string are the PowerShell escape for a literal
        //     quote and are kept inside the string instead of closing it.
        //   • Nesting: separators inside (...), $(...), @(...) and { ... } are NOT split — they
        //     belong to one statement (e.g. foreach ($i in $x) { a; b } stays whole).
        private static List<string> SplitSegments(string command)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return parts;

            var buf = new StringBuilder();
            const int Normal = 0, HereSingle = 1, HereDouble = 2; // plus '\'' / '"' for quotes
            int state = Normal;
            int depth = 0; // nesting of () and {} while in Normal state

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

                // Inside a single-quoted string: literal except a doubled '' (PowerShell escape).
                if (state == '\'')
                {
                    buf.Append(c);
                    if (c == '\'') { if (next == '\'') { buf.Append(next); i++; } else state = Normal; }
                    continue;
                }
                // Inside a double-quoted string: backtick/backslash escape the next char, a doubled
                // "" is a literal quote, a lone " closes the string.
                if (state == '"')
                {
                    if ((c == '`' || c == '\\') && next != '\0') { buf.Append(c); buf.Append(next); i++; continue; }
                    buf.Append(c);
                    if (c == '"') { if (next == '"') { buf.Append(next); i++; } else state = Normal; }
                    continue;
                }

                // Normal state
                if (c == '`' && next != '\0') { buf.Append(c); buf.Append(next); i++; continue; } // PowerShell escape
                if (c == '@' && (next == '\'' || next == '"')) { state = next == '\'' ? HereSingle : HereDouble; buf.Append(c); buf.Append(next); i++; continue; }
                if (c == '\'' || c == '"') { state = c; buf.Append(c); continue; }
                if (c == '(' || c == '{') { depth++; buf.Append(c); continue; }
                if (c == ')' || c == '}') { if (depth > 0) depth--; buf.Append(c); continue; }
                if (depth > 0) { buf.Append(c); continue; } // separators inside (...) / {...} don't split
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
