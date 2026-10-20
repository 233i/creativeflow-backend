using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IModelSlotRepository
{
    /// <summary>列出默认模型角色，保留现有筛选、排序与访问范围。</summary>
    Task<IReadOnlyList<ConfiguredModelSlot>> ListAsync(CancellationToken ct);

    /// <summary>保存管理员选择的默认模型角色配置。</summary>
    Task SetAsync(
        string kind,
        ProviderId? providerId,
        ModelId? modelId,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelId">本地模型配置标识。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record ConfiguredModelSlot(
    string Kind,
    ProviderId? ProviderId,
    ModelId? ModelId,
    DateTimeOffset UpdatedAt);
