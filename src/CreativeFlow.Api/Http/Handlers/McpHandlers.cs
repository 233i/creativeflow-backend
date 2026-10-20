using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>
/// 按调用设备限定作用域的 MCP 服务器注册。
/// </summary>
internal static class McpHandlers
{
    /// <summary>列出MCP 服务配置，保留现有筛选、排序与访问范围。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http,
        IMcpServerRepository servers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        var list = await servers.ListAsync(deviceId, ct);
        var migrated = new List<McpServer>(list.Count);
        foreach (var server in list)
        {
            var protectedServer = ProtectLegacySecrets(server, protector);
            if (protectedServer != server) await servers.UpdateAsync(protectedServer, ct);
            migrated.Add(protectedServer);
        }

        return ApiResults.Ok(new McpServerListResponse([.. migrated.Select(ToResponse)]));
    }

    /// <summary>校验输入并创建MCP 服务配置。</summary>
    internal static async Task<IActionResult> CreateAsync(
        CreateMcpServerRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!TryBuild(request, deviceId, DateTimeOffset.UtcNow, protector, out var server, out var error))
        {
            return Invalid(error);
        }

        var existing = await servers.ListAsync(deviceId, ct);
        if (existing.Any(s => s.Name == server.Name))
        {
            return ApiResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"A server named '{server.Name}' already exists."]));
        }

        await servers.InsertAsync(server, ct);

        return ApiResults.Created($"/api/v1/mcp/servers/{server.Id}", ToResponse(server));
    }

    /// <summary>批量导入；名称已存在的服务器会被跳过，因此可安全重复执行。</summary>
    internal static async Task<IActionResult> ImportAsync(
        ImportMcpServersRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var built = new List<McpServer>(request.Servers.Length);

        foreach (var entry in request.Servers)
        {
            if (!TryBuild(entry, deviceId, now, protector, out var server, out var error))
            {
                return Invalid(error);
            }

            built.Add(server);
        }

        var added = await servers.InsertIfAbsentAsync(built, ct);

        return ApiResults.Ok(new ImportMcpServersResponse(added));
    }

    /// <summary>更新MCP 服务配置，保留原有访问范围和并发结果。</summary>
    internal static async Task<IActionResult> UpdateAsync(
        string id,
        UpdateMcpServerRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        // 未重新提交的秘密需要按现有更新规则保留，不能用展示用掩码覆盖已保存的密钥。
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!McpServerId.TryParse(id, out var serverId))
        {
            return Invalid("malformed server id");
        }

        var existing = await servers.FindAsync(deviceId, serverId, ct);
        if (existing is null)
        {
            return ApiResults.NotFound();
        }

        var transport = existing.Transport;
        if (request.Transport is not null
            && !AiEnumNames.TryParseMcpTransport(request.Transport, out transport))
        {
            return Invalid($"unknown transport '{request.Transport}'");
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Description = request.Description ?? existing.Description,
            Enabled = request.Enabled ?? existing.Enabled,
            Transport = transport,
            Command = request.Command ?? existing.Command,
            ArgsJson = request.Args is null ? existing.ArgsJson : SerializeArray(request.Args),
            EnvJson = request.Env is null
                ? ProtectLegacyEntries(existing.EnvJson, protector)
                : SerializeSecretEntries(request.Env, protector),
            Cwd = request.Cwd ?? existing.Cwd,
            Url = request.Url ?? existing.Url,
            HeadersJson = request.Headers is null
                ? ProtectLegacyEntries(existing.HeadersJson, protector)
                : SerializeSecretEntries(request.Headers, protector),
            AutoFallback = request.AutoFallback ?? existing.AutoFallback,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        if (!ValidateTransportFields(updated, out var transportError))
        {
            return Invalid(transportError);
        }

        await servers.UpdateAsync(updated, ct);

        return ApiResults.Ok(ToResponse(updated));
    }

    /// <summary>检查MCP 服务配置对应的上游可用性。</summary>
    internal static async Task<IActionResult> CheckAsync(
        string id,
        HttpContext http,
        IMcpServerRepository servers,
        IMcpRuntime runtime,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!McpServerId.TryParse(id, out var serverId)) return Invalid("malformed server id");
        var server = await servers.FindAsync(deviceId, serverId, ct);
        if (server is null) return ApiResults.NotFound();
        if (server.Transport == McpTransport.Stdio)
        {
            return ApiResults.Ok(new McpServerCheckResponse(
                false, "The stdio MCP transport is not supported.", 0));
        }

        try
        {
            var tools = await runtime.ListToolsAsync(server, ct);
            return ApiResults.Ok(new McpServerCheckResponse(true, null, tools.Count));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException or HttpRequestException or TaskCanceledException)
        {
            return ApiResults.Ok(new McpServerCheckResponse(
                false, BoundedError(exception.Message), 0));
        }
    }

    /// <summary>读取当前可用工具目录。</summary>
    internal static async Task<IActionResult> ListToolsAsync(
        string id,
        HttpContext http,
        IMcpServerRepository servers,
        IMcpRuntime runtime,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!McpServerId.TryParse(id, out var serverId)) return Invalid("malformed server id");
        var server = await servers.FindAsync(deviceId, serverId, ct);
        if (server is null) return ApiResults.NotFound();
        try
        {
            var tools = await runtime.ListToolsAsync(server, ct);
            return ApiResults.Ok(new McpToolListResponse(
            [
                .. tools.Select(tool => new McpToolResponse(
                    tool.Name, tool.Description, tool.InputSchemaJson)),
            ]));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException or HttpRequestException or TaskCanceledException)
        {
            return Invalid(BoundedError(exception.Message));
        }
    }

    /// <summary>删除MCP 服务配置，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!McpServerId.TryParse(id, out var serverId))
        {
            return Invalid("malformed server id");
        }

        return await servers.DeleteAsync(deviceId, serverId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>验证 MCP 配置并构造服务端保存的记录。</summary>
    private static bool TryBuild(
        CreateMcpServerRequest request,
        CreativeFlow.Domain.Identity.DeviceId deviceId,
        DateTimeOffset now,
        IApiKeyProtector protector,
        out McpServer server,
        out string error)
    {
        // HTTP、SSE 与进程型配置各有输入要求，先按传输类型校验再形成统一配置记录。
        server = null!;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "name is required";
            return false;
        }

        if (!AiEnumNames.TryParseMcpTransport(request.Transport, out var transport))
        {
            error = $"unknown transport '{request.Transport}'";
            return false;
        }

        var candidate = new McpServer(
            McpServerId.New(),
            deviceId,
            request.Name.Trim(),
            request.Description,
            request.Enabled ?? true,
            transport,
            request.Command,
            SerializeArray(request.Args ?? []),
            SerializeSecretEntries(request.Env ?? [], protector),
            request.Cwd,
            request.Url,
            SerializeSecretEntries(request.Headers ?? [], protector),
            request.AutoFallback ?? false,
            now,
            now);

        if (!ValidateTransportFields(candidate, out error))
        {
            return false;
        }

        server = candidate;
        return true;
    }

    /// <summary>
    /// 检查传输方式实际使用的字段是否存在。
    /// </summary>
    /// <remarks>
    /// 架构会接受没有命令的标准输入输出服务器或没有 URL 的 HTTP 服务器，但它们永远无法连接。
    /// 在此拒绝可将令人困惑的运行时失败转为即时、明确的消息。
    /// </remarks>
    private static bool ValidateTransportFields(McpServer server, out string error)
    {
        if (server.Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                error = "command is required for the stdio transport";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(server.Url))
        {
            error = $"url is required for the {server.Transport.ToWire()} transport";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // 使用 JsonNode 而非序列化上下文：这些是用户提供且结构不固定的任意字符串映射，
    // JsonNode 不会对类型进行反射。
    //
    // 值以 JsonNode 添加，而不使用泛型 JsonArray.Add<T> 或索引器重载；后者要求动态代码，
    // 因为必须在运行时处理任意 T。通过 JsonValue.Create(string) 可让整条路径保持静态可解析。
    /// <summary>将字符串列表编码为既有 JSON 数组格式。</summary>
    private static string SerializeArray(string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode)JsonValue.Create(value));
        }

        return array.ToJsonString();
    }

    /// <summary>编码秘密配置项，保持加密值与公开元数据分离。</summary>
    private static string SerializeSecretEntries(
        McpEnvEntry[] entries,
        IApiKeyProtector protector)
    {
        var obj = new JsonObject();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrEmpty(entry.Value)) continue;
            // 后出现的重复项覆盖前项，与读取 JSON 对象时的行为一致。
            obj[entry.Key.Trim()] = JsonValue.Create(McpSecretCodec.Protect(protector, entry.Value));
        }

        return obj.ToJsonString();
    }

    /// <summary>将仍以旧格式保存的秘密项转换为受保护内容。</summary>
    private static string ProtectLegacyEntries(string json, IApiKeyProtector protector)
    {
        var entries = DeserializeEntries(json);
        if (entries.Length == 0) return "{}";

        var obj = new JsonObject();
        foreach (var entry in entries)
        {
            obj[entry.Key] = JsonValue.Create(
                McpSecretCodec.IsProtected(entry.Value)
                    ? entry.Value
                    : McpSecretCodec.Protect(protector, entry.Value));
        }
        return obj.ToJsonString();
    }

    /// <summary>为旧 MCP 配置补充秘密保护，保留非秘密字段。</summary>
    private static McpServer ProtectLegacySecrets(McpServer server, IApiKeyProtector protector)
    {
        var migrateEnv = DeserializeEntries(server.EnvJson)
            .Any(entry => !McpSecretCodec.IsProtected(entry.Value));
        var migrateHeaders = DeserializeEntries(server.HeadersJson)
            .Any(entry => !McpSecretCodec.IsProtected(entry.Value));
        if (!migrateEnv && !migrateHeaders) return server;

        return server with
        {
            EnvJson = migrateEnv ? ProtectLegacyEntries(server.EnvJson, protector) : server.EnvJson,
            HeadersJson = migrateHeaders
                ? ProtectLegacyEntries(server.HeadersJson, protector)
                : server.HeadersJson,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>读取已保存的字符串数组，沿用缺失和无效数据回退。</summary>
    private static string[] DeserializeArray(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonArray array
                ? [.. array.Select(node => node?.GetValue<string>()).OfType<string>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>读取 MCP 环境变量或请求头配置项。</summary>
    private static McpEnvEntry[] DeserializeEntries(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return [];
            }

            var entries = new List<McpEnvEntry>(obj.Count);
            foreach (var (key, value) in obj)
            {
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                {
                    entries.Add(new McpEnvEntry(key, text));
                }
            }

            return [.. entries];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>只投影秘密项的公开名称和掩码信息。</summary>
    private static McpSecretEntry[] SecretMetadata(string json) =>
    [
        .. DeserializeEntries(json).Select(entry =>
            new McpSecretEntry(entry.Key, IsConfigured: true, Hint: "••••")),
    ];

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static McpServerResponse ToResponse(McpServer server) => new(
        server.Id.ToString(),
        server.Name,
        server.Description,
        server.Enabled,
        server.Transport.ToWire(),
        server.Command,
        DeserializeArray(server.ArgsJson),
        SecretMetadata(server.EnvJson),
        server.Cwd,
        server.Url,
        SecretMetadata(server.HeadersJson),
        server.AutoFallback,
        server.CreatedAt.ToString("O"),
        server.UpdatedAt.ToString("O"));

    /// <summary>限制对外错误文本的长度，保留既有截断规则。</summary>
    private static string BoundedError(string message) =>
        message.Length <= 400 ? message : message[..400] + "…";

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
