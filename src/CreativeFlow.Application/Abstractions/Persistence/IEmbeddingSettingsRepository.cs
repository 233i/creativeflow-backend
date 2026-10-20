using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>保存向量参数的不可变版本，不存储第二份模型凭据。</summary>
public interface IEmbeddingSettingsRepository
{
    /// <summary>读取该模型最近保存的配置；尚未保存时返回空。</summary>
    Task<EmbeddingSettingsResponse?> FindAsync(ModelId modelId, CancellationToken ct);
    /// <summary>追加版本并返回其标识；不能原地修改先前版本。</summary>
    Task<EmbeddingSettingsResponse> SaveAsync(ModelId modelId, EmbeddingSettings settings, CancellationToken ct);
}
