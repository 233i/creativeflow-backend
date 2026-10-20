using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai.Mcp;

/// <summary>初始化 McpRuntime 的依赖与实例状态。</summary>
internal sealed class McpRuntime(
    IApiKeyProtector protector,
    IMcpNetworkGuard networkGuard,
    McpOptions options) : IMcpRuntime
{
    /// <summary>当前 MCP 会话协商采用的协议版本。</summary>
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>通过已支持的 MCP 传输获取工具目录，过滤无名称条目并保留工具描述及输入 schema。</summary>
    public async Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(
        McpServer server,
        CancellationToken ct)
    {
        EnsureSupported(server);
        var result = await ExecuteSequenceAsync(server, "tools/list", new JsonObject(), ct);
        if (result["tools"] is not JsonArray tools) return [];

        var mapped = new List<McpRuntimeTool>(tools.Count);
        foreach (var node in tools)
        {
            if (node is not JsonObject tool
                || tool["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            mapped.Add(new McpRuntimeTool(
                name,
                tool["description"]?.GetValue<string>() ?? string.Empty,
                tool["inputSchema"]?.ToJsonString() ?? "{\"type\":\"object\"}"));
        }
        return mapped;
    }

    /// <summary>解析工具参数并执行 MCP 调用，保留错误标记；响应超过 64 KiB 时返回截断的错误结果。</summary>
    public async Task<McpRuntimeResult> CallToolAsync(
        McpServer server,
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        EnsureSupported(server);
        var arguments = ParseObject(argumentsJson);
        var result = await ExecuteSequenceAsync(
            server,
            "tools/call",
            new JsonObject { ["name"] = toolName, ["arguments"] = arguments },
            ct);
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var json = result.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > 65_536)
        {
            var preview = json.Length <= 48_000 ? json : json[..48_000];
            json = new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray((JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "MCP result exceeded 64 KiB and was truncated.\n" + preview,
                }),
            }.ToJsonString();
            isError = true;
        }
        return new McpRuntimeResult(json, isError);
    }

    /// <summary>按配置选择 MCP 传输并执行初始化与业务请求序列。</summary>
    private async Task<JsonObject> ExecuteSequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        return server.Transport switch
        {
            McpTransport.StreamableHttp => await ExecuteHttpSequenceAsync(
                server, method, parameters, deadline.Token),
            McpTransport.Sse => await ExecuteLegacySequenceAsync(
                server, method, parameters, deadline.Token),
            _ => throw new NotSupportedException("The stdio MCP transport is not supported."),
        };
    }

    /// <summary>通过 Streamable HTTP 执行 MCP 会话与业务请求。</summary>
    private async Task<JsonObject> ExecuteHttpSequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        var uri = RequiredUri(server);
        using var client = await CreateClientAsync(uri, ct);
        string? sessionId = null;

        try
        {
            var initialized = await PostAsync(
                client, uri, server, Request(1, "initialize", InitializeParams()), sessionId, ct);
            sessionId = initialized.SessionId;
            ThrowIfError(initialized.Message);

            _ = await PostAsync(
                client, uri, server,
                Notification("notifications/initialized", new JsonObject()), sessionId, ct,
                expectResponse: false);

            var response = await PostAsync(
                client, uri, server, Request(2, method, parameters), sessionId, ct);
            ThrowIfError(response.Message);
            return response.Message["result"] as JsonObject ?? new JsonObject();
        }
        finally
        {
            if (sessionId is not null)
            {
                await CloseSessionAsync(client, uri, server, sessionId);
            }
        }
    }

    /// <summary>通过旧版 SSE 会话发现消息端点并交换 MCP 请求。</summary>
    private async Task<JsonObject> ExecuteLegacySequenceAsync(
        McpServer server,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        // 旧版 SSE 先从事件流取得同源消息端点，再按 initialize、initialized、业务请求的顺序完成握手。
        var streamUri = RequiredUri(server);
        using var client = await CreateClientAsync(streamUri, ct);
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, streamUri);
        ApplyHeaders(streamRequest, server);
        streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var streamResponse = await client.SendAsync(
            streamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!streamResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP SSE connection failed ({(int)streamResponse.StatusCode}).");
        }

        await using var source = await streamResponse.Content.ReadAsStreamAsync(ct);
        await using var bounded = new BoundedReadStream(source, options.MaxResponseBytes);
        await using var enumerator = SseReader.ReadAsync(bounded, ct).GetAsyncEnumerator(ct);
        var endpoint = await ReadEndpointAsync(enumerator, streamUri, ct);
        if (!string.Equals(endpoint.Scheme, streamUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.Host, streamUri.Host, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != streamUri.Port)
        {
            throw new InvalidOperationException(
                "The MCP SSE message endpoint must use the configured origin.");
        }
        _ = await networkGuard.ValidateAsync(endpoint, ct);

        await PostLegacyAsync(client, endpoint, server, Request(1, "initialize", InitializeParams()), ct);
        var initialized = await ReadResponseAsync(enumerator, id: 1, ct);
        ThrowIfError(initialized);

        await PostLegacyAsync(
            client, endpoint, server,
            Notification("notifications/initialized", new JsonObject()), ct);
        await PostLegacyAsync(client, endpoint, server, Request(2, method, parameters), ct);
        var response = await ReadResponseAsync(enumerator, id: 2, ct);
        ThrowIfError(response);
        return response["result"] as JsonObject ?? new JsonObject();
    }

    /// <summary>发送带当前会话和协议头的 MCP JSON-RPC 请求。</summary>
    private async Task<HttpResult> PostAsync(
        HttpClient client,
        Uri uri,
        McpServer server,
        JsonObject body,
        string? sessionId,
        CancellationToken ct,
        bool expectResponse = true)
    {
        // 同一 MCP 调用可能返回 JSON 或 SSE；两种格式都限制读取大小，并保留响应带回的 session 标识。
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, server);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP request failed ({(int)response.StatusCode}).");
        }
        var nextSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() ?? sessionId
            : sessionId;
        if (!expectResponse || response.StatusCode == HttpStatusCode.Accepted)
        {
            return new HttpResult(new JsonObject(), nextSession);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var bounded = new BoundedReadStream(source, options.MaxResponseBytes);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonObject message;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            message = await ReadJsonFrameAsync(
                bounded,
                body["id"]?.GetValue<long>(),
                ct);
        }
        else
        {
            message = await JsonNode.ParseAsync(bounded, cancellationToken: ct) as JsonObject
                ?? throw new InvalidOperationException("MCP returned an invalid JSON-RPC response.");
        }
        return new HttpResult(message, nextSession);
    }

    /// <summary>按既有规则关闭已经建立的 MCP HTTP 会话。</summary>
    private async Task CloseSessionAsync(
        HttpClient client,
        Uri uri,
        McpServer server,
        string sessionId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            ApplyHeaders(request, server);
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await client.SendAsync(request, timeout.Token);
        }
        catch
        {
            // 会话清理采用尽力而为方式；工具结果不能被删除请求失败所取代。
        }
    }

    /// <summary>向旧版 MCP SSE 会话的消息端点提交请求。</summary>
    private async Task PostLegacyAsync(
        HttpClient client,
        Uri endpoint,
        McpServer server,
        JsonObject body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request, server);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP SSE message failed ({(int)response.StatusCode}).");
        }
    }

    /// <summary>校验目的地址并创建带既有网络限制的 MCP HTTP 客户端。</summary>
    private async Task<HttpClient> CreateClientAsync(Uri uri, CancellationToken ct)
    {
        var addresses = await networkGuard.ValidateAsync(uri, ct);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        last = exception;
                        socket.Dispose();
                        if (exception is OperationCanceledException) throw;
                    }
                }
                throw new HttpRequestException("Could not connect to the validated MCP address.", last);
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>将已解密且允许的 MCP 请求头应用到请求。</summary>
    private void ApplyHeaders(HttpRequestMessage request, McpServer server)
    {
        foreach (var (key, encrypted) in SecretEntries(server.HeadersJson))
        {
            if (ForbiddenHeader(key)) continue;
            var value = McpSecretCodec.Unprotect(protector, encrypted);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>阻止调用方覆盖由传输或 HTTP 客户端管理的请求头。</summary>
    private static bool ForbiddenHeader(string key) => key.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取并解密 MCP 环境变量或请求头中的秘密项。</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> SecretEntries(string json)
    {
        var entries = new List<KeyValuePair<string, string>>();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return entries;
            foreach (var (key, value) in obj)
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    entries.Add(KeyValuePair.Create(key, text));
                }
            }
        }
        catch (JsonException)
        {
            // 忽略损坏的可选请求头；初始化过程会报告可用连接状态。
        }
        return entries;
    }

    /// <summary>构造当前支持版本的 MCP 初始化参数。</summary>
    private static JsonObject InitializeParams() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "CreativeFlow", ["version"] = "1" },
    };

    /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static JsonObject Request(long id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
    };

    /// <summary>构造不带响应 id 的 MCP JSON-RPC 通知。</summary>
    private static JsonObject Notification(string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters,
    };

    /// <summary>要求 JSON-RPC 消息是对象并返回其字段。</summary>
    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>检测上游 JSON-RPC 错误并保留现有异常映射。</summary>
    private static void ThrowIfError(JsonObject response)
    {
        if (response["error"] is not JsonObject error) return;
        var message = error["message"]?.GetValue<string>() ?? "MCP JSON-RPC error.";
        throw new InvalidOperationException(message.Length <= 400 ? message : message[..400] + "…");
    }

    /// <summary>从 MCP HTTP/SSE 响应中读取所需 JSON 帧。</summary>
    private static async Task<JsonObject> ReadJsonFrameAsync(
        Stream stream,
        long? expectedId,
        CancellationToken ct)
    {
        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0 || JsonNode.Parse(frame.Data) is not JsonObject result) continue;
            if (expectedId is null || result["id"]?.GetValue<long>() == expectedId) return result;
        }
        throw new InvalidOperationException("The MCP SSE response ended before its JSON-RPC response.");
    }

    /// <summary>从旧版 SSE 初始化事件中读取消息提交端点。</summary>
    private static async Task<Uri> ReadEndpointAsync(
        IAsyncEnumerator<SseFrame> frames,
        Uri streamUri,
        CancellationToken ct)
    {
        while (await frames.MoveNextAsync())
        {
            var frame = frames.Current;
            if (!string.Equals(frame.EventName, "endpoint", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(streamUri, frame.Data.Trim(), out var endpoint)) return endpoint;
            throw new InvalidOperationException("The MCP SSE endpoint event contained an invalid URL.");
        }
        throw new InvalidOperationException("The MCP SSE stream ended before its endpoint event.");
    }

    /// <summary>按请求关联 id 读取对应 MCP 响应。</summary>
    private static async Task<JsonObject> ReadResponseAsync(
        IAsyncEnumerator<SseFrame> frames,
        long id,
        CancellationToken ct)
    {
        while (await frames.MoveNextAsync())
        {
            var frame = frames.Current;
            if (frame.Data.Length == 0 || JsonNode.Parse(frame.Data) is not JsonObject message) continue;
            if (message["id"]?.GetValue<long>() == id) return message;
        }
        throw new InvalidOperationException($"The MCP SSE stream ended before response {id}.");
    }

    /// <summary>验证当前 MCP 传输方式是否属于运行时支持范围。</summary>
    private static void EnsureSupported(McpServer server)
    {
        if (!server.Enabled) throw new InvalidOperationException("The MCP server is disabled.");
        if (server.Transport == McpTransport.Stdio)
        {
            throw new NotSupportedException("The stdio MCP transport is not supported.");
        }
    }

    /// <summary>验证配置提供了符合现有规则的绝对地址。</summary>
    private static Uri RequiredUri(McpServer server) =>
        Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("The MCP server URL is invalid.");

    /// <param name="Message">供用户、模型或调用方阅读的消息正文。</param>
    /// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
    private sealed record HttpResult(JsonObject Message, string? SessionId);

    /// <summary>初始化 BoundedReadStream 的依赖与实例状态。</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        /// <summary>当前包装流累计读取的字节数。</summary>
        private long _read;
        /// <summary>沿用底层响应流的可读能力。</summary>
        public override bool CanRead => inner.CanRead;
        /// <summary>声明该受限响应流不支持定位读取位置。</summary>
        public override bool CanSeek => false;
        /// <summary>声明该受限响应流不支持写入。</summary>
        public override bool CanWrite => false;
        /// <summary>拒绝查询响应总长度，因为包装器只按累计读取量限制大小。</summary>
        public override long Length => throw new NotSupportedException();
        /// <summary>返回累计读取字节数；拒绝设置读取位置。</summary>
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        /// <summary>将刷新操作转发给底层流，不改变累计读取量。</summary>
        public override void Flush() => inner.Flush();
        /// <summary>从底层流同步读取并累计字节数，超过响应大小上限时抛出异常。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }
        /// <summary>从底层流异步读取并累计字节数，透传取消且在超过响应大小上限时抛出异常。</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Count(read);
            return read;
        }
        /// <summary>累计已读取字节并执行现有响应大小限制。</summary>
        private void Count(int read)
        {
            _read += read;
            if (_read > limit) throw new InvalidOperationException("MCP response exceeded the size limit.");
        }
        /// <summary>拒绝流定位，防止绕过按顺序累计的读取限制。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>拒绝修改底层响应流长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>拒绝写入只读的 MCP 响应流。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <summary>同步释放包装器时一并释放它拥有的底层响应流。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        /// <summary>异步释放底层响应流并抑制包装器的终结操作。</summary>
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
