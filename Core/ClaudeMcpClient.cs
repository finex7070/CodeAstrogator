using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>One MCP server the CLI knows. Secrets stay out: of env and headers only the names are kept.</summary>
    public sealed class McpServerInfo
    {
        public string Name { get; set; } = "";
        /// <summary><c>user</c> | <c>local</c> | <c>project</c> (editable) — or <c>plugin</c> | <c>claude.ai</c> |
        /// <c>other</c> (listed by the CLI, managed elsewhere).</summary>
        public string Scope { get; set; } = "";
        /// <summary><c>stdio</c> | <c>http</c> | <c>sse</c> | <c>ws</c> …</summary>
        public string? Transport { get; set; }
        public string? Command { get; set; }
        public List<string> Args { get; set; } = new List<string>();
        public string? Url { get; set; }
        public List<string> EnvNames { get; set; } = new List<string>();
        public List<string> HeaderNames { get; set; } = new List<string>();
        /// <summary><c>connected</c> | <c>failed</c> | <c>pending</c> (project server awaiting approval) |
        /// <c>needs-auth</c> | <c>unknown</c> (not checked yet).</summary>
        public string Status { get; set; } = McpStatus.Unknown;
        /// <summary>The CLI's reason, e.g. "CONNECTION_CLOSED: Connection closed".</summary>
        public string? StatusDetail { get; set; }
        /// <summary>What the CLI printed as the target (command line or URL).</summary>
        public string? Target { get; set; }
        public bool ReadOnly => Scope != ClaudeSettingsFiles.User && Scope != ClaudeSettingsFiles.Local && Scope != ClaudeSettingsFiles.Project;
    }

    public static class McpStatus
    {
        public const string Connected = "connected";
        public const string Failed = "failed";
        public const string Pending = "pending";
        public const string NeedsAuth = "needs-auth";
        public const string Unknown = "unknown";
    }

    /// <summary>A <c>claude mcp</c> change — the command prints text only (no <c>--json</c>).</summary>
    public sealed class McpCommandResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; } = "";
        public bool TimedOut { get; set; }
    }

    /// <summary>
    /// MCP servers through <c>claude mcp …</c> (verified against CLI 2.1.319, 2026-11-06 — see
    /// docs/plugins-skills-plan.md). The configuration is read from the CLI's files — <c>~/.claude.json</c>
    /// (<c>mcpServers</c> = user scope, <c>projects[&lt;workspace&gt;].mcpServers</c> = local) and
    /// <c>&lt;workspace&gt;/.mcp.json</c> (project) — because <c>mcp list</c>/<c>get</c> have no JSON form; the live
    /// status comes from <c>mcp list</c>. Changes only through the CLI.
    /// </summary>
    public sealed class ClaudeMcpClient
    {
        /// <summary><c>mcp list</c> starts every approved stdio server briefly to check it.</summary>
        public const int ListTimeoutMs = 2 * 60_000;
        public const int ChangeTimeoutMs = 60_000;

        private readonly Func<CliCommand, CancellationToken, Task<CliCommandResult>> _run;

        public ClaudeMcpClient(string exe)
            : this((cmd, ct) => CliCommandRunner.RunAsync(exe, cmd, ct)) { }

        public ClaudeMcpClient(Func<CliCommand, CancellationToken, Task<CliCommandResult>> run) => _run = run;

        /// <summary>The configured servers with their live status (runs the health check).</summary>
        public async Task<IReadOnlyList<McpServerInfo>> ListAsync(string? workspace, CancellationToken ct, string? claudeJsonPath = null)
        {
            var configured = ReadConfigured(workspace, claudeJsonPath);
            var r = await _run(new CliCommand { Args = new[] { "mcp", "list" }, WorkingDirectory = workspace, TimeoutMs = ListTimeoutMs }, ct)
                .ConfigureAwait(false);
            return Merge(configured, ParseList(r.StdOut + "\n" + r.StdErr));
        }

        /// <summary>Adds a stdio server: <c>mcp add &lt;name&gt; --scope s -e K=V … -- &lt;command&gt; [args…]</c>
        /// (<c>--</c> so the server's own flags are not read as the CLI's).</summary>
        public Task<McpCommandResult> AddStdioAsync(string name, string command, IEnumerable<string> args,
            IDictionary<string, string>? env, string scope, string? workspace, CancellationToken ct)
        {
            var list = new List<string> { "mcp", "add", name, "--scope", scope };
            foreach (var kv in env ?? new Dictionary<string, string>())
                list.AddRange(new[] { "--env", kv.Key + "=" + kv.Value });
            list.Add("--");
            list.Add(command);
            list.AddRange(args ?? Enumerable.Empty<string>());
            return ChangeAsync(list, workspace, ct);
        }

        /// <summary>Adds an HTTP/SSE server; headers go last (the option takes several values).</summary>
        public Task<McpCommandResult> AddRemoteAsync(string name, string transport, string url,
            IDictionary<string, string>? headers, string scope, string? workspace, CancellationToken ct)
        {
            var list = new List<string> { "mcp", "add", "--transport", transport, "--scope", scope, name, url };
            foreach (var kv in headers ?? new Dictionary<string, string>())
                list.AddRange(new[] { "--header", kv.Key + ": " + kv.Value });
            return ChangeAsync(list, workspace, ct);
        }

        public Task<McpCommandResult> RemoveAsync(string name, string scope, string? workspace, CancellationToken ct) =>
            ChangeAsync(new List<string> { "mcp", "remove", name, "--scope", scope }, workspace, ct);

        private async Task<McpCommandResult> ChangeAsync(List<string> args, string? workspace, CancellationToken ct)
        {
            var r = await _run(new CliCommand { Args = args, WorkingDirectory = workspace, TimeoutMs = ChangeTimeoutMs }, ct)
                .ConfigureAwait(false);
            var text = ClaudeBackgroundSessions.StripAnsi((r.StdOut + "\n" + r.StdErr).Replace("\r", ""));
            var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0)
                .Where(l => !l.StartsWith("File modified:", StringComparison.Ordinal)) // a local path, noise for the panel
                .ToList();
            return new McpCommandResult
            {
                Succeeded = r.ExitCode == 0 && !r.TimedOut && r.StartError == null,
                TimedOut = r.TimedOut,
                Message = r.StartError ?? (r.TimedOut ? "The Claude CLI did not finish in time." : string.Join("\n", lines)),
            };
        }

        /// <summary>Approves or rejects a project (<c>.mcp.json</c>) server the way the CLI's own prompt does: in the
        /// workspace's <c>.claude/settings.local.json</c>, <c>enabledMcpjsonServers</c> / <c>disabledMcpjsonServers</c>
        /// (string lists; a rejection in any settings file wins — settings docs, verified 2026-11-10). Moves the name
        /// from one list to the other. Note: <c>claude -p</c> turns load project servers without asking, so for the
        /// extension's own turns only a rejection changes anything.</summary>
        public static bool SetProjectApproval(string workspace, string name, bool approve)
        {
            var path = ClaudeSettingsFiles.PathOf(ClaudeSettingsFiles.Local, workspace);
            if (path == null || string.IsNullOrEmpty(name))
                return false;
            return ClaudeSettingsFiles.Update(path, root =>
            {
                Edit(root, approve ? "enabledMcpjsonServers" : "disabledMcpjsonServers", add: true);
                Edit(root, approve ? "disabledMcpjsonServers" : "enabledMcpjsonServers", add: false);
            });

            void Edit(JObject root, string key, bool add)
            {
                var list = root[key] as JArray;
                if (add)
                {
                    if (list == null)
                        root[key] = list = new JArray();
                    if (!list.Any(t => t.Type == JTokenType.String && t.ToString() == name))
                        list.Add(name);
                }
                else if (list != null)
                {
                    foreach (var t in list.Where(t => t.Type == JTokenType.String && t.ToString() == name).ToList())
                        t.Remove();
                    if (!list.HasValues)
                        root.Remove(key);
                }
            }
        }

        // ── parsers (pure) ──────────────────────────────────────────────────

        /// <summary>The servers in the CLI's files: user + local from <c>~/.claude.json</c>, project from
        /// <c>.mcp.json</c>. Values of env/headers are dropped (secrets).</summary>
        public static IReadOnlyList<McpServerInfo> ReadConfigured(string? workspace, string? claudeJsonPath = null)
        {
            var list = new List<McpServerInfo>();
            claudeJsonPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            var root = ClaudeSettingsFiles.Read(claudeJsonPath);
            list.AddRange(ServersOf(root?["mcpServers"] as JObject, ClaudeSettingsFiles.User));
            if (!string.IsNullOrEmpty(workspace) && root?["projects"] is JObject projects)
            {
                var key = ProjectKey(workspace!);
                var entry = projects.Properties()
                    .FirstOrDefault(p => string.Equals(ProjectKey(p.Name), key, StringComparison.OrdinalIgnoreCase))?.Value as JObject;
                list.AddRange(ServersOf(entry?["mcpServers"] as JObject, ClaudeSettingsFiles.Local));
            }
            if (!string.IsNullOrEmpty(workspace))
            {
                var mcpJson = ClaudeSettingsFiles.Read(Path.Combine(workspace, ".mcp.json"));
                list.AddRange(ServersOf(mcpJson?["mcpServers"] as JObject, ClaudeSettingsFiles.Project));
            }
            return list;
        }

        /// <summary>The CLI keys projects by the working directory with forward slashes (<c>C:/Users/…</c>).</summary>
        public static string ProjectKey(string path) => path.Replace('\\', '/').TrimEnd('/');

        private static IEnumerable<McpServerInfo> ServersOf(JObject? servers, string scope)
        {
            if (servers == null)
                yield break;
            foreach (var p in servers.Properties())
            {
                if (!(p.Value is JObject o))
                    continue;
                var url = o.Value<string>("url");
                yield return new McpServerInfo
                {
                    Name = p.Name,
                    Scope = scope,
                    Transport = o.Value<string>("type") ?? (url != null ? "http" : "stdio"),
                    Command = o.Value<string>("command"),
                    Args = (o["args"] as JArray)?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                    Url = url,
                    EnvNames = (o["env"] as JObject)?.Properties().Select(e => e.Name).ToList() ?? new List<string>(),
                    HeaderNames = (o["headers"] as JObject)?.Properties().Select(h => h.Name).ToList() ?? new List<string>(),
                };
            }
        }

        /// <summary>Words a status starts with (after its symbol).</summary>
        private static readonly string[] StatusWords = { "Connected", "Failed", "Pending", "Needs", "Disabled", "Connecting", "Error" };

        /// <summary>One line per server, <c>&lt;name&gt;: &lt;command or url&gt; [(HTTP)] - &lt;status&gt;</c> (measured):
        /// <c>✔ Connected</c>, <c>✘ Failed to connect — &lt;reason&gt;</c>, <c>⏸ Pending approval (…)</c>. Names may
        /// contain colons (<c>plugin:a:b</c>) or spaces (<c>claude.ai Docs</c>) — the first ": " ends the name.
        /// The symbols differ by terminal: through a redirected pipe from .NET the CLI prints the console fallbacks
        /// (<c>√ Connected</c> — measured), so the status is found by its <b>word</b>, not its symbol: the first
        /// " - " followed by a status word (a "-" inside the target or the reason cannot be mistaken for it).</summary>
        public static IReadOnlyList<McpServerInfo> ParseList(string text)
        {
            var list = new List<McpServerInfo>();
            foreach (var raw in ClaudeBackgroundSessions.StripAnsi(text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                var sep = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep <= 0)
                    continue;
                var dash = -1;
                for (var i = line.IndexOf(" - ", sep, StringComparison.Ordinal); i >= 0; i = line.IndexOf(" - ", i + 1, StringComparison.Ordinal))
                {
                    var rest = StripSymbols(line.Substring(i + 3));
                    if (StatusWords.Any(w => rest.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                    {
                        dash = i;
                        break;
                    }
                }
                if (dash <= sep)
                    continue;
                var name = line.Substring(0, sep);
                var target = line.Substring(sep + 2, dash - sep - 2).Trim();
                var status = line.Substring(dash + 3).Trim();
                var info = new McpServerInfo { Name = name, Target = target };
                if (target.EndsWith("(HTTP)", StringComparison.Ordinal))
                {
                    info.Transport = "http";
                    info.Target = target.Substring(0, target.Length - 6).Trim();
                }
                else if (target.EndsWith("(SSE)", StringComparison.Ordinal))
                {
                    info.Transport = "sse";
                    info.Target = target.Substring(0, target.Length - 5).Trim();
                }
                (info.Status, info.StatusDetail) = ClassifyStatus(status);
                info.Scope = name.StartsWith("plugin:", StringComparison.Ordinal) ? "plugin"
                    : name.StartsWith("claude.ai ", StringComparison.Ordinal) ? "claude.ai"
                    : "other";
                list.Add(info);
            }
            return list;
        }

        /// <summary>Drops the leading status symbol, whatever the terminal made of it (✔ / √, ✘ / ×, ⏸, ⚠ / ‼ …).</summary>
        private static string StripSymbols(string s) => new string(s.SkipWhile(c => !char.IsLetter(c)).ToArray());

        private static (string, string?) ClassifyStatus(string status)
        {
            var s = StripSymbols(status);
            // "Failed to connect — <reason>"; a console without the em dash may print " - "
            var dash = s.IndexOf(" — ", StringComparison.Ordinal);
            if (dash < 0)
                dash = s.IndexOf(" - ", StringComparison.Ordinal);
            var detail = dash >= 0 ? s.Substring(dash + 3).Trim() : null;
            if (s.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                return (McpStatus.Connected, null);
            if (s.StartsWith("Pending approval", StringComparison.OrdinalIgnoreCase))
                return (McpStatus.Pending, null);
            if (s.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                return (McpStatus.NeedsAuth, detail ?? s);
            if (s.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
                return (McpStatus.Failed, detail);
            return (McpStatus.Unknown, s.Length > 0 ? s : null);
        }

        /// <summary>The configured servers with the status <c>mcp list</c> reported, plus the servers only the CLI
        /// knows (from plugins, claude.ai connectors) as read-only rows.</summary>
        public static IReadOnlyList<McpServerInfo> Merge(IReadOnlyList<McpServerInfo> configured, IReadOnlyList<McpServerInfo> listed)
        {
            var result = new List<McpServerInfo>();
            var byName = listed.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in configured)
            {
                if (byName.TryGetValue(c.Name, out var l) && used.Add(c.Name))
                {
                    c.Status = l.Status;
                    c.StatusDetail = l.StatusDetail;
                    c.Target = l.Target;
                }
                result.Add(c);
            }
            result.AddRange(listed.Where(l => !used.Contains(l.Name) && !configured.Any(c => c.Name == l.Name)));
            return result;
        }
    }
}
