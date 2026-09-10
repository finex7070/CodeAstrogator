using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// What the installed CLI actually supports, read from <c>claude --help</c> once per executable
    /// and cached for the process. Exists for one reason: the value that means "ask the user about
    /// every tool" was renamed, and getting it wrong is silent.
    /// <para>
    /// CLI ≤ 2.1.178 offered <c>default | acceptEdits | plan | bypassPermissions</c> (plus the then
    /// unused <c>auto</c>/<c>dontAsk</c>); 2.1.263 offers
    /// <c>acceptEdits | auto | bypassPermissions | manual | dontAsk | plan</c> — <c>default</c> is
    /// gone, "ask" is now <c>manual</c>, and <b>omitting</b> the flag resolves to <c>auto</c>, which
    /// lets the MODEL decide whether to consult <c>--permission-prompt-tool</c> at all (measured
    /// 2026-09-10: Opus 5 self-approves ordinary edits and never calls the hook, Haiku calls it).
    /// So neither an explicit legacy value nor omission works everywhere — we have to ask the binary.
    /// </para>
    /// </summary>
    public static class ClaudeCliCapabilities
    {
        /// <summary>Per-executable cache (the user can repoint the path in the settings).</summary>
        private static readonly ConcurrentDictionary<string, Task<CliCapabilities>> Cache =
            new ConcurrentDictionary<string, Task<CliCapabilities>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads (and caches) the capabilities of <paramref name="exePath"/>. Never throws:
        /// on any failure the result reports "unknown", and callers keep the legacy behaviour.</summary>
        public static Task<CliCapabilities> GetAsync(string exePath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(exePath))
                return Task.FromResult(CliCapabilities.Unknown);
            return Cache.GetOrAdd(exePath, path => ProbeAsync(path, ct));
        }

        /// <summary>Drops the cache (used when the configured executable path changes).</summary>
        public static void Invalidate() => Cache.Clear();

        private static async Task<CliCapabilities> ProbeAsync(string exePath, CancellationToken ct)
        {
            var help = await RunHelpAsync(exePath, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(help))
                return CliCapabilities.Unknown;
            return ParseHelp(help!);
        }

        /// <summary>Extracts the <c>--permission-mode</c> choice list from a <c>--help</c> dump.
        /// Internal for testing; returns "unknown" when the list cannot be found.</summary>
        internal static CliCapabilities ParseHelp(string help)
        {
            // The help text wraps the choices over several lines, e.g.
            //   --permission-mode <mode>   Permission mode to use for the session
            //                              (choices: "acceptEdits", "auto",
            //                              "bypassPermissions", "manual", …)
            var start = help.IndexOf("--permission-mode", StringComparison.Ordinal);
            if (start < 0)
                return CliCapabilities.Unknown;
            var choices = help.IndexOf("choices:", start, StringComparison.Ordinal);
            if (choices < 0)
                return CliCapabilities.Unknown;
            var end = help.IndexOf(')', choices);
            if (end < 0)
                end = Math.Min(help.Length, choices + 400);
            var segment = help.Substring(choices, end - choices);

            var modes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(segment, "\"([A-Za-z-]+)\""))
                modes.Add(m.Groups[1].Value);
            if (modes.Count == 0)
                return CliCapabilities.Unknown;

            return new CliCapabilities
            {
                Known = true,
                SupportsManualPermissionMode = modes.Contains("manual"),
                SupportsDefaultPermissionMode = modes.Contains("default"),
            };
        }

        /// <summary>Runs <c>claude --help</c> with no console window and returns stdout (null on
        /// failure/timeout). Same hidden-process shape as <see cref="ClaudeUsageClient"/>, so this
        /// cannot flash a console window either.</summary>
        private static async Task<string?> RunHelpAsync(string exePath, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--help",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) =>
                {
                    try { exited.TrySetResult(process.ExitCode); }
                    catch { exited.TrySetResult(-1); }
                };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        lock (stdout) stdout.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, __) => { /* --help writes to stdout; ignore */ };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); } catch { }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
                using (timeoutCts.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch { /* already gone */ }
                }))
                {
                    await exited.Task.ConfigureAwait(false);
                    try { process.WaitForExit(); } catch { }
                    if (timeoutCts.IsCancellationRequested)
                        return null;
                    lock (stdout) return stdout.ToString();
                }
            }
            catch (Exception)
            {
                return null; // not launchable / no permission → treat as unknown
            }
        }
    }

    /// <summary>Capabilities of one <c>claude</c> executable (see <see cref="ClaudeCliCapabilities"/>).</summary>
    public sealed class CliCapabilities
    {
        /// <summary>Nothing could be read (probe failed) — callers must keep the legacy behaviour.</summary>
        public static readonly CliCapabilities Unknown = new CliCapabilities();

        /// <summary>False when the probe failed; the two flags below are then meaningless.</summary>
        public bool Known { get; set; }

        /// <summary>CLI accepts <c>--permission-mode manual</c> (2.1.2xx+): the only value that
        /// reliably routes every edit through <c>--permission-prompt-tool</c>.</summary>
        public bool SupportsManualPermissionMode { get; set; }

        /// <summary>CLI still accepts the legacy <c>--permission-mode default</c> (≤ 2.1.1xx).</summary>
        public bool SupportsDefaultPermissionMode { get; set; }

        /// <summary>The value to pass for "ask the user about everything", or null to omit the flag
        /// (legacy CLIs, where omission still means exactly that).</summary>
        public string? AskPermissionModeArg => SupportsManualPermissionMode ? "manual" : null;
    }
}
