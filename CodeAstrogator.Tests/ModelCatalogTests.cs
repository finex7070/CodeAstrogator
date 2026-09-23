using System.Linq;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class ModelCatalogTests
    {
        // Real envelopes from `claude -p /model --model <id> --output-format json` (CLI 2.1.263,
        // 2026-09-23). A known ID renders as its display label, an unknown one is echoed verbatim.
        private static string Envelope(string rendered) =>
            "{\"is_error\":false,\"num_turns\":0,\"total_cost_usd\":0,\"duration_api_ms\":0," +
            "\"result\":\"Current model: `" + rendered + "` (effort: high)\\nUsage: /model <name>. " +
            "Available: sonnet, opus, haiku, fable, best, opusplan, default, or a full model ID.\"," +
            "\"subtype\":\"success\",\"type\":\"result\"}";

        private const string DefaultsJson = @"{
          ""version"": 1,
          ""models"": [
            { ""id"": ""claude-opus-5-5"",   ""label"": ""Opus 5.5"",   ""family"": ""opus"" },
            { ""id"": ""claude-fable-5-1"",  ""label"": ""Fable 5.1"",  ""family"": ""fable"" },
            { ""id"": ""claude-sonnet-5"",   ""label"": ""Sonnet 5"",   ""family"": ""sonnet"" },
            { ""id"": ""claude-haiku-4-5"",  ""label"": ""Haiku 4.5"",  ""family"": ""haiku"" },
            { ""id"": ""claude-opus-5"",     ""label"": ""Opus 5"",     ""family"": ""opus"" },
            { ""id"": ""claude-opus-4-8"",   ""label"": ""Opus 4.8"",   ""family"": ""opus"" }
          ]
        }";

        private static ModelEntry Entry(string id, string family, bool? available, string label = "L") =>
            new ModelEntry { Id = id, Label = label, Family = family, Available = available };

        // ── CLI probe ────────────────────────────────────────────────────────

        [Fact]
        public void KnownModel_RendersLabel_SoItCountsAsSupported()
        {
            Assert.True(ClaudeModelCatalog.ParseModelKnown(Envelope("Opus 5.5"), "claude-opus-5-5"));
            Assert.True(ClaudeModelCatalog.ParseModelKnown(Envelope("Fable 5.1"), "claude-fable-5-1"));
        }

        [Fact]
        public void UnknownModel_EchoesTheRawId()
        {
            // CLI 2.1.263 does not know Opus 5.5 — it echoes the ID instead of a label.
            Assert.False(ClaudeModelCatalog.ParseModelKnown(Envelope("claude-opus-5-5"), "claude-opus-5-5"));
            Assert.False(ClaudeModelCatalog.ParseModelKnown(Envelope("CLAUDE-OPUS-5-5"), "claude-opus-5-5"));
        }

        [Fact]
        public void HookPreambleBeforeTheEnvelopeIsTolerated()
        {
            var output = "SessionStart hook says hello\n" + Envelope("Opus 5.5");
            Assert.True(ClaudeModelCatalog.ParseModelKnown(output, "claude-opus-5-5"));
        }

        [Fact]
        public void UnparseableOutput_IsUndetermined()
        {
            Assert.Null(ClaudeModelCatalog.ParseModelKnown("", "claude-opus-5-5"));
            Assert.Null(ClaudeModelCatalog.ParseModelKnown("command not found", "claude-opus-5-5"));
            Assert.Null(ClaudeModelCatalog.ParseModelKnown(
                "{\"result\":\"[claude-code:unrecognized_model]\",\"type\":\"result\"}", "claude-opus-5-5"));
        }

        [Fact]
        public void PlainTextReportWithoutJsonEnvelopeStillParses()
        {
            Assert.True(ClaudeModelCatalog.ParseModelKnown("Current model: `Opus 5.5` (effort: high)", "claude-opus-5-5"));
        }

        // ── defaults / cache parsing ─────────────────────────────────────────

        [Fact]
        public void DefaultsAreReadInFileOrder()
        {
            var defaults = ModelCatalog.ParseDefaults(DefaultsJson);
            Assert.Equal(
                new[] { "claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5", "claude-haiku-4-5", "claude-opus-5", "claude-opus-4-8" },
                defaults.Select(m => m.Id));
            Assert.Equal("Opus 5.5", defaults[0].Label);
            Assert.Equal("opus", defaults[0].Family);
            Assert.All(defaults, m => Assert.Equal(ModelEntry.SourceCatalog, m.Source));
            Assert.All(defaults, m => Assert.Null(m.Available));
        }

        [Fact]
        public void BrokenOrEmptyDefaultsYieldNothing()
        {
            Assert.Empty(ModelCatalog.ParseDefaults(""));
            Assert.Empty(ModelCatalog.ParseDefaults("not json"));
            Assert.Empty(ModelCatalog.ParseDefaults("{\"models\":[{\"label\":\"no id\"}]}"));
        }

        [Fact]
        public void CacheRoundTripsThroughSerialize()
        {
            var data = new ModelCatalogData
            {
                DefaultsSource = "remote",
                DefaultsFetchedAt = new System.DateTimeOffset(2026, 9, 23, 8, 0, 0, System.TimeSpan.FromHours(2)),
                Models =
                {
                    new ModelEntry { Id = "claude-opus-5", Label = "Opus 5", Family = "opus",
                        Source = ModelEntry.SourceCatalog, Available = true, CheckedCliVersion = "2.1.263" },
                    new ModelEntry { Id = "my-model", Label = "Mine", Family = "opus", Source = ModelEntry.SourceUser },
                },
            };

            var reread = ModelCatalog.ParseCache(ModelCatalog.Serialize(data));

            Assert.NotNull(reread);
            Assert.Equal("remote", reread!.DefaultsSource);
            Assert.Equal(data.DefaultsFetchedAt, reread.DefaultsFetchedAt);
            Assert.Equal(new[] { "claude-opus-5", "my-model" }, reread.Models.Select(m => m.Id));
            Assert.True(reread.Models[0].Available);
            Assert.Equal("2.1.263", reread.Models[0].CheckedCliVersion);
            Assert.Equal(ModelEntry.SourceUser, reread.Models[1].Source);
            Assert.Null(reread.Models[1].Available);
        }

        [Fact]
        public void HandWrittenCacheEntryCountsAsUserEntry()
        {
            // No "source" field — what a user adding a model by hand would write.
            var data = ModelCatalog.ParseCache("{\"models\":[{\"id\":\"claude-opus-9\",\"label\":\"Opus 9\"}]}");
            Assert.Equal(ModelEntry.SourceUser, data!.Models[0].Source);
            Assert.Equal("other", data.Models[0].Family); // no family given → own group
        }

        // ── merge ────────────────────────────────────────────────────────────

        [Fact]
        public void MergeKeepsProbedAvailabilityAndUserEntries()
        {
            var cache = ModelCatalog.ParseCache(
                "{\"models\":[" +
                "{\"id\":\"claude-fable-5-1\",\"label\":\"old label\",\"family\":\"fable\",\"source\":\"catalog\"," +
                "\"available\":true,\"checkedCliVersion\":\"2.1.263\"}," +
                "{\"id\":\"my-model\",\"label\":\"Mine\",\"family\":\"opus\"}]}");

            var merged = ModelCatalog.MergeDefaults(cache, ModelCatalog.ParseDefaults(DefaultsJson));

            var fable = merged.Models.Single(m => m.Id == "claude-fable-5-1");
            Assert.True(fable.Available);                 // probe result survives
            Assert.Equal("Fable 5.1", fable.Label);        // catalog entry gets its label refreshed
            var mine = merged.Models.Single(m => m.Id == "my-model");
            Assert.Equal("Mine", mine.Label);              // user entry untouched
            Assert.Equal(ModelEntry.SourceUser, mine.Source);
            Assert.Equal(7, merged.Models.Count);          // 6 defaults + the user entry
        }

        [Fact]
        public void NewDefaultLandsNextToItsGenerationNotAtTheBottom()
        {
            // Cache without the freshly released model (Opus 5.5 is the first default entry).
            var cache = ModelCatalog.ParseCache(
                "{\"models\":[" +
                "{\"id\":\"claude-fable-5-1\",\"family\":\"fable\",\"source\":\"catalog\",\"available\":true}," +
                "{\"id\":\"claude-opus-5\",\"family\":\"opus\",\"source\":\"catalog\",\"available\":true}]}");

            var merged = ModelCatalog.MergeDefaults(cache, ModelCatalog.ParseDefaults(DefaultsJson));

            // It has no predecessor in the default list → front of the list → it becomes the opus
            // top-level row as soon as the CLI accepts it.
            Assert.Equal("claude-opus-5-5", merged.Models[0].Id);
            Assert.True(merged.Models.IndexOf(merged.Models.Single(m => m.Id == "claude-opus-4-8")) >
                        merged.Models.IndexOf(merged.Models.Single(m => m.Id == "claude-opus-5")));
        }

        [Fact]
        public void MergeWithoutCacheStartsFromTheDefaults()
        {
            var merged = ModelCatalog.MergeDefaults(null, ModelCatalog.ParseDefaults(DefaultsJson));
            Assert.Equal(6, merged.Models.Count);
            Assert.All(merged.Models, m => Assert.Null(m.Available));
        }

        [Fact]
        public void NeedsProbe_TracksTheCliVersion()
        {
            var entry = Entry("claude-opus-5", "opus", true);
            Assert.True(ModelCatalog.NeedsProbe(entry, "2.1.263")); // never checked
            entry.CheckedCliVersion = "2.1.263";
            Assert.False(ModelCatalog.NeedsProbe(entry, "2.1.263"));
            Assert.True(ModelCatalog.NeedsProbe(entry, "2.2.0"));   // CLI updated → re-probe
            entry.Available = null;
            entry.CheckedCliVersion = "2.1.263";
            Assert.True(ModelCatalog.NeedsProbe(entry, "2.1.263")); // probe failed earlier
        }

        // ── picker layout ────────────────────────────────────────────────────

        [Fact]
        public void PickerShowsTheNewestAvailableModelOfEachFamilyAtTheTop()
        {
            var entries = new[]
            {
                Entry("claude-opus-5-5", "opus", true, "Opus 5.5"),
                Entry("claude-fable-5-1", "fable", true, "Fable 5.1"),
                Entry("claude-sonnet-5", "sonnet", true, "Sonnet 5"),
                Entry("claude-haiku-4-5", "haiku", true, "Haiku 4.5"),
                Entry("claude-opus-5", "opus", true, "Opus 5"),
                Entry("claude-opus-4-8", "opus", true, "Opus 4.8"),
            };

            var rows = ModelCatalog.BuildPicker(entries);

            Assert.Equal(
                new[] { "claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5", "claude-haiku-4-5" },
                rows.Where(r => r.Primary).Select(r => r.Id));
            Assert.Equal(
                new[] { "claude-opus-5", "claude-opus-4-8" },
                rows.Where(r => !r.Primary).Select(r => r.Id));
        }

        [Fact]
        public void ModelTheCliRejectsIsDropped_AndTheNextOneMovesUp()
        {
            var entries = new[]
            {
                Entry("claude-opus-5-5", "opus", false, "Opus 5.5"),   // CLI too old
                Entry("claude-fable-5-1", "fable", true, "Fable 5.1"),
                Entry("claude-opus-5", "opus", true, "Opus 5"),
                Entry("claude-opus-4-8", "opus", true, "Opus 4.8"),
            };

            var rows = ModelCatalog.BuildPicker(entries);

            Assert.DoesNotContain(rows, r => r.Id == "claude-opus-5-5");
            // Opus keeps its place at the top even though its newest member was dropped.
            Assert.Equal(new[] { "claude-opus-5", "claude-fable-5-1" }, rows.Where(r => r.Primary).Select(r => r.Id));
            Assert.Equal(new[] { "claude-opus-4-8" }, rows.Where(r => !r.Primary).Select(r => r.Id));
        }

        [Fact]
        public void UnprobedModelsAreNotOffered()
        {
            var rows = ModelCatalog.BuildPicker(new[]
            {
                Entry("claude-opus-5-5", "opus", null),   // not probed yet
                Entry("claude-opus-5", "opus", true, "Opus 5"),
            });

            Assert.Equal(new[] { "claude-opus-5" }, rows.Select(r => r.Id));
            Assert.True(rows[0].Primary);
        }

        [Fact]
        public void UserModelWithoutFamilyGetsItsOwnTopLevelRow()
        {
            var rows = ModelCatalog.BuildPicker(new[]
            {
                Entry("claude-opus-5", "opus", true, "Opus 5"),
                Entry("my-model", "other", true, "Mine"),
            });

            Assert.Equal(new[] { "claude-opus-5", "my-model" }, rows.Where(r => r.Primary).Select(r => r.Id));
        }

        // ── shipped catalog ──────────────────────────────────────────────────

        [Fact]
        public void ShippedModelsJsonIsValidAndNewestFirstPerFamily()
        {
            var path = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, ClaudeModelCatalog.CatalogFileName);
            Assert.True(System.IO.File.Exists(path), "models.json must be copied next to the assembly");

            var defaults = ModelCatalog.ParseDefaults(System.IO.File.ReadAllText(path));
            Assert.NotEmpty(defaults);
            Assert.Equal(defaults.Select(m => m.Id).Distinct().Count(), defaults.Count);
            Assert.All(defaults, m => Assert.StartsWith("claude-", m.Id));

            // The first entry of each family is the one the picker promotes → it must be the newest.
            Assert.Equal("claude-opus-5-5", defaults.First(m => m.Family == "opus").Id);
            Assert.Equal("claude-fable-5-1", defaults.First(m => m.Family == "fable").Id);
            Assert.Equal("claude-sonnet-5", defaults.First(m => m.Family == "sonnet").Id);
            Assert.Equal("claude-haiku-4-5", defaults.First(m => m.Family == "haiku").Id);
        }
    }
}
