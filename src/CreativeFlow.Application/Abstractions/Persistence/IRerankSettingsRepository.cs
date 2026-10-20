using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>只追加非敏感重排序配置版本，不复制渠道凭据。</summary>
public interface IRerankSettingsRepository
{
    /// <summary>读取指定模型最新参数版本，不覆盖历史记录。</summary>
    Task<RerankSettingsResponse?> FindAsync(ModelId id, CancellationToken ct);
    /// <summary>追加独立配置版本并返回收据，不覆盖旧记录或调用模型。</summary>
    Task<RerankSettingsResponse> SaveAsync(ModelId id, RerankSettings settings, CancellationToken ct);
}
