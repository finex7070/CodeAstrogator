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
            var chat = store.StartNew();
            chat.Title = "Parser question";
            chat.Id = "cli-123";
            chat.HasCliSession = true;
            chat.Messages.Add(Msg("user", "Explain the parser"));
            chat.Messages.Add(Msg("assistant", "It reads NDJSON."));
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            var list = reloaded.ToSessionList();

            var entry = Assert.Single(list);
            Assert.Equal("cli-123", entry.Value<string>("id"));
            Assert.Equal("Parser question", entry.Value<string>("title"));
            Assert.Contains("NDJSON", entry.Value<string>("preview"));

            var record = reloaded.Open("cli-123");
            Assert.NotNull(record);
            Assert.True(record!.HasCliSession); // --resume survives the restart
            Assert.Equal(2, record.Messages.Count);
        }

        [Fact]
        public void PromptSuggestion_SurvivesSaveAndLoad()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Id = "cli-9";
            chat.HasCliSession = true;
            chat.Messages.Add(Msg("user", "Fix the cart"));
            chat.PromptSuggestion = "run the tests again";
            store.Save();

            Assert.Equal("run the tests again", SessionHistoryStore.LoadFrom(_path).Find("cli-9")!.PromptSuggestion);
        }

        [Fact]
        public void ReloadFrom_SwitchesToAnotherWorkspacesHistory()
        {
            var otherPath = Path.Combine(Path.GetDirectoryName(_path)!, "other.json");
            var other = SessionHistoryStore.LoadFrom(otherPath);
            var otherChat = other.StartNew();
            otherChat.Id = "cli-other";
            otherChat.Messages.Add(Msg("user", "Other workspace"));
            other.Save();

            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Id = "cli-global";
            chat.Messages.Add(Msg("user", "Global chat"));
            store.Save();

            store.ReloadFrom(otherPath);

            Assert.Equal(otherPath, store.FilePath);
            Assert.Empty(store.OpenRecords);                 // nothing open; the caller opens what its tabs show
            Assert.NotNull(store.Find("cli-other"));
            Assert.Null(store.Find("cli-global"));            // the global chat stays in its own file
            Assert.NotNull(SessionHistoryStore.LoadFrom(_path).Find("cli-global"));
        }

        [Fact]
        public void Import_ReplacesMessagesButKeepsTheTitle()
        {
            // a remote session that ended while VS was closed: the transcript wins, the (renamed) title stays
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Id = "cli-777";
            chat.HasCliSession = true;
            chat.Title = "Checkout refactor";
            chat.Messages.Add(Msg("user", "Start"));
            Assert.True(store.Contains("CLI-777"));
            Assert.False(store.Contains("cli-other"));

            var record = store.Import("cli-777", "Start", new[] { Msg("user", "Start"), Msg("user", "From the phone") },
                DateTime.UtcNow);

            Assert.Same(chat, record);
            Assert.Equal("Checkout refactor", record.Title);
            Assert.Equal(2, record.Messages.Count);

            var fresh = store.Import("cli-new", "Phone only", new[] { Msg("user", "Phone only") }, DateTime.UtcNow);
            Assert.Equal("Phone only", fresh.Title); // a new record takes the transcript's title
            Assert.False(store.IsOpen("cli-new"));   // imported, not opened
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
            var first = store.StartNew();
            first.Messages.Add(Msg("user", "first session"));
            store.Close(first);
            var second = store.StartNew();
            second.Messages.Add(Msg("user", "second session"));
            second.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
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
        public void Rename_OpenSession_UpdatesTitleAndPersists()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Messages.Add(Msg("user", "hello"));

            Assert.True(store.Rename(chat.Id, "  My renamed chat  "));
            Assert.Equal("My renamed chat", chat.Title); // trimmed
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            Assert.Equal("My renamed chat", reloaded.ToSessionList().Single().Value<string>("title"));
        }

        [Fact]
        public void Rename_ClosedSession_UpdatesWithoutChangingOrder()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var old = store.StartNew();
            old.Id = "old";
            old.Messages.Add(Msg("user", "first session"));
            store.Close(old);
            var current = store.StartNew();
            current.Messages.Add(Msg("user", "second session"));
            current.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);

            Assert.True(store.Rename("old", "Archived title"));

            var list = store.ToSessionList();
            Assert.Equal("Archived title", list[1].Value<string>("title"));
            Assert.Contains("second", list[0].Value<string>("preview")); // recency untouched
        }

        [Fact]
        public void Rename_UnknownIdOrEmptyTitle_ReturnsFalse()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            Assert.False(store.Rename("does-not-exist", "Title"));
            Assert.False(store.Rename(chat.Id, "   "));
            Assert.Equal("Untitled", chat.Title);
        }

        [Fact]
        public void Delete_ClosedSession_RemovesItAndKeepsTheOpenOne()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var old = store.StartNew();
            old.Id = "old";
            old.Messages.Add(Msg("user", "first session"));
            store.Close(old);
            var current = store.StartNew();
            current.Messages.Add(Msg("user", "second session"));

            var (deleted, wasOpen) = store.Delete("old");
            Assert.True(deleted);
            Assert.False(wasOpen);
            Assert.Contains("second", store.ToSessionList().Single().Value<string>("preview"));
        }

        [Fact]
        public void Delete_OpenSession_ReportsWasOpenAndClosesIt()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Messages.Add(Msg("user", "hello"));

            var (deleted, wasOpen) = store.Delete(chat.Id);
            Assert.True(deleted);
            Assert.True(wasOpen);
            Assert.Empty(store.OpenRecords);      // the tab starts a fresh chat itself
            Assert.Empty(store.ToSessionList());  // nothing left to show
        }

        [Fact]
        public void Delete_UnknownId_ReturnsFalse()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            store.StartNew().Messages.Add(Msg("user", "hello"));
            var (deleted, wasOpen) = store.Delete("does-not-exist");
            Assert.False(deleted);
            Assert.False(wasOpen);
        }

        // ── several open chats (one per tab) ─────────────────────────────────

        [Fact]
        public void SeveralOpenChats_AreAllListedAndSaved()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var a = store.StartNew();
            var b = store.StartNew();
            a.Messages.Add(Msg("user", "tab one"));
            b.Messages.Add(Msg("user", "tab two"));
            b.UpdatedAtUtc = a.UpdatedAtUtc.AddMinutes(1);

            Assert.Equal(2, store.OpenRecords.Count);
            store.Save();

            var previews = SessionHistoryStore.LoadFrom(_path).ToSessionList().Select(s => s.Value<string>("preview")).ToArray();
            Assert.Equal(new[] { "tab two", "tab one" }, previews);
        }

        [Fact]
        public void OpenChats_ComeFirstEvenWhenOlder()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var old = store.StartNew();
            old.Messages.Add(Msg("user", "open but old"));
            old.UpdatedAtUtc = DateTime.UtcNow.AddDays(-2);
            var recent = store.StartNew();
            recent.Messages.Add(Msg("user", "closed but recent"));
            store.Close(recent);

            var list = store.ToSessionList();
            Assert.Contains("open but old", list[0].Value<string>("preview"));
            Assert.Contains("closed but recent", list[1].Value<string>("preview"));
        }

        [Fact]
        public void EmptyChat_IsNeverListedOrSaved_AndDroppedOnClose()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var empty = store.StartNew();
            Assert.Empty(store.ToSessionList());
            Assert.True(store.Contains(empty.Id)); // known while its tab shows it

            store.Close(empty);
            Assert.False(store.Contains(empty.Id));
            store.Save();
            Assert.Empty(SessionHistoryStore.LoadFrom(_path).ToSessionList());
        }

        [Fact]
        public void Close_KeepsAChatWithMessages_AndOpenBringsItBack()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var chat = store.StartNew();
            chat.Id = "cli-1";
            chat.Messages.Add(Msg("user", "keep me"));

            store.Close(chat);
            Assert.False(store.IsOpen("cli-1"));
            Assert.Single(store.ToSessionList());

            Assert.Same(chat, store.Open("cli-1"));
            Assert.True(store.IsOpen("CLI-1"));
            Assert.Same(chat, store.Open("cli-1")); // opening twice keeps one entry
            Assert.Single(store.OpenRecords);
            Assert.Null(store.Open("unknown"));
        }

        [Fact]
        public void OpenTabs_AreSavedInOrderWithTheActiveOne_AndEmptyOnesSkipped()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var a = store.StartNew();
            a.Id = "cli-a";
            a.Messages.Add(Msg("user", "tab one"));
            var empty = store.StartNew(); // a fresh tab — nothing to restore
            var b = store.StartNew();
            b.Id = "cli-b";
            b.Messages.Add(Msg("user", "tab three"));
            store.ActiveOpenId = "cli-b";
            store.Save();

            var reloaded = SessionHistoryStore.LoadFrom(_path);
            Assert.Equal(new[] { "cli-a", "cli-b" }, reloaded.SavedOpenIds);
            Assert.Equal("cli-b", reloaded.SavedActiveId);
            Assert.Empty(reloaded.OpenRecords); // restoring is the panel's job

            // an active tab without messages is not remembered
            store.ActiveOpenId = empty.Id;
            store.Save();
            Assert.Null(SessionHistoryStore.LoadFrom(_path).SavedActiveId);
        }

        [Fact]
        public void FileFromBeforeTabs_HasNoSavedTabs()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, new JObject { ["sessions"] = new JArray() }.ToString());
            var store = SessionHistoryStore.LoadFrom(_path);
            Assert.Empty(store.SavedOpenIds);
            Assert.Null(store.SavedActiveId);
        }

        [Fact]
        public void Save_CapKeepsOpenChats()
        {
            var store = SessionHistoryStore.LoadFrom(_path);
            var open = store.StartNew();
            open.Id = "open-old";
            open.Messages.Add(Msg("user", "oldest but open"));
            open.UpdatedAtUtc = DateTime.UtcNow.AddDays(-5);
            for (var i = 0; i < 60; i++)
            {
                var r = store.StartNew();
                r.Messages.Add(Msg("user", "chat " + i));
                store.Close(r);
            }
            store.Save();

            Assert.NotNull(SessionHistoryStore.LoadFrom(_path).Find("open-old"));
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
