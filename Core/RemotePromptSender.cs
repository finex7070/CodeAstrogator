using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Sends a prompt from the panel into a running Remote Control background session. The CLI has no command for
    /// that (2.1.319: <c>agents</c>, <c>attach</c>, <c>logs</c>, <c>respawn</c>, <c>stop</c>, <c>rm</c>), so it does
    /// what a person would do: <c>claude attach &lt;id&gt;</c> in a hidden pseudo console, paste the prompt into the
    /// input box, Enter, detach (the session keeps running — measured). Guards, because the TUI is not an API:
    /// <list type="bullet">
    /// <item>only when the screen shows the empty input box — never while a dialog (approval, question, plan) is
    /// open, where typed text or Enter would pick an option;</item>
    /// <item>bracketed paste with <c>\n</c> line breaks only — a <c>\r</c> inside the paste submits the line
    /// (measured); escape and other control characters are removed;</item>
    /// <item>Enter only after the pasted text is seen in the box; otherwise the box is cleared again (Ctrl+U per
    /// line) and the send fails.</item>
    /// </list>
    /// See docs/NOTES.md "Remote Control" → "Prompting from the panel".
    /// </summary>
    public static class RemotePromptSender
    {
        private const char PromptMark = '❯'; // ❯ — the input box (and, in dialogs, the selection cursor)

        /// <summary>Sends <paramref name="prompt"/> to the background session <paramref name="backgroundId"/>.
        /// Returns null when it went out, else why not (shown to the user).</summary>
        public static async Task<string?> SendAsync(string exe, string backgroundId, string prompt, string? workingDirectory,
            CancellationToken ct)
        {
            var text = SanitizePrompt(prompt);
            if (text.Length == 0)
                return "Nothing to send.";
            using var console = new PseudoConsole();
            try
            {
                console.Start(ClaudeCliProcessHost.Quote(exe) + " attach " + ClaudeCliProcessHost.Quote(backgroundId),
                    workingDirectory);
            }
            catch (Exception ex)
            {
                return "Could not open the remote session (" + ex.Message + ").";
            }

            // 1. the TUI has drawn its input box and gone quiet
            if (!await WaitQuietAsync(console, 20_000, 800, requireMark: true, ct).ConfigureAwait(false))
                return "The remote session did not answer.";
            var screen = StripAnsi(console.Output);
            if (!IsInputReady(screen))
                return "The remote session is waiting for an answer (an approval or a question) — answer it in the Claude app first.";
            if (InputBoxText(screen).Length > 0)
                ClearInput(console, 1 + CountLines(InputBoxText(screen))); // something left over from before

            // 2. paste, then check it arrived
            var mark = console.OutputLength;
            console.Write("\u001b[200~" + text + "\u001b[201~");
            await WaitQuietAsync(console, 4000, 400, requireMark: false, ct).ConfigureAwait(false);
            var afterPaste = StripAnsi(console.Output.Substring(Math.Min(mark, console.OutputLength)));
            if (!PasteVisible(afterPaste, text))
            {
                ClearInput(console, CountLines(text));
                return "The prompt did not reach the remote session's input — nothing was sent.";
            }

            // 3. submit
            console.Write("\r");
            await WaitQuietAsync(console, 3000, 500, requireMark: false, ct).ConfigureAwait(false);
            return null; // detaching (Dispose) leaves the session running
        }

        private static async Task<bool> WaitQuietAsync(PseudoConsole console, int timeoutMs, int quietMs, bool requireMark,
            CancellationToken ct)
        {
            var start = Environment.TickCount;
            var last = -1;
            var quietSince = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) < timeoutMs)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var length = console.OutputLength;
                if (length != last)
                {
                    last = length;
                    quietSince = Environment.TickCount;
                    continue;
                }
                if (length > 0 && unchecked(Environment.TickCount - quietSince) >= quietMs
                    && (!requireMark || console.Output.IndexOf(PromptMark) >= 0))
                    return true;
            }
            return false;
        }

        /// <summary>Ctrl+U clears the box's current line; Backspace joins the line above (repeat per line).</summary>
        private static void ClearInput(PseudoConsole console, int lines)
        {
            try
            {
                console.Write("\u0015");
                for (var i = 1; i < Math.Min(lines, 200); i++)
                    console.Write("\u007f\u0015");
                Thread.Sleep(300);
            }
            catch { /* detached anyway */ }
        }

        private static int CountLines(string text) => text.Split('\n').Length;

        // ── screen reading (pure, tested) ────────────────────────────────────────

        private static readonly Regex Osc = new Regex("\u001b\\][^\u0007\u001b]*(\u0007|\u001b\\\\)", RegexOptions.Compiled);
        private static readonly Regex Csi = new Regex("\u001b\\[[0-9;?<>=]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex OtherEscape = new Regex("\u001b[()][A-Za-z0-9]|\u001b[78=>MNOPc]", RegexOptions.Compiled);

        /// <summary>The terminal output as plain text: escape sequences become a space (cursor moves separate words),
        /// control characters other than line breaks go.</summary>
        public static string StripAnsi(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            var text = Osc.Replace(raw, "");
            text = Csi.Replace(text, " ");
            text = OtherEscape.Replace(text, "");
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\n')
                    sb.Append('\n');
                else if (c >= ' ' || c == '\t')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static readonly string[] DialogMarkers =
        {
            "Do you want", "Would you like", "Enter to select", "Esc to cancel", "to navigate", "Enter to confirm",
        };

        /// <summary>
        /// True when the latest frame shows the input box (not a dialog): the text after the last <c>❯</c> is not a
        /// numbered option ("❯ 1. Yes") and the box's bottom border follows it, and the tail of the screen holds no
        /// dialog wording. The TUI redraws frames into one stream, so only the end of it is the current state.
        /// </summary>
        public static bool IsInputReady(string screen)
        {
            if (string.IsNullOrEmpty(screen))
                return false;
            var i = screen.LastIndexOf(PromptMark);
            if (i < 0)
                return false;
            var after = screen.Substring(i + 1);
            if (Regex.IsMatch(after, @"^\s*\d+\."))
                return false;
            if (after.IndexOf("────", StringComparison.Ordinal) < 0) // ──── the box's bottom border
                return false;
            var tail = screen.Substring(Math.Max(0, screen.Length - 1500));
            foreach (var marker in DialogMarkers)
                if (tail.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            return true;
        }

        /// <summary>What the input box holds in the latest frame (lines between <c>❯</c> and the bottom border), trimmed;
        /// "" for an empty box or the placeholder hint (<c>Try "…"</c>).</summary>
        public static string InputBoxText(string screen)
        {
            var i = screen.LastIndexOf(PromptMark);
            if (i < 0)
                return "";
            var after = screen.Substring(i + 1);
            var border = after.IndexOf("────", StringComparison.Ordinal);
            var box = (border >= 0 ? after.Substring(0, border) : after).Trim();
            return box.StartsWith("Try \"", StringComparison.Ordinal) ? "" : box;
        }

        /// <summary>True when the new output after the paste shows the pasted text in the box — its first words, or the
        /// TUI's "[Pasted text …]" placeholder for long pastes.</summary>
        public static bool PasteVisible(string screenAfterPaste, string pasted)
        {
            if (screenAfterPaste.IndexOf("[Pasted text", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            var firstLine = pasted.Split('\n')[0].Trim();
            if (firstLine.Length == 0)
                return true;
            var probe = Collapse(firstLine.Length > 24 ? firstLine.Substring(0, 24) : firstLine);
            return Collapse(screenAfterPaste).IndexOf(probe, StringComparison.Ordinal) >= 0;
        }

        private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        /// <summary>The prompt as it may be pasted: <c>\n</c> line breaks only (a <c>\r</c> would submit the line), no
        /// escape or other control characters (they could end the paste early or drive the TUI), tabs as spaces.</summary>
        public static string SanitizePrompt(string? prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                return "";
            var text = prompt!.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                // C0 controls, DEL and the C1 range (U+0080–U+009F, e.g. U+009B is a one-character CSI) go
                if (c == '\n' || (c >= ' ' && !(c >= '\u007f' && c <= '\u009f')))
                    sb.Append(c);
            }
            var result = sb.ToString().Trim();
            // At the very start of the box "!" switches the TUI to bash mode (runs a shell command) and "#" to memory
            // mode — a prompt from the panel is always a prompt.
            if (result.Length > 0 && (result[0] == '!' || result[0] == '#'))
                result = " " + result;
            return result;
        }
    }
}
