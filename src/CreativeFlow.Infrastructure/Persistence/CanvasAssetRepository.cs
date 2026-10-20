using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 CanvasAssetRepository 的依赖与实例状态。</summary>
public sealed class CanvasAssetRepository(CreativeFlowConnectionFactory connections) : ICanvasAssetRepository
{
    /// <summary>保存图片或视频的素材元数据、存储键及上游来源信息，不在此处写入素材字节。</summary>
    public async Task InsertAsync(CanvasAsset asset, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO canvas_asset (asset_id, device_id, kind, media_type, storage_path,
                                      byte_size, prompt, created_at, group_id,
                                      source_url, source_url_expires_at, name, origin, sha256)
            VALUES (@AssetId, @DeviceId, @Kind, @MediaType, @StoragePath,
                    @ByteSize, @Prompt, @CreatedAt, @GroupId,
                    @SourceUrl, @SourceUrlExpiresAt, @Name, @Origin,
                    (SELECT sha256 FROM storage_object WHERE storage_path=@StoragePath AND state='ready'))
            """,
            new InsertAssetParams
            {
                AssetId = asset.AssetId,
                DeviceId = asset.DeviceId.Value,
                Kind = asset.Kind,
                MediaType = asset.MediaType,
                StoragePath = asset.StoragePath,
                ByteSize = asset.ByteSize,
                Prompt = asset.Prompt,
                CreatedAt = asset.CreatedAt,
                GroupId = asset.GroupId,
                SourceUrl = asset.SourceUrl,
                SourceUrlExpiresAt = asset.SourceUrlExpiresAt,
                Name = asset.Name,
                Origin = asset.Origin == "unknown" && asset.Prompt is not null ? "generated" : asset.Origin,
            });
    }

    /// <summary>在当前可访问设备范围内读取指定素材元数据，防止仅凭标识跨账号访问。</summary>
    public async Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetRow>(
            """
            SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                   created_at, group_id, source_url, source_url_expires_at, deleted_at
            FROM canvas_asset
            WHERE asset_id = @AssetId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND purged_at IS NULL AND NOT purge_pending
            """,
            new AssetScopeParams { AssetId = assetId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    /// <summary>仅当来源与派生素材均可访问时，按来源标识和派生方式查找缓存素材。</summary>
    public async Task<CanvasAsset?> FindDerivativeAsync(
        DeviceId deviceId,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetRow>(
            """
            SELECT derived.asset_id, derived.device_id, derived.kind, derived.media_type,
                   derived.storage_path, derived.byte_size, derived.prompt,
                   derived.created_at, derived.group_id,
                   derived.source_url, derived.source_url_expires_at
            FROM canvas_asset derived
            JOIN canvas_asset source ON source.asset_id = derived.source_asset_id
            WHERE source.asset_id = @SourceAssetId
              AND source.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND derived.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND derived.derivation = @Derivation
              AND derived.deleted_at IS NULL AND derived.purged_at IS NULL AND NOT derived.purge_pending
              AND source.deleted_at IS NULL AND source.purged_at IS NULL AND NOT source.purge_pending
            """,
            new AssetDerivativeScopeParams
            {
                SourceAssetId = sourceAssetId,
                DeviceId = deviceId.Value,
                Derivation = derivation,
            });

        return row?.ToDomain();
    }

    /// <summary>确认源素材可访问后尝试保存唯一派生素材，已存在相同派生结果时返回 false 供调用方复用。</summary>
    public async Task<bool> TryInsertDerivativeAsync(
        CanvasAsset asset,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            INSERT INTO canvas_asset (asset_id, device_id, kind, media_type, storage_path,
                                      byte_size, prompt, created_at, group_id,
                                      source_asset_id, derivation,
                                      source_url, source_url_expires_at, origin, sha256)
            SELECT @AssetId, @DeviceId, @Kind, @MediaType, @StoragePath,
                   @ByteSize, @Prompt, @CreatedAt, @GroupId,
                   @SourceAssetId, @Derivation,
                   @SourceUrl, @SourceUrlExpiresAt, 'derived',
                   (SELECT sha256 FROM storage_object WHERE storage_path=@StoragePath AND state='ready')
            WHERE EXISTS (
                SELECT 1
                FROM canvas_asset source
                WHERE source.asset_id = @SourceAssetId
                  AND source.deleted_at IS NULL AND source.purged_at IS NULL AND NOT source.purge_pending
                  AND source.device_id IN (
                      SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            )
            ON CONFLICT (source_asset_id, derivation)
                WHERE source_asset_id IS NOT NULL AND derivation IS NOT NULL
            DO NOTHING
            """,
            new InsertAssetDerivativeParams
            {
                AssetId = asset.AssetId,
                DeviceId = asset.DeviceId.Value,
                Kind = asset.Kind,
                MediaType = asset.MediaType,
                StoragePath = asset.StoragePath,
                ByteSize = asset.ByteSize,
                Prompt = asset.Prompt,
                CreatedAt = asset.CreatedAt,
                GroupId = asset.GroupId,
                SourceAssetId = sourceAssetId,
                Derivation = derivation,
                SourceUrl = asset.SourceUrl,
                SourceUrlExpiresAt = asset.SourceUrlExpiresAt,
            });

        return affected > 0;
    }

    /// <summary>按可选素材类型及数量上限列出可访问素材，最新结果优先返回。</summary>
    public async Task<IReadOnlyList<CanvasAsset>> ListAsync(
        DeviceId deviceId,
        string? kind,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 使用两条语句而不是带 `(@Kind IS NULL OR kind = @Kind)` 的单条语句：
        // 后一种写法会让规划器为两种形态选择同一计划，而 Dapper.AOT 更适合可静态绑定的字面谓词。
        var rows = kind is null
            ? await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at, group_id, source_url, source_url_expires_at
                FROM canvas_asset
                WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
                  AND deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new AssetListParams { DeviceId = deviceId.Value, Limit = limit })
            : await connection.QueryAsync<AssetRow>(
                """
                SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt,
                       created_at, group_id, source_url, source_url_expires_at
                FROM canvas_asset
                WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND kind = @Kind
                  AND deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new AssetListByKindParams
                {
                    DeviceId = deviceId.Value,
                    Kind = kind,
                    Limit = limit,
                });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>列出可访问的素材分组，并统计各组归属设备下的素材数量。</summary>
    public async Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var rows = await connection.QueryAsync<AssetGroupRow>(
            """
            SELECT g.group_id, g.device_id, g.name, g.created_at, g.updated_at,
                   COUNT(a.asset_id)::int AS asset_count
            FROM canvas_asset_group g
            LEFT JOIN canvas_asset a ON a.group_id = g.group_id AND a.device_id = g.device_id AND a.deleted_at IS NULL AND a.purged_at IS NULL AND NOT a.purge_pending
            WHERE g.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            GROUP BY g.group_id, g.device_id, g.name, g.created_at, g.updated_at
            ORDER BY g.created_at, g.group_id
            """,
            new AssetGroupDeviceParam { DeviceId = deviceId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>读取可访问分组及其素材数量，分组不存在或不可访问时返回空。</summary>
    public async Task<CanvasAssetGroup?> FindGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssetGroupRow>(
            """
            SELECT g.group_id, g.device_id, g.name, g.created_at, g.updated_at,
                   COUNT(a.asset_id)::int AS asset_count
            FROM canvas_asset_group g
            LEFT JOIN canvas_asset a ON a.group_id = g.group_id AND a.device_id = g.device_id AND a.deleted_at IS NULL AND a.purged_at IS NULL AND NOT a.purge_pending
            WHERE g.group_id = @GroupId AND g.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            GROUP BY g.group_id, g.device_id, g.name, g.created_at, g.updated_at
            """,
            new GroupScopeParams { GroupId = groupId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    /// <summary>创建素材分组并返回新记录，不区分大小写的名称冲突时返回空。</summary>
    public async Task<CanvasAssetGroup?> InsertGroupAsync(
        CanvasAssetGroup group,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 唯一索引不区分大小写。返回数据行使端点无需第二次查询，
        // 即可将名称冲突转换为有意义的 409 响应。
        var row = await connection.QueryFirstOrDefaultAsync<AssetGroupRow>(
            """
            INSERT INTO canvas_asset_group (group_id, device_id, name, created_at, updated_at)
            VALUES (@GroupId, @DeviceId, @Name, @CreatedAt, @UpdatedAt)
            ON CONFLICT DO NOTHING
            RETURNING group_id, device_id, name, created_at, updated_at, 0::int AS asset_count
            """,
            new InsertGroupParams
            {
                GroupId = group.GroupId,
                DeviceId = group.DeviceId.Value,
                Name = group.Name,
                CreatedAt = group.CreatedAt,
                UpdatedAt = group.UpdatedAt,
            });

        return row?.ToDomain();
    }

    /// <summary>仅在可访问且新名称未占用时重命名分组，并返回更新后包含素材数量的记录。</summary>
    public async Task<CanvasAssetGroup?> RenameGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        string name,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // NOT EXISTS 谓词使重复名称返回正常空结果而非数据库异常，
        // 第二条查询则保留当前素材数量。
        var changed = await connection.ExecuteAsync(
            """
            UPDATE canvas_asset_group AS g
            SET name = @Name, updated_at = @UpdatedAt
            WHERE g.group_id = @GroupId AND g.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND NOT EXISTS (
                  SELECT 1
                  FROM canvas_asset_group other
                  WHERE other.device_id = g.device_id
                    AND other.group_id <> g.group_id
                    AND lower(other.name) = lower(@Name)
              )
            """,
            new RenameGroupParams
            {
                GroupId = groupId,
                DeviceId = deviceId.Value,
                Name = name,
                UpdatedAt = updatedAt,
            });

        return changed == 0 ? null : await FindGroupAsync(deviceId, groupId, ct);
    }

    /// <summary>删除可访问的素材分组，组内素材由外键规则转为未分组而不删除素材。</summary>
    public async Task<bool> DeleteGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM canvas_asset_group
            WHERE group_id = @GroupId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new GroupScopeParams { GroupId = groupId, DeviceId = deviceId.Value });

        return affected > 0;
    }

    /// <summary>批量设置可访问素材的分组或清空分组关联，返回实际更新数量。</summary>
    public async Task<int> AssignGroupAsync(
        DeviceId deviceId,
        IReadOnlyList<Guid> assetIds,
        Guid? groupId,
        CancellationToken ct)
    {
        if (assetIds.Count == 0) return 0;

        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteAsync(
            """
            UPDATE canvas_asset
            SET group_id = @GroupId,management_version=management_version+1,updated_at=now()
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND asset_id = ANY(@AssetIds)
              AND deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending
            """,
            new AssignGroupParams
            {
                DeviceId = deviceId.Value,
                AssetIds = [.. assetIds],
                GroupId = groupId,
            });
    }
}

internal sealed class AssetListParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal sealed class AssetListByKindParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>当前资源或数据项的种类。</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal class InsertAssetParams
{
    /// <summary>只用于资产列表的展示名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>服务端确认的素材来源类型。</summary>
    public string Origin { get; init; } = "unknown";
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>当前资源或数据项的种类。</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>内容的 MIME 类型，响应时沿用已验证的存储值。</summary>
    public string MediaType { get; init; } = string.Empty;
    /// <summary>存储后端的不透明键，不能直接当作用户可控文件路径。</summary>
    public string StoragePath { get; init; } = string.Empty;
    /// <summary>素材或文件内容的字节数。</summary>
    public long ByteSize { get; init; }
    /// <summary>提交给生成模型的用户提示词。</summary>
    public string? Prompt { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid? GroupId { get; init; }
    /// <summary>上游返回的原始素材地址，保留供后续来源选择使用。</summary>
    public string? SourceUrl { get; init; }
    /// <summary>上游原始素材地址的已知失效时间。</summary>
    public DateTimeOffset? SourceUrlExpiresAt { get; init; }
}

internal sealed class AssetScopeParams
{
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class AssetDerivativeScopeParams
{
    /// <summary>派生素材关联的原始素材标识。</summary>
    public Guid SourceAssetId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>素材派生操作的稳定名称，例如视频尾帧。</summary>
    public string Derivation { get; init; } = string.Empty;
}

internal sealed class InsertAssetDerivativeParams : InsertAssetParams
{
    /// <summary>派生素材关联的原始素材标识。</summary>
    public Guid SourceAssetId { get; init; }
    /// <summary>素材派生操作的稳定名称，例如视频尾帧。</summary>
    public string Derivation { get; init; } = string.Empty;
}

internal sealed class AssetRow
{
    /// <summary>回收时间；原历史内容仍可通过授权读取。</summary>
    public DateTime? DeletedAt { get; init; }
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>当前资源或数据项的种类。</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>内容的 MIME 类型，响应时沿用已验证的存储值。</summary>
    public string MediaType { get; init; } = string.Empty;
    /// <summary>存储后端的不透明键，不能直接当作用户可控文件路径。</summary>
    public string StoragePath { get; init; } = string.Empty;
    /// <summary>素材或文件内容的字节数。</summary>
    public long ByteSize { get; init; }
    /// <summary>提交给生成模型的用户提示词。</summary>
    public string? Prompt { get; init; }
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid? GroupId { get; init; }
    /// <summary>上游返回的原始素材地址，保留供后续来源选择使用。</summary>
    public string? SourceUrl { get; init; }

    // timestamptz 读取为 DateTime，参见 ProviderRow 上的说明。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>上游原始素材地址的已知失效时间。</summary>
    public DateTime? SourceUrlExpiresAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public CanvasAsset ToDomain() => new(
        AssetId,
        new DeviceId(DeviceId),
        Kind,
        MediaType,
        StoragePath,
        ByteSize,
        Prompt,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        GroupId,
        SourceUrl,
        SourceUrlExpiresAt is { } expires
            ? new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc))
            : null) { IsRecycled = DeletedAt is not null };
}

internal sealed class AssetGroupDeviceParam
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class GroupScopeParams
{
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid GroupId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class InsertGroupParams
{
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid GroupId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class RenameGroupParams
{
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid GroupId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class AssignGroupParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>参与当前操作的素材标识集合。</summary>
    public Guid[] AssetIds { get; init; } = [];
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid? GroupId { get; init; }
}

internal sealed class AssetGroupRow
{
    /// <summary>素材分组标识；允许空值的场景表示未分组。</summary>
    public Guid GroupId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>该分组或结果中的素材数量。</summary>
    public int AssetCount { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public CanvasAssetGroup ToDomain() => new(
        GroupId,
        new DeviceId(DeviceId),
        Name,
        AssetCount,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
