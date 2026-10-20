using System.Net;
using System.Net.Sockets;
using System.Text;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Mcp;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Ai;

public class McpRuntimeTests
{
    /// <summary>当前测试独立使用的合成密钥，不用于真实凭据。</summary>
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    /// <summary>回归场景：Private And Reserved Addresses Are Forbidden。</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.2.3")]
    [InlineData("192.168.1.4")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void PrivateAndReservedAddressesAreForbidden(string text)
    {
        Assert.True(McpNetworkGuard.IsForbidden(IPAddress.Parse(text)));
    }

    /// <summary>回归场景：Public Addresses Are Allowed。</summary>
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string text)
    {
        Assert.False(McpNetworkGuard.IsForbidden(IPAddress.Parse(text)));
    }

    /// <summary>回归场景：Streamable Http Initializes And Lists Tools。</summary>
    [Fact]
    public async Task StreamableHttpInitializesAndListsTools()
    {
        await using var server = await LoopbackMcpServer.StartAsync();
        var runtime = Runtime();

        var tools = await runtime.ListToolsAsync(
            Registration(server.Url), TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("search", tool.Name);
        Assert.Equal("Search things", tool.Description);
        Assert.Equal("{\"type\":\"object\"}", tool.InputSchemaJson);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
    }

    /// <summary>回归场景：Streamable Http Calls A Tool And Returns Structured Content。</summary>
    [Fact]
    public async Task StreamableHttpCallsAToolAndReturnsStructuredContent()
    {
        await using var server = await LoopbackMcpServer.StartAsync();
        var runtime = Runtime();

        var result = await runtime.CallToolAsync(
            Registration(server.Url), "search", "{\"q\":\"cats\"}",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("cats found", result.Json, StringComparison.Ordinal);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/call"], server.Methods);
    }

    /// <summary>回归场景：Legacy Sse Discovers Its Message Endpoint And Lists Tools。</summary>
    [Fact]
    public async Task LegacySseDiscoversItsMessageEndpointAndListsTools()
    {
        await using var server = await LegacySseMcpServer.StartAsync();
        var runtime = Runtime();

        var tools = await runtime.ListToolsAsync(
            Registration(server.Url) with { Transport = McpTransport.Sse },
            TestContext.Current.CancellationToken);

        Assert.Equal("search", Assert.Single(tools).Name);
        await server.Completion;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
    }

    /// <summary>回归场景：Plain Http Is Rejected Without Development Loopback Option。</summary>
    [Fact]
    public async Task PlainHttpIsRejectedWithoutDevelopmentLoopbackOption()
    {
        var options = new McpOptions { AllowLoopbackHttp = false };
        var guard = new McpNetworkGuard(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await guard.ValidateAsync(
                new Uri("http://127.0.0.1:1234/mcp"),
                TestContext.Current.CancellationToken));

        Assert.Contains("Plain HTTP", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>组装使用测试网络边界的 MCP 运行器。</summary>
    private static McpRuntime Runtime()
    {
        var options = new McpOptions
        {
            AllowLoopbackHttp = true,
            RequestTimeoutSeconds = 10,
            MaxResponseBytes = 65_536,
        };
        var protector = new AesGcmApiKeyProtector(new AiOptions { EncryptionKey = TestKey });
        return new McpRuntime(protector, new McpNetworkGuard(options), options);
    }

    /// <summary>回归场景：Registration。</summary>
    private static McpServer Registration(string url) => new(
        McpServerId.New(), DeviceId.New(), "test", null, true,
        McpTransport.StreamableHttp, null, "[]", "{}", null, url, "{}", false,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class LegacySseMcpServer : IAsyncDisposable
    {
        /// <summary>测试使用的本地 HTTP 监听器。</summary>
        private readonly TcpListener _listener;
        /// <summary>用于停止当前测试回环服务的取消源。</summary>
        private readonly CancellationTokenSource _stop = new();

        /// <summary>初始化 LegacySseMcpServer，保留既有依赖、配置校验与资源生命周期。</summary>
        private LegacySseMcpServer(TcpListener listener)
        {
            _listener = listener;
            Completion = ServeAsync(_stop.Token);
        }

        /// <summary>资源的交付或访问地址。</summary>
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/sse";
        /// <summary>回环 MCP 服务按收到顺序记录的方法名。</summary>
        public List<string> Methods { get; } = [];
        /// <summary>当前工具调用或审批等待的完成通知。</summary>
        public Task Completion { get; }

        /// <summary>校验输入并提交异步视频生成任务。</summary>
        public static Task<LegacySseMcpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LegacySseMcpServer(listener));
        }

        /// <summary>在测试回环服务器处理当前场景约定的 HTTP 或 SSE 请求。</summary>
        private async Task ServeAsync(CancellationToken ct)
        {
            // 测试服务复现旧 SSE 的端点公告与握手顺序，确保客户端没有跳过初始化直接调用工具。
            using var sseClient = await _listener.AcceptTcpClientAsync(ct);
            await using var sse = sseClient.GetStream();
            using (var reader = new StreamReader(sse, Encoding.UTF8, false, leaveOpen: true))
            {
                while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0) { }
            }
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: keep-alive\r\n\r\n");
            await sse.WriteAsync(headers, ct);
            var endpoint = Encoding.UTF8.GetBytes(
                $"event: endpoint\ndata: /message\n\n");
            await sse.WriteAsync(endpoint, ct);
            await sse.FlushAsync(ct);

            for (var index = 0; index < 3; index++)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var body = await ReadBodyAsync(stream, ct);
                var method = ReadJsonRpcMethod(body);
                Methods.Add(method);
                await WriteHttpAsync(stream, 202, string.Empty, ct);

                string? response = method switch
                {
                    "initialize" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}",
                    "tools/list" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Search things\",\"inputSchema\":{\"type\":\"object\"}}]}}",
                    _ => null,
                };
                if (response is not null)
                {
                    var frame = Encoding.UTF8.GetBytes($"event: message\ndata: {response}\n\n");
                    await sse.WriteAsync(frame, ct);
                    await sse.FlushAsync(ct);
                }
            }
        }

        /// <summary>根据 HTTP 头声明的长度读取测试请求正文。</summary>
        private static async Task<string> ReadBodyAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            var length = 0;
            while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line["Content-Length:".Length..].Trim());
            }
            var chars = new char[length];
            var read = 0;
            while (read < chars.Length)
            {
                var count = await reader.ReadAsync(chars.AsMemory(read), ct);
                if (count == 0) break;
                read += count;
            }
            return new string(chars, 0, read);
        }

        /// <summary>从测试收到的 JSON-RPC 正文提取方法名。</summary>
        private static string ReadJsonRpcMethod(string body)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.GetProperty("method").GetString() ?? string.Empty;
        }

        /// <summary>向测试回环连接写入完整的 HTTP 状态、头和响应正文。</summary>
        private static async Task WriteHttpAsync(
            Stream stream,
            int status,
            string body,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} Accepted\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>取消并停止测试监听器，等待连接处理结束后释放停止令牌源。</summary>
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await Completion; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private sealed class LoopbackMcpServer : IAsyncDisposable
    {
        /// <summary>测试使用的本地 HTTP 监听器。</summary>
        private readonly TcpListener _listener;
        /// <summary>用于停止当前测试回环服务的取消源。</summary>
        private readonly CancellationTokenSource _stop = new();

        /// <summary>初始化 LoopbackMcpServer，保留既有依赖、配置校验与资源生命周期。</summary>
        private LoopbackMcpServer(TcpListener listener)
        {
            _listener = listener;
            Completion = ServeAsync(_stop.Token);
        }

        /// <summary>资源的交付或访问地址。</summary>
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp";
        /// <summary>回环 MCP 服务按收到顺序记录的方法名。</summary>
        public List<string> Methods { get; } = [];
        /// <summary>当前工具调用或审批等待的完成通知。</summary>
        public Task Completion { get; }

        /// <summary>校验输入并提交异步视频生成任务。</summary>
        public static Task<LoopbackMcpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackMcpServer(listener));
        }

        /// <summary>在测试回环服务器处理当前场景约定的 HTTP 或 SSE 请求。</summary>
        private async Task ServeAsync(CancellationToken ct)
        {
            // 回环服务按真实 HTTP 请求记录 JSON-RPC 顺序，并返回当前测试需要的 JSON 或 SSE 响应。
            for (var index = 0; index < 3; index++)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(ct) is { } line && line.Length > 0)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                }
                var chars = new char[contentLength];
                var read = 0;
                while (read < chars.Length)
                {
                    var count = await reader.ReadAsync(chars.AsMemory(read), ct);
                    if (count == 0) break;
                    read += count;
                }
                var body = new string(chars, 0, read);
                var method = ReadMethod(body);
                Methods.Add(method);

                if (method == "notifications/initialized")
                {
                    await WriteAsync(stream, 202, string.Empty, ct);
                    continue;
                }

                var response = method switch
                {
                    "initialize" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
                    "tools/list" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Search things\",\"inputSchema\":{\"type\":\"object\"}}]}}",
                    "tools/call" =>
                        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"cats found\"}],\"isError\":false}}",
                    _ => throw new InvalidOperationException($"Unexpected method {method}"),
                };
                await WriteAsync(stream, 200, response, ct);
            }
        }

        /// <summary>从测试请求行与正文解析当前 JSON-RPC 方法。</summary>
        private static string ReadMethod(string body)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.GetProperty("method").GetString() ?? string.Empty;
        }

        /// <summary>向测试连接写入当前场景约定的响应。</summary>
        private static async Task WriteAsync(
            Stream stream,
            int status,
            string body,
            CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : "Accepted";
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, ct);
            if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>取消并停止回环 MCP 测试服务，等待处理任务结束并释放停止令牌源。</summary>
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await Completion; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
