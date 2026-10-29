using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CodeAstrogator.Core
{
    /// <summary>One snapshot: a parentless commit stored behind its own ref (see docs/NOTES.md
    /// "Checkpoints"). Parentless means pruning is a ref delete + gc, and the sha of a snapshot
    /// never changes — persisted checkpoint shas in the chat history stay valid.</summary>
    internal sealed class CheckpointInfo
    {
        public string Sha { get; set; } = "";
        public string RefName { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Label { get; set; } = "";

        public string ShortSha => Sha.Length >= 8 ? Sha.Substring(0, 8) : Sha;
    }

    /// <summary>One changed file between two snapshots (repo-relative path, forward slashes).</summary>
    internal sealed class CheckpointFileChange
    {
        public string Path { get; set; } = "";
        public int Added { get; set; }
        public int Removed { get; set; }
        public bool Binary { get; set; }
        /// <summary>"A" = created since the snapshot, "D" = deleted since it, "M" = modified.</summary>
        public string Status { get; set; } = "M";
    }

    /// <summary>Outcome of a rewind. <see cref="Skipped"/> holds paths deliberately left alone
    /// (symlink / hard link / unreadable) — surfaced as "restored the code, but skipped N files".</summary>
    internal sealed class RestoreResult
    {
        /// <summary>Paths written back to their checkpoint content.</summary>
        public List<string> Restored { get; } = new List<string>();
        /// <summary>Paths removed again because they only existed after the checkpoint.</summary>
        public List<string> Deleted { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
        /// <summary>The diff entries a rewind actually acted on (same `+n/−m` numbers the preview
        /// showed), so the transcript can list them exactly like the rewind dialog did.</summary>
        public List<CheckpointFileChange> Applied { get; } = new List<CheckpointFileChange>();
        public string? SafetySha { get; set; }
        public string? Error { get; set; }

        public int RestoredCount => Restored.Count;
        public int DeletedCount => Deleted.Count;
        /// <summary>Everything that actually changed on disk — named in the transcript note and in
        /// the hint the next prompt carries, so both the user and Claude know what moved.</summary>
        public IEnumerable<string> Touched => Restored.Concat(Deleted);
    }

    /// <summary>
    /// What a snapshot is allowed to contain (from the settings). Applied in two places: as ignore
    /// patterns in the shadow repo's <c>info/exclude</c>, so untracked files never get added, and as a
    /// per-blob decision on the freshly written tree, which also catches files that were tracked
    /// before the filter changed.
    /// </summary>
    internal sealed class CheckpointFilter
    {
        /// <summary>0 = no limit.</summary>
        public long MaxFileBytes { get; set; }
        /// <summary>false = the extensions are skipped, true = only they are included.</summary>
        public bool Whitelist { get; set; }
        /// <summary>Lowercase, with a leading dot.</summary>
        public IReadOnlyList<string> Extensions { get; set; } = new string[0];

        public bool ShouldDrop(string path, long size)
        {
            if (MaxFileBytes > 0 && size > MaxFileBytes)
                return true;
            if (Extensions.Count == 0)
                return false;
            var ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            var listed = Extensions.Contains(ext);
            return Whitelist ? !listed : listed;
        }

        /// <summary>The ignore rules for <c>info/exclude</c>. A whitelist needs the standard
        /// "exclude everything, keep walking directories, re-include these" idiom.</summary>
        public IEnumerable<string> BuildIgnoreLines()
        {
            if (Extensions.Count == 0)
                yield break;
            if (Whitelist)
            {
                yield return "# whitelist: only these extensions are snapshotted";
                yield return "*";
                yield return "!*/";
                foreach (var ext in Extensions)
                    yield return "!*" + ext;
            }
            else
            {
                yield return "# blacklist: these extensions are skipped";
                foreach (var ext in Extensions)
                    yield return "*" + ext;
            }
        }
    }

    /// <summary>
    /// File checkpoints for the workspace, kept in a <em>shadow repo</em>: a git dir under
    /// <c>%LocalAppData%\CodeAstrogator\Checkpoints\&lt;hash&gt;\.git</c> whose work-tree points at the
    /// solution directory. The user's own <c>.git</c> is never touched (git always excludes a
    /// directory named <c>.git</c> from <c>add -A</c>), and the project's <c>.gitignore</c> files apply
    /// automatically.
    ///
    /// Snapshots are parentless commits behind <c>refs/ca-checkpoints/&lt;session&gt;/&lt;n&gt;-pre|-post</c>.
    /// Taking one before AND after every turn makes "what did Claude change in this turn" a
    /// tree-to-tree diff, which covers bash commands, scripts and subagents — not just the
    /// Write/Edit tools (see docs/git-checkpoints-plan.md).
    ///
    /// Every method is best-effort: git failures are returned as text, never thrown, so a broken
    /// checkpoint can't take a turn down with it.
    /// </summary>
    internal sealed class GitCheckpointService
    {
        public const string RefRoot = "refs/ca-checkpoints";
        private const int DefaultTimeoutMs = 60_000;
        private const string OversizedMarker = "# --- oversized files (auto-generated) ---";
        private const string FilterMarker = "# --- extension filter (from the settings) ---";
        /// <summary>Commit-message line with the exact UTC ticks a snapshot read the work-tree at (the commit
        /// date has whole seconds only). Only the subject is ever shown, so the trailer stays invisible.</summary>
        private const string TakenTrailer = "ca-taken: ";

        /// <summary>
        /// What may go into a snapshot (size limit + extension black/whitelist), assigned from the
        /// settings by the bridge. Checkpoints exist to recover source code, and one snapshot of a game
        /// project can otherwise cost gigabytes: a real-world Unity workspace produced 2.1 GiB from
        /// five snapshots, almost all of it a 1.1 GB profiler capture plus committed DLLs.
        /// </summary>
        public CheckpointFilter Filter { get; set; } = new CheckpointFilter
        {
            MaxFileBytes = 10L * 1024 * 1024,
        };
        /// <summary>Keeps a single `git checkout`/`diff` command line well under the Windows limit.</summary>
        private const int MaxPathspecChars = 6000;

        private static int _gitAvailable = -1; // -1 = not probed yet, 0 = no, 1 = yes
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _initialized
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// <c>-c</c> overrides for every git call except <c>checkout</c> (see RunAsync): the project's Git LFS clean
        /// filter must not run in the shadow repo.
        /// It would put every LFS-tracked file into <c>.git/lfs/objects</c> — uncompressed, outside git's object
        /// store, so <c>gc</c> never reclaimed it (one Unity workspace: 12 GB there vs. 89 MB of git objects) — and
        /// leave only a 130-byte pointer in the tree, which the size limit then never saw. With it off the file is
        /// an ordinary (compressed, prunable, size-checked) blob. <c>checkout</c> keeps the filters, so a rewind
        /// to an older pointer snapshot still gets the real content back (smudge passes plain content through).
        /// </summary>
        /// <summary>Per-command timeout of the first snapshot of a workspace (see WriteCurrentTreeAsync).</summary>
        private const int FirstSnapshotTimeoutMs = 300_000;

        /// <summary><c>info/exclude</c> text the "untrack now-ignored paths" check last ran against, per workspace.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _checkedExcludeRules
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] NoLfsClean =
        {
            "-c", "filter.lfs.clean=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
        };

        // ── discovery / paths ────────────────────────────────────────────────

        /// <summary>Whether a usable <c>git</c> is on PATH (probed once, then cached).</summary>
        public static bool IsGitAvailable()
        {
            if (_gitAvailable >= 0)
                return _gitAvailable == 1;
            var ok = false;
            try
            {
                // Off the caller's thread: the probe may run on the UI thread (settings window) and
                // must not depend on its synchronization context to pump the process wait. Blocking is
                // intentional and safe here — the work runs on the thread pool with no UI affinity.
#pragma warning disable VSTHRD002
                var result = Task.Run(() => RunAsync(null, null, new[] { "--version" }, timeoutMs: 10_000))
                    .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                ok = result.ExitCode == 0 && result.StdOut.IndexOf("git version", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { /* no git → feature stays off */ }
            Interlocked.Exchange(ref _gitAvailable, ok ? 1 : 0);
            return ok;
        }

        /// <summary>Base directory of the shadow repo for a solution (stable hash of its path).</summary>
        public static string GetRepoDir(string solutionDir)
        {
            var key = HashKey((solutionDir ?? "").TrimEnd('\\', '/').ToLowerInvariant());
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "Checkpoints", key);
        }

        private static string GetGitDir(string solutionDir) => Path.Combine(GetRepoDir(solutionDir), ".git");

        private static string GetIndexFile(string solutionDir) => Path.Combine(GetGitDir(solutionDir), "ca-index");

        /// <summary>Builds a valid ref name for a snapshot: <c>refs/ca-checkpoints/&lt;session&gt;/&lt;name&gt;</c>.</summary>
        public static string BuildRefName(string sessionId, string name) =>
            RefRoot + "/" + SanitizeRefPart(sessionId) + "/" + SanitizeRefPart(name);

        /// <summary>All refs of one session (prefix for for-each-ref / prune).</summary>
        public static string SessionRefPrefix(string sessionId) => RefRoot + "/" + SanitizeRefPart(sessionId);

        internal static string SanitizeRefPart(string value)
        {
            var s = (value ?? "").Trim();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.';
                sb.Append(ok ? c : '-');
            }
            var result = sb.ToString().Trim('.', '-');
            if (result.Length == 0)
                result = "s";
            if (result.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - 5) + "-lock";
            return result.Replace("..", "-");
        }

        // ── init ────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the shadow repo on first use: a bare git dir (with <c>core.bare=false</c> so the
        /// <c>--work-tree</c> we pass on every call is accepted), a local identity so commits work
        /// without a global git config, no reflogs (so a pruned ref really releases its objects),
        /// and — when the project has no <c>.gitignore</c> — a default exclude list so snapshots
        /// don't swallow bin/obj/node_modules.
        /// </summary>
        public async Task<string?> EnsureInitializedAsync(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
                return "No workspace directory.";
            if (!IsGitAvailable())
                return "Git was not found on PATH.";
            // The HEAD check keeps the cache honest: a retention sweep may have deleted the repo
            // behind our back, in which case we simply re-create it.
            if (_initialized.ContainsKey(solutionDir) && File.Exists(Path.Combine(GetGitDir(solutionDir), "HEAD")))
                return null;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var gitDir = GetGitDir(solutionDir);
                if (_initialized.ContainsKey(solutionDir) && File.Exists(Path.Combine(gitDir, "HEAD")))
                    return null;
                if (!File.Exists(Path.Combine(gitDir, "HEAD")))
                {
                    Directory.CreateDirectory(GetRepoDir(solutionDir));
                    var init = await RunAsync(null, null, new[] { "init", "--bare", "--quiet", Q(gitDir) }).ConfigureAwait(false);
                    if (init.ExitCode != 0)
                        return Describe("git init", init);

                    foreach (var cfg in new[]
                    {
                        new[] { "core.bare", "false" },
                        new[] { "core.logAllRefUpdates", "false" },
                        new[] { "core.autocrlf", "false" },
                        new[] { "core.safecrlf", "false" },
                        new[] { "core.fsmonitor", "false" },
                        // Let git pack loose objects once there are enough of them — without this a
                        // busy workspace ends up with tens of thousands of loose files, which is both
                        // wasteful on disk and slow for anything that walks the directory.
                        new[] { "gc.auto", "256" },
                        // The first snapshot of an oversized file still writes its blob before the
                        // path is excluded; a short prune window makes that orphan disappear instead
                        // of sitting there for git's default two weeks. Safely above any in-flight
                        // write of our own.
                        new[] { "gc.pruneExpire", "1.hour.ago" },
                        new[] { "commit.gpgsign", "false" },
                        new[] { "tag.gpgsign", "false" },
                        new[] { "user.name", "Code Astrogator" },
                        new[] { "user.email", "checkpoints@codeastrogator.local" },
                    })
                    {
                        // quoted: an unquoted "Code Astrogator" set user.name to "Code"
                        await RunAsync(gitDir, null, new[] { "config", cfg[0], Q(cfg[1]) }).ConfigureAwait(false);
                    }

                    WriteMeta(solutionDir);
                    WriteExcludeFile(solutionDir, gitDir, new List<string>());
                }

                _initialized[solutionDir] = true;
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private static void WriteMeta(string solutionDir)
        {
            try
            {
                var meta = "{\n  \"workspace\": " + ToJsonString(solutionDir)
                    + ",\n  \"created\": " + ToJsonString(DateTime.UtcNow.ToString("o")) + "\n}\n";
                File.WriteAllText(Path.Combine(GetRepoDir(solutionDir), "meta.json"), meta);
            }
            catch { /* diagnostics only */ }
        }

        /// <summary>
        /// Writes the shadow repo's <c>info/exclude</c>: a default ignore list when the project has no
        /// <c>.gitignore</c> of its own (otherwise a snapshot would swallow bin/obj/node_modules), plus
        /// the auto-generated block of oversized paths. Both parts are rewritten together, so a file
        /// that shrank below the limit simply drops out of the list.
        /// </summary>
        private void WriteExcludeFile(string solutionDir, string gitDir, IEnumerable<string> oversized)
        {
            try
            {
                var lines = new List<string>();
                if (!File.Exists(Path.Combine(solutionDir, ".gitignore")))
                {
                    lines.AddRange(new[]
                    {
                        "# Code Astrogator checkpoint defaults (the project has no .gitignore)",
                        "bin/", "obj/", ".vs/", ".vscode/", "node_modules/", "packages/",
                        "*.user", "*.suo", "*.dll", "*.pdb", "*.exe", "*.cache",
                        "TestResults/", "artifacts/", "dist/", "build/", "target/",
                        "__pycache__/", ".venv/", "venv/", ".mypy_cache/", ".pytest_cache/",
                        "",
                    });
                }
                var filterLines = Filter.BuildIgnoreLines().ToList();
                if (filterLines.Count > 0)
                {
                    lines.Add(FilterMarker);
                    lines.AddRange(filterLines);
                    lines.Add("");
                }
                // Literal oversized paths come LAST: in whitelist mode the block above re-includes
                // whole extensions, and a later rule wins.
                var paths = oversized.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count > 0)
                {
                    lines.Add(OversizedMarker);
                    lines.Add("# over the configured size limit — left out of snapshots");
                    lines.AddRange(paths.Select(EscapeIgnorePattern));
                    lines.Add("");
                }
                var infoDir = Path.Combine(gitDir, "info");
                Directory.CreateDirectory(infoDir);
                File.WriteAllText(Path.Combine(infoDir, "exclude"), string.Join("\n", lines));
            }
            catch { /* best-effort */ }
        }

        /// <summary>Reads back the auto-generated oversized block, so the list survives a restart.</summary>
        private static List<string> ReadOversizedPaths(string gitDir)
        {
            var result = new List<string>();
            try
            {
                var path = Path.Combine(gitDir, "info", "exclude");
                if (!File.Exists(path))
                    return result;
                var inBlock = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    if (raw.StartsWith(OversizedMarker, StringComparison.Ordinal))
                    {
                        inBlock = true;
                        continue;
                    }
                    if (!inBlock)
                        continue;
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    result.Add(UnescapeIgnorePattern(line));
                }
            }
            catch { /* treat as empty */ }
            return result;
        }

        /// <summary>Anchors a literal path as a gitignore pattern and escapes the glob characters.</summary>
        private static string EscapeIgnorePattern(string path)
        {
            var sb = new StringBuilder("/");
            foreach (var c in path)
            {
                if (c == '*' || c == '?' || c == '[' || c == ']' || c == '\\' || c == '!' || c == '#')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string UnescapeIgnorePattern(string pattern)
        {
            var sb = new StringBuilder();
            for (var i = pattern[0] == '/' ? 1 : 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\\' && i + 1 < pattern.Length)
                    i++;
                sb.Append(pattern[i]);
            }
            return sb.ToString();
        }

        // ── snapshots ───────────────────────────────────────────────────────

        /// <summary>
        /// Snapshots the whole work-tree and points <paramref name="refName"/> at it. Uses our own
        /// index file, so the repo's default index (and the user's repo) stay untouched.
        /// Returns null when git failed — the caller treats that as "no checkpoint for this turn".
        /// </summary>
        public async Task<CheckpointInfo?> SnapshotAsync(string solutionDir, string refName, string label,
            int timeoutMs = DefaultTimeoutMs)
        {
            if (await EnsureInitializedAsync(solutionDir).ConfigureAwait(false) != null)
                return null;
            var gitDir = GetGitDir(solutionDir);
            var takenUtc = DateTime.UtcNow; // before the work-tree is read — see DropFilterArtifactsAsync
            var tree = await WriteCurrentTreeAsync(solutionDir, timeoutMs).ConfigureAwait(false);
            if (tree == null)
                return null;

            var commit = await RunAsync(gitDir, solutionDir,
                new[] { "commit-tree", tree!, "-m", Q(label ?? ""), "-m", Q(TakenTrailer + takenUtc.Ticks) },
                GetIndexFile(solutionDir), timeoutMs: timeoutMs).ConfigureAwait(false);
            var sha = commit.StdOut.Trim();
            if (commit.ExitCode != 0 || sha.Length < 40)
                return null;

            var update = await RunAsync(gitDir, solutionDir,
                new[] { "update-ref", Q(refName), sha }, GetIndexFile(solutionDir), timeoutMs: timeoutMs)
                .ConfigureAwait(false);
            if (update.ExitCode != 0)
                return null;

            return new CheckpointInfo
            {
                Sha = sha,
                RefName = refName,
                CreatedUtc = DateTime.UtcNow,
                Label = label ?? "",
            };
        }

        /// <summary>Stages the work-tree into our private index and writes it out as a tree object.
        /// Also used for previews: a tree needs no ref, so an unused one is just gc fodder.</summary>
        private async Task<string?> WriteCurrentTreeAsync(string solutionDir, int timeoutMs)
        {
            var gitDir = GetGitDir(solutionDir);
            var index = GetIndexFile(solutionDir);
            await MigrateLfsIndexOnceAsync(gitDir, index).ConfigureAwait(false);
            // No index yet = the first snapshot of this workspace (or the one after the LFS migration / "Delete
            // all"): every file gets hashed and written once. In a Unity project that is ~60 000 files and well
            // over the per-command timeout of a normal snapshot — which then failed and, with no ref, left all
            // objects loose and unpacked for the next try. Give that one a long timeout.
            if (!File.Exists(index))
                timeoutMs = Math.Max(timeoutMs, FirstSnapshotTimeoutMs);
            // Rewrite the ignore rules first: the filter can have changed since the last snapshot,
            // and they have to be in place before `add -A` picks up untracked files.
            WriteExcludeFile(solutionDir, gitDir, ReadOversizedPaths(gitDir));
            // An ignore rule doesn't untrack a path our index already has (e.g. after an extension was added to
            // the blacklist) — drop those first, so `add` doesn't even read them again. Only when the rules
            // changed since the last check: on 60 000 tracked paths this costs ~3 s.
            var rules = ReadExcludeRules(gitDir);
            if (File.Exists(index) && (!_checkedExcludeRules.TryGetValue(solutionDir, out var known) || known != rules))
            {
                var stale = await RunAsync(gitDir, solutionDir,
                    new[] { "ls-files", "--cached", "--ignored", "--exclude-standard" }, index, timeoutMs: timeoutMs)
                    .ConfigureAwait(false);
                foreach (var chunk in ChunkPaths(SplitLines(stale.StdOut)))
                {
                    if (chunk.Count == 0)
                        continue;
                    await RunAsync(gitDir, solutionDir,
                        Args(new[] { "update-index", "--force-remove", "--" }, chunk), index, timeoutMs: timeoutMs)
                        .ConfigureAwait(false);
                }
                if (stale.ExitCode == 0)
                    _checkedExcludeRules[solutionDir] = rules;
            }
            // A locked/unreadable file must not abort the whole snapshot → --ignore-errors, and its
            // non-zero exit code is deliberately not treated as fatal (write-tree decides).
            await RunAsync(gitDir, solutionDir, new[] { "add", "-A", "--ignore-errors" }, index, timeoutMs: timeoutMs)
                .ConfigureAwait(false);
            var tree = await RunAsync(gitDir, solutionDir, new[] { "write-tree" }, index, timeoutMs: timeoutMs)
                .ConfigureAwait(false);
            var sha = tree.StdOut.Trim();
            if (tree.ExitCode != 0 || sha.Length < 40)
                return null;
            return await ApplyFilterAsync(solutionDir, sha, timeoutMs).ConfigureAwait(false);
        }

        /// <summary>The current <c>info/exclude</c> text — what the "untrack now-ignored paths" check depends on
        /// (filter settings, oversized paths). A changed project <c>.gitignore</c> is picked up on the next VS
        /// start, when the check runs once anyway.</summary>
        private static string ReadExcludeRules(string gitDir)
        {
            try
            {
                var path = Path.Combine(gitDir, "info", "exclude");
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch
            {
                return Guid.NewGuid().ToString(); // unreadable → check again
            }
        }

        /// <summary>
        /// One-time step for a shadow repo whose snapshots went through the LFS clean filter (it has a
        /// <c>lfs</c> store): our index still holds LFS pointers, and git keeps an index entry whose file is
        /// unchanged on disk — so without this every unchanged LFS file would stay a pointer into the old store
        /// forever. Dropping the private index makes the next <c>add</c> hash the real content once (slower
        /// one time); the old store then empties as the old snapshots expire (<see cref="PruneLfsStoreAsync"/>).
        /// Marked done in the repo config (<c>ca.lfsMigrated</c>).
        /// </summary>
        private static async Task MigrateLfsIndexOnceAsync(string gitDir, string index)
        {
            if (!Directory.Exists(Path.Combine(gitDir, "lfs")))
                return;
            var done = await RunAsync(gitDir, null, new[] { "config", "--get", "ca.lfsMigrated" }).ConfigureAwait(false);
            if (done.StdOut.Trim() == "true")
                return;
            try
            {
                if (File.Exists(index))
                    File.Delete(index);
            }
            catch
            {
                return; // try again next time
            }
            await RunAsync(gitDir, null, new[] { "config", "ca.lfsMigrated", "true" }).ConfigureAwait(false);
        }

        /// <summary>
        /// Enforces <see cref="Filter"/> on the freshly written tree. The tree already knows every
        /// blob's size (<c>ls-tree -rl</c>), so this costs one cheap call instead of a directory walk —
        /// and it catches paths that were tracked before the filter changed, which an ignore rule alone
        /// cannot: a path already in the index stays tracked. Dropped paths are removed from our index
        /// (size-based ones additionally recorded in <c>info/exclude</c>, since no pattern covers them),
        /// then the tree is written again.
        /// </summary>
        private async Task<string?> ApplyFilterAsync(string solutionDir, string treeSha, int timeoutMs)
        {
            var gitDir = GetGitDir(solutionDir);
            var index = GetIndexFile(solutionDir);
            var listed = await RunAsync(gitDir, solutionDir, new[] { "ls-tree", "-rl", treeSha }, index,
                timeoutMs: timeoutMs).ConfigureAwait(false);
            if (listed.ExitCode != 0)
                return treeSha;

            var dropped = new List<string>();
            var oversized = new List<string>();
            foreach (var line in SplitLines(listed.StdOut))
            {
                // "<mode> SP <type> SP <sha> SP+ <size> TAB <path>"; size is "-" for non-blobs.
                var tab = line.IndexOf('\t');
                if (tab < 0)
                    continue;
                var fields = line.Substring(0, tab).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4 || fields[1] != "blob")
                    continue;
                if (!long.TryParse(fields[3], out var size))
                    continue;
                var path = line.Substring(tab + 1).Trim();
                if (!Filter.ShouldDrop(path, size))
                    continue;
                dropped.Add(path);
                if (Filter.MaxFileBytes > 0 && size > Filter.MaxFileBytes)
                    oversized.Add(path); // no ignore pattern covers a size, so remember the path
            }
            if (dropped.Count == 0)
                return treeSha;

            if (oversized.Count > 0)
            {
                var known = ReadOversizedPaths(gitDir);
                known.AddRange(oversized);
                WriteExcludeFile(solutionDir, gitDir, known);
            }

            foreach (var chunk in ChunkPaths(dropped))
            {
                if (chunk.Count == 0)
                    continue;
                await RunAsync(gitDir, solutionDir,
                    Args(new[] { "update-index", "--force-remove", "--" }, chunk), index, timeoutMs: timeoutMs)
                    .ConfigureAwait(false);
            }

            var pruned = await RunAsync(gitDir, solutionDir, new[] { "write-tree" }, index, timeoutMs: timeoutMs)
                .ConfigureAwait(false);
            var prunedSha = pruned.StdOut.Trim();
            return pruned.ExitCode == 0 && prunedSha.Length >= 40 ? prunedSha : treeSha;
        }

        /// <summary>True when the next snapshot has to read the whole work-tree (no private index yet — first
        /// checkpoint of the workspace, or after "Delete all checkpoints"); the UI says it may take a while.</summary>
        public static bool IsFirstSnapshot(string solutionDir) =>
            !string.IsNullOrEmpty(solutionDir) && !File.Exists(GetIndexFile(solutionDir));

        /// <summary>Lets git pack loose objects when enough have piled up (no-op below the threshold).
        /// Fire-and-forget after a turn — <c>gc.autoDetach</c> keeps it out of our way.</summary>
        public async Task MaintainAsync(string solutionDir)
        {
            if (!IsGitAvailable() || !File.Exists(Path.Combine(GetGitDir(solutionDir), "HEAD")))
                return;
            await RunAsync(GetGitDir(solutionDir), null, new[] { "gc", "--auto", "--quiet" }, timeoutMs: 300_000)
                .ConfigureAwait(false);
        }

        /// <summary>True when the object still exists (a pruned checkpoint reports false).</summary>
        public async Task<ISet<string>> FilterExistingAsync(string solutionDir, IEnumerable<string> shas)
        {
            var wanted = (shas ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0 || !IsGitAvailable() || !File.Exists(Path.Combine(GetGitDir(solutionDir), "HEAD")))
                return found;

            // --ignore-missing makes rev-list print exactly the commits that are still there and stay
            // silent about the pruned ones, so one call per chunk answers the whole question.
            foreach (var chunk in ChunkPaths(wanted))
            {
                var result = await RunAsync(GetGitDir(solutionDir), null,
                    Args(new[] { "rev-list", "--no-walk", "--ignore-missing" }, chunk)).ConfigureAwait(false);
                foreach (var line in SplitLines(result.StdOut))
                    found.Add(line.Trim());
            }
            return found;
        }

        /// <summary>Snapshots of one session, newest first (the UI shows at most 100, like the CLI).</summary>
        public async Task<IReadOnlyList<CheckpointInfo>> ListAsync(string solutionDir, string sessionId, int max = 100)
        {
            var list = new List<CheckpointInfo>();
            if (!IsGitAvailable() || !File.Exists(Path.Combine(GetGitDir(solutionDir), "HEAD")))
                return list;
            var result = await RunAsync(GetGitDir(solutionDir), null, new[]
            {
                "for-each-ref", "--sort=-committerdate",
                "--format=%(objectname)%09%(refname)%09%(committerdate:unix)%09%(contents:subject)",
                Q(SessionRefPrefix(sessionId)),
            }).ConfigureAwait(false);
            foreach (var line in SplitLines(result.StdOut))
            {
                var f = line.Split('\t');
                if (f.Length < 3)
                    continue;
                list.Add(new CheckpointInfo
                {
                    Sha = f[0],
                    RefName = f[1],
                    CreatedUtc = FromUnix(f[2]),
                    Label = f.Length > 3 ? f[3] : "",
                });
                if (list.Count >= max)
                    break;
            }
            return list;
        }

        // ── diff ────────────────────────────────────────────────────────────

        /// <summary>
        /// Changed files between two tree-ish objects. <paramref name="toSha"/> null = the current
        /// work-tree (snapshotted into a throw-away tree first, so the comparison never depends on
        /// the state of a git index). <paramref name="paths"/> null = the whole work-tree.
        /// </summary>
        public async Task<IReadOnlyList<CheckpointFileChange>> DiffAsync(
            string solutionDir, string fromSha, string? toSha, IReadOnlyList<string>? paths)
        {
            var changes = new Dictionary<string, CheckpointFileChange>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fromSha) || await EnsureInitializedAsync(solutionDir).ConfigureAwait(false) != null)
                return new List<CheckpointFileChange>();

            var to = toSha;
            if (string.IsNullOrEmpty(to))
                to = await WriteCurrentTreeAsync(solutionDir, DefaultTimeoutMs).ConfigureAwait(false);
            if (string.IsNullOrEmpty(to))
                return new List<CheckpointFileChange>();
            if (paths != null && paths.Count == 0)
                return new List<CheckpointFileChange>();

            var gitDir = GetGitDir(solutionDir);
            foreach (var chunk in ChunkPaths(paths))
            {
                // --no-renames keeps a rename an add + a delete, which is exactly how a rewind has to
                // treat it (write the old path back, remove the new one).
                var numstat = await RunAsync(gitDir, solutionDir,
                    Args(new[] { "diff", "--numstat", "--no-renames", fromSha, to!, "--" }, chunk),
                    GetIndexFile(solutionDir)).ConfigureAwait(false);
                foreach (var line in SplitLines(numstat.StdOut))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3)
                        continue;
                    var path = f[2].Trim();
                    if (path.Length == 0)
                        continue;
                    var binary = f[0] == "-";
                    changes[path] = new CheckpointFileChange
                    {
                        Path = path,
                        Binary = binary,
                        Added = binary ? 0 : ParseInt(f[0]),
                        Removed = binary ? 0 : ParseInt(f[1]),
                    };
                }

                var status = await RunAsync(gitDir, solutionDir,
                    Args(new[] { "diff", "--name-status", "--no-renames", fromSha, to!, "--" }, chunk),
                    GetIndexFile(solutionDir)).ConfigureAwait(false);
                foreach (var line in SplitLines(status.StdOut))
                {
                    var f = line.Split('\t');
                    if (f.Length < 2)
                        continue;
                    var path = f[f.Length - 1].Trim();
                    if (changes.TryGetValue(path, out var change))
                        change.Status = f[0].Substring(0, 1).ToUpperInvariant();
                }
            }

            var list = changes.Values.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
            // against the present work-tree (the rewind preview): what the filter left out is no change
            return string.IsNullOrEmpty(toSha)
                ? await DropFilterArtifactsAsync(solutionDir, fromSha, list).ConfigureAwait(false)
                : list;
        }

        /// <summary>
        /// For a diff from a checkpoint to the <b>present</b> work-tree: drops entries that only exist because
        /// one of the two snapshots left a file out (LFS, extension, size — or a filter that changed in between),
        /// not because it changed. "D" (in the checkpoint, not in the present tree) while the file is still on
        /// disk = the present filter skips it; "A" (in the present tree, not in the checkpoint) while the file is
        /// older than the checkpoint = it existed back then and was skipped. A rewind acting on those would
        /// rewrite or delete files the checkpoint never covered — after LFS files started being skipped, every
        /// asset of a Unity project would have counted as "deleted since". Matches the settings' promise that
        /// excluded files are never restored by a rewind.
        /// </summary>
        private async Task<List<CheckpointFileChange>> DropFilterArtifactsAsync(string solutionDir, string fromSha,
            List<CheckpointFileChange> changes)
        {
            if (!changes.Any(c => c.Status == "D" || c.Status == "A"))
                return changes;
            // the exact time the snapshot read the work-tree (trailer), else the commit's whole second + 1
            var shown = await RunAsync(GetGitDir(solutionDir), null,
                new[] { "log", "-1", "--no-walk", "--format=%ct%n%b", fromSha }).ConfigureAwait(false);
            var taken = DateTime.MinValue;
            if (shown.ExitCode == 0)
            {
                var lines = SplitLines(shown.StdOut);
                var trailer = lines.FirstOrDefault(l => l.StartsWith(TakenTrailer, StringComparison.Ordinal));
                if (trailer != null && long.TryParse(trailer.Substring(TakenTrailer.Length).Trim(), out var ticks)
                    && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                    taken = new DateTime(ticks, DateTimeKind.Utc);
                else if (lines.Count > 0 && FromUnix(lines[0]) is var seconds && seconds != DateTime.MinValue)
                    taken = seconds.AddSeconds(1); // whole seconds — when in doubt keep the file
            }
            var kept = new List<CheckpointFileChange>();
            foreach (var change in changes)
            {
                var full = Path.Combine(solutionDir, change.Path.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (change.Status == "D" && File.Exists(full))
                        continue;
                    // when in doubt keep the file: a leftover is harmless, deleting an asset the checkpoint
                    // never had is not
                    if (change.Status == "A" && taken != DateTime.MinValue && File.Exists(full)
                        && File.GetCreationTimeUtc(full) < taken)
                        continue;
                }
                catch { /* can't tell → keep it */ }
                kept.Add(change);
            }
            return kept;
        }

        // ── restore ─────────────────────────────────────────────────────────

        /// <summary>
        /// Rewinds the work-tree to <paramref name="sha"/>. Non-destructive: the current state is
        /// snapshotted to <paramref name="safetyRefName"/> first, so a rewind can itself be undone.
        /// Files created after the snapshot are deleted, modified/deleted ones written back.
        /// Symlinks, hard links and unreadable paths are skipped and reported, never written through.
        /// <paramref name="paths"/> null = everything that changed.
        /// </summary>
        public async Task<RestoreResult> RestoreAsync(string solutionDir, string sha,
            IReadOnlyList<string>? paths, string safetyRefName)
        {
            var result = new RestoreResult();
            var initError = await EnsureInitializedAsync(solutionDir).ConfigureAwait(false);
            if (initError != null)
            {
                result.Error = initError;
                return result;
            }

            var safety = await SnapshotAsync(solutionDir, safetyRefName, "auto: before rewind to " + Short(sha))
                .ConfigureAwait(false);
            if (safety == null)
            {
                result.Error = "Could not snapshot the current state — nothing was changed.";
                return result;
            }
            result.SafetySha = safety.Sha;

            var changes = await DiffAsync(solutionDir, sha, safety.Sha, paths).ConfigureAwait(false);
            // the safety snapshot IS the present work-tree
            changes = await DropFilterArtifactsAsync(solutionDir, sha, changes.ToList()).ConfigureAwait(false);
            if (changes.Count == 0)
                return result;

            var gitDir = GetGitDir(solutionDir);
            var index = GetIndexFile(solutionDir);
            var toWrite = new List<string>();   // present in the snapshot → check it back out
            var toDelete = new List<string>();  // created afterwards → remove it

            foreach (var change in changes)
            {
                var full = Path.Combine(solutionDir, change.Path.Replace('/', Path.DirectorySeparatorChar));
                if (IsLinkOrIrregular(full))
                {
                    result.Skipped.Add(change.Path);
                    continue;
                }
                if (change.Status == "A")
                    toDelete.Add(change.Path);
                else
                    toWrite.Add(change.Path);
            }

            foreach (var chunk in ChunkPaths(toWrite))
            {
                if (chunk.Count == 0)
                    continue;
                var checkout = await RunAsync(gitDir, solutionDir,
                    Args(new[] { "checkout", sha, "--" }, chunk), index).ConfigureAwait(false);
                if (checkout.ExitCode != 0)
                    result.Error = Describe("git checkout", checkout);
                else
                    result.Restored.AddRange(chunk);
            }

            foreach (var rel in toDelete)
            {
                var full = Path.Combine(solutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(full))
                    {
                        var attrs = File.GetAttributes(full);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
                        File.Delete(full);
                    }
                    result.Deleted.Add(rel);
                }
                catch
                {
                    result.Skipped.Add(rel); // locked / in use — leave it alone
                }
            }

            // Carry the diff entries of everything that moved, for the transcript's file list.
            var byPath = new Dictionary<string, CheckpointFileChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
                byPath[change.Path] = change;
            foreach (var path in result.Restored.Concat(result.Deleted))
            {
                if (byPath.TryGetValue(path, out var change))
                    result.Applied.Add(change);
            }

            return result;
        }

        // ── retention ───────────────────────────────────────────────────────

        /// <summary>
        /// Deletes snapshots older than <paramref name="retentionDays"/> and reclaims their objects
        /// (<c>gc --prune=now</c>). <c>0</c> or less = keep forever (no-op). Returns how many
        /// snapshots were removed.
        /// </summary>
        public Task<int> PruneAsync(string solutionDir, int retentionDays) =>
            PruneRepoAsync(GetRepoDir(solutionDir), retentionDays);

        /// <summary>Prunes every workspace's checkpoint repo (the startup / settings-save sweep, which
        /// doesn't know which workspaces exist). Returns how many snapshots were removed in total.</summary>
        public static async Task<int> PruneAllAsync(int retentionDays)
        {
            if (retentionDays <= 0 || !IsGitAvailable())
                return 0;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "Checkpoints");
            if (!Directory.Exists(root))
                return 0;
            var total = 0;
            foreach (var repoDir in Directory.EnumerateDirectories(root))
            {
                try { total += await PruneRepoAsync(repoDir, retentionDays).ConfigureAwait(false); }
                catch { /* one broken repo must not stop the sweep */ }
            }
            return total;
        }

        private static async Task<int> PruneRepoAsync(string repoDir, int retentionDays)
        {
            if (retentionDays <= 0 || !IsGitAvailable())
                return 0;
            var gitDir = Path.Combine(repoDir, ".git");
            if (!File.Exists(Path.Combine(gitDir, "HEAD")))
                return 0;

            var listed = await RunAsync(gitDir, null, new[]
            {
                "for-each-ref", "--format=%(refname)%09%(committerdate:unix)", RefRoot,
            }).ConfigureAwait(false);

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var stale = new List<string>();
            foreach (var line in SplitLines(listed.StdOut))
            {
                var f = line.Split('\t');
                if (f.Length < 2)
                    continue;
                if (FromUnix(f[1]) < cutoff)
                    stale.Add(f[0]);
            }
            if (stale.Count == 0)
            {
                await PruneLfsStoreAsync(gitDir).ConfigureAwait(false);
                return 0;
            }

            var removed = 0;
            foreach (var refName in stale)
            {
                var deleted = await RunAsync(gitDir, null, new[] { "update-ref", "-d", Q(refName) })
                    .ConfigureAwait(false);
                if (deleted.ExitCode == 0)
                    removed++;
            }
            if (removed == 0)
                return 0;

            await RunAsync(gitDir, null, new[] { "gc", "--prune=now", "--quiet" }, timeoutMs: 120_000)
                .ConfigureAwait(false);

            // Nothing left → drop the repo entirely; the next prompt re-initializes it.
            var remaining = await RunAsync(gitDir, null, new[] { "for-each-ref", "--format=%(refname)", RefRoot })
                .ConfigureAwait(false);
            if (SplitLines(remaining.StdOut).Count == 0)
                DeleteDirectoryForce(repoDir);
            else
                await PruneLfsStoreAsync(gitDir).ConfigureAwait(false);

            return removed;
        }

        /// <summary>
        /// Deletes the Git LFS objects (<c>.git/lfs/objects</c>) no remaining snapshot points to. Before the clean
        /// filter was switched off (<see cref="NoLfsClean"/>), snapshots stored LFS-tracked files there — and that
        /// store is not git's: <c>gc</c> never reclaimed it, so pruned checkpoints left their LFS content behind
        /// for good (3 GB of 12 GB in one workspace). Referenced = an LFS pointer blob (<c>oid sha256:…</c>) among
        /// the repo's objects after <c>gc --prune=now</c>. Everything unreferenced goes; an empty store goes
        /// entirely. Best-effort: on any git failure nothing is deleted.
        /// </summary>
        internal static async Task PruneLfsStoreAsync(string gitDir)
        {
            try
            {
                var store = Path.Combine(gitDir, "lfs");
                var objects = Path.Combine(store, "objects");
                if (!Directory.Exists(objects))
                    return;

                // pointer files are ~130 bytes — only small blobs can be one
                var check = await RunAsync(gitDir, null, new[]
                {
                    "cat-file", "--batch-all-objects", "--batch-check=\"%(objectname) %(objecttype) %(objectsize)\"",
                }, timeoutMs: 300_000).ConfigureAwait(false);
                if (check.ExitCode != 0)
                    return;
                var small = new StringBuilder();
                foreach (var line in SplitLines(check.StdOut))
                {
                    var f = line.Split(' ');
                    if (f.Length == 3 && f[1] == "blob" && long.TryParse(f[2], out var size) && size < 1024)
                        small.Append(f[0]).Append('\n');
                }

                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (small.Length > 0)
                {
                    var contents = await RunAsync(gitDir, null, new[] { "cat-file", "--batch" },
                        timeoutMs: 300_000, stdin: small.ToString()).ConfigureAwait(false);
                    if (contents.ExitCode != 0)
                        return;
                    foreach (var oid in ParseLfsPointerOids(contents.StdOut))
                        referenced.Add(oid);
                }

                foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).ToList())
                {
                    if (referenced.Contains(Path.GetFileName(file)))
                        continue;
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch { /* in use — next sweep */ }
                }
                if (referenced.Count == 0)
                    DeleteDirectoryForce(store); // incl. lfs/tmp, lfs/incomplete
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>The <c>oid sha256:&lt;64 hex&gt;</c> values of every LFS pointer in a <c>cat-file --batch</c> dump.</summary>
        internal static IEnumerable<string> ParseLfsPointerOids(string batchOutput)
        {
            foreach (var line in (batchOutput ?? "").Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("oid sha256:", StringComparison.Ordinal) && t.Length >= 11 + 64)
                {
                    var oid = t.Substring(11, 64);
                    if (oid.All(Uri.IsHexDigit))
                        yield return oid.ToLowerInvariant();
                }
            }
        }

        /// <summary>Bytes on disk for this workspace's checkpoints.</summary>
        public static long GetRepoSize(string solutionDir) => DirectorySize(GetRepoDir(solutionDir));

        /// <summary>Bytes on disk for every workspace's checkpoints (settings window display).</summary>
        public static long GetAllReposSize() => DirectorySize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeAstrogator", "Checkpoints"));

        private static long DirectorySize(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return 0;
                return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        /// <summary>Deletes every checkpoint of a workspace (settings window "Delete all", retention).</summary>
        public void DeleteRepo(string solutionDir)
        {
            _initialized.TryRemove(solutionDir, out _);
            DeleteDirectoryForce(GetRepoDir(solutionDir));
        }

        /// <summary>Deletes every checkpoint of every workspace (…\CodeAstrogator\Checkpoints).</summary>
        public static void DeleteAllRepos()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator", "Checkpoints");
            DeleteDirectoryForce(root);
        }

        /// <summary>Git keeps loose objects read-only, so a plain recursive delete fails.</summary>
        private static void DeleteDirectoryForce(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attrs = File.GetAttributes(file);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                    catch { /* skip */ }
                }
                Directory.Delete(dir, true);
            }
            catch { /* best-effort */ }
        }

        // ── link detection (a rewind must never write through a link) ────────

        /// <summary>True for a symlink/junction (or a file under one) and for a hard-linked file —
        /// mirrors the CLI's "skipped N files" behaviour instead of clobbering a linked target.</summary>
        internal static bool IsLinkOrIrregular(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                    return false; // nothing there to protect (created-after-snapshot case)
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    return true;
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)
                    && (File.GetAttributes(parent!) & FileAttributes.ReparsePoint) != 0)
                    return true;
                return HasMultipleHardLinks(fullPath);
            }
            catch
            {
                return false; // can't tell → treat it as a normal file
            }
        }

        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_SHARE_ALL = 0x00000007;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // Pack = 4 is essential: the native FILETIME members are two DWORDs each, so the default
        // 8-byte alignment of the long fields would shift every following member — NumberOfLinks would
        // read garbage and a rewind would "skip" every file as if it were hard-linked.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        private static bool HasMultipleHardLinks(string fullPath)
        {
            using (var handle = CreateFile(fullPath, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return false;
                return GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1;
            }
        }

        // ── git plumbing ────────────────────────────────────────────────────

        private sealed class GitResult
        {
            public int ExitCode = -1;
            public string StdOut = "";
            public string StdErr = "";
        }

        /// <summary>
        /// Runs one git command. <paramref name="gitDir"/>/<paramref name="workTree"/> map to
        /// <c>--git-dir</c>/<c>--work-tree</c>; <paramref name="indexFile"/> to GIT_INDEX_FILE, so we
        /// never disturb the repo's own index. Arguments must already be quoted (see <see cref="Q"/>).
        /// Everything is passed as arguments; only the LFS-store sweep feeds object ids via
        /// <paramref name="stdin"/> (<c>cat-file --batch</c>) — otherwise stdin is closed at once.
        /// </summary>
        private static async Task<GitResult> RunAsync(string? gitDir, string? workTree, IReadOnlyList<string> args,
            string? indexFile = null, int timeoutMs = DefaultTimeoutMs, string? stdin = null)
        {
            var all = new List<string> { "-c", "core.quotePath=false" };
            if (!string.IsNullOrEmpty(gitDir))
                all.Add("--git-dir=" + Q(gitDir!));
            if (!string.IsNullOrEmpty(workTree))
                all.Add("--work-tree=" + Q(workTree!));
            // Every command, not just `add`: one that writes the index re-hashes "racily clean" entries (file
            // mtime = index mtime) through the clean filter — `write-tree` right after `add` did, now and then.
            // `checkout` (rewind) is the exception: it needs the smudge side for older pointer snapshots.
            if (args.Count == 0 || args[0] != "checkout")
                all.AddRange(NoLfsClean);
            all.AddRange(args);

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", all),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = !string.IsNullOrEmpty(workTree) && Directory.Exists(workTree)
                    ? workTree!
                    : Path.GetTempPath(),
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            if (!string.IsNullOrEmpty(indexFile))
                psi.EnvironmentVariables["GIT_INDEX_FILE"] = indexFile;

            var result = new GitResult();
            using (var process = new Process { StartInfo = psi })
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var exited = new TaskCompletionSource<int>();
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data).Append('\n'); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Append(e.Data).Append('\n'); };
                process.Exited += (_, __) => exited.TrySetResult(SafeExitCode(process));

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (stdin != null)
                {
                    try
                    {
                        // stdout/stderr are already drained asynchronously, so a large input can't deadlock
                        process.StandardInput.Write(stdin);
                    }
                    catch { /* git exited early — its exit code tells */ }
                }
                process.StandardInput.Close(); // never let a git command block on stdin

                var finished = await Task.WhenAny(exited.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != exited.Task)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    result.ExitCode = -1;
                    result.StdErr = "git timed out after " + (timeoutMs / 1000) + "s";
                    return result;
                }

                result.ExitCode = await exited.Task.ConfigureAwait(false);
                try { process.WaitForExit(); } catch { } // drain the async readers
                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
            }
            return result;
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        private static string Describe(string what, GitResult r)
        {
            var text = (r.StdErr.Trim().Length > 0 ? r.StdErr : r.StdOut).Trim();
            if (text.Length > 400)
                text = text.Substring(0, 400) + "…";
            return text.Length > 0 ? what + " failed: " + text : what + " failed (exit " + r.ExitCode + ")";
        }

        private static string Q(string value) => ClaudeCliProcessHost.Quote(value);

        private static List<string> Args(IEnumerable<string> head, IEnumerable<string> paths)
        {
            var list = new List<string>(head);
            list.AddRange(paths.Select(Q));
            return list;
        }

        /// <summary>Splits a pathspec list into command lines that stay under the Windows limit.
        /// A null list yields one empty chunk = "no pathspec" (the whole work-tree).</summary>
        private static IEnumerable<List<string>> ChunkPaths(IReadOnlyList<string>? paths)
        {
            if (paths == null)
            {
                yield return new List<string>();
                yield break;
            }
            var chunk = new List<string>();
            var length = 0;
            foreach (var p in paths)
            {
                if (chunk.Count > 0 && length + p.Length + 3 > MaxPathspecChars)
                {
                    yield return chunk;
                    chunk = new List<string>();
                    length = 0;
                }
                chunk.Add(p);
                length += p.Length + 3;
            }
            if (chunk.Count > 0)
                yield return chunk;
        }

        private static List<string> SplitLines(string text) =>
            (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

        private static int ParseInt(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;

        private static DateTime FromUnix(string seconds) =>
            long.TryParse(seconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s)
                : DateTime.MinValue;

        private static string Short(string sha) => sha != null && sha.Length >= 8 ? sha.Substring(0, 8) : sha ?? "";

        private static string ToJsonString(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string HashKey(string value)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
