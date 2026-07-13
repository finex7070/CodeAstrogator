using System;
using System.IO;
using System.Linq;
using CodeAstrogator.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class SessionHistoryStoreTests : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), "hist-test-" + Guid.NewGuid().ToString("n"), "history.json");

        public void Dispose()
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }

        private static JObject Msg(string role, string text) => new JObject
        {
            ["role"] = role,
            ["id"] = Guid.NewGuid().ToString("n"),
            ["text"] = text,
            ["ts"] = DateTime.UtcNow.ToString("o"),
        };

        [Fact]
        public void SaveAndLoad_RoundtripsSessions()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Title = "Parser question";
            store.Current.Id = "cli-123";
            store.Current.HasCliSession = true;
            store.Current.Messages.Add(Msg("user", "Explain the parser"));
            store.Current.Messages.Add(Msg("assistant", "It reads NDJSON."));
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            var list = reloaded.ToSessionList();

            var entry = Assert.Single(list);
            Assert.Equal("cli-123", entry.Value<string>("id"));
            Assert.Equal("Parser question", entry.Value<string>("title"));
            Assert.Contains("NDJSON", entry.Value<string>("preview"));

            var record = reloaded.Load("cli-123");
            Assert.NotNull(record);
            Assert.True(record!.HasCliSession); // --resume survives the restart
            Assert.Equal(2, record.Messages.Count);
        }

        [Fact]
        public void Load_FromMissingFile_IsEmpty()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            Assert.Empty(store.ToSessionList());
        }

        [Fact]
        public void Load_FromCorruptFile_IsEmpty()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{ not valid json !!");
            var store = SessionHistoryStore.LoadFrom(_path);
            Assert.Empty(store.ToSessionList());
        }

        [Fact]
        public void Save_KeepsMostRecentSessionsFirst()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Messages.Add(Msg("user", "first session"));
            store.StartNew();
            store.Current.Messages.Add(Msg("user", "second session"));
            store.Current.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            var titlesInOrder = reloaded.ToSessionList()
                .Select(s => s.Value<string>("preview"))
                .ToArray();

            Assert.Equal(2, titlesInOrder.Length);
            Assert.Contains("second", titlesInOrder[0]);
            Assert.Contains("first", titlesInOrder[1]);
        }

        [Fact]
        public void Rename_CurrentSession_UpdatesTitleAndPersists()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Messages.Add(Msg("user", "hello"));

            Assert.True(store.Rename(store.Current.Id, "  My renamed chat  "));
            Assert.Equal("My renamed chat", store.Current.Title); // trimmed
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            Assert.Equal("My renamed chat", reloaded.ToSessionList().Single().Value<string>("title"));
        }

        [Fact]
        public void Rename_ArchivedSession_UpdatesWithoutChangingOrder()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Id = "old";
            store.Current.Messages.Add(Msg("user", "first session"));
            store.StartNew();
            store.Current.Messages.Add(Msg("user", "second session"));
            store.Current.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);

            Assert.True(store.Rename("old", "Archived title"));

            var list = store.ToSessionList();
            Assert.Equal("Archived title", list[1].Value<string>("title"));
            Assert.Contains("second", list[0].Value<string>("preview")); // recency untouched
        }

        [Fact]
        public void Rename_UnknownIdOrEmptyTitle_ReturnsFalse()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            Assert.False(store.Rename("does-not-exist", "Title"));
            Assert.False(store.Rename(store.Current.Id, "   "));
            Assert.Equal("Untitled", store.Current.Title);
        }

        [Fact]
        public void Delete_ArchivedSession_RemovesItAndKeepsCurrent()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Id = "old";
            store.Current.Messages.Add(Msg("user", "first session"));
            store.StartNew();
            store.Current.Messages.Add(Msg("user", "second session"));

            var (deleted, wasCurrent) = store.Delete("old");
            Assert.True(deleted);
            Assert.False(wasCurrent);
            Assert.Contains("second", store.ToSessionList().Single().Value<string>("preview"));
        }

        [Fact]
        public void Delete_CurrentSession_ReportsWasCurrentAndStartsFresh()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var currentId = store.Current.Id;
            store.Current.Messages.Add(Msg("user", "hello"));

            var (deleted, wasCurrent) = store.Delete(currentId);
            Assert.True(deleted);
            Assert.True(wasCurrent);
            Assert.NotEqual(currentId, store.Current.Id); // replaced with a fresh empty record
            Assert.Empty(store.ToSessionList());           // nothing left to show
        }

        [Fact]
        public void Delete_UnknownId_ReturnsFalse()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.Current.Messages.Add(Msg("user", "hello"));
            var (deleted, wasCurrent) = store.Delete("does-not-exist");
            Assert.False(deleted);
            Assert.False(wasCurrent);
        }

        [Fact]
        public void LoadFrom_WithRetention_DropsSessionsOlderThanCutoff()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var recent = DateTime.UtcNow.AddDays(-3).ToString("o");
            var old = DateTime.UtcNow.AddDays(-40).ToString("o");
            var root = new JObject
            {
                ["sessions"] = new JArray
                {
                    new JObject { ["id"] = "fresh", ["title"] = "Fresh", ["updatedAt"] = recent,
                        ["messages"] = new JArray { Msg("user", "recent") } },
                    new JObject { ["id"] = "stale", ["title"] = "Stale", ["updatedAt"] = old,
                        ["messages"] = new JArray { Msg("user", "ancient") } },
                },
            };
            File.WriteAllText(_path, root.ToString());

            // retentionDays = 0 → keep everything
            Assert.Equal(2, SessionHistoryStore.LoadFrom(_path, 0).ToSessionList().Count);

            // retentionDays = 30 → the 40-day-old session is dropped
            var pruned = SessionHistoryStore.LoadFrom(_path, 30);
            var entry = Assert.Single(pruned.ToSessionList());
            Assert.Equal("fresh", entry.Value<string>("id"));
        }

        [Fact]
        public void GetHistoryPath_IsStablePerWorkspaceAndIgnoresCaseAndTrailingSlash()
        {
            var a = SessionHistoryStore.GetHistoryPath(@"C:\Users\Jan\source\repos\Foo");
            var b = SessionHistoryStore.GetHistoryPath(@"c:\users\jan\source\repos\foo\");
            var c = SessionHistoryStore.GetHistoryPath(@"C:\other");
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.EndsWith(".json", a);
        }
    }
}
