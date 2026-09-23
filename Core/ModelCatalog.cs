using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>One model in the catalog — a default entry from <c>models.json</c> or one the user
    /// added to their local cache by hand.</summary>
    public sealed class ModelEntry
    {
        /// <summary>Entry came from the shipped/fetched default catalog (refreshed on every merge).</summary>
        public const string SourceCatalog = "catalog";

        /// <summary>Entry only exists in the local cache — added by the user, never overwritten.</summary>
        public const string SourceUser = "user";

        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>Generation group ("opus", "fable", …). The newest available member of a family is
        /// the one shown at the top level of the picker; the rest move into the submenu.</summary>
        public string Family { get; set; } = "";
        public string Source { get; set; } = SourceCatalog;
        /// <summary>Whether the installed CLI accepts the ID; null = not probed yet.</summary>
        public bool? Available { get; set; }
        /// <summary>CLI version <see cref="Available"/> was measured against (re-probe when it changes).</summary>
        public string? CheckedCliVersion { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }

        public ModelEntry Clone() => new ModelEntry
        {
            Id = Id,
            Label = Label,
            Family = Family,
            Source = Source,
            Available = Available,
            CheckedCliVersion = CheckedCliVersion,
            CheckedAt = CheckedAt,
        };
    }

    /// <summary>Contents of the local catalog cache (<c>%LocalAppData%\CodeAstrogator\models.json</c>).</summary>
    public sealed class ModelCatalogData
    {
        public int Version { get; set; } = 1;
        /// <summary>When the default catalog was last fetched from the repo (throttles the next fetch).</summary>
        public DateTimeOffset? DefaultsFetchedAt { get; set; }
        /// <summary>Where the defaults last came from: <c>remote</c> or <c>bundled</c>.</summary>
        public string DefaultsSource { get; set; } = "";
        public List<ModelEntry> Models { get; set; } = new List<ModelEntry>();
    }

    /// <summary>One row of the Model · Mode picker.</summary>
    public sealed class PickerModel
    {
        public PickerModel(string id, string label, string family, bool primary)
        {
            Id = id;
            Label = label;
            Family = family;
            Primary = primary;
        }

        public string Id { get; }
        public string Label { get; }
        public string Family { get; }
        /// <summary>Top-level row (newest available model of its family) vs. "More models" submenu.</summary>
        public bool Primary { get; }
    }

    /// <summary>
    /// Pure catalog logic: reading <c>models.json</c> (defaults and cache), merging the two, and
    /// splitting the result into the picker's top-level rows and its submenu. No I/O, no processes —
    /// <see cref="ClaudeModelCatalog"/> owns those.
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>Parses a default catalog (repo <c>models.json</c>). Entries without an
        /// <c>id</c> are skipped; a missing label falls back to the ID, a missing family to
        /// <c>other</c>. Returns an empty list when nothing usable is in there.</summary>
        public static IReadOnlyList<ModelEntry> ParseDefaults(string json)
        {
            var result = new List<ModelEntry>();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            JToken root;
            try { root = JToken.Parse(json); }
            catch { return result; }

            var models = (root as JObject)?["models"] as JArray ?? root as JArray;
            if (models == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in models.OfType<JObject>())
            {
                var id = (item.Value<string>("id") ?? "").Trim();
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                result.Add(new ModelEntry
                {
                    Id = id,
                    Label = NonEmpty(item.Value<string>("label"), id),
                    Family = NonEmpty(item.Value<string>("family"), "other"),
                    Source = ModelEntry.SourceCatalog,
                });
            }
            return result;
        }

        /// <summary>Parses the local cache file; null when it is missing/unreadable (caller then
        /// starts from the defaults).</summary>
        public static ModelCatalogData? ParseCache(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var root = ParseObject(json);
                var data = new ModelCatalogData
                {
                    Version = root.Value<int?>("version") ?? 1,
                    DefaultsSource = root.Value<string>("defaultsSource") ?? "",
                };
                data.DefaultsFetchedAt = ReadDate(root, "defaultsFetchedAt");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in (root["models"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var id = (item.Value<string>("id") ?? "").Trim();
                    if (id.Length == 0 || !seen.Add(id))
                        continue;
                    var entry = new ModelEntry
                    {
                        Id = id,
                        Label = NonEmpty(item.Value<string>("label"), id),
                        Family = NonEmpty(item.Value<string>("family"), "other"),
                        // Anything the user wrote by hand is unmarked → treat it as a user entry so a
                        // merge never overwrites or drops it.
                        Source = item.Value<string>("source") == ModelEntry.SourceCatalog
                            ? ModelEntry.SourceCatalog
                            : ModelEntry.SourceUser,
                        Available = item.Value<bool?>("available"),
                        CheckedCliVersion = item.Value<string>("checkedCliVersion"),
                    };
                    entry.CheckedAt = ReadDate(item, "checkedAt");
                    data.Models.Add(entry);
                }
                return data;
            }
            catch
            {
                return null;
            }
        }

        public static string Serialize(ModelCatalogData data)
        {
            var models = new JArray();
            foreach (var m in data.Models)
            {
                var item = new JObject
                {
                    ["id"] = m.Id,
                    ["label"] = m.Label,
                    ["family"] = m.Family,
                    ["source"] = m.Source,
                };
                if (m.Available != null)
                    item["available"] = m.Available.Value;
                if (!string.IsNullOrEmpty(m.CheckedCliVersion))
                    item["checkedCliVersion"] = m.CheckedCliVersion;
                if (m.CheckedAt != null)
                    item["checkedAt"] = m.CheckedAt.Value.ToString("o");
                models.Add(item);
            }

            var root = new JObject
            {
                ["_comment"] = new JArray(
                    "Local model catalog of Code Astrogator — availability is probed against the installed",
                    "Claude CLI and cached here (checkedCliVersion; a CLI update re-probes everything).",
                    "Own models can be added: id + label + family (\"opus\", \"fable\", \"sonnet\", \"haiku\" or",
                    "your own group). Entries with \"source\": \"user\" are never overwritten or removed by the",
                    "default catalog; delete this file to start over."),
                ["version"] = data.Version,
                ["defaultsSource"] = data.DefaultsSource ?? "",
            };
            if (data.DefaultsFetchedAt != null)
                root["defaultsFetchedAt"] = data.DefaultsFetchedAt.Value.ToString("o");
            root["models"] = models;
            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Merges the default catalog into the cache. Existing entries keep their probed availability
        /// and their position; catalog entries get their label/family refreshed from the defaults;
        /// user entries are left untouched. A default that is missing from the cache is inserted
        /// directly after its predecessor in the default list (so a newly released model lands next to
        /// its own generation rather than at the bottom) — that also makes it the family's top-level
        /// row as soon as the CLI accepts it.
        /// </summary>
        public static ModelCatalogData MergeDefaults(ModelCatalogData? cache, IReadOnlyList<ModelEntry> defaults)
        {
            var merged = new ModelCatalogData
            {
                Version = cache?.Version ?? 1,
                DefaultsFetchedAt = cache?.DefaultsFetchedAt,
                DefaultsSource = cache?.DefaultsSource ?? "",
                Models = (cache?.Models ?? new List<ModelEntry>()).Select(m => m.Clone()).ToList(),
            };

            var byId = merged.Models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < defaults.Count; i++)
            {
                var def = defaults[i];
                if (byId.TryGetValue(def.Id, out var existing))
                {
                    if (existing.Source == ModelEntry.SourceCatalog)
                    {
                        existing.Label = def.Label;   // the repo owns the presentation of its own entries
                        existing.Family = def.Family;
                    }
                    continue;
                }

                var entry = def.Clone();
                merged.Models.Insert(InsertIndexFor(merged.Models, defaults, i), entry);
                byId[entry.Id] = entry;
            }

            return merged;
        }

        /// <summary>Position for a new default: right after the closest preceding default that is
        /// already in the list; 0 when none of them is.</summary>
        private static int InsertIndexFor(
            List<ModelEntry> models, IReadOnlyList<ModelEntry> defaults, int defaultIndex)
        {
            for (var i = defaultIndex - 1; i >= 0; i--)
            {
                var idx = models.FindIndex(m => string.Equals(m.Id, defaults[i].Id, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    return idx + 1;
            }
            return 0;
        }

        /// <summary>True when this entry has to be probed against <paramref name="cliVersion"/>
        /// (never probed, or probed against a different CLI build).</summary>
        public static bool NeedsProbe(ModelEntry entry, string? cliVersion) =>
            entry.Available == null ||
            !string.Equals(entry.CheckedCliVersion ?? "", cliVersion ?? "", StringComparison.Ordinal);

        /// <summary>
        /// The picker rows: every model the CLI accepts, in catalog order. The first available model of
        /// each family is a top-level row (families ordered by their first appearance), all others go
        /// into the "More models" submenu. Models the CLI rejects are dropped entirely; models that
        /// have not been probed yet are treated as unavailable (never offer an ID that fails the turn).
        /// </summary>
        public static IReadOnlyList<PickerModel> BuildPicker(IEnumerable<ModelEntry> entries)
        {
            var all = entries.Where(e => !string.IsNullOrEmpty(e.Id)).ToList();

            // Family order comes from the catalog as a whole, not from what happens to be available:
            // an old CLI that cannot run the newest Opus must not push the whole Opus group to the
            // bottom of the picker.
            var familyOrder = new List<string>();
            foreach (var family in all.Select(FamilyOf))
            {
                if (!familyOrder.Contains(family, StringComparer.OrdinalIgnoreCase))
                    familyOrder.Add(family);
            }

            var available = all.Where(e => e.Available == true).ToList();
            var primaryOfFamily = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in available)
            {
                if (!primaryOfFamily.ContainsKey(FamilyOf(e)))
                    primaryOfFamily[FamilyOf(e)] = e;
            }

            var rows = new List<PickerModel>();
            foreach (var family in familyOrder)
            {
                if (primaryOfFamily.TryGetValue(family, out var e))
                    rows.Add(new PickerModel(e.Id, e.Label, family, true));
            }
            foreach (var e in available)
            {
                if (primaryOfFamily.TryGetValue(FamilyOf(e), out var primary) && primary == e)
                    continue;
                rows.Add(new PickerModel(e.Id, e.Label, FamilyOf(e), false));
            }
            return rows;
        }

        private static string FamilyOf(ModelEntry entry) =>
            string.IsNullOrEmpty(entry.Family) ? "other" : entry.Family;

        /// <summary>Parses with <c>DateParseHandling.None</c>: by default Newtonsoft turns an ISO
        /// timestamp into a <c>Date</c> token, and reading that back yields a culture-formatted string
        /// that no longer round-trips. Keeping every value textual makes the parse culture-proof.</summary>
        private static JObject ParseObject(string json)
        {
            using var reader = new JsonTextReader(new System.IO.StringReader(json))
            {
                DateParseHandling = DateParseHandling.None,
            };
            return JObject.Load(reader);
        }

        /// <summary>Reads an ISO timestamp (written by <see cref="Serialize"/> with "o").</summary>
        private static DateTimeOffset? ReadDate(JObject obj, string name)
        {
            var text = obj.Value<string>(name);
            return !string.IsNullOrWhiteSpace(text) &&
                   DateTimeOffset.TryParse(
                       text, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        private static string NonEmpty(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
    }
}
