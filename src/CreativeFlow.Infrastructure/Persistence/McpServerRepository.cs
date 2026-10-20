using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <remarks>
/// <c>args</c>、<c>env</c> 和 <c>headers</c> 以 JSON 文本跨越边界，并在 SQL 中转换为 jsonb。
/// Npgsql 的 <c>EnableDynamicJson</c> 在 AOT 下不可用，因此保持显式转换，
/// 而不依赖运行时类型映射器。
/// </remarks>
/// <summary>初始化 McpServerRepository 的依赖与实例状态。</summary>
public sealed class McpServerRepository(CreativeFlowConnectionFactory connections) : IMcpServerRepository
{
    /// <summary>该仓储查询使用的固定列清单。</summary>
    private const string Columns = """
        server_id, device_id, name, description, enabled, transport, command,
        args::text AS args_json, env::text AS env_json, cwd, url,
        headers::text AS headers_json, auto_fallback, created_at, updated_at
        """;

    /// <summary>按创建顺序列出当前可访问设备范围内的 MCP 服务配置。</summary>
    public async Task<IReadOnlyList<McpServer>> ListAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<McpServerRow>(
            $"""
            SELECT {Columns}
            FROM mcp_server
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY created_at
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在可访问设备范围内查询指定 MCP 服务及其传输配置。</summary>
    public async Task<McpServer?> FindAsync(
        DeviceId deviceId,
        McpServerId serverId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<McpServerRow>(
            $"""
            SELECT {Columns}
            FROM mcp_server
            WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new McpScopeParam { ServerId = serverId.Value, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    /// <summary>插入新的 MCP 服务配置，保留冲突错误供调用方处理。</summary>
    public async Task InsertAsync(McpServer server, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await InsertCoreAsync(connection, server, onConflictDoNothing: false);
    }

    /// <summary>逐项导入尚不存在的 MCP 服务配置并返回新增数量，空列表不访问数据库。</summary>
    public async Task<int> InsertIfAbsentAsync(
        IReadOnlyList<McpServer> servers,
        CancellationToken ct)
    {
        if (servers.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);

        var added = 0;
        foreach (var server in servers)
        {
            added += await InsertCoreAsync(connection, server, onConflictDoNothing: true);
        }

        return added;
    }

    /// <summary>在同一连接和事务边界内完成当前原子写入操作。</summary>
    private static async Task<int> InsertCoreAsync(
        NpgsqlConnection connection,
        McpServer server,
        bool onConflictDoNothing)
    {
        var conflict = onConflictDoNothing ? "ON CONFLICT (device_id, name) DO NOTHING" : "";

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO mcp_server (server_id, device_id, name, description, enabled, transport,
                                    command, args, env, cwd, url, headers, auto_fallback,
                                    created_at, updated_at)
            VALUES (@ServerId, @DeviceId, @Name, @Description, @Enabled, @Transport,
                    @Command, @ArgsJson::jsonb, @EnvJson::jsonb, @Cwd, @Url,
                    @HeadersJson::jsonb, @AutoFallback, @CreatedAt, @UpdatedAt)
            {conflict}
            """,
            new InsertMcpParams
            {
                ServerId = server.Id.Value,
                DeviceId = server.DeviceId.Value,
                Name = server.Name,
                Description = server.Description,
                Enabled = server.Enabled,
                Transport = server.Transport.ToWire(),
                Command = server.Command,
                ArgsJson = server.ArgsJson,
                EnvJson = server.EnvJson,
                Cwd = server.Cwd,
                Url = server.Url,
                HeadersJson = server.HeadersJson,
                AutoFallback = server.AutoFallback,
                CreatedAt = server.CreatedAt,
                UpdatedAt = server.UpdatedAt,
            });
    }

    /// <summary>在可访问设备范围内更新 MCP 服务的传输参数、密钥配置和启用状态。</summary>
    public async Task UpdateAsync(McpServer server, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE mcp_server
            SET name = @Name, description = @Description, enabled = @Enabled,
                transport = @Transport, command = @Command, args = @ArgsJson::jsonb,
                env = @EnvJson::jsonb, cwd = @Cwd, url = @Url,
                headers = @HeadersJson::jsonb, auto_fallback = @AutoFallback,
                updated_at = @UpdatedAt
            WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new UpdateMcpParams
            {
                ServerId = server.Id.Value,
                DeviceId = server.DeviceId.Value,
                Name = server.Name,
                Description = server.Description,
                Enabled = server.Enabled,
                Transport = server.Transport.ToWire(),
                Command = server.Command,
                ArgsJson = server.ArgsJson,
                EnvJson = server.EnvJson,
                Cwd = server.Cwd,
                Url = server.Url,
                HeadersJson = server.HeadersJson,
                AutoFallback = server.AutoFallback,
                UpdatedAt = server.UpdatedAt,
            });
    }

    /// <summary>在可访问设备范围内删除 MCP 服务配置，返回是否实际删除。</summary>
    public async Task<bool> DeleteAsync(
        DeviceId deviceId,
        McpServerId serverId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM mcp_server WHERE server_id = @ServerId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new McpScopeParam { ServerId = serverId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }
}

internal sealed class McpScopeParam
{
    /// <summary>MCP 服务配置的唯一标识。</summary>
    public Guid ServerId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class InsertMcpParams
{
    /// <summary>MCP 服务配置的唯一标识。</summary>
    public Guid ServerId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>MCP 服务使用的传输方式。</summary>
    public string Transport { get; init; } = string.Empty;
    /// <summary>已配置的进程或脚本入口命令。</summary>
    public string? Command { get; init; }
    /// <summary>以 JSON 保存的进程参数列表。</summary>
    public string ArgsJson { get; init; } = "[]";
    /// <summary>以 JSON 保存的环境变量配置；敏感值通过既有密钥机制保护。</summary>
    public string EnvJson { get; init; } = "{}";
    /// <summary>进程执行时使用的工作目录。</summary>
    public string? Cwd { get; init; }
    /// <summary>资源的交付或访问地址。</summary>
    public string? Url { get; init; }
    /// <summary>以 JSON 保存的请求头配置，秘密值不直接回传。</summary>
    public string HeadersJson { get; init; } = "{}";
    /// <summary>是否允许按既有策略选择回退来源。</summary>
    public bool AutoFallback { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateMcpParams
{
    /// <summary>MCP 服务配置的唯一标识。</summary>
    public Guid ServerId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>MCP 服务使用的传输方式。</summary>
    public string Transport { get; init; } = string.Empty;
    /// <summary>已配置的进程或脚本入口命令。</summary>
    public string? Command { get; init; }
    /// <summary>以 JSON 保存的进程参数列表。</summary>
    public string ArgsJson { get; init; } = "[]";
    /// <summary>以 JSON 保存的环境变量配置；敏感值通过既有密钥机制保护。</summary>
    public string EnvJson { get; init; } = "{}";
    /// <summary>进程执行时使用的工作目录。</summary>
    public string? Cwd { get; init; }
    /// <summary>资源的交付或访问地址。</summary>
    public string? Url { get; init; }
    /// <summary>以 JSON 保存的请求头配置，秘密值不直接回传。</summary>
    public string HeadersJson { get; init; } = "{}";
    /// <summary>是否允许按既有策略选择回退来源。</summary>
    public bool AutoFallback { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class McpServerRow
{
    /// <summary>MCP 服务配置的唯一标识。</summary>
    public Guid ServerId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>MCP 服务使用的传输方式。</summary>
    public string Transport { get; init; } = string.Empty;
    /// <summary>已配置的进程或脚本入口命令。</summary>
    public string? Command { get; init; }
    /// <summary>以 JSON 保存的进程参数列表。</summary>
    public string ArgsJson { get; init; } = "[]";
    /// <summary>以 JSON 保存的环境变量配置；敏感值通过既有密钥机制保护。</summary>
    public string EnvJson { get; init; } = "{}";
    /// <summary>进程执行时使用的工作目录。</summary>
    public string? Cwd { get; init; }
    /// <summary>资源的交付或访问地址。</summary>
    public string? Url { get; init; }
    /// <summary>以 JSON 保存的请求头配置，秘密值不直接回传。</summary>
    public string HeadersJson { get; init; } = "{}";
    /// <summary>是否允许按既有策略选择回退来源。</summary>
    public bool AutoFallback { get; init; }

    // timestamptz 读取为 UTC DateTime；若此处使用 DateTimeOffset，Dapper 会尝试不存在的
    // Convert.ChangeType 转换并在运行时抛错。参见 ProviderRow。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public McpServer ToDomain()
    {
        if (!AiEnumNames.TryParseMcpTransport(Transport, out var transport))
        {
            throw new InvalidOperationException(
                $"MCP server {ServerId} has unrecognised transport '{Transport}'.");
        }

        return new McpServer(
            new McpServerId(ServerId),
            new DeviceId(DeviceId),
            Name,
            Description,
            Enabled,
            transport,
            Command,
            ArgsJson,
            EnvJson,
            Cwd,
            Url,
            HeadersJson,
            AutoFallback,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }
}
