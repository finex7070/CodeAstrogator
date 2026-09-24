using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class TaskOutputWatcherTests : IDisposable
    {
        private readonly string _root;
        private readonly List<(string Id, string Text)> _received = new List<(string, string)>();

        public TaskOutputWatcherTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ca-taskout-" + Guid.NewGuid().ToString("n"), "claude");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch { }
        }

        private TaskOutputWatcher NewWatcher() =>
            new TaskOutputWatcher((id, text) => _received.Add((id, text)), new[] { _root }, pollMs: 0);

        private string Received(string toolUseId) =>
            string.Concat(_received.Where(r => r.Id == toolUseId).Select(r => r.Text));

        // The CLI keeps the file open for writing while the command runs — append the way it does.
        private static void Append(string path, byte[] bytes)
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(bytes, 0, bytes.Length);
        }

        private static string CreateTaskFile(string root, string cwd, string session, string task)
        {
            var path = TaskOutputWatcher.BuildPath(root, cwd, session, task);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
            return path;
        }

        [Fact]
        public void BuildPath_MatchesTheCliLayout()
        {
            // Measured against CLI 2.1.280: cwd C:\Users\Jan\AppData\Local\Temp\cc-live →
            // %TEMP%\claude\C--Users-Jan-AppData-Local-Temp-cc-live\<session>\tasks\<task>.output
            var path = TaskOutputWatcher.BuildPath(@"C:\T\claude", @"C:\Users\Jan\AppData\Local\Temp\cc-live", "sess", "brchlhc2o");
            Assert.Equal(@"C:\T\claude\C--Users-Jan-AppData-Local-Temp-cc-live\sess\tasks\brchlhc2o.output", path);
        }

        [Fact]
        public void StreamsOnlyNewBytes_WhileTheCommandRuns()
        {
            var path = CreateTaskFile(_root, @"C:\repo", "s1", "t1");
            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");

            Append(path, Encoding.UTF8.GetBytes("line1\r\n"));
            watcher.Poll();
            Append(path, Encoding.UTF8.GetBytes("line2\r\n"));
            watcher.Poll();
            watcher.Poll(); // nothing new → nothing reported

            Assert.Equal(new[] { "line1\r\n", "line2\r\n" }, _received.Select(r => r.Text));
            Assert.All(_received, r => Assert.Equal("toolu_1", r.Id));
        }

        [Fact]
        public void MultiByteCharacterSplitAcrossReads_DecodesIntact()
        {
            // Measured: the CLI writes UTF-8 ("Größe äöü € 1\r\n" = 22 bytes per line).
            var path = CreateTaskFile(_root, @"C:\repo", "s1", "t1");
            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");

            var bytes = Encoding.UTF8.GetBytes("Größe €\n");
            var euro = Array.IndexOf(bytes, (byte)0xE2); // first byte of "€"
            Append(path, bytes.Take(euro + 1).ToArray()); // cut inside the 3-byte sequence
            watcher.Poll();
            Append(path, bytes.Skip(euro + 1).ToArray());
            watcher.Poll();

            Assert.Equal("Größe €\n", Received("toolu_1"));
        }

        [Fact]
        public void FileDeletedAfterBeingSeen_EndsTheTail()
        {
            var path = CreateTaskFile(_root, @"C:\repo", "s1", "t1");
            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");
            Append(path, Encoding.UTF8.GetBytes("done\n"));
            watcher.Poll();
            Assert.Equal(1, watcher.ActiveCount);

            File.Delete(path); // the CLI removes the file when the command ends
            watcher.Poll();
            Assert.Equal(0, watcher.ActiveCount);
        }

        [Fact]
        public void Stop_FlushesWhatIsLeft()
        {
            var path = CreateTaskFile(_root, @"C:\repo", "s1", "t1");
            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");
            Append(path, Encoding.UTF8.GetBytes("last words\n"));

            watcher.StopByToolUse("toolu_1"); // tool_result arrived before the next poll

            Assert.Equal("last words\n", Received("toolu_1"));
            Assert.Equal(0, watcher.ActiveCount);
        }

        [Fact]
        public void FindsTheFile_WhenTheCwdIsMungedDifferently()
        {
            // The CLI's own munging differs from ours → fall back to <root>\*\<session>\tasks\<task>.output.
            var odd = Path.Combine(_root, "some-other-munging", "s1", "tasks");
            Directory.CreateDirectory(odd);
            var path = Path.Combine(odd, "t1.output");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("found\n"));

            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");
            watcher.Poll();

            Assert.Equal("found\n", Received("toolu_1"));
        }

        [Fact]
        public void MissingIds_AreIgnored()
        {
            using var watcher = NewWatcher();
            watcher.Start("", "toolu_1", "s1", @"C:\repo");
            watcher.Start("t1", "", "s1", @"C:\repo");
            watcher.Start("t1", "toolu_1", null, @"C:\repo");
            Assert.Equal(0, watcher.ActiveCount);
        }

        [Fact]
        public void StopAll_DropsEveryTail()
        {
            CreateTaskFile(_root, @"C:\repo", "s1", "t1");
            CreateTaskFile(_root, @"C:\repo", "s1", "t2");
            using var watcher = NewWatcher();
            watcher.Start("t1", "toolu_1", "s1", @"C:\repo");
            watcher.Start("t2", "toolu_2", "s1", @"C:\repo");
            Assert.Equal(2, watcher.ActiveCount);

            watcher.StopAll();
            Assert.Equal(0, watcher.ActiveCount);
        }
    }
}
