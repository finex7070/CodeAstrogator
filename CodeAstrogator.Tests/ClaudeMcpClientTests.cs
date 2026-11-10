using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Xunit;

namespace CodeAstrogator.Tests
{
    /// <summary><see cref="ClaudeMcpClient"/> on output and files captured from CLI 2.1.319 (sandbox, 2026-11-06).</summary>
    public class ClaudeMcpClientTests : IDisposable
    {
        private readonly string _dir;

        public ClaudeMcpClientTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ca-mcp-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_dir, "SampleShop"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string ListOutput = "Checking MCP server health…\n\n"
            + "claude.ai Sample Docs: https://example.invalid/v1/pages/mcp - ✔ Connected\n"
            + "plugin:sample-devtools:devtools: npx sample-devtools-mcp@1.5.0 - ✔ Connected\n"
            + "shop-db: does-not-exist-cmd --flag - ✘ Failed to connect — CONNECTION_CLOSED: Connection closed\n"
            + "shop-api: https://example.invalid/mcp (HTTP) - ⏸ Pending approval (run `claude` to approve)\n"
            + "tracker: https://tracker.example.invalid/mcp (HTTP) - ⚠ Needs authentication\n";

        [Fact]
        public void ParseList_NamesWithColonsAndSpaces_AndEveryStatus()
        {
            var list = ClaudeMcpClient.ParseList(ListOutput).ToDictionary(s => s.Name);
            Assert.Equal(5, list.Count);
            Assert.Equal("claude.ai", list["claude.ai Sample Docs"].Scope);
            Assert.Equal(McpStatus.Connected, list["claude.ai Sample Docs"].Status);
            Assert.Equal("plugin", list["plugin:sample-devtools:devtools"].Scope);
            Assert.Equal("npx sample-devtools-mcp@1.5.0", list["plugin:sample-devtools:devtools"].Target);
            Assert.Equal(McpStatus.Failed, list["shop-db"].Status);
            Assert.Equal("CONNECTION_CLOSED: Connection closed", list["shop-db"].StatusDetail);
            Assert.Equal(McpStatus.Pending, list["shop-api"].Status);
            Assert.Equal("http", list["shop-api"].Transport);
            Assert.Equal("https://example.invalid/mcp", list["shop-api"].Target);
            Assert.Equal(McpStatus.NeedsAuth, list["tracker"].Status);
        }

        [Fact]
        public void ParseList_ConsoleFallbackSymbols_AndDashesInTargets()
        {
            // measured: through a .NET pipe the CLI prints "√ Connected"; the cross/dash fallbacks are assumed alike
            var text = "unity-tools: unity mcp - √ Connected\n"
                + "shop-db: node shop-db-server --mode read-only - × Failed to connect - CONNECTION_CLOSED: Connection closed\n";
            var list = ClaudeMcpClient.ParseList(text).ToDictionary(s => s.Name);
            Assert.Equal(McpStatus.Connected, list["unity-tools"].Status);
            Assert.Equal(McpStatus.Failed, list["shop-db"].Status);
            Assert.Equal("node shop-db-server --mode read-only", list["shop-db"].Target);
            Assert.Equal("CONNECTION_CLOSED: Connection closed", list["shop-db"].StatusDetail);
        }

        [Fact]
        public void ReadConfigured_UserLocalProject_WithoutSecretValues()
        {
            var ws = Path.Combine(_dir, "SampleShop");
            var claudeJson = Path.Combine(_dir, ".claude.json");
            File.WriteAllText(claudeJson, @"{
  ""mcpServers"": { ""shop-db"": { ""type"": ""stdio"", ""command"": ""does-not-exist-cmd"", ""args"": [""--flag""], ""env"": { ""DB_KEY"": ""secret-value"" } } },
  ""projects"": { """ + ClaudeMcpClient.ProjectKey(ws) + @""": { ""mcpServers"": { ""local-tool"": { ""type"": ""stdio"", ""command"": ""node"", ""args"": [""-e"", """"] } } } }
}");
            File.WriteAllText(Path.Combine(ws, ".mcp.json"),
                @"{ ""mcpServers"": { ""shop-api"": { ""type"": ""http"", ""url"": ""https://example.invalid/mcp"", ""headers"": { ""Authorization"": ""Bearer secret"" } } } }");

            var list = ClaudeMcpClient.ReadConfigured(ws, claudeJson).ToDictionary(s => s.Name);
            Assert.Equal("user", list["shop-db"].Scope);
            Assert.Equal(new[] { "--flag" }, list["shop-db"].Args);
            Assert.Equal(new[] { "DB_KEY" }, list["shop-db"].EnvNames);
            Assert.Equal("local", list["local-tool"].Scope);
            Assert.Equal("project", list["shop-api"].Scope);
            Assert.Equal(new[] { "Authorization" }, list["shop-api"].HeaderNames);
            Assert.False(list["shop-api"].ReadOnly);
            // no secret reaches the model objects
            var all = string.Join("|", list.Values.SelectMany(s => s.Args.Concat(s.EnvNames).Concat(s.HeaderNames)));
            Assert.DoesNotContain("secret", all);
        }

        [Fact]
        public void ReadConfigured_ProjectKeyIsCaseAndSlashInsensitive()
        {
            var ws = Path.Combine(_dir, "SampleShop");
            var claudeJson = Path.Combine(_dir, ".claude.json");
            File.WriteAllText(claudeJson, @"{ ""projects"": { """ + ClaudeMcpClient.ProjectKey(ws).ToUpperInvariant() + @"/"": { ""mcpServers"": { ""a"": { ""command"": ""x"" } } } } }");
            Assert.Equal("local", ClaudeMcpClient.ReadConfigured(ws + "\\", claudeJson).Single().Scope);
        }

        [Fact]
        public void Merge_AddsStatusAndCliOnlyServers()
        {
            var configured = new List<McpServerInfo>
            {
                new McpServerInfo { Name = "shop-db", Scope = "user" },
                new McpServerInfo { Name = "shop-api", Scope = "project" },
                new McpServerInfo { Name = "not-listed", Scope = "local" },
            };
            var merged = ClaudeMcpClient.Merge(configured, ClaudeMcpClient.ParseList(ListOutput)).ToDictionary(s => s.Name);
            Assert.Equal(McpStatus.Failed, merged["shop-db"].Status);
            Assert.Equal(McpStatus.Pending, merged["shop-api"].Status);
            Assert.Equal(McpStatus.Unknown, merged["not-listed"].Status);
            Assert.True(merged["plugin:sample-devtools:devtools"].ReadOnly);
            Assert.True(merged["claude.ai Sample Docs"].ReadOnly);
            Assert.Equal(6, merged.Count); // 3 configured + the 3 only the CLI listed (claude.ai, plugin, tracker)
        }

        [Fact]
        public async Task Add_BuildsTheDocumentedArguments_AndCleansTheMessage()
        {
            var seen = new List<CliCommand>();
            var client = new ClaudeMcpClient((cmd, ct) =>
            {
                seen.Add(cmd);
                return Task.FromResult(new CliCommandResult
                {
                    ExitCode = 0,
                    StdOut = "Added stdio MCP server shop-db with command: node server.js --port 1 to user config\nFile modified: C:\\Users\\dev\\.claude.json\n",
                });
            });
            var r = await client.AddStdioAsync("shop-db", "node", new[] { "server.js", "--port", "1" },
                new Dictionary<string, string> { ["DB_KEY"] = "v" }, "user", null, CancellationToken.None);
            await client.AddRemoteAsync("shop-api", "http", "https://example.invalid/mcp",
                new Dictionary<string, string> { ["Authorization"] = "Bearer x" }, "project", @"C:\src\SampleShop", CancellationToken.None);
            await client.RemoveAsync("shop-db", "user", null, CancellationToken.None);

            Assert.True(r.Succeeded);
            Assert.Equal("Added stdio MCP server shop-db with command: node server.js --port 1 to user config", r.Message);
            Assert.Equal(new[] { "mcp", "add", "shop-db", "--scope", "user", "--env", "DB_KEY=v", "--", "node", "server.js", "--port", "1" }, seen[0].Args);
            Assert.Equal(new[] { "mcp", "add", "--transport", "http", "--scope", "project", "shop-api", "https://example.invalid/mcp",
                "--header", "Authorization: Bearer x" }, seen[1].Args);
            Assert.Equal(@"C:\src\SampleShop", seen[1].WorkingDirectory);
            Assert.Equal(new[] { "mcp", "remove", "shop-db", "--scope", "user" }, seen[2].Args);
        }

        [Fact]
        public void ProjectApproval_MovesTheNameBetweenTheLists_KeepsOtherKeys()
        {
            var ws = Path.Combine(_dir, "SampleShop");
            var local = Path.Combine(ws, ".claude", "settings.local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, "{ \"permissions\": { \"allow\": [\"Bash(dotnet test)\"] }, \"enabledMcpjsonServers\": [\"shop-api\", \"other\"] }");

            Assert.True(ClaudeMcpClient.SetProjectApproval(ws, "shop-api", approve: false));
            var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(local));
            Assert.Equal(new[] { "other" }, o["enabledMcpjsonServers"]!.ToObject<string[]>());
            Assert.Equal(new[] { "shop-api" }, o["disabledMcpjsonServers"]!.ToObject<string[]>());
            Assert.Equal("Bash(dotnet test)", (string)o["permissions"]!["allow"]![0]!);

            Assert.True(ClaudeMcpClient.SetProjectApproval(ws, "shop-api", approve: true));
            o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(local));
            Assert.Null(o["disabledMcpjsonServers"]); // emptied list is dropped
            Assert.Equal(new[] { "other", "shop-api" }, o["enabledMcpjsonServers"]!.ToObject<string[]>());
        }

        [Fact]
        public async Task Remove_UnknownName_Fails()
        {
            var client = new ClaudeMcpClient((cmd, ct) => Task.FromResult(new CliCommandResult
            {
                ExitCode = 1,
                StdErr = "No MCP server named \"nope\". Configured servers: shop-api\n",
            }));
            var r = await client.RemoveAsync("nope", "user", null, CancellationToken.None);
            Assert.False(r.Succeeded);
            Assert.StartsWith("No MCP server named", r.Message);
        }
    }
}
