using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeAstrogator.Core
{
    /// <summary>One git checkpoint (a commit in the shadow repo).</summary>
    public sealed class CheckpointInfo
    {
        public string Sha { get; set; } = "";
        public string ShortSha { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Git-based per-turn checkpoints (Cursor-style "restore"). Uses a <em>shadow repo</em>:
    /// a git repo whose <c>--git-dir</c> lives outside the project (under
    /// <c>%LOCALAPPDATA%\CodeAstrogator\Checkpoints\&lt;hash&gt;\.git</c>) while its
    /// <c>--work-tree</c> points at the solution directory. This snapshots the workspace files
    /// without ever touching the user's own <c>.git</c> (git always ignores a nested <c>.git</c>
    /// on <c>add -A</c>) and honours the project's <c>.gitignore</c>. All git calls run on a
    /// background thread; failures are surfaced as <see cref="GitCheckpointException"/> so the
    /// feature can never crash a turn. UI-free and unit-testable.
    /// </summary>
    public sealed class GitCheckpointService
    {
        // Default excludes when the project has no .gitignore of its own — keeps snapshots small.
        private static readonly string[] DefaultExcludes =
        {
            "bin/", "obj/", ".vs/", "node_modules/", "*.user", "*.suo",
            "packages/", "TestResults/", ".idea/", "*.tmp",
        };

        private static int _gitAvailable = -1; // -1 unknown, 0 no, 1 yes (cached)

        /// <summary>True if a <c>git</c> executable is on PATH (result cached for the process).</summary>
        public static bool IsGitAvailable()
        {
            var cached = Interlocked.CompareExchange(ref _gitAvailable, -1, -1);
            if (cached != -1)
                return cached == 1;
            bool ok;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    ok = false;
                }
                else
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    ok = p.WaitForExit(5000) && p.ExitCode == 0;
                }
            }
            catch
            {
                ok = false;
            }
            Interlocked.Exchange(ref _gitAvailable, ok ? 1 : 0);
            return ok;
        }

        /// <summary>Root of all shadow repos: <c>%LOCALAPPDATA%\CodeAstrogator\Checkpoints</c>.</summary>
        internal static string CheckpointsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeAstrogator", "Checkpoints");

        /// <summary>The <c>--git-dir</c> for a solution's shadow repo (stable per solution path).</summary>
        internal static string GitDirFor(string solutionDir)
        {
            var key = HashKey(solutionDir.TrimEnd('\\', '/').ToLowerInvariant());
            return Path.Combine(CheckpointsRoot, key, ".git");
        }

        /// <summary>
        /// Lazily creates the shadow repo for the solution: <c>git init</c> with a separate
        /// git-dir, a local identity (so commits work without a global git identity), gpg-signing
        /// off, default excludes when the project has no <c>.gitignore</c>, and a <c>meta.json</c>
        /// recording the original path. Idempotent.
        /// </summary>
        public async Task EnsureInitializedAsync(string solutionDir, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
                throw new GitCheckpointException("No workspace directory to checkpoint.");

            var gitDir = GitDirFor(solutionDir);
            if (Directory.Exists(gitDir) && File.Exists(Path.Combine(gitDir, "HEAD")))
                return; // already initialized

            Directory.CreateDirectory(gitDir);

            await RunAsync(gitDir, solutionDir, new[] { "init" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir, new[] { "config", "user.name", "Code Astrogator" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir, new[] { "config", "user.email", "checkpoints@codeastrogator.local" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir, new[] { "config", "commit.gpgsign", "false" }, ct).ConfigureAwait(false);
            // Some hosts default core.autocrlf on; keep snapshots byte-faithful and quiet.
            await RunAsync(gitDir, solutionDir, new[] { "config", "core.autocrlf", "false" }, ct).ConfigureAwait(false);

            // Default excludes only if the project ships no .gitignore of its own (else honour it).
            if (!File.Exists(Path.Combine(solutionDir, ".gitignore")))
            {
                try
                {
                    var infoDir = Path.Combine(gitDir, "info");
                    Directory.CreateDirectory(infoDir);
                    File.WriteAllText(Path.Combine(infoDir, "exclude"),
                        string.Join("\n", DefaultExcludes) + "\n");
                }
                catch { /* excludes are best-effort */ }
            }

            try
            {
                File.WriteAllText(Path.Combine(gitDir, "..", "meta.json"),
                    "{\"originalPath\":" + JsonString(solutionDir)
                    + ",\"createdUtc\":" + JsonString(DateTime.UtcNow.ToString("o")) + "}");
            }
            catch { /* meta is informational only */ }
        }

        /// <summary>
        /// Commits the current workspace state (<c>add -A</c> then <c>commit --allow-empty</c>) so
        /// every prompt gets a restore point even if nothing changed. Returns the new commit's SHA.
        /// </summary>
        public async Task<CheckpointInfo> CommitAsync(string solutionDir, string label, CancellationToken ct = default)
        {
            var gitDir = GitDirFor(solutionDir);
            await RunAsync(gitDir, solutionDir, new[] { "add", "-A" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir,
                new[] { "commit", "--allow-empty", "-m", string.IsNullOrEmpty(label) ? "checkpoint" : label },
                ct).ConfigureAwait(false);
            return await HeadInfoAsync(gitDir, solutionDir, label, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Forward-only restore of the workspace <em>files</em> to <paramref name="sha"/> (the chat
        /// is untouched): (1) safety-commit the current state, (2) <c>checkout &lt;sha&gt; -- .</c>,
        /// (3) delete files that were added after the target, (4) commit the restored state. The
        /// history stays linear, so every checkpoint remains reachable ("redo" = restore a later one).
        /// </summary>
        public async Task RestoreAsync(string solutionDir, string sha, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sha))
                throw new GitCheckpointException("No checkpoint selected.");
            var gitDir = GitDirFor(solutionDir);

            // 1. Safety commit of the current (about-to-be-overwritten) state.
            await RunAsync(gitDir, solutionDir, new[] { "add", "-A" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir,
                new[] { "commit", "--allow-empty", "-m", "auto: before restore" }, ct).ConfigureAwait(false);

            // 2. Restore tracked files' content to the target commit.
            await RunAsync(gitDir, solutionDir, new[] { "checkout", sha, "--", "." }, ct).ConfigureAwait(false);

            // 3. Remove files that did not exist at the target (added between it and HEAD).
            var added = await RunAsync(gitDir, solutionDir,
                new[] { "diff", "--diff-filter=A", "--name-only", sha, "HEAD" }, ct).ConfigureAwait(false);
            foreach (var rel in added.StdOut.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(solutionDir, rel.Trim().Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full))
                        File.Delete(full);
                }
                catch { /* a locked/absent file must not abort the restore */ }
            }

            // 4. Commit the restored state → linear history, nothing lost.
            var shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;
            await RunAsync(gitDir, solutionDir, new[] { "add", "-A" }, ct).ConfigureAwait(false);
            await RunAsync(gitDir, solutionDir,
                new[] { "commit", "--allow-empty", "-m", "Restored to " + shortSha }, ct).ConfigureAwait(false);
        }

        /// <summary>Lists checkpoints newest-first (optional; for a future checkpoint list UI).</summary>
        public async Task<IReadOnlyList<CheckpointInfo>> ListAsync(string solutionDir, CancellationToken ct = default)
        {
            var gitDir = GitDirFor(solutionDir);
            // U+001F (unit separator) delimits fields — it never appears in a commit subject.
            var result = await RunAsync(gitDir, solutionDir,
                new[] { "log", "--pretty=format:%H\u001f%h\u001f%cI\u001f%s" }, ct, allowFailure: true)
                .ConfigureAwait(false);
            var list = new List<CheckpointInfo>();
            if (result.ExitCode != 0)
                return list;
            foreach (var line in result.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\u001f');
                if (parts.Length < 4)
                    continue;
                DateTime.TryParse(parts[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts);
                list.Add(new CheckpointInfo { Sha = parts[0], ShortSha = parts[1], TimestampUtc = ts, Label = parts[3] });
            }
            return list;
        }

        private async Task<CheckpointInfo> HeadInfoAsync(string gitDir, string workTree, string label, CancellationToken ct)
        {
            var sha = (await RunAsync(gitDir, workTree, new[] { "rev-parse", "HEAD" }, ct).ConfigureAwait(false))
                .StdOut.Trim();
            return new CheckpointInfo
            {
                Sha = sha,
                ShortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha,
                Label = label ?? "",
                TimestampUtc = DateTime.UtcNow,
            };
        }

        // ── git process plumbing ─────────────────────────────────────────────

        internal sealed class GitResult
        {
            public int ExitCode;
            public string StdOut = "";
            public string StdErr = "";
        }

        /// <summary>
        /// Runs <c>git --git-dir=&lt;gitDir&gt; --work-tree=&lt;workTree&gt; &lt;args…&gt;</c> on a
        /// background thread. Throws <see cref="GitCheckpointException"/> on a non-zero exit unless
        /// <paramref name="allowFailure"/> is set (callers that inspect the exit code themselves).
        /// </summary>
        internal async Task<GitResult> RunAsync(
            string gitDir, string workTree, IReadOnlyList<string> args,
            CancellationToken ct, bool allowFailure = false)
        {
            var full = new List<string>
            {
                "--git-dir=" + gitDir,
                "--work-tree=" + workTree,
            };
            full.AddRange(args);

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = BuildArgs(full),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Directory.Exists(workTree) ? workTree : Environment.CurrentDirectory,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            Process process;
            try
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            }
            catch (Exception ex)
            {
                throw new GitCheckpointException("Could not start git: " + ex.Message, ex);
            }

            using (process)
            {
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) =>
                {
                    try { exited.TrySetResult(process.ExitCode); }
                    catch { exited.TrySetResult(-1); }
                };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data).Append('\n'); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Append(e.Data).Append('\n'); };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    throw new GitCheckpointException("Could not start git: " + ex.Message, ex);
                }

                using (ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                {
                    var exitCode = await exited.Task.ConfigureAwait(false);
                    try { process.WaitForExit(); } catch { } // drain async handlers

                    var result = new GitResult
                    {
                        ExitCode = exitCode,
                        StdOut = stdout.ToString(),
                        StdErr = stderr.ToString(),
                    };
                    if (!allowFailure && exitCode != 0)
                    {
                        var detail = result.StdErr.Trim();
                        if (detail.Length == 0) detail = result.StdOut.Trim();
                        throw new GitCheckpointException(
                            "git " + (args.Count > 0 ? args[0] : "") + " failed (exit " + exitCode + "): " + detail);
                    }
                    return result;
                }
            }
        }

        /// <summary>Joins args with the same Windows argv quoting the CLI host uses.</summary>
        internal static string BuildArgs(IReadOnlyList<string> args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(ClaudeCliProcessHost.Quote(args[i]));
            }
            return sb.ToString();
        }

        private static string JsonString(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string HashKey(string value)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>A git checkpoint operation failed (caught by the bridge; never crashes a turn).</summary>
    public sealed class GitCheckpointException : Exception
    {
        public GitCheckpointException(string message) : base(message) { }
        public GitCheckpointException(string message, Exception inner) : base(message, inner) { }
    }
}
