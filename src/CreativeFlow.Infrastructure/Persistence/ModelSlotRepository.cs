using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 ModelSlotRepository 的依赖与实例状态。</summary>
public sealed class ModelSlotRepository(NpgsqlDataSource dataSource) : IModelSlotRepository
{
    /// <summary>按图片、视频、聊天槽位顺序读取后台托管模型绑定及更新时间。</summary>
    public async Task<IReadOnlyList<ConfiguredModelSlot>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ModelSlotRow>(
            """
            SELECT slot_kind, provider_id, model_id, updated_at
            FROM ai_model_slot
            ORDER BY CASE slot_kind
                WHEN 'image' THEN 0
                WHEN 'video' THEN 1
                WHEN 'chat' THEN 2
                ELSE 3
            END
            """);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>插入或更新指定模型槽位的渠道、模型和修改时间；空标识用于清除绑定。</summary>
    public async Task SetAsync(
        string kind,
        ProviderId? providerId,
        ModelId? modelId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO ai_model_slot (slot_kind, provider_id, model_id, updated_at)
            VALUES (@Kind, @ProviderId, @ModelId, @UpdatedAt)
            ON CONFLICT (slot_kind) DO UPDATE
            SET provider_id = EXCLUDED.provider_id,
                model_id = EXCLUDED.model_id,
                updated_at = EXCLUDED.updated_at
            """,
            new ModelSlotWriteArgs
            {
                Kind = kind,
                ProviderId = providerId?.Value,
                ModelId = modelId?.Value,
                UpdatedAt = now,
            });
    }
}

internal sealed class ModelSlotRow
{
    /// <summary>管理员配置的默认模型角色类别。</summary>
    public string SlotKind { get; init; } = string.Empty;
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>本地模型配置标识。</summary>
    public Guid? ModelId { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public ConfiguredModelSlot ToDomain() => new(
        SlotKind,
        ProviderId is { } provider ? new ProviderId(provider) : null,
        ModelId is { } model ? new ModelId(model) : null,
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}

internal sealed class ModelSlotWriteArgs
{
    /// <summary>当前资源或数据项的种类。</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>本地模型配置标识。</summary>
    public Guid? ModelId { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
