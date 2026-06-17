using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// In-process localhost MCP server (Teil A §A5) exposing the <c>permission_prompt</c> tool
    /// referenced by <c>--permission-prompt-tool mcp__vsbridge__permission_prompt</c>. The CLI
    /// connects over HTTP (<c>--mcp-config</c>, <c>type:"http"</c>); every non-pre-approved tool
    /// call blocks on <c>tools/call</c> until the UI decides. Minimal HTTP/1.1 over a
    /// <see cref="TcpListener"/> on 127.0.0.1 — no http.sys URL-ACL needed in a non-admin VS
    /// process (unlike <c>HttpListener</c>).
    ///
    /// Wire protocol verified empirically against CLI 2.1.162 (spike):
    ///  - <c>initialize</c> sends protocolVersion "2025-11-25" (echo it) + Accept includes
    ///    text/event-stream; reply result {protocolVersion, capabilities:{tools:{}}, serverInfo}
    ///    and an <c>Mcp-Session-Id</c> header.
    ///  - <c>notifications/*</c> → 202, no body. A <c>GET /mcp</c> (SSE) may be answered 405 safely.
    ///  - <c>tools/call</c> arguments = { <c>tool_name</c>, <c>input</c>, <c>tool_use_id</c>, _meta }.
    ///  - result = { content:[{type:"text", text:&lt;json&gt;}], isError:false } where json is
    ///    {"behavior":"allow","updatedInput":&lt;input&gt;} (updatedInput MUST be the input object —
    ///    a null updatedInput is treated as a DENIAL by the CLI!) or {"behavior":"deny","message":…}.
    ///  - Custom <c>X-Auth</c> header is echoed on every request → used to reject foreign callers.
    /// </summary>
    public sealed class McpPermissionBridge : IPermissionBridge, IDisposable
    {
        public const string ServerName = "vsbridge";
        public const string ToolName = "permission_prompt";
        /// <summary>Value for <c>--permission-prompt-tool</c> (mcp__&lt;server&gt;__&lt;tool&gt;).</summary>
        public const string PermissionPromptToolRef = "mcp__" + ServerName + "__" + ToolName;

        private const string ProtocolVersionFallback = "2025-11-25";
        private const string SessionId = "codeastrogator-mcp";

        /// <summary>
        /// Default time (ms) the CLI is allowed to wait on a permission/AskUserQuestion prompt
        /// before it gives up (1 hour) — far longer than the CLI's own short default (≈ a minute),
        /// which would "time out" a human decision. The effective value is the instance
        /// <see cref="ToolTimeoutMs"/> (seeded from the user's "Prompt timeout" setting).
        /// </summary>
        public const int DefaultToolTimeoutMs = 3_600_000; // 1 hour

        /// <summary>
        /// Effective per-tool-call timeout (ms) written to the config's <c>timeout</c> field.
        /// Verified against CLI 2.1.178: the config <c>timeout</c> field IS applied to tool calls
        /// and TAKES PRECEDENCE over the <c>MCP_TOOL_TIMEOUT</c> env var (the opposite of the older
        /// 2.1.16x behaviour) — so the user's configured timeout must live here, not only in the env
        /// var. Set before <see cref="Start"/>; change later via <see cref="UpdateToolTimeout"/>.
        /// </summary>
        public int ToolTimeoutMs { get; set; } = DefaultToolTimeoutMs;

        private readonly string _authToken = Guid.NewGuid().ToString("n");
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> _clients
            = new System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte>();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private string? _mcpConfigPath;
        private bool _disposed;

        /// <summary>Host-supplied UI round-trip; returns the user's decision (CLI blocks meanwhile).</summary>
        public Func<PermissionRequest, CancellationToken, Task<PermissionDecision>>? OnPermissionRequested { get; set; }

        public bool IsAvailable => _listener != null && _mcpConfigPath != null;
        public string? McpConfigPath => _mcpConfigPath;
        public int Port { get; private set; }

        /// <summary>Starts the listener and writes the --mcp-config file. Idempotent.</summary>
        public void Start()
        {
            if (_listener != null || _disposed)
                return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _mcpConfigPath = WriteMcpConfig(Port, _authToken);
            _ = AcceptLoopAsync(listener, _cts.Token);
        }

        public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            var handler = OnPermissionRequested;
            if (handler == null)
                return Task.FromResult(new PermissionDecision { Behavior = "deny", Message = "No permission handler attached." });
            return handler(request, ct);
        }

        // ── HTTP server loop ─────────────────────────────────────────────────────

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; } // listener stopped/disposed
                _ = HandleConnectionAsync(client, ct);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
        {
            // Track the accepted socket so Dispose() can Close() it: on net472,
            // NetworkStream.ReadAsync does NOT honour a CancellationToken mid-read, so a
            // parked keep-alive read can only be unblocked by closing the socket.
            _clients.TryAdd(client, 0);
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var carry = new List<byte>(); // bytes read past one request (pipelined next request)
                    while (!ct.IsCancellationRequested)
                    {
                        var req = await HttpReadRequestAsync(stream, carry, ct).ConfigureAwait(false);
                        if (req == null)
                            break; // connection closed by peer

                        if (!IsAuthorized(req.Headers))
                        {
                            await HttpWriteAsync(stream, "401 Unauthorized", null, null, ct).ConfigureAwait(false);
                            break;
                        }
                        if (req.Method == "GET")
                        {
                            // The CLI opens a GET SSE stream; we don't push server→client messages.
                            await HttpWriteAsync(stream, "405 Method Not Allowed", null, null, ct).ConfigureAwait(false);
                            break;
                        }

                        var (status, body, sessionHeader) = await DispatchAsync(req.Body, ct).ConfigureAwait(false);
                        var extra = sessionHeader ? new[] { ("Mcp-Session-Id", SessionId) } : null;
                        await HttpWriteAsync(stream, status, body, extra, ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // connection-level error (peer reset, CLI killed mid-turn, Dispose closed us) — drop it
            }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }

        private bool IsAuthorized(IDictionary<string, string> headers) =>
            IsAuthorized(headers.TryGetValue("X-Auth", out var v) ? v : null, _authToken);

        internal static bool IsAuthorized(string? authHeaderValue, string expectedToken) =>
            !string.IsNullOrEmpty(expectedToken) && authHeaderValue == expectedToken;

        // ── JSON-RPC dispatch (pure-ish; testable via DispatchAsync) ──────────────

        /// <summary>
        /// Dispatches one JSON-RPC request body. Returns the HTTP status line, the response
        /// body (null = no body, e.g. notifications), and whether to add the Mcp-Session-Id header.
        /// </summary>
        internal async Task<(string status, string? body, bool sessionHeader)> DispatchAsync(string requestBody, CancellationToken ct)
        {
            JObject msg;
            try { msg = JObject.Parse(requestBody); }
            catch { return ("400 Bad Request", null, false); }

            var method = msg.Value<string>("method") ?? "";
            var id = msg["id"];

            if (method == "initialize")
            {
                var pv = msg["params"]?.Value<string>("protocolVersion") ?? ProtocolVersionFallback;
                return ("200 OK", BuildInitializeResult(id, pv).ToString(Formatting.None), true);
            }
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return ("202 Accepted", null, false);
            }
            if (method == "tools/list")
            {
                return ("200 OK", BuildToolsListResult(id).ToString(Formatting.None), false);
            }
            if (method == "tools/call")
            {
                var args = msg["params"]?["arguments"] as JObject ?? new JObject();
                var toolName = args.Value<string>("tool_name") ?? "";
                var input = args["input"] as JObject ?? new JObject();
                var toolUseId = args.Value<string>("tool_use_id");

                var request = new PermissionRequest
                {
                    RequestId = string.IsNullOrEmpty(toolUseId) ? Guid.NewGuid().ToString("n") : toolUseId!,
                    ToolName = toolName,
                    Input = input,
                };

                PermissionDecision decision;
                try { decision = await RequestAsync(request, ct).ConfigureAwait(false); }
                catch (Exception ex) { decision = new PermissionDecision { Behavior = "deny", Message = "Permission error: " + ex.Message }; }

                return ("200 OK", BuildToolResult(id, decision, input).ToString(Formatting.None), false);
            }

            // Unknown method — respond with a JSON-RPC error (not an HTTP error).
            return ("200 OK", new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found: " + method },
            }.ToString(Formatting.None), false);
        }

        internal static JObject BuildInitializeResult(JToken? id, string protocolVersion) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JObject
            {
                ["protocolVersion"] = protocolVersion,
                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = "1.0.0" },
            },
        };

        internal static JObject BuildToolsListResult(JToken? id) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = ToolName,
                        ["description"] = "Approve or deny a tool use requested by Claude. Returns allow (with the input to run) or deny (with a reason).",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["tool_name"] = new JObject { ["type"] = "string" },
                                ["input"] = new JObject { ["type"] = "object" },
                            },
                            ["required"] = new JArray { "tool_name", "input" },
                        },
                    },
                },
            },
        };

        /// <summary>
        /// Builds the tools/call result. On allow, updatedInput is the edited input or — when the
        /// UI did not edit it — the ORIGINAL input echoed back (a null updatedInput is treated as a
        /// denial by the CLI). On deny, a message is returned.
        /// </summary>
        internal static JObject BuildToolResult(JToken? id, PermissionDecision decision, JObject originalInput)
        {
            JObject inner;
            if (string.Equals(decision.Behavior, "allow", StringComparison.OrdinalIgnoreCase))
            {
                inner = new JObject
                {
                    ["behavior"] = "allow",
                    ["updatedInput"] = decision.UpdatedInput ?? originalInput,
                };
            }
            else
            {
                inner = new JObject
                {
                    ["behavior"] = "deny",
                    ["message"] = string.IsNullOrEmpty(decision.Message) ? "Denied by user." : decision.Message,
                };
            }

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = inner.ToString(Formatting.None) } },
                    ["isError"] = false,
                },
            };
        }

        // ── --mcp-config file ─────────────────────────────────────────────────────

        internal static JObject BuildMcpConfig(int port, string authToken, int timeoutMs = DefaultToolTimeoutMs) => new JObject
        {
            ["mcpServers"] = new JObject
            {
                [ServerName] = new JObject
                {
                    ["type"] = "http",
                    ["url"] = $"http://127.0.0.1:{port}/mcp",
                    ["headers"] = new JObject { ["X-Auth"] = authToken },
                    // The CLI honours this for tool calls and prefers it over MCP_TOOL_TIMEOUT
                    // (verified 2.1.178) → carry the user's configured prompt timeout here.
                    ["timeout"] = timeoutMs,
                },
            },
        };

        private string WriteMcpConfig(int port, string authToken)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAstrogator");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mcp-permission-" + port + ".json");
            File.WriteAllText(path, BuildMcpConfig(port, authToken, ToolTimeoutMs).ToString(Formatting.None));
            return path;
        }

        /// <summary>
        /// Updates the per-tool-call timeout and rewrites the live <c>--mcp-config</c> file so the
        /// next turn picks it up (the per-turn host re-reads the file each turn; the persistent
        /// host only at process restart, like the env var). No-op if unchanged or not started.
        /// </summary>
        public void UpdateToolTimeout(int timeoutMs)
        {
            if (timeoutMs <= 0 || timeoutMs == ToolTimeoutMs)
                return;
            ToolTimeoutMs = timeoutMs;
            if (_mcpConfigPath == null)
                return; // not started yet — Start() will write it with the new value
            try { File.WriteAllText(_mcpConfigPath, BuildMcpConfig(Port, _authToken, ToolTimeoutMs).ToString(Formatting.None)); }
            catch { /* best-effort; the env var still carries the value as a fallback */ }
        }

        // ── minimal HTTP/1.1 read/write over the socket ───────────────────────────

        internal sealed class HttpRequest
        {
            public string Method = "";
            public string Path = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = "";
        }

        /// <param name="carry">Bytes already read from the socket that belong to THIS request and
        /// possibly the next one (HTTP keep-alive pipelining). Seeded from the previous call; on
        /// return it holds whatever was read past this request's end so the next request isn't lost.</param>
        internal static async Task<HttpRequest?> HttpReadRequestAsync(System.IO.Stream stream, List<byte> carry, CancellationToken ct)
        {
            var buf = new byte[8192];
            var acc = new List<byte>(carry); // start from leftover bytes of the previous request
            carry.Clear();
            int headerEnd = IndexOfDoubleCrlf(acc);
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (n == 0)
                    return null; // peer closed
                for (int i = 0; i < n; i++)
                    acc.Add(buf[i]);
                headerEnd = IndexOfDoubleCrlf(acc);
                if (acc.Count > 2_000_000)
                    return null; // runaway guard
            }

            var headerText = Encoding.ASCII.GetString(acc.ToArray(), 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var reqLine = lines[0].Split(' ');
            var req = new HttpRequest
            {
                Method = reqLine.Length > 0 ? reqLine[0] : "",
                Path = reqLine.Length > 1 ? reqLine[1] : "",
            };
            for (int i = 1; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(':');
                if (idx > 0)
                    req.Headers[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
            }

            int contentLength = 0;
            if (req.Headers.TryGetValue("Content-Length", out var cl))
                int.TryParse(cl, out contentLength);

            int bodyStart = headerEnd + 4;
            while (acc.Count < bodyStart + contentLength)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                for (int i = 0; i < n; i++)
                    acc.Add(buf[i]);
            }
            if (contentLength > 0)
                req.Body = Encoding.UTF8.GetString(acc.ToArray(), bodyStart, Math.Min(contentLength, acc.Count - bodyStart));

            // preserve any bytes past this request for the next one (pipelined keep-alive)
            int consumed = bodyStart + contentLength;
            for (int i = consumed; i < acc.Count; i++)
                carry.Add(acc[i]);
            return req;
        }

        private static int IndexOfDoubleCrlf(List<byte> b)
        {
            for (int i = 0; i + 3 < b.Count; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10)
                    return i;
            return -1;
        }

        private static async Task HttpWriteAsync(NetworkStream stream, string status, string? body, (string, string)[]? extraHeaders, CancellationToken ct)
        {
            var bodyBytes = body != null ? Encoding.UTF8.GetBytes(body) : new byte[0];
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            if (body != null)
                sb.Append("Content-Type: application/json\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            if (extraHeaders != null)
                foreach (var (k, v) in extraHeaders)
                    sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("Connection: keep-alive\r\n\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { } // stops accepting; does NOT close accepted clients
            foreach (var client in _clients.Keys) // close accepted sockets to unblock parked reads
                try { client.Close(); } catch { }
            _cts?.Dispose();
            try { if (_mcpConfigPath != null) File.Delete(_mcpConfigPath); } catch { }
        }
    }
}
