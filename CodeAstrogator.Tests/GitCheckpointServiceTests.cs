using System;
using System.IO;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>
    /// End-to-end tests against a real git executable and a real temp work-tree. Each test uses a
    /// unique temp directory (so the shadow git-dir under %LOCALAPPDATA% is unique) and cleans both
    /// up. Skipped as a no-op when git isn't on PATH so the suite still passes on a git-less machine.
    /// </summary>
    public class GitCheckpointServiceTests
    {
        private static bool GitPresent => GitCheckpointService.IsGitAvailable();

        private static string NewWorkspace()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ca-ckpt-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string workspace)
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            try
            {
                var gitDir = GitCheckpointService.GitDirFor(workspace);
                var repoRoot = Path.GetDirectoryName(gitDir); // ...\Checkpoints\<hash>
                if (repoRoot != null && Directory.Exists(repoRoot)) Directory.Delete(repoRoot, true);
            }
            catch { }
        }

        [Fact]
        public async Task Restore_RevertsChangedFileToCheckpointContent()
        {
            if (!GitPresent) return;
            var ws = NewWorkspace();
            try
            {
                var svc = new GitCheckpointService();
                var file = Path.Combine(ws, "a.txt");
                File.WriteAllText(file, "original");

                await svc.EnsureInitializedAsync(ws);
                var cp = await svc.CommitAsync(ws, "Turn 1");
                Assert.False(string.IsNullOrEmpty(cp.Sha));

                File.WriteAllText(file, "modified");
                await svc.CommitAsync(ws, "Turn 2");

                await svc.RestoreAsync(ws, cp.Sha);

                Assert.Equal("original", File.ReadAllText(file));
            }
            finally { Cleanup(ws); }
        }

        [Fact]
        public async Task Restore_RemovesFilesAddedAfterTheCheckpoint()
        {
            if (!GitPresent) return;
            var ws = NewWorkspace();
            try
            {
                var svc = new GitCheckpointService();
                File.WriteAllText(Path.Combine(ws, "keep.txt"), "keep");

                await svc.EnsureInitializedAsync(ws);
                var cp = await svc.CommitAsync(ws, "Turn 1");

                var added = Path.Combine(ws, "added.txt");
                File.WriteAllText(added, "new file");
                await svc.CommitAsync(ws, "Turn 2");

                await svc.RestoreAsync(ws, cp.Sha);

                Assert.False(File.Exists(added));           // added-after file is gone
                Assert.True(File.Exists(Path.Combine(ws, "keep.txt"))); // pre-existing file stays
            }
            finally { Cleanup(ws); }
        }

        [Fact]
        public async Task Restore_KeepsHistoryLinear_AllCheckpointsRemain()
        {
            if (!GitPresent) return;
            var ws = NewWorkspace();
            try
            {
                var svc = new GitCheckpointService();
                var file = Path.Combine(ws, "a.txt");

                File.WriteAllText(file, "v1");
                await svc.EnsureInitializedAsync(ws);
                var cp1 = await svc.CommitAsync(ws, "Turn 1");

                File.WriteAllText(file, "v2");
                var cp2 = await svc.CommitAsync(ws, "Turn 2");

                var before = await svc.ListAsync(ws);
                await svc.RestoreAsync(ws, cp1.Sha);
                var after = await svc.ListAsync(ws);

                // Restore adds commits (safety + restore) and never rewrites history → count grows.
                Assert.True(after.Count >= before.Count + 2);
                // Both original checkpoints are still reachable (nothing lost).
                Assert.Contains(after, c => c.Sha == cp1.Sha);
                Assert.Contains(after, c => c.Sha == cp2.Sha);

                // Redo: restoring the later checkpoint brings the newer content back.
                await svc.RestoreAsync(ws, cp2.Sha);
                Assert.Equal("v2", File.ReadAllText(file));
            }
            finally { Cleanup(ws); }
        }

        [Fact]
        public async Task Commit_AllowsEmpty_EveryPromptGetsARestorePoint()
        {
            if (!GitPresent) return;
            var ws = NewWorkspace();
            try
            {
                var svc = new GitCheckpointService();
                File.WriteAllText(Path.Combine(ws, "a.txt"), "x");
                await svc.EnsureInitializedAsync(ws);

                var cp1 = await svc.CommitAsync(ws, "Turn 1");
                var cp2 = await svc.CommitAsync(ws, "Turn 2"); // nothing changed → still a new commit

                Assert.NotEqual(cp1.Sha, cp2.Sha);
            }
            finally { Cleanup(ws); }
        }

        [Fact]
        public void BuildArgs_QuotesGitDirWithSpaces()
        {
            var args = GitCheckpointService.BuildArgs(new[] { "--git-dir=C:\\a b\\.git", "status" });
            Assert.Equal("\"--git-dir=C:\\a b\\.git\" status", args);
        }
    }
}
