using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary>Parsers and command lines of <see cref="ClaudePluginClient"/>, on output captured from CLI 2.1.319.</summary>
    public class ClaudePluginClientTests
    {
        private const string ListJson = @"[
  {
    ""id"": ""commit-commands@claude-plugins-official"",
    ""version"": ""1.0.1"",
    ""scope"": ""project"",
    ""enabled"": true,
    ""installPath"": ""C:\\Users\\dev\\.claude\\plugins\\cache\\claude-plugins-official\\commit-commands\\1.0.1"",
    ""installedAt"": ""2026-11-06T13:51:53.265Z"",
    ""lastUpdated"": ""2026-11-06T13:51:53.265Z"",
    ""projectPath"": ""C:\\src\\SampleShop"",
    ""projectEnabled"": true
  },
  { ""id"": ""sample-lint@sample-market"", ""version"": ""0d95a81d35a9"", ""scope"": ""user"", ""enabled"": false,
    ""installPath"": ""C:\\x"", ""errors"": [""manifest missing""] }
]";

        [Fact]
        public void ParseInstalled_ReadsEveryField()
        {
            var list = ClaudePluginClient.ParseInstalled(ListJson);
            Assert.Equal(2, list.Count);
            var p = list[0];
            Assert.Equal("commit-commands", p.Name);
            Assert.Equal("claude-plugins-official", p.Marketplace);
            Assert.Equal("1.0.1", p.Version);
            Assert.Equal("project", p.Scope);
            Assert.True(p.Enabled);
            Assert.True(p.ProjectEnabled);
            Assert.Equal(@"C:\src\SampleShop", p.ProjectPath);
            Assert.Equal(2026, p.InstalledAt!.Value.Year);
            Assert.False(list[1].Enabled);
            Assert.Equal(new[] { "manifest missing" }, list[1].Errors);
        }

        [Fact]
        public void ParseInstalled_GarbageIsEmpty()
        {
            Assert.Empty(ClaudePluginClient.ParseInstalled("Error: something broke"));
            Assert.Empty(ClaudePluginClient.ParseInstalled(""));
        }

        [Fact]
        public void ParseAvailable_TakesTheAvailableArray()
        {
            var json = @"{ ""installed"": [ { ""id"": ""x@y"" } ], ""available"": [
  { ""pluginId"": ""code-review@claude-plugins-official"", ""name"": ""code-review"", ""description"": ""Review PRs"",
    ""marketplaceName"": ""claude-plugins-official"", ""source"": ""./plugins/code-review"", ""installCount"": 477558 },
  { ""pluginId"": ""feature-dev@anthropic-plugin-directory"", ""name"": ""feature-dev"", ""marketplaceName"": ""anthropic-plugin-directory"",
    ""source"": { ""source"": ""git-subdir"", ""url"": ""https://github.com/anthropics/claude-plugins-official"" } } ] }";
            var list = ClaudePluginClient.ParseAvailable(json);
            Assert.Equal(2, list.Count);
            Assert.Equal(477558, list[0].InstallCount);
            Assert.Equal("Review PRs", list[0].Description);
            Assert.Null(list[1].InstallCount);
            Assert.Equal("anthropic-plugin-directory", list[1].MarketplaceName);
        }

        [Fact]
        public void ParseMarketplaces()
        {
            var json = @"[ { ""name"": ""claude-plugins-official"", ""source"": ""github"", ""repo"": ""anthropics/claude-plugins-official"",
                ""installLocation"": ""C:\\x"" }, { ""name"": ""local-market"", ""source"": ""directory"", ""path"": ""C:\\src\\market"" } ]";
            var list = ClaudePluginClient.ParseMarketplaces(json);
            Assert.Equal("anthropics/claude-plugins-official", list[0].Origin);
            Assert.Equal(@"C:\src\market", list[1].Origin);
        }

        [Fact]
        public void ParseResultLine_Success_UpdateAndUninstallFields()
        {
            var install = ClaudePluginClient.ParseResultLine(
                "Cloning…\n{\"command\":\"install\",\"outcome\":\"ok\",\"plugin\":\"commit-commands@claude-plugins-official\",\"pluginId\":\"commit-commands@claude-plugins-official\",\"scope\":\"user\",\"message\":\"Successfully installed\"}\n")!;
            Assert.True(install.Succeeded);
            Assert.Equal("user", install.Scope);
            Assert.Equal("commit-commands@claude-plugins-official", install.PluginId);

            var update = ClaudePluginClient.ParseResultLine(
                "{\"command\":\"update\",\"outcome\":\"ok\",\"message\":\"already at the latest version (1.0.1).\",\"updateOutcome\":\"up_to_date\",\"oldVersion\":\"1.0.1\",\"newVersion\":\"1.0.1\"}")!;
            Assert.Equal("up_to_date", update.UpdateOutcome);
            Assert.Equal("1.0.1", update.NewVersion);

            var uninstall = ClaudePluginClient.ParseResultLine("{\"command\":\"uninstall\",\"outcome\":\"ok\",\"keptData\":false,\"message\":\"done\"}")!;
            Assert.False(uninstall.KeptData);
        }

        [Fact]
        public void ParseResultLine_AlreadyInGoalState_CountsAsSuccess()
        {
            // measured: enable on an enabled plugin — "failed" + already_in_goal_state, exit code 0
            var r = ClaudePluginClient.ParseResultLine(
                "{\"command\":\"enable\",\"outcome\":\"failed\",\"plugin\":\"sample-lint@sample-market\",\"message\":\"Plugin is already enabled\",\"failureCode\":\"already_in_goal_state\",\"alreadyInGoalState\":true}")!;
            Assert.True(r.Succeeded);
            Assert.False(r.NeedsCommandConfirmation);
        }

        [Fact]
        public void ParseResultLine_ShownCommand_NeedsConfirmation()
        {
            var r = ClaudePluginClient.ParseResultLine(
                "{\"command\":\"install\",\"outcome\":\"failed\",\"message\":\"confirm the command\",\"failureCode\":\"command_not_accepted\",\"shownCommand\":{\"command\":\"npx sample-setup\",\"sha256\":\"ab12\",\"acceptCommandMatched\":false}}")!;
            Assert.False(r.Succeeded);
            Assert.True(r.NeedsCommandConfirmation);
            Assert.Equal("npx sample-setup", r.ShownCommand!.Command);
            Assert.Equal("ab12", r.ShownCommand.Sha256);
            Assert.False(r.ShownCommand.AcceptCommandMatched);
        }

        [Fact]
        public void ParseResultLine_TakesTheLastJsonLine_IgnoresOtherText()
        {
            var r = ClaudePluginClient.ParseResultLine("{\"not\":\"a result\"}\nprogress…\n{\"command\":\"disable\",\"outcome\":\"ok\",\"message\":\"m\"}\n\n")!;
            Assert.Equal("disable", r.Command);
            Assert.Null(ClaudePluginClient.ParseResultLine("error: unknown option '--jsn'"));
        }

        [Fact]
        public void ParseDetails_InventoryCostAndComponents()
        {
            const string text = @"token-saver 5.13.60
  Description: Audit and monitor context window usage.
  Source: token-saver@sample-market

Component inventory
  Skills (3)  quick, health, coach
  Agents (0)
  Hooks (2)  PreToolUse, SessionStart  (harness-only — no model context cost)
  MCP servers (0)

Projected token cost
  Always-on:   ~1.2k tok   added to every session

Per-component (rounded)
  component          always-on  on-invoke
  quick                    ~50      ~2.2k
  health                 < 20       ~430

  On-invoke cost is paid each time a skill or agent fires.
";
            var d = ClaudePluginClient.ParseDetails(text);
            Assert.Equal("token-saver", d.Name);
            Assert.Equal("5.13.60", d.Version);
            Assert.Equal("Audit and monitor context window usage.", d.Description);
            Assert.Equal(new[] { "quick", "health", "coach" }, d.Inventory["Skills"]);
            Assert.Empty(d.Inventory["Agents"]);
            Assert.Equal(new[] { "PreToolUse", "SessionStart" }, d.Inventory["Hooks"]);
            Assert.Equal(1200, d.AlwaysOnTokens);
            Assert.Equal(2, d.Components.Count);
            Assert.Equal("<20", d.Components[1].AlwaysOn);
            Assert.Equal("~430", d.Components[1].OnInvoke);
            Assert.Contains("Per-component", d.RawText);
        }

        [Fact]
        public async Task Commands_BuildTheDocumentedArguments()
        {
            var seen = new List<CliCommand>();
            var client = new ClaudePluginClient((cmd, ct) =>
            {
                seen.Add(cmd);
                return Task.FromResult(new CliCommandResult { ExitCode = 0, StdOut = "{\"command\":\"x\",\"outcome\":\"ok\",\"message\":\"\"}" });
            });
            await client.InstallAsync("a@m", "project", @"C:\src\SampleShop", "ab12", CancellationToken.None);
            await client.UninstallAsync("a@m", "user", keepData: true, null, CancellationToken.None);
            await client.RemoveMarketplaceAsync("m", null, null, CancellationToken.None);

            Assert.Equal(new[] { "plugin", "install", "a@m", "--scope", "project", "--json", "--accept-command", "ab12" }, seen[0].Args);
            Assert.Equal(@"C:\src\SampleShop", seen[0].WorkingDirectory);
            Assert.Equal(ClaudePluginClient.LongTimeoutMs, seen[0].TimeoutMs);
            Assert.Equal(new[] { "plugin", "uninstall", "a@m", "--scope", "user", "--json", "--keep-data" }, seen[1].Args);
            Assert.Equal(new[] { "plugin", "marketplace", "remove", "m", "--json" }, seen[2].Args);
        }

        [Fact]
        public async Task Change_WithoutResultLine_ReportsTheCliText()
        {
            var client = new ClaudePluginClient((cmd, ct) =>
                Task.FromResult(new CliCommandResult { ExitCode = 1, StdErr = "error: unknown option '--sope'\n" }));
            var r = await client.EnableAsync("a@m", "user", null, CancellationToken.None);
            Assert.False(r.Succeeded);
            Assert.Equal("error: unknown option '--sope'", r.Message);
            Assert.Equal(1, r.ExitCode);
        }

        [Fact]
        public void PluginIds_SplitAtTheLastAt()
        {
            Assert.Equal("commit-commands", PluginIds.NameOf("commit-commands@claude-plugins-official"));
            Assert.Equal("claude-plugins-official", PluginIds.MarketplaceOf("commit-commands@claude-plugins-official"));
            Assert.Equal("bare", PluginIds.NameOf("bare"));
            Assert.Equal("", PluginIds.MarketplaceOf("bare"));
        }
    }
}
