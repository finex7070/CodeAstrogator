using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>An installed plugin, from <c>claude plugin list --json</c>.</summary>
    public sealed class InstalledPlugin
    {
        public string Id { get; set; } = "";
        public string Name => PluginIds.NameOf(Id);
        public string Marketplace => PluginIds.MarketplaceOf(Id);
        public string? Version { get; set; }
        /// <summary><c>user</c> | <c>project</c> | <c>local</c> | <c>managed</c>.</summary>
        public string Scope { get; set; } = "";
        public bool Enabled { get; set; }
        public bool ProjectEnabled { get; set; }
        public string? InstallPath { get; set; }
        public string? ProjectPath { get; set; }
        public DateTime? InstalledAt { get; set; }
        public DateTime? LastUpdated { get; set; }
        public bool HasUserConfig { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>A plugin a configured marketplace offers, from <c>list --available --json</c>.</summary>
    public sealed class AvailablePlugin
    {
        public string PluginId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? MarketplaceName { get; set; }
        public string? Version { get; set; }
        public long? InstallCount { get; set; }
    }

    /// <summary>A configured marketplace, from <c>plugin marketplace list --json</c>.</summary>
    public sealed class MarketplaceInfo
    {
        public string Name { get; set; } = "";
        /// <summary><c>github</c> | <c>git</c> | <c>url</c> | <c>directory</c> | <c>file</c> …</summary>
        public string? Source { get; set; }
        public string? Repo { get; set; }
        public string? Url { get; set; }
        public string? Path { get; set; }
        public string? InstallLocation { get; set; }
        /// <summary>Where it came from, for display: the repo, URL or path.</summary>
        public string Origin => Repo ?? Url ?? Path ?? "";
    }

    /// <summary>A marketplace-declared command the CLI showed but did not run (install/update): it runs only
    /// after a person has seen it, by passing <see cref="Sha256"/> back as <c>--accept-command</c>.</summary>
    public sealed class ShownCommand
    {
        public string? Command { get; set; }
        public string? Sha256 { get; set; }
        /// <summary>False when the sha256 that was passed no longer matches (the catalog changed).</summary>
        public bool? AcceptCommandMatched { get; set; }
    }

    /// <summary>The one result line a <c>--json</c> management command prints last.</summary>
    public sealed class PluginCommandResult
    {
        public string? Command { get; set; }
        /// <summary><c>ok</c> | <c>failed</c>; null when there was no result line (usage error, crash, timeout).</summary>
        public string? Outcome { get; set; }
        public string Message { get; set; } = "";
        public string? PluginId { get; set; }
        public string? Marketplace { get; set; }
        public string? Scope { get; set; }
        public string? FailureCode { get; set; }
        public bool AlreadyInGoalState { get; set; }
        public bool? KeptData { get; set; }
        public string? UpdateOutcome { get; set; }
        public string? OldVersion { get; set; }
        public string? NewVersion { get; set; }
        public ShownCommand? ShownCommand { get; set; }
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }

        /// <summary>Done — including "already in that state", which the CLI reports as failed but scripts
        /// are told to treat as success.</summary>
        public bool Succeeded => Outcome == "ok" || AlreadyInGoalState;

        /// <summary>The CLI wants a person to confirm a marketplace command first.</summary>
        public bool NeedsCommandConfirmation => !Succeeded && !string.IsNullOrEmpty(ShownCommand?.Sha256);
    }

    /// <summary>What <c>claude plugin details</c> reports (text only — there is no JSON form).</summary>
    public sealed class PluginDetails
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        /// <summary>Component kind → names, e.g. "Skills" → [commit, …]; "Hooks" → [PreToolUse, …].</summary>
        public Dictionary<string, List<string>> Inventory { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tokens added to every session ("Always-on: ~491 tok"); null when not reported.</summary>
        public int? AlwaysOnTokens { get; set; }
        public List<PluginComponentCost> Components { get; set; } = new List<PluginComponentCost>();
        /// <summary>The whole report, for display when parsing found less than expected.</summary>
        public string RawText { get; set; } = "";
    }

    public sealed class PluginComponentCost
    {
        public string Name { get; set; } = "";
        public string? AlwaysOn { get; set; }
        public string? OnInvoke { get; set; }
    }

    /// <summary>Splits plugin ids of the form <c>name@marketplace</c>.</summary>
    public static class PluginIds
    {
        public static string NameOf(string id)
        {
            var at = (id ?? "").LastIndexOf('@');
            return at > 0 ? id!.Substring(0, at) : id ?? "";
        }

        public static string MarketplaceOf(string id)
        {
            var at = (id ?? "").LastIndexOf('@');
            return at > 0 && at < id!.Length - 1 ? id.Substring(at + 1) : "";
        }
    }

    /// <summary>
    /// Plugins and marketplaces through <c>claude plugin …</c> (verified against CLI 2.1.319, 2026-11-06 —
    /// see docs/plugins-skills-plan.md). Every change goes through the official binary; this class never
    /// writes the CLI's files. The parsers are pure (tested on captured output); the commands run through
    /// <see cref="CliCommandRunner"/> or the runner given for tests.
    /// </summary>
    public sealed class ClaudePluginClient
    {
        public const int ShortTimeoutMs = 60_000;
        /// <summary>Installs and updates clone git repositories.</summary>
        public const int LongTimeoutMs = 5 * 60_000;

        private readonly Func<CliCommand, CancellationToken, Task<CliCommandResult>> _run;

        public ClaudePluginClient(string exe)
            : this((cmd, ct) => CliCommandRunner.RunAsync(exe, cmd, ct)) { }

        public ClaudePluginClient(Func<CliCommand, CancellationToken, Task<CliCommandResult>> run) => _run = run;

        // ── queries ─────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<InstalledPlugin>> ListInstalledAsync(string? workspace, CancellationToken ct)
        {
            var r = await RunAsync(workspace, ShortTimeoutMs, ct, "plugin", "list", "--json").ConfigureAwait(false);
            return ParseInstalled(r.StdOut);
        }

        public async Task<IReadOnlyList<AvailablePlugin>> ListAvailableAsync(string? workspace, CancellationToken ct)
        {
            var r = await RunAsync(workspace, LongTimeoutMs, ct, "plugin", "list", "--available", "--json").ConfigureAwait(false);
            return ParseAvailable(r.StdOut);
        }

        public async Task<IReadOnlyList<MarketplaceInfo>> ListMarketplacesAsync(string? workspace, CancellationToken ct)
        {
            var r = await RunAsync(workspace, ShortTimeoutMs, ct, "plugin", "marketplace", "list", "--json").ConfigureAwait(false);
            return ParseMarketplaces(r.StdOut);
        }

        public async Task<PluginDetails?> DetailsAsync(string name, string? workspace, CancellationToken ct)
        {
            var r = await RunAsync(workspace, ShortTimeoutMs, ct, "plugin", "details", name).ConfigureAwait(false);
            return r.ExitCode == 0 ? ParseDetails(r.StdOut) : null;
        }

        // ── changes ─────────────────────────────────────────────────────────

        /// <param name="acceptCommandSha256">Only after a person saw the command a previous run reported in
        /// <see cref="PluginCommandResult.ShownCommand"/>.</param>
        public Task<PluginCommandResult> InstallAsync(string pluginId, string scope, string? workspace,
            string? acceptCommandSha256, CancellationToken ct)
        {
            var args = new List<string> { "plugin", "install", pluginId, "--scope", scope, "--json" };
            if (!string.IsNullOrEmpty(acceptCommandSha256))
                args.AddRange(new[] { "--accept-command", acceptCommandSha256! });
            return ChangeAsync(args, workspace, LongTimeoutMs, ct);
        }

        public Task<PluginCommandResult> EnableAsync(string pluginId, string scope, string? workspace, CancellationToken ct) =>
            ChangeAsync(new List<string> { "plugin", "enable", pluginId, "--scope", scope, "--json" }, workspace, ShortTimeoutMs, ct);

        public Task<PluginCommandResult> DisableAsync(string pluginId, string scope, string? workspace, CancellationToken ct) =>
            ChangeAsync(new List<string> { "plugin", "disable", pluginId, "--scope", scope, "--json" }, workspace, ShortTimeoutMs, ct);

        public Task<PluginCommandResult> UpdateAsync(string pluginId, string scope, string? workspace,
            string? acceptCommandSha256, CancellationToken ct)
        {
            var args = new List<string> { "plugin", "update", pluginId, "--scope", scope, "--json" };
            if (!string.IsNullOrEmpty(acceptCommandSha256))
                args.AddRange(new[] { "--accept-command", acceptCommandSha256! });
            return ChangeAsync(args, workspace, LongTimeoutMs, ct);
        }

        public Task<PluginCommandResult> UninstallAsync(string pluginId, string scope, bool keepData, string? workspace, CancellationToken ct)
        {
            var args = new List<string> { "plugin", "uninstall", pluginId, "--scope", scope, "--json" };
            if (keepData)
                args.Add("--keep-data");
            return ChangeAsync(args, workspace, ShortTimeoutMs, ct);
        }

        public Task<PluginCommandResult> AddMarketplaceAsync(string source, string scope, string? workspace, CancellationToken ct) =>
            ChangeAsync(new List<string> { "plugin", "marketplace", "add", source, "--scope", scope, "--json" }, workspace, LongTimeoutMs, ct);

        /// <summary>Needs a name: without one the CLI refuses <c>--json</c>.</summary>
        public Task<PluginCommandResult> UpdateMarketplaceAsync(string name, string? workspace, CancellationToken ct) =>
            ChangeAsync(new List<string> { "plugin", "marketplace", "update", name, "--json" }, workspace, LongTimeoutMs, ct);

        /// <param name="scope">Null = remove the declaration from every scope (the CLI's default).</param>
        public Task<PluginCommandResult> RemoveMarketplaceAsync(string name, string? scope, string? workspace, CancellationToken ct)
        {
            var args = new List<string> { "plugin", "marketplace", "remove", name, "--json" };
            if (!string.IsNullOrEmpty(scope))
                args.AddRange(new[] { "--scope", scope! });
            return ChangeAsync(args, workspace, ShortTimeoutMs, ct);
        }

        private async Task<PluginCommandResult> ChangeAsync(List<string> args, string? workspace, int timeoutMs, CancellationToken ct)
        {
            var r = await RunAsync(workspace, timeoutMs, ct, args.ToArray()).ConfigureAwait(false);
            var result = ParseResultLine(r.StdOut) ?? new PluginCommandResult
            {
                // no result line: usage error, crash or timeout — say what the CLI said
                Message = r.StartError
                    ?? (r.TimedOut ? "The Claude CLI did not finish in time." : FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? "The Claude CLI reported an error."),
            };
            result.ExitCode = r.ExitCode;
            result.TimedOut = r.TimedOut;
            return result;
        }

        private Task<CliCommandResult> RunAsync(string? workspace, int timeoutMs, CancellationToken ct, params string[] args) =>
            _run(new CliCommand { Args = args, WorkingDirectory = workspace, TimeoutMs = timeoutMs }, ct);

        private static string? FirstLine(string text) =>
            (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

        // ── parsers (pure) ──────────────────────────────────────────────────

        public static IReadOnlyList<InstalledPlugin> ParseInstalled(string json)
        {
            var list = new List<InstalledPlugin>();
            foreach (var o in JsonArrayOf(json))
            {
                var id = o.Value<string>("id");
                if (string.IsNullOrEmpty(id))
                    continue;
                list.Add(new InstalledPlugin
                {
                    Id = id!,
                    Version = o.Value<string>("version"),
                    Scope = o.Value<string>("scope") ?? "",
                    Enabled = o.Value<bool?>("enabled") ?? false,
                    ProjectEnabled = o.Value<bool?>("projectEnabled") ?? false,
                    InstallPath = o.Value<string>("installPath"),
                    ProjectPath = o.Value<string>("projectPath"),
                    InstalledAt = Date(o["installedAt"]),
                    LastUpdated = Date(o["lastUpdated"]),
                    HasUserConfig = o.Value<bool?>("hasUserConfig") ?? false,
                    Errors = Strings(o["errors"]),
                });
            }
            return list;
        }

        /// <summary><c>{ installed: […], available: […] }</c> — only the available ones.</summary>
        public static IReadOnlyList<AvailablePlugin> ParseAvailable(string json)
        {
            var list = new List<AvailablePlugin>();
            var root = JsonObjectOf(json);
            if (!(root?["available"] is JArray available))
                return list;
            foreach (var o in available.OfType<JObject>())
            {
                var id = o.Value<string>("pluginId");
                if (string.IsNullOrEmpty(id))
                    continue;
                list.Add(new AvailablePlugin
                {
                    PluginId = id!,
                    Name = o.Value<string>("name") ?? PluginIds.NameOf(id!),
                    Description = o.Value<string>("description"),
                    MarketplaceName = o.Value<string>("marketplaceName") ?? PluginIds.MarketplaceOf(id!),
                    Version = o.Value<string>("version"),
                    InstallCount = o["installCount"]?.Type == JTokenType.Integer ? o.Value<long>("installCount") : (long?)null,
                });
            }
            return list;
        }

        public static IReadOnlyList<MarketplaceInfo> ParseMarketplaces(string json) =>
            JsonArrayOf(json)
                .Where(o => !string.IsNullOrEmpty(o.Value<string>("name")))
                .Select(o => new MarketplaceInfo
                {
                    Name = o.Value<string>("name")!,
                    Source = o.Value<string>("source"),
                    Repo = o.Value<string>("repo"),
                    Url = o.Value<string>("url"),
                    Path = o.Value<string>("path"),
                    InstallLocation = o.Value<string>("installLocation"),
                })
                .ToList();

        /// <summary>The last stdout line that is a JSON object with a <c>command</c> (the documented contract:
        /// "the last line of stdout is one JSON object"). Null when there is none.</summary>
        public static PluginCommandResult? ParseResultLine(string stdout)
        {
            var lines = (stdout ?? "").Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal))
                    continue;
                JObject o;
                try { o = JObject.Parse(line); }
                catch { continue; }
                if (o["command"] == null && o["outcome"] == null)
                    continue;
                var shown = o["shownCommand"] as JObject;
                return new PluginCommandResult
                {
                    Command = o.Value<string>("command"),
                    Outcome = o.Value<string>("outcome"),
                    Message = o.Value<string>("message") ?? "",
                    PluginId = o.Value<string>("pluginId") ?? o.Value<string>("plugin"),
                    Marketplace = o.Value<string>("marketplace"),
                    Scope = o.Value<string>("scope"),
                    FailureCode = o.Value<string>("failureCode"),
                    AlreadyInGoalState = o.Value<bool?>("alreadyInGoalState") ?? false,
                    KeptData = o.Value<bool?>("keptData"),
                    UpdateOutcome = o.Value<string>("updateOutcome"),
                    OldVersion = o.Value<string>("oldVersion"),
                    NewVersion = o.Value<string>("newVersion"),
                    ShownCommand = shown == null ? null : new ShownCommand
                    {
                        Command = shown.Value<string>("command") ?? shown.Value<string>("displayed") ?? shown.Value<string>("text"),
                        Sha256 = shown.Value<string>("sha256"),
                        AcceptCommandMatched = shown.Value<bool?>("acceptCommandMatched"),
                    },
                };
            }
            return null;
        }

        private static readonly Regex InventoryLine = new Regex(@"^\s{2}(?<kind>[A-Za-z][A-Za-z ]*?)\s+\((?<n>\d+)\)\s*(?<items>.*)$", RegexOptions.Compiled);
        private static readonly Regex AlwaysOnLine = new Regex(@"Always-on:\s*~?\s*(?<n>[\d.,]+)\s*(?<k>k?)\s*tok", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ComponentLine = new Regex(@"^\s{2}(?<name>\S+)\s{2,}(?<on>(<\s*)?~?[\d.,]+k?)\s{2,}(?<inv>(<\s*)?~?[\d.,]+k?)\s*$", RegexOptions.Compiled);

        /// <summary>Parses the text of <c>plugin details</c> (measured on CLI 2.1.319): a "name version" head,
        /// "Description:", the "Component inventory" lines ("  Skills (3)  a, b, c"), "Always-on: ~71 tok" and the
        /// per-component table. Whatever does not parse stays only in <see cref="PluginDetails.RawText"/>.</summary>
        public static PluginDetails ParseDetails(string text)
        {
            var clean = ClaudeBackgroundSessions.StripAnsi(text ?? "").Replace("\r", "");
            var details = new PluginDetails { RawText = clean.Trim() };
            var lines = clean.Split('\n');
            var head = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            if (head != null)
            {
                var sp = head.LastIndexOf(' ');
                details.Name = sp > 0 ? head.Substring(0, sp) : head;
                details.Version = sp > 0 ? head.Substring(sp + 1) : null;
            }
            var section = "";
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("Description:", StringComparison.Ordinal))
                    details.Description = trimmed.Substring("Description:".Length).Trim();
                else if (trimmed == "Component inventory" || trimmed == "Projected token cost" || trimmed.StartsWith("Per-component", StringComparison.Ordinal))
                    section = trimmed;
                else if (section == "Component inventory" && InventoryLine.Match(raw) is { Success: true } inv)
                {
                    var items = inv.Groups["items"].Value;
                    var paren = items.IndexOf("  (", StringComparison.Ordinal); // "(harness-only — …)"
                    if (paren >= 0)
                        items = items.Substring(0, paren);
                    details.Inventory[inv.Groups["kind"].Value.Trim()] = items
                        .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                }
                else if (AlwaysOnLine.Match(raw) is { Success: true } on)
                    details.AlwaysOnTokens = Tokens(on.Groups["n"].Value, on.Groups["k"].Value);
                else if (section.StartsWith("Per-component", StringComparison.Ordinal)
                    && ComponentLine.Match(raw) is { Success: true } comp
                    && comp.Groups["name"].Value != "component")
                {
                    details.Components.Add(new PluginComponentCost
                    {
                        Name = comp.Groups["name"].Value,
                        AlwaysOn = comp.Groups["on"].Value.Replace(" ", ""),
                        OnInvoke = comp.Groups["inv"].Value.Replace(" ", ""),
                    });
                }
            }
            return details;
        }

        private static int? Tokens(string number, string k)
        {
            if (!double.TryParse(number.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return null;
            return (int)Math.Round(k.Length > 0 ? n * 1000 : n);
        }

        private static IEnumerable<JObject> JsonArrayOf(string json)
        {
            var start = (json ?? "").IndexOf('[');
            if (start < 0)
                return Enumerable.Empty<JObject>();
            try { return JArray.Parse(json!.Substring(start)).OfType<JObject>().ToList(); }
            catch { return Enumerable.Empty<JObject>(); }
        }

        private static JObject? JsonObjectOf(string json)
        {
            var start = (json ?? "").IndexOf('{');
            if (start < 0)
                return null;
            try { return JObject.Parse(json!.Substring(start)); }
            catch { return null; }
        }

        private static DateTime? Date(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Date)
                return token.Value<DateTime>().ToUniversalTime();
            return DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
        }

        private static List<string> Strings(JToken? token) =>
            token is JArray a
                ? a.Select(t => t.Type == JTokenType.String ? t.ToString() : (t["message"]?.ToString() ?? t.ToString(Newtonsoft.Json.Formatting.None))).ToList()
                : new List<string>();
    }
}
