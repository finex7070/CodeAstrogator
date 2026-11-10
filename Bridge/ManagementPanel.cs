using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace CodeAstrogator.Bridge
{
    /// <summary>
    /// Host side of the "Plugins, skills &amp; MCP" panel (docs/plugins-skills-plan.md, phase 3: read-only).
    /// Answers the page's <c>manage.*</c> requests off the UI thread through the core clients — every CLI call is
    /// a hidden <c>claude plugin …</c> / <c>claude mcp …</c> process — and posts the results back.
    /// </summary>
    internal sealed class ManagementPanel
    {
        private readonly CodeAstrogatorPackage _package;
        private readonly Action<JObject> _post;
        // names of the skills the last turn's session loaded (system/init) — marks which skills are in use
        private volatile IReadOnlyList<string>? _loadedSkills;
        // one in-flight load per tab; a second request for the same tab waits for nothing and is dropped
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.Ordinal);

        public ManagementPanel(CodeAstrogatorPackage package, Action<JObject> post)
        {
            _package = package;
            _post = post;
        }

        /// <summary>From the stream thread, on every <c>system/init</c>.</summary>
        public void SetLoadedSkills(IReadOnlyList<string> names) => _loadedSkills = names;

        /// <summary>A <c>manage.*</c> message from the page (UI thread). False when it is not one of ours.</summary>
        public bool Handle(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (msg.Value<string>("type"))
            {
                case "manage.load":
                    Load(msg.Value<string>("tab") ?? "");
                    return true;
                case "manage.details":
                    LoadDetails(msg.Value<string>("id") ?? "");
                    return true;
                case "manage.action":
                    RunAction(msg);
                    return true;
                default:
                    return false;
            }
        }

        // ── actions (phase 4) ───────────────────────────────────────────────
        // One at a time: plugin, MCP and skill changes all write the same settings files.
        private readonly SemaphoreSlim _actionGate = new SemaphoreSlim(1, 1);

        private sealed class ActionOutcome
        {
            public bool Ok;
            public string Message = "";
            public ShownCommand? Confirm;    // the CLI wants a person to see this marketplace command first
            public string[] Reload = Array.Empty<string>();
            public string? OpenPath;         // a new SKILL.md to open in the editor
        }

        private void RunAction(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var requestId = msg.Value<string>("requestId") ?? "";
            var action = msg.Value<string>("action") ?? "";
            var workspace = _package.GetSolutionDirectory();
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                ActionOutcome outcome;
                await _actionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    outcome = await ExecuteAsync(action, msg, exe, workspace).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    outcome = new ActionOutcome { Message = ex.Message };
                }
                finally
                {
                    _actionGate.Release();
                }
                var result = new JObject
                {
                    ["type"] = "manage.actionResult",
                    ["requestId"] = requestId,
                    ["action"] = action,
                    ["ok"] = outcome.Ok,
                    ["message"] = outcome.Message,
                    ["reload"] = new JArray(outcome.Reload),
                };
                if (outcome.Confirm != null)
                    result["confirm"] = new JObject { ["command"] = outcome.Confirm.Command, ["sha256"] = outcome.Confirm.Sha256 };
                if (outcome.OpenPath != null)
                    result["openPath"] = outcome.OpenPath;
                _post(result);
            }).Task.Forget();
        }

        private static async Task<ActionOutcome> ExecuteAsync(string action, JObject m, string? exe, string? workspace)
        {
            var ct = CancellationToken.None; // the runner's own timeouts bound every call
            string S(string key) => m.Value<string>(key) ?? "";
            string? Opt(string key) => string.IsNullOrEmpty(m.Value<string>(key)) ? null : m.Value<string>(key);

            // skills need no CLI
            switch (action)
            {
                case "skill.state":
                    return SetSkillState(S("name"), S("value"), S("scope"), workspace);
                case "skill.bundled":
                {
                    var ok = SkillCatalog.WriteBundledDisabled(S("scope"), workspace, m.Value<bool>("disabled"));
                    return Done(ok, ok ? "" : "The settings file could not be written (is it valid JSON?).", "skills");
                }
                case "skill.create":
                {
                    var dir = S("where") == "project"
                        ? (string.IsNullOrEmpty(workspace) ? null : System.IO.Path.Combine(workspace, ".claude", "skills"))
                        : System.IO.Path.Combine(ClaudeSettingsFiles.UserClaudeDir(), "skills");
                    if (dir == null)
                        return Done(false, "Open a solution or folder to create a project skill.");
                    var file = SkillCatalog.CreateSkill(dir, S("name"), S("description"));
                    var o = Done(true, "Created " + S("name") + ".", "skills");
                    o.OpenPath = file;
                    return o;
                }
                case "skill.delete":
                {
                    var file = S("file");
                    var skill = SkillCatalog.Scan(new SkillScanContext { Workspace = workspace })
                        .FirstOrDefault(s => string.Equals(s.SkillFile, file, StringComparison.OrdinalIgnoreCase));
                    if (skill == null)
                        return Done(false, "That skill is no longer there.", "skills");
                    SkillCatalog.DeleteSkill(skill); // refuses anything but personal/project skills
                    return Done(true, "Deleted " + skill.Name + ".", "skills");
                }
                case "mcp.approve":
                    return ApproveProjectServer(S("name"), m.Value<bool>("approve"), workspace);
            }

            if (exe == null)
                return Done(false, "The Claude CLI was not found.");
            var plugins = new ClaudePluginClient(exe);
            var mcp = new ClaudeMcpClient(exe);
            switch (action)
            {
                case "plugin.install":
                    return FromPlugin(await plugins.InstallAsync(S("id"), S("scope"), workspace, Opt("acceptSha256"), ct).ConfigureAwait(false),
                        "installed", "discover", "skills", "mcp");
                case "plugin.enable":
                    return FromPlugin(await plugins.EnableAsync(S("id"), S("scope"), workspace, ct).ConfigureAwait(false), "installed", "skills", "mcp");
                case "plugin.disable":
                    return FromPlugin(await plugins.DisableAsync(S("id"), S("scope"), workspace, ct).ConfigureAwait(false), "installed", "skills", "mcp");
                case "plugin.update":
                    return FromPlugin(await plugins.UpdateAsync(S("id"), S("scope"), workspace, Opt("acceptSha256"), ct).ConfigureAwait(false), "installed");
                case "plugin.uninstall":
                    return FromPlugin(await plugins.UninstallAsync(S("id"), S("scope"), m.Value<bool>("keepData"), workspace, ct).ConfigureAwait(false),
                        "installed", "discover", "skills", "mcp");
                case "marketplace.add":
                    return FromPlugin(await plugins.AddMarketplaceAsync(S("source"), S("scope"), workspace, ct).ConfigureAwait(false), "marketplaces", "discover");
                case "marketplace.update":
                    return FromPlugin(await plugins.UpdateMarketplaceAsync(S("name"), workspace, ct).ConfigureAwait(false), "marketplaces", "discover");
                case "marketplace.remove":
                    return FromPlugin(await plugins.RemoveMarketplaceAsync(S("name"), null, workspace, ct).ConfigureAwait(false), "marketplaces", "discover");
                case "mcp.addStdio":
                {
                    var args = (m["args"] as JArray)?.Select(a => a.ToString()).ToList() ?? new List<string>();
                    var env = (m["env"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
                    return FromMcp(await mcp.AddStdioAsync(S("name"), S("command"), args, env, S("scope"), workspace, ct).ConfigureAwait(false));
                }
                case "mcp.addRemote":
                {
                    var headers = (m["headers"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
                    return FromMcp(await mcp.AddRemoteAsync(S("name"), S("transport"), S("url"), headers, S("scope"), workspace, ct).ConfigureAwait(false));
                }
                case "mcp.remove":
                    return FromMcp(await mcp.RemoveAsync(S("name"), S("scope"), workspace, ct).ConfigureAwait(false));
                default:
                    return Done(false, "Unknown action: " + action);
            }
        }

        private static ActionOutcome Done(bool ok, string message, params string[] reload) =>
            new ActionOutcome { Ok = ok, Message = message, Reload = reload };

        private static ActionOutcome FromPlugin(PluginCommandResult r, params string[] reload) => new ActionOutcome
        {
            Ok = r.Succeeded,
            Message = r.Message,
            Confirm = r.NeedsCommandConfirmation ? r.ShownCommand : null,
            Reload = r.Succeeded ? reload : Array.Empty<string>(),
        };

        private static ActionOutcome FromMcp(McpCommandResult r) =>
            Done(r.Succeeded, r.Message, r.Succeeded ? new[] { "mcp" } : Array.Empty<string>());

        private static ActionOutcome SetSkillState(string name, string value, string scope, string? workspace)
        {
            if (!SkillCatalog.States.Contains(value))
                return Done(false, "Unknown state: " + value);
            var ok = SkillCatalog.SetState(scope, workspace, name, value);
            return Done(ok, ok ? "" : "The settings file could not be written (no workspace, or not valid JSON).", "skills");
        }

        private static ActionOutcome ApproveProjectServer(string name, bool approve, string? workspace)
        {
            if (string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(name))
                return Done(false, "Open the solution or folder the server belongs to.");
            var ok = ClaudeMcpClient.SetProjectApproval(workspace, name, approve);
            return Done(ok, ok ? "" : "The settings file could not be written (is it valid JSON?).", "mcp");
        }

        private void Load(string tab)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (_loading)
            {
                if (!_loading.Add(tab))
                    return;
            }
            var workspace = _package.GetSolutionDirectory(); // UI-thread only — read before going off it
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                try
                {
                    if (exe == null)
                    {
                        PostError(tab, "The Claude CLI was not found. Install it or set its path in Advanced options.");
                        return;
                    }
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var ct = cts.Token;
                    switch (tab)
                    {
                        case "installed":
                        {
                            var installed = await new ClaudePluginClient(exe).ListInstalledAsync(workspace, ct).ConfigureAwait(false);
                            _post(new JObject { ["type"] = "manage.plugins", ["installed"] = new JArray(installed.Select(PluginJson)) });
                            break;
                        }
                        case "discover":
                        {
                            var client = new ClaudePluginClient(exe);
                            var available = await client.ListAvailableAsync(workspace, ct).ConfigureAwait(false);
                            var installed = await client.ListInstalledAsync(workspace, ct).ConfigureAwait(false);
                            _post(new JObject
                            {
                                ["type"] = "manage.available",
                                ["plugins"] = new JArray(available.Select(a => new JObject
                                {
                                    ["id"] = a.PluginId,
                                    ["name"] = a.Name,
                                    ["description"] = a.Description,
                                    ["marketplace"] = a.MarketplaceName,
                                    ["version"] = a.Version,
                                    ["installCount"] = a.InstallCount,
                                })),
                                ["installedIds"] = new JArray(installed.Select(p => p.Id).Distinct()),
                            });
                            break;
                        }
                        case "marketplaces":
                        {
                            var list = await new ClaudePluginClient(exe).ListMarketplacesAsync(workspace, ct).ConfigureAwait(false);
                            _post(new JObject
                            {
                                ["type"] = "manage.marketplaces",
                                ["list"] = new JArray(list.Select(m => new JObject
                                {
                                    ["name"] = m.Name,
                                    ["source"] = m.Source,
                                    ["origin"] = m.Origin,
                                    ["installLocation"] = m.InstallLocation,
                                })),
                            });
                            break;
                        }
                        case "skills":
                        {
                            var installed = await new ClaudePluginClient(exe).ListInstalledAsync(workspace, ct).ConfigureAwait(false);
                            var loaded = _loadedSkills;
                            var skills = SkillCatalog.Scan(new SkillScanContext
                            {
                                Workspace = workspace,
                                Plugins = installed,
                                LoadedNames = loaded,
                            });
                            var bundled = SkillCatalog.ReadBundledDisabled(workspace);
                            _post(new JObject
                            {
                                ["type"] = "manage.skills",
                                ["skills"] = new JArray(skills.Select(s => new JObject
                                {
                                    ["name"] = s.Name,
                                    ["description"] = s.Description,
                                    ["source"] = s.Source.ToString().ToLowerInvariant(),
                                    ["file"] = s.SkillFile,
                                    ["pluginId"] = s.PluginId,
                                    ["state"] = s.EffectiveState,
                                    ["stateScope"] = s.OverrideScope,
                                    ["loaded"] = s.Loaded,
                                    ["canOverride"] = s.CanOverride,
                                    ["canEdit"] = s.CanEdit,
                                })),
                                ["bundledDisabled"] = bundled.Disabled,
                                ["bundledScope"] = bundled.Scope,
                                // without a turn in this window there is no system/init yet → "in use" is unknown
                                ["loadedKnown"] = loaded != null,
                                ["hasWorkspace"] = !string.IsNullOrEmpty(workspace),
                            });
                            break;
                        }
                        case "mcp":
                        {
                            var servers = await new ClaudeMcpClient(exe).ListAsync(workspace, ct).ConfigureAwait(false);
                            _post(new JObject
                            {
                                ["type"] = "manage.mcp",
                                ["servers"] = new JArray(servers.Select(s => new JObject
                                {
                                    ["name"] = s.Name,
                                    ["scope"] = s.Scope,
                                    ["transport"] = s.Transport,
                                    ["command"] = s.Command,
                                    ["args"] = new JArray(s.Args),
                                    ["url"] = s.Url,
                                    ["envNames"] = new JArray(s.EnvNames),
                                    ["headerNames"] = new JArray(s.HeaderNames),
                                    ["status"] = s.Status,
                                    ["detail"] = s.StatusDetail,
                                    ["target"] = s.Target,
                                    ["readOnly"] = s.ReadOnly,
                                })),
                            });
                            break;
                        }
                        default:
                            PostError(tab, "Unknown section: " + tab);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    PostError(tab, ex.Message);
                }
                finally
                {
                    lock (_loading)
                        _loading.Remove(tab);
                }
            }).Task.Forget();
        }

        private void LoadDetails(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(id))
                return;
            var workspace = _package.GetSolutionDirectory();
            var exe = ClaudeExecutableLocator.Locate(_package.GetOptions().ClaudeExecutablePath);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                PluginDetails? d = null;
                try
                {
                    if (exe != null)
                        d = await new ClaudePluginClient(exe).DetailsAsync(PluginIds.NameOf(id), workspace, CancellationToken.None).ConfigureAwait(false);
                }
                catch { d = null; }
                var details = d == null ? null : new JObject
                {
                    ["description"] = d.Description,
                    ["alwaysOnTokens"] = d.AlwaysOnTokens,
                    ["inventory"] = new JObject(d.Inventory.Select(kv => new JProperty(kv.Key, new JArray(kv.Value)))),
                    ["components"] = new JArray(d.Components.Select(c => new JObject
                    {
                        ["name"] = c.Name,
                        ["alwaysOn"] = c.AlwaysOn,
                        ["onInvoke"] = c.OnInvoke,
                    })),
                    ["raw"] = d.RawText,
                };
                _post(new JObject { ["type"] = "manage.details", ["id"] = id, ["details"] = details });
            }).Task.Forget();
        }

        private static JObject PluginJson(InstalledPlugin p) => new JObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["marketplace"] = p.Marketplace,
            ["version"] = p.Version,
            ["scope"] = p.Scope,
            ["enabled"] = p.Enabled,
            ["projectEnabled"] = p.ProjectEnabled,
            ["projectPath"] = p.ProjectPath,
            ["installedAt"] = p.InstalledAt?.ToString("o"),
            ["lastUpdated"] = p.LastUpdated?.ToString("o"),
            ["hasUserConfig"] = p.HasUserConfig,
            ["errors"] = new JArray(p.Errors),
        };

        private void PostError(string tab, string message) =>
            _post(new JObject { ["type"] = "manage.error", ["tab"] = tab, ["message"] = message });
    }
}
