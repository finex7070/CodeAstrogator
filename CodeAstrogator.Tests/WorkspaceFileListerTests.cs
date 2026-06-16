using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class WorkspaceFileListerTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceFileListerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "wsl-test-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "bin"));
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
            Directory.CreateDirectory(Path.Combine(_root, ".github"));
            File.WriteAllText(Path.Combine(_root, "README.md"), "");
            File.WriteAllText(Path.Combine(_root, ".gitignore"), "");
            File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "");
            File.WriteAllText(Path.Combine(_root, "bin", "junk.dll"), "");
            File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "");
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        [Fact]
        public void List_ReturnsRelativeForwardSlashPaths()
        {
            var entries = WorkspaceFileLister.List(_root);
            Assert.Contains(entries, e => e.Path == "src/Program.cs" && !e.IsDir);
            Assert.Contains(entries, e => e.Path == "src" && e.IsDir);
            Assert.Contains(entries, e => e.Path == "README.md");
        }

        [Fact]
        public void List_ExcludesBuildAndVcsDirectories()
        {
            var entries = WorkspaceFileLister.List(_root);
            Assert.DoesNotContain(entries, e => e.Path.StartsWith("bin", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, e => e.Path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, e => e.Path == ".git");
            // dotfiles and non-excluded dot-directories stay visible (like the VS Code picker)
            Assert.Contains(entries, e => e.Path == ".gitignore");
            Assert.Contains(entries, e => e.Path == ".github" && e.IsDir);
        }

        [Fact]
        public void List_ShallowEntriesComeFirst()
        {
            var entries = WorkspaceFileLister.List(_root).ToList();
            var topLevel = entries.FindIndex(e => e.Path == "README.md");
            var nested = entries.FindIndex(e => e.Path == "src/Program.cs");
            Assert.True(topLevel >= 0 && nested > topLevel, "breadth-first: top-level before nested");
        }

        [Fact]
        public void List_RespectsMaxEntries()
        {
            Assert.True(WorkspaceFileLister.List(_root, maxEntries: 3).Count <= 3);
        }

        [Fact]
        public void List_MissingRootReturnsEmpty()
        {
            Assert.Empty(WorkspaceFileLister.List(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("n"))));
            Assert.Empty(WorkspaceFileLister.List(null));
        }
    }
}
