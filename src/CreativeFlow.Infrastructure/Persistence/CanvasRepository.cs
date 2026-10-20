using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <remarks>
/// 图以 JSON 文本跨越边界，并在 SQL 中转换为 jsonb。Npgsql 的动态 JSON 映射在 AOT 下不可用，
/// 因此保持显式转换；<see cref="McpServerRepository"/> 对参数、环境变量和请求头列也采用相同方式。
/// </remarks>
/// <summary>初始化 CanvasRepository 的依赖与实例状态。</summary>
public sealed class CanvasRepository(CreativeFlowConnectionFactory connections) : ICanvasRepository
{
    /// <summary>按可访问设备范围列出画布摘要，在 SQL 中安全计算节点数量而不传输完整图。</summary>
    public async Task<IReadOnlyList<CanvasSummary>> ListAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 在 SQL 中计算节点数量，使列表请求无需传输图数据。
        // jsonb_array_length 遇到非数组会抛出异常，因此需要类型保护；
        // 旧客户端写入的图或空默认值不能导致整个列表查询失败。
        var rows = await connection.QueryAsync<CanvasSummaryRow>(
            """
            SELECT canvas_id,
                   name,
                   CASE WHEN jsonb_typeof(graph -> 'nodes') = 'array'
                        THEN jsonb_array_length(graph -> 'nodes')
                        ELSE 0
                   END AS node_count,
                   created_at,
                   updated_at
            FROM canvas
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY updated_at DESC
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在可访问设备范围内读取完整画布及图 JSON，未命中时返回空。</summary>
    public async Task<Canvas?> FindAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<CanvasRow>(
            """
            SELECT canvas_id, device_id, name, graph::text AS graph_json, created_at, updated_at
            FROM canvas
            WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new CanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    /// <summary>保存新画布的名称、归属设备、完整图 JSON 和时间信息。</summary>
    public async Task InsertAsync(Canvas canvas, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO canvas (canvas_id, device_id, name, graph, created_at, updated_at)
            VALUES (@CanvasId, @DeviceId, @Name, @GraphJson::jsonb, @CreatedAt, @UpdatedAt)
            """,
            new InsertCanvasParams
            {
                CanvasId = canvas.Id.Value,
                DeviceId = canvas.DeviceId.Value,
                Name = canvas.Name,
                GraphJson = canvas.GraphJson,
                CreatedAt = canvas.CreatedAt,
                UpdatedAt = canvas.UpdatedAt,
            });
    }

    /// <summary>在可访问设备范围内更新画布名称、图 JSON 和更新时间，返回是否命中记录。</summary>
    public async Task<bool> UpdateAsync(Canvas canvas, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            UPDATE canvas
            SET name = @Name, graph = @GraphJson::jsonb, updated_at = @UpdatedAt
            WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new UpdateCanvasParams
            {
                CanvasId = canvas.Id.Value,
                DeviceId = canvas.DeviceId.Value,
                Name = canvas.Name,
                GraphJson = canvas.GraphJson,
                UpdatedAt = canvas.UpdatedAt,
            });

        return affected > 0;
    }

    /// <summary>删除当前设备可访问的指定画布，并返回是否实际删除。</summary>
    public async Task<bool> DeleteAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM canvas WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new CanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }
}

internal sealed class CanvasScopeParams
{
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class InsertCanvasParams
{
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateCanvasParams
{
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class CanvasRow
{
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";

    // timestamptz 会读取为 UTC DateTime；若读取行声明为 DateTimeOffset，Dapper 会尝试不存在的
    // Convert.ChangeType 转换并在运行时抛错，而构建阶段不会报错。参见 ProviderRow。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public Canvas ToDomain() => new(
        new CanvasId(CanvasId),
        new DeviceId(DeviceId),
        Name,
        GraphJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}

internal sealed class CanvasSummaryRow
{
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>节点图包含的节点数量。</summary>
    public int NodeCount { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public CanvasSummary ToDomain() => new(
        CanvasId,
        Name,
        NodeCount,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
