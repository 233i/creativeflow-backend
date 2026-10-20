using Dapper;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 WorkflowRepository 的依赖与实例状态。</summary>
public sealed class WorkflowRepository(CreativeFlowConnectionFactory connections) : IWorkflowRepository
{
    /// <summary>列出当前可访问的普通工作流摘要，排除模板调试图与后台 API 工作流。</summary>
    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowSummaryRow>(
            """
            SELECT workflow_id,
                   name,
                   CASE WHEN jsonb_typeof(graph -> 'nodes') = 'array'
                        THEN jsonb_array_length(graph -> 'nodes')
                        ELSE 0
                   END AS node_count,
                   created_at,
                   updated_at
            FROM workflow
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND debug_template_id IS NULL
              AND NOT is_api
              AND deleted_at IS NULL
            ORDER BY updated_at DESC
            """,
            new WorkflowDeviceParams { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToSummary()).ToList();
    }

    /// <summary>在可访问设备范围内读取工作流及完整图快照，不在仓储额外改变图类型。</summary>
    public async Task<Workflow?> FindAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRow>(
            """
            SELECT workflow_id, device_id, name, schema_version, is_api, copied_from_source, copied_from_id,
                   graph::text AS graph_json, created_at, updated_at
            FROM workflow
            WHERE workflow_id = @WorkflowId
              AND deleted_at IS NULL
              AND (debug_template_id IS NULL OR EXISTS (SELECT 1 FROM workflow_template t WHERE t.template_id = workflow.debug_template_id AND t.deleted_at IS NULL))
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new WorkflowScopeParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });

        return row?.ToDomain();
    }

    /// <summary>为后台列出 API 工作流及其最新发布信息。</summary>
    public async Task<IReadOnlyList<ManagedWorkflowApi>> ListApisAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<ManagedWorkflowApiRow>(
            """
            SELECT w.workflow_id, w.name, w.updated_at,
                   COALESCE(d.status, 'draft') AS status, d.version
            FROM workflow w
            LEFT JOIN LATERAL (
                SELECT status, version FROM workflow_deployment
                WHERE workflow_id = w.workflow_id ORDER BY version DESC LIMIT 1
            ) d ON true
            WHERE w.is_api AND w.deleted_at IS NULL
            ORDER BY w.updated_at DESC
            """);
        return rows.Select(row => new ManagedWorkflowApi(
            row.WorkflowId, row.Name, row.Status, row.Version,
            WorkflowPersistenceTime.ToOffset(row.UpdatedAt))).ToList();
    }

    /// <summary>按标识读取标记为 API 的后台工作流，不依赖普通用户设备查询入口。</summary>
    public async Task<Workflow?> FindApiAsync(WorkflowId workflowId, CancellationToken ct, bool includeDeleted = false)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRow>(
            """
            SELECT workflow_id, device_id, name, schema_version, is_api, copied_from_source, copied_from_id,
                   graph::text AS graph_json, created_at, updated_at
            FROM workflow WHERE workflow_id = @WorkflowId AND is_api AND (deleted_at IS NULL OR @IncludeDeleted)
            """, new WorkflowApiLookupParams { WorkflowId = workflowId.Value, IncludeDeleted = includeDeleted });
        return row?.ToDomain();
    }

    /// <summary>在事务中保存新工作流；API 草稿同时建立独立资源设备，不要求管理员绑定普通用户账号。</summary>
    public async Task InsertAsync(Workflow workflow, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var parameters = WorkflowWriteParams.From(workflow);
        if (workflow.IsApi)
        {
            // API 草稿使用独立资源设备，不要求管理员同时登录普通用户账号。
            await connection.ExecuteAsync(
                """
                INSERT INTO device (device_id, fp_exact, fp_coarse, ip_prefix,
                                    signals, sig_version, user_agent)
                VALUES (@DeviceId, @DeviceFingerprint, @DeviceFingerprint, '127.0.0.1',
                        '{"source":"workflow_api"}'::jsonb, 1, 'workflow-api')
                """, parameters, transaction);
        }
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow
                (workflow_id, device_id, name, schema_version, graph, is_api, created_at, updated_at, copied_from_source, copied_from_id)
            VALUES
                (@WorkflowId, @DeviceId, @Name, @SchemaVersion, @GraphJson::jsonb, @IsApi,
                 @CreatedAt, @UpdatedAt, @CopiedFromSource, @CopiedFromId)
            """, parameters, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>按模板与设备唯一关联创建或更新调试工作流，返回可直接运行的图快照。</summary>
    public async Task<Workflow> UpsertTemplateDebugAsync(
        WorkflowTemplateId templateId,
        DeviceId deviceId,
        string name,
        int schemaVersion,
        string graphJson,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        try
        {
            var row = await connection.QuerySingleAsync<WorkflowRow>(
                """
                INSERT INTO workflow
                    (workflow_id, device_id, name, schema_version, graph,
                     debug_template_id, created_at, updated_at)
                VALUES
                    (@WorkflowId, @DeviceId, @Name, @SchemaVersion, @GraphJson::jsonb,
                     @TemplateId, @Now, @Now)
                ON CONFLICT (debug_template_id, device_id)
                    WHERE debug_template_id IS NOT NULL
                DO UPDATE SET
                    name = EXCLUDED.name,
                    schema_version = EXCLUDED.schema_version,
                    graph = EXCLUDED.graph,
                    updated_at = EXCLUDED.updated_at
                RETURNING workflow_id, device_id, name, schema_version, is_api,
                          graph::text AS graph_json, created_at, updated_at
                """,
                new WorkflowTemplateDebugParams
                {
                    WorkflowId = WorkflowId.New().Value,
                    TemplateId = templateId.Value,
                    DeviceId = deviceId.Value,
                    Name = name,
                    SchemaVersion = schemaVersion,
                    GraphJson = graphJson,
                    Now = now,
                });

            return row.ToDomain();
        }
        catch (Npgsql.PostgresException exception) when (exception.ConstraintName == "workflow_deleted")
        {
            throw new CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception("模板已删除，不能再创建或更新调试工作流。");
        }
    }

    /// <summary>在当前可访问设备范围内更新工作流名称、版本和图快照。</summary>
    public async Task<bool> UpdateAsync(Workflow workflow, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        try
        {
            var affected = await connection.ExecuteAsync(
                """
                UPDATE workflow
                SET name = @Name,
                    schema_version = @SchemaVersion,
                    graph = @GraphJson::jsonb,
                    updated_at = @UpdatedAt
                WHERE workflow_id = @WorkflowId
                  AND deleted_at IS NULL
                  AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
                """,
                WorkflowWriteParams.From(workflow));

            return affected > 0;
        }
        catch (Npgsql.PostgresException exception) when (exception.ConstraintName == "workflow_deleted")
        {
            // 模板可能在请求读图后被删除；迟到保存按资源已消失处理，而不是返回 500。
            return false;
        }
    }

    /// <summary>删除当前可访问设备范围内的指定工作流，返回是否实际删除。</summary>
    public async Task<bool> DeleteAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM workflow
            WHERE workflow_id = @WorkflowId
              AND NOT is_api AND debug_template_id IS NULL AND deleted_at IS NULL
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new WorkflowScopeParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });

        return affected > 0;
    }
}

/// <summary>仅显式只读素材历史入口可以读取软删除 API 的原资源归属。</summary>
internal sealed class WorkflowApiLookupParams
{
    /// <summary>后台工作流标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>是否包含已删除资源；默认不包含。</summary>
    public bool IncludeDeleted { get; init; }
}

internal sealed class WorkflowDeviceParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class WorkflowScopeParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class WorkflowTemplateDebugParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>工作流模板的唯一标识。</summary>
    public Guid TemplateId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowWriteParams
{
    /// <summary>已校验的显式分叉来源。</summary>
    public string? CopiedFromSource { get; init; }
    /// <summary>分叉原对象标识，与新草稿在同一事务内保存。</summary>
    public Guid? CopiedFromId { get; init; }
    /// <summary>后台专用 API 草稿标记。</summary>
    public bool IsApi { get; init; }
    /// <summary>独立资源设备的随机标识摘要，不参与浏览器指纹识别。</summary>
    public byte[] DeviceFingerprint { get; init; } = [];

    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowWriteParams From(Workflow workflow) => new()
    {
        IsApi = workflow.IsApi,
        CopiedFromSource = workflow.CopiedFromSource,
        CopiedFromId = workflow.CopiedFromId,
        DeviceFingerprint = System.Security.Cryptography.SHA256.HashData(workflow.DeviceId.Value.ToByteArray()),
        WorkflowId = workflow.Id.Value,
        DeviceId = workflow.DeviceId.Value,
        Name = workflow.Name,
        SchemaVersion = workflow.SchemaVersion,
        GraphJson = workflow.GraphJson,
        CreatedAt = workflow.CreatedAt,
        UpdatedAt = workflow.UpdatedAt,
    };
}

internal sealed class WorkflowRow
{
    /// <summary>原始分叉来源，不跟随编辑改变。</summary>
    public string? CopiedFromSource { get; init; }
    /// <summary>原始分叉标识。</summary>
    public Guid? CopiedFromId { get; init; }
    /// <summary>后台专用 API 草稿标记。</summary>
    public bool IsApi { get; init; }

    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public Workflow ToDomain() => new(
        new WorkflowId(WorkflowId),
        new DeviceId(DeviceId),
        Name,
        SchemaVersion,
        GraphJson,
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(UpdatedAt)) { IsApi = IsApi, CopiedFromSource = CopiedFromSource, CopiedFromId = CopiedFromId };
}

internal sealed class WorkflowSummaryRow
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>节点图包含的节点数量。</summary>
    public int NodeCount { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>将持久记录投影为列表摘要。</summary>
    public WorkflowSummary ToSummary() => new(
        WorkflowId,
        Name,
        NodeCount,
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(UpdatedAt));
}

internal static class WorkflowPersistenceTime
{
    /// <summary>按既有 UTC 约定转换数据库时间值。</summary>
    public static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>按既有 UTC 约定转换数据库时间值。</summary>
    public static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } time ? ToOffset(time) : null;
}

internal sealed class ManagedWorkflowApiRow
{
    /// <summary>API 工作流标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>API 名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>最新发布状态。</summary>
    public string Status { get; init; } = "draft";
    /// <summary>最新发布版本。</summary>
    public int? Version { get; init; }
    /// <summary>草稿更新时间。</summary>
    public DateTime UpdatedAt { get; init; }
}
