using System.Threading;
using System.Threading.Tasks;
using CodeAstrogator.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodeAstrogator.Tests
{
    public class PermissionBridgeTests
    {
        // ── pure result/config builders ──────────────────────────────────────────

        [Fact]
        public void BuildInitializeResult_EchoesProtocolVersion_AndDeclaresTools()
        {
            var r = McpPermissionBridge.BuildInitializeResult(new JValue(7), "2025-11-25");
            Assert.Equal(7, r.Value<int>("id"));
            var result = r["result"]!;
            Assert.Equal("2025-11-25", result.Value<string>("protocolVersion"));
            Assert.NotNull(result["capabilities"]!["tools"]);
            Assert.Equal(McpPermissionBridge.ServerName, result["serverInfo"]!.Value<string>("name"));
        }

        [Fact]
        public void BuildToolsListResult_ContainsPermissionPromptTool()
        {
            var r = McpPermissionBridge.BuildToolsListResult(new JValue(1));
            var tools = (JArray)r["result"]!["tools"]!;
            Assert.Single(tools);
            Assert.Equal(McpPermissionBridge.ToolName, tools[0].Value<string>("name"));
            var props = tools[0]!["inputSchema"]!["properties"]!;
            Assert.NotNull(props["tool_name"]);
            Assert.NotNull(props["input"]);
        }

        [Fact]
        public void BuildToolResult_Allow_EchoesOriginalInputWhenNotEdited()
        {
            var input = new JObject { ["file_path"] = "a.cs", ["content"] = "x" };
            var decision = new PermissionDecision { Behavior = "allow" }; // UpdatedInput null
            var r = McpPermissionBridge.BuildToolResult(new JValue(2), decision, input);

            var inner = InnerDecision(r);
            Assert.Equal("allow", inner.Value<string>("behavior"));
            // updatedInput MUST be the input object (a null updatedInput is treated as a denial)
            Assert.Equal("a.cs", inner["updatedInput"]!.Value<string>("file_path"));
            Assert.Equal("x", inner["updatedInput"]!.Value<string>("content"));
            Assert.False(r["result"]!.Value<bool>("isError"));
        }

        [Fact]
        public void BuildToolResult_Allow_UsesUpdatedInputWhenProvided()
        {
            var input = new JObject { ["content"] = "old" };
            var decision = new PermissionDecision { Behavior = "allow", UpdatedInput = new JObject { ["content"] = "new" } };
            var inner = InnerDecision(McpPermissionBridge.BuildToolResult(new JValue(3), decision, input));
            Assert.Equal("new", inner["updatedInput"]!.Value<string>("content"));
        }

        [Fact]
        public void BuildToolResult_Deny_CarriesMessage()
        {
            var inner = InnerDecision(McpPermissionBridge.BuildToolResult(
                new JValue(4), new PermissionDecision { Behavior = "deny", Message = "nope" }, new JObject()));
            Assert.Equal("deny", inner.Value<string>("behavior"));
            Assert.Equal("nope", inner.Value<string>("message"));
            Assert.Null(inner["updatedInput"]);
        }

        [Fact]
        public void BuildMcpConfig_IsHttpWithPortAuthAndTimeout()
        {
            var cfg = McpPermissionBridge.BuildMcpConfig(54321, "tok123");
            var srv = cfg["mcpServers"]![McpPermissionBridge.ServerName]!;
            Assert.Equal("http", srv.Value<string>("type"));
            Assert.Equal("http://127.0.0.1:54321/mcp", srv.Value<string>("url"));
            Assert.Equal("tok123", srv["headers"]!.Value<string>("X-Auth"));
            Assert.Equal(McpPermissionBridge.ToolTimeoutMs, srv.Value<int>("timeout"));
        }

        [Theory]
        [InlineData("tok", "tok", true)]
        [InlineData("wrong", "tok", false)]
        [InlineData(null, "tok", false)]
        [InlineData("tok", "", false)]
        public void IsAuthorized_MatchesExactToken(string? header, string expected, bool ok)
        {
            Assert.Equal(ok, McpPermissionBridge.IsAuthorized(header, expected));
        }

        // ── JSON-RPC dispatch routing ─────────────────────────────────────────────

        [Fact]
        public async Task Dispatch_Initialize_Returns200_WithSessionHeader()
        {
            var b = new McpPermissionBridge();
            var (status, body, sessionHeader) = await b.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}",
                CancellationToken.None);
            Assert.Equal("200 OK", status);
            Assert.True(sessionHeader);
            Assert.Contains("2025-11-25", body);
        }

        [Fact]
        public async Task Dispatch_Notification_Returns202_NoBody()
        {
            var b = new McpPermissionBridge();
            var (status, body, _) = await b.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", CancellationToken.None);
            Assert.Equal("202 Accepted", status);
            Assert.Null(body);
        }

        [Fact]
        public async Task Dispatch_ToolsCall_InvokesHandler_AndReturnsAllow()
        {
            PermissionRequest? seen = null;
            var b = new McpPermissionBridge
            {
                OnPermissionRequested = (req, ct) =>
                {
                    seen = req;
                    return Task.FromResult(new PermissionDecision { Behavior = "allow" });
                },
            };
            var call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"permission_prompt\",\"arguments\":{\"tool_name\":\"Write\",\"input\":{\"file_path\":\"f.txt\",\"content\":\"hi\"},\"tool_use_id\":\"toolu_1\"}}}";
            var (status, body, _) = await b.DispatchAsync(call, CancellationToken.None);

            Assert.Equal("200 OK", status);
            Assert.NotNull(seen);
            Assert.Equal("Write", seen!.ToolName);
            Assert.Equal("toolu_1", seen.RequestId);
            Assert.Equal("f.txt", seen.Input.Value<string>("file_path"));

            var inner = InnerDecision(JObject.Parse(body!));
            Assert.Equal("allow", inner.Value<string>("behavior"));
            Assert.Equal("hi", inner["updatedInput"]!.Value<string>("content"));
        }

        [Fact]
        public async Task Dispatch_UnknownMethod_ReturnsJsonRpcError()
        {
            var b = new McpPermissionBridge();
            var (status, body, _) = await b.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"resources/list\"}", CancellationToken.None);
            Assert.Equal("200 OK", status);
            var o = JObject.Parse(body!);
            Assert.Equal(-32601, o["error"]!.Value<int>("code"));
        }

        // ── keep-alive HTTP read (two pipelined requests must both parse) ──────────

        [Fact]
        public async Task HttpRead_HandlesTwoPipelinedRequestsOnOneConnection()
        {
            string Req(string body) =>
                "POST /mcp HTTP/1.1\r\nContent-Length: "
                + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
            var bytes = System.Text.Encoding.UTF8.GetBytes(Req("{\"a\":1}") + Req("{\"b\":2}"));
            using (var ms = new System.IO.MemoryStream(bytes))
            {
                var carry = new System.Collections.Generic.List<byte>();
                var r1 = await McpPermissionBridge.HttpReadRequestAsync(ms, carry, CancellationToken.None);
                var r2 = await McpPermissionBridge.HttpReadRequestAsync(ms, carry, CancellationToken.None);
                Assert.NotNull(r1);
                Assert.Equal("POST", r1!.Method);
                Assert.Equal("{\"a\":1}", r1.Body); // first request intact
                Assert.NotNull(r2);
                Assert.Equal("{\"b\":2}", r2!.Body); // second (pipelined) request not lost
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private static JObject InnerDecision(JObject toolResult)
        {
            var text = toolResult["result"]!["content"]![0]!.Value<string>("text")!;
            return JObject.Parse(text);
        }
    }
}
