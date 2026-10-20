using Dapper;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 WorkflowExternalApiRepository 的依赖与实例状态。</summary>
public sealed class WorkflowExternalApiRepository(CreativeFlowConnectionFactory connections)
    : IWorkflowExternalApiRepository
{
    /// <summary>查询工作流发布快照时使用的固定列清单。</summary>
    private const string DeploymentColumns = """
        deployment_id, workflow_id, version, schema_version,
        graph_snapshot::text AS graph_json,
        input_schema::text AS input_schema_json,
        output_schema::text AS output_schema_json,
        execution_snapshot::text AS execution_snapshot_json,
        description, status, max_concurrency, created_at, published_at, disabled_at
        """;

    /// <summary>查询 API Key 时使用的固定数据库列清单。</summary>
    private const string ApiKeyColumns = """
        api_key_id, workflow_id, name, key_prefix, key_hint, key_hash, enabled,
        expires_at, last_used_at, created_at, revoked_at
        """;

    /// <summary>在可访问工作流范围内串行分配发布版本，事务中停用旧发布并保存新部署快照，排除模板调试图。</summary>
    public async Task<WorkflowDeployment?> PublishAsync(
        DeviceId deviceId,
        WorkflowDeployment deployment,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var owned = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM workflow
                WHERE workflow_id = @WorkflowId
                  AND deleted_at IS NULL
                  AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
                  AND debug_template_id IS NULL
            )
            """,
            new DeploymentOwnerParams
            {
                WorkflowId = deployment.WorkflowId.Value,
                DeviceId = deviceId.Value,
            },
            transaction);
        if (!owned)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        // Serialize publishers for this workflow so two clicks cannot allocate the same version.
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@WorkflowId::text, 0))",
            new WorkflowOnlyParams { WorkflowId = deployment.WorkflowId.Value },
            transaction);

        var version = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(version), 0) + 1
            FROM workflow_deployment
            WHERE workflow_id = @WorkflowId
            """,
            new WorkflowOnlyParams { WorkflowId = deployment.WorkflowId.Value },
            transaction);

        await connection.ExecuteAsync(
            """
            UPDATE workflow_deployment
            SET status = 'disabled', disabled_at = @Now
            WHERE workflow_id = @WorkflowId AND status = 'published'
            """,
            new DisableDeploymentParams
            {
                WorkflowId = deployment.WorkflowId.Value,
                Now = deployment.PublishedAt,
            },
            transaction);

        var published = deployment with { Version = version };
        try
        {
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_deployment
                (deployment_id, workflow_id, version, schema_version, graph_snapshot,
                 input_schema, output_schema, description, status, max_concurrency,
                 created_at, published_at, disabled_at, execution_snapshot)
            VALUES
                (@DeploymentId, @WorkflowId, @Version, @SchemaVersion, @GraphJson::jsonb,
                 @InputSchemaJson::jsonb, @OutputSchemaJson::jsonb, @Description,
                 @Status, @MaxConcurrency, @CreatedAt, @PublishedAt, @DisabledAt, @ExecutionSnapshotJson::jsonb)
            """,
            WorkflowDeploymentWriteParams.From(published),
            transaction);
        }
        catch (Npgsql.PostgresException exception) when (exception.ConstraintName == "workflow_deleted")
        {
            await transaction.RollbackAsync(ct);
            return null;
        }
        catch (Npgsql.PostgresException exception) when (exception.ConstraintName == "knowledge_version_unavailable")
        {
            await transaction.RollbackAsync(ct);
            throw new CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception("知识版本在发布期间已不可用，请重新确认资源配置。");
        }

        await transaction.CommitAsync(ct);
        return published;
    }

    /// <summary>在可访问工作流范围内读取全部部署版本及状态。</summary>
    public async Task<IReadOnlyList<WorkflowDeployment>> ListDeploymentsAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowDeploymentRow>(
            $"""
            SELECT {DeploymentColumns}
            FROM workflow_deployment deployment
            WHERE workflow_id = @WorkflowId
              AND EXISTS (
                  SELECT 1 FROM workflow
                  WHERE workflow.workflow_id = deployment.workflow_id
                    AND workflow.device_id IN (
                        SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)))
            ORDER BY version DESC
            """,
            new DeploymentOwnerParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在可访问工作流范围内读取当前发布的部署快照。</summary>
    public async Task<WorkflowDeployment?> FindPublishedAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowDeploymentRow>(
            $"""
            SELECT {DeploymentColumns}
            FROM workflow_deployment deployment
            WHERE workflow_id = @WorkflowId AND status = 'published'
              AND EXISTS (
                  SELECT 1 FROM workflow
                  WHERE workflow.workflow_id = deployment.workflow_id
                    AND workflow.device_id IN (
                        SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)))
            """,
            new DeploymentOwnerParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });
        return row?.ToDomain();
    }

    /// <summary>供外部调用按工作流标识读取已发布目录项，不暴露模板调试工作流。</summary>
    public async Task<PublishedWorkflowCatalogItem?> FindPublishedAsync(
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<PublishedWorkflowRow>(
            """
            SELECT deployment.deployment_id, deployment.workflow_id, deployment.version,
                   deployment.schema_version,
                   deployment.graph_snapshot::text AS graph_json,
                   deployment.input_schema::text AS input_schema_json,
                   deployment.output_schema::text AS output_schema_json,
                   deployment.execution_snapshot::text AS execution_snapshot_json,
                   deployment.description, deployment.status, deployment.max_concurrency,
                   deployment.created_at, deployment.published_at, deployment.disabled_at,
                   workflow.name AS workflow_name, workflow.device_id AS owner_device_id
            FROM workflow_deployment deployment
            JOIN workflow ON workflow.workflow_id = deployment.workflow_id
            WHERE deployment.workflow_id = @WorkflowId AND deployment.status = 'published'
              AND workflow.deleted_at IS NULL
              AND workflow.debug_template_id IS NULL
            """,
            new WorkflowOnlyParams { WorkflowId = workflowId.Value });
        return row?.ToDomain();
    }

    /// <summary>按部署标识读取对应版本及工作流目录信息，用于引用历史发布快照。</summary>
    public async Task<PublishedWorkflowCatalogItem?> FindDeploymentAsync(
        WorkflowDeploymentId deploymentId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<PublishedWorkflowRow>(
            """
            SELECT deployment.deployment_id, deployment.workflow_id, deployment.version,
                   deployment.schema_version,
                   deployment.graph_snapshot::text AS graph_json,
                   deployment.input_schema::text AS input_schema_json,
                   deployment.output_schema::text AS output_schema_json,
                   deployment.execution_snapshot::text AS execution_snapshot_json,
                   deployment.description, deployment.status, deployment.max_concurrency,
                   deployment.created_at, deployment.published_at, deployment.disabled_at,
                   workflow.name AS workflow_name, workflow.device_id AS owner_device_id
            FROM workflow_deployment deployment
            JOIN workflow ON workflow.workflow_id = deployment.workflow_id
            WHERE deployment.deployment_id = @DeploymentId
            """,
            new DeploymentIdParams { DeploymentId = deploymentId.Value });
        return row?.ToDomain();
    }

    /// <summary>按数量上限列出当前发布的工作流目录，排除模板调试图。</summary>
    public async Task<IReadOnlyList<PublishedWorkflowCatalogItem>> ListPublishedAsync(
        int limit,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<PublishedWorkflowRow>(
            """
            SELECT deployment.deployment_id, deployment.workflow_id, deployment.version,
                   deployment.schema_version,
                   deployment.graph_snapshot::text AS graph_json,
                   deployment.input_schema::text AS input_schema_json,
                   deployment.output_schema::text AS output_schema_json,
                   deployment.execution_snapshot::text AS execution_snapshot_json,
                   deployment.description, deployment.status, deployment.max_concurrency,
                   deployment.created_at, deployment.published_at, deployment.disabled_at,
                   workflow.name AS workflow_name, workflow.device_id AS owner_device_id
            FROM workflow_deployment deployment
            JOIN workflow ON workflow.workflow_id = deployment.workflow_id
            WHERE deployment.status = 'published' AND workflow.debug_template_id IS NULL AND workflow.deleted_at IS NULL
            ORDER BY deployment.published_at DESC
            LIMIT @Limit
            """,
            new PublishedWorkflowListParams { Limit = limit });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在可访问工作流范围内停用当前发布版本并记录停用时间。</summary>
    public async Task<bool> DisableAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_deployment deployment
            SET status = 'disabled', disabled_at = @Now
            WHERE workflow_id = @WorkflowId AND status = 'published'
              AND EXISTS (
                  SELECT 1 FROM workflow
                  WHERE workflow.workflow_id = deployment.workflow_id
                    AND workflow.device_id IN (
                        SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)))
            """,
            new DisableOwnedDeploymentParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
                Now = now,
            });
        return affected > 0;
    }

    /// <summary>保存外部工作流 API Key 的归属、查询前缀、哈希和权限配置，不保存 Key 明文。</summary>
    public async Task InsertApiKeyAsync(WorkflowApiKey apiKey, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_api_key
                (api_key_id, workflow_id, name, key_prefix, key_hint, key_hash, enabled,
                 expires_at, last_used_at, created_at, revoked_at)
            VALUES
                (@ApiKeyId, @WorkflowId, @Name, @KeyPrefix, @KeyHint, @KeyHash, @Enabled,
                 @ExpiresAt, @LastUsedAt, @CreatedAt, @RevokedAt)
            """,
            WorkflowApiKeyWriteParams.From(apiKey));
    }

    /// <summary>在可访问工作流范围内列出 API Key 记录及使用状态。</summary>
    public async Task<IReadOnlyList<WorkflowApiKey>> ListApiKeysAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowApiKeyRow>(
            $"""
            SELECT {ApiKeyColumns}
            FROM workflow_api_key api_key
            WHERE workflow_id = @WorkflowId
              AND EXISTS (
                  SELECT 1 FROM workflow
                  WHERE workflow.workflow_id = api_key.workflow_id
                    AND workflow.device_id IN (
                        SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)))
            ORDER BY created_at DESC
            """,
            new DeploymentOwnerParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在可访问工作流范围内禁用指定 API Key，保留首次撤销时间以支持重复撤销。</summary>
    public async Task<bool> RevokeApiKeyAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowApiKeyId apiKeyId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_api_key api_key
            SET enabled = false, revoked_at = COALESCE(revoked_at, @Now)
            WHERE api_key_id = @ApiKeyId AND workflow_id = @WorkflowId
              AND EXISTS (
                  SELECT 1 FROM workflow
                  WHERE workflow.workflow_id = api_key.workflow_id
                    AND workflow.device_id IN (
                        SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)))
            """,
            new RevokeApiKeyParams
            {
                ApiKeyId = apiKeyId.Value,
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
                Now = now,
            });
        return affected > 0;
    }

    /// <summary>根据查询前缀读取 API Key 候选记录，哈希校验与可用状态判断由调用方继续完成。</summary>
    public async Task<WorkflowApiKey?> FindApiKeyByPrefixAsync(
        string prefix,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowApiKeyRow>(
            $"""
            SELECT {ApiKeyColumns}
            FROM workflow_api_key
            WHERE key_prefix = @Prefix
            """,
            new ApiKeyPrefixParams { Prefix = prefix });
        return row?.ToDomain();
    }

    /// <summary>按服务端运行绑定的 Key 标识读取当前许可记录，供后台执行复验。</summary>
    public async Task<WorkflowApiKey?> FindApiKeyAsync(WorkflowApiKeyId id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowApiKeyRow>(
            $"SELECT {ApiKeyColumns} FROM workflow_api_key WHERE api_key_id = @ApiKeyId",
            new ApiKeyIdParams { ApiKeyId = id.Value });
        return row?.ToDomain();
    }

    /// <summary>记录指定 API Key 最近一次使用时间。</summary>
    public async Task TouchApiKeyAsync(
        WorkflowApiKeyId apiKeyId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE workflow_api_key SET last_used_at = @Now WHERE api_key_id = @ApiKeyId",
            new TouchApiKeyParams { ApiKeyId = apiKeyId.Value, Now = now });
    }

    /// <summary>保存外部上传素材与工作流、原素材及有效期的关联记录。</summary>
    public async Task InsertExternalAssetAsync(
        WorkflowExternalAsset asset,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_external_asset
                (asset_id, workflow_id, api_key_id, created_at, expires_at, consumed_at)
            VALUES
                (@AssetId, @WorkflowId, @ApiKeyId, @CreatedAt, @ExpiresAt, @ConsumedAt)
            """,
            WorkflowExternalAssetWriteParams.From(asset));
    }

    /// <summary>在指定工作流范围内查找未过期或已消费的外部素材，保持已受理任务对素材的引用。</summary>
    public async Task<WorkflowExternalAsset?> FindExternalAssetAsync(
        WorkflowId workflowId,
        Guid assetId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowExternalAssetRow>(
            """
            SELECT asset_id, workflow_id, api_key_id, created_at, expires_at, consumed_at
            FROM workflow_external_asset
            WHERE workflow_id = @WorkflowId AND asset_id = @AssetId
              AND (expires_at IS NULL OR expires_at > now() OR consumed_at IS NOT NULL)
            """,
            new ExternalAssetScopeParams
            {
                WorkflowId = workflowId.Value,
                AssetId = assetId,
            });
        return row?.ToDomain();
    }

    /// <summary>为指定工作流的素材记录首次消费时间，重复消费不覆盖原时间。</summary>
    public async Task MarkExternalAssetsConsumedAsync(
        WorkflowId workflowId,
        IReadOnlyList<Guid> assetIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (assetIds.Count == 0) return;
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE workflow_external_asset
            SET consumed_at = COALESCE(consumed_at, @Now)
            WHERE workflow_id = @WorkflowId AND asset_id = ANY(@AssetIds)
            """,
            new ConsumeExternalAssetsParams
            {
                WorkflowId = workflowId.Value,
                AssetIds = [.. assetIds],
                Now = now,
            });
    }
}

internal class WorkflowDeploymentRow
{
    /// <summary>v2 模型及资源固定引用的 JSON，不包含凭据。</summary>
    public string? ExecutionSnapshotJson { get; init; }
    /// <summary>工作流发布版本的唯一标识。</summary>
    public Guid DeploymentId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>对外输入契约的 JSON schema。</summary>
    public string InputSchemaJson { get; init; } = "[]";
    /// <summary>对外输出契约的 JSON schema。</summary>
    public string OutputSchemaJson { get; init; } = "[]";
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "disabled";
    /// <summary>当前工作流允许同时执行的运行数量上限。</summary>
    public int MaxConcurrency { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>模板或工作流版本发布时间。</summary>
    public DateTime PublishedAt { get; init; }
    /// <summary>账号或资源被停用的时间。</summary>
    public DateTime? DisabledAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowDeployment ToDomain() => new(
        new WorkflowDeploymentId(DeploymentId),
        new WorkflowId(WorkflowId),
        Version,
        SchemaVersion,
        GraphJson,
        InputSchemaJson,
        OutputSchemaJson,
        Description,
        Status == "published" ? WorkflowDeploymentStatus.Published : WorkflowDeploymentStatus.Disabled,
        MaxConcurrency,
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(PublishedAt),
        WorkflowPersistenceTime.ToOffset(DisabledAt)) { ExecutionSnapshotJson = ExecutionSnapshotJson };
}

internal sealed class PublishedWorkflowRow : WorkflowDeploymentRow
{
    /// <summary>工作流的显示名称。</summary>
    public string WorkflowName { get; init; } = string.Empty;
    /// <summary>资源实际归属的设备标识，用于恢复业务访问范围。</summary>
    public Guid OwnerDeviceId { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public new PublishedWorkflowCatalogItem ToDomain() => new(
        base.ToDomain(), WorkflowName, new DeviceId(OwnerDeviceId));
}

internal sealed class WorkflowDeploymentWriteParams
{
    /// <summary>发布时固定的 v2 执行配置；旧版本保持空值。</summary>
    public string? ExecutionSnapshotJson { get; init; }
    /// <summary>工作流发布版本的唯一标识。</summary>
    public Guid DeploymentId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>对外输入契约的 JSON schema。</summary>
    public string InputSchemaJson { get; init; } = "[]";
    /// <summary>对外输出契约的 JSON schema。</summary>
    public string OutputSchemaJson { get; init; } = "[]";
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "published";
    /// <summary>当前工作流允许同时执行的运行数量上限。</summary>
    public int MaxConcurrency { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>模板或工作流版本发布时间。</summary>
    public DateTimeOffset PublishedAt { get; init; }
    /// <summary>账号或资源被停用的时间。</summary>
    public DateTimeOffset? DisabledAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowDeploymentWriteParams From(WorkflowDeployment value) => new()
    {
        ExecutionSnapshotJson = value.ExecutionSnapshotJson,
        DeploymentId = value.Id.Value,
        WorkflowId = value.WorkflowId.Value,
        Version = value.Version,
        SchemaVersion = value.SchemaVersion,
        GraphJson = value.GraphJson,
        InputSchemaJson = value.InputSchemaJson,
        OutputSchemaJson = value.OutputSchemaJson,
        Description = value.Description,
        Status = value.Status == WorkflowDeploymentStatus.Published ? "published" : "disabled",
        MaxConcurrency = value.MaxConcurrency,
        CreatedAt = value.CreatedAt,
        PublishedAt = value.PublishedAt,
        DisabledAt = value.DisabledAt,
    };
}

internal sealed class WorkflowApiKeyRow
{
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>用于定位 API Key 记录的公开前缀。</summary>
    public string KeyPrefix { get; init; } = string.Empty;
    /// <summary>对外展示的 API Key 掩码。</summary>
    public string KeyHint { get; init; } = string.Empty;
    /// <summary>工作流 API Key 的验证摘要，避免存储明文。</summary>
    public byte[] KeyHash { get; init; } = [];
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTime? ExpiresAt { get; init; }
    /// <summary>API Key 最近一次通过认证并被使用的时间。</summary>
    public DateTime? LastUsedAt { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>会话或 API Key 被撤销的时间。</summary>
    public DateTime? RevokedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowApiKey ToDomain() => new(
        new WorkflowApiKeyId(ApiKeyId),
        new WorkflowId(WorkflowId),
        Name,
        KeyPrefix,
        KeyHint,
        KeyHash,
        Enabled,
        WorkflowPersistenceTime.ToOffset(ExpiresAt),
        WorkflowPersistenceTime.ToOffset(LastUsedAt),
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(RevokedAt));
}

internal sealed class WorkflowApiKeyWriteParams
{
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>用于定位 API Key 记录的公开前缀。</summary>
    public string KeyPrefix { get; init; } = string.Empty;
    /// <summary>对外展示的 API Key 掩码。</summary>
    public string KeyHint { get; init; } = string.Empty;
    /// <summary>工作流 API Key 的验证摘要，避免存储明文。</summary>
    public byte[] KeyHash { get; init; } = [];
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>API Key 最近一次通过认证并被使用的时间。</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>会话或 API Key 被撤销的时间。</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowApiKeyWriteParams From(WorkflowApiKey value) => new()
    {
        ApiKeyId = value.Id.Value,
        WorkflowId = value.WorkflowId.Value,
        Name = value.Name,
        KeyPrefix = value.KeyPrefix,
        KeyHint = value.KeyHint,
        KeyHash = value.KeyHash,
        Enabled = value.Enabled,
        ExpiresAt = value.ExpiresAt,
        LastUsedAt = value.LastUsedAt,
        CreatedAt = value.CreatedAt,
        RevokedAt = value.RevokedAt,
    };
}

internal sealed class WorkflowExternalAssetRow
{
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTime? ExpiresAt { get; init; }
    /// <summary>一次性凭据或外部素材被消费的时间。</summary>
    public DateTime? ConsumedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowExternalAsset ToDomain() => new(
        AssetId,
        new WorkflowId(WorkflowId),
        new WorkflowApiKeyId(ApiKeyId),
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(ExpiresAt),
        WorkflowPersistenceTime.ToOffset(ConsumedAt));
}

internal sealed class WorkflowExternalAssetWriteParams
{
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>一次性凭据或外部素材被消费的时间。</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowExternalAssetWriteParams From(WorkflowExternalAsset value) => new()
    {
        AssetId = value.AssetId,
        WorkflowId = value.WorkflowId.Value,
        ApiKeyId = value.ApiKeyId.Value,
        CreatedAt = value.CreatedAt,
        ExpiresAt = value.ExpiresAt,
        ConsumedAt = value.ConsumedAt,
    };
}

internal sealed class DeploymentOwnerParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class WorkflowOnlyParams {
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; } }
internal sealed class PublishedWorkflowListParams {
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; } }
internal sealed class DeploymentIdParams {
    /// <summary>工作流发布版本的唯一标识。</summary>
    public Guid DeploymentId { get; init; } }
internal sealed class DisableDeploymentParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class DisableOwnedDeploymentParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class RevokeApiKeyParams
{
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class ApiKeyPrefixParams {
    /// <summary>用于命名空间隔离、查找或验证的固定前缀。</summary>
    public string Prefix { get; init; } = string.Empty; }
/// <summary>固定 Key 标识查询参数，不接受明文。</summary>
internal sealed class ApiKeyIdParams
{
    /// <summary>数据库中的 Key 记录标识。</summary>
    public Guid ApiKeyId { get; init; }
}
internal sealed class TouchApiKeyParams
{
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class ExternalAssetScopeParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
}
internal sealed class ConsumeExternalAssetsParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>参与当前操作的素材标识集合。</summary>
    public Guid[] AssetIds { get; init; } = [];
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
