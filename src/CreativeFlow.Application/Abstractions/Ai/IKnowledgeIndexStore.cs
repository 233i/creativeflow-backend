using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>只处理固定库及索引版本的向量存储；不允许调用者指定集合名称或网络地址。</summary>
public interface IKnowledgeIndexStore
{
    /// <summary>表示部署者已启用独立、受限的索引端点。</summary>
    bool Available { get; }
    /// <summary>建立或验证固定维度的候选集合及必需过滤索引。</summary>
    Task PrepareAsync(Guid library, Guid version, int dimensions, CancellationToken ct);
    /// <summary>使用稳定片段 ID 幂等写入；失败可重写，不需要重新向量化。</summary>
    Task WriteAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, CancellationToken ct);
    /// <summary>验证指定资源过滤下的精确条数。</summary>
    Task<int> CountAsync(Guid library, Guid version, CancellationToken ct);
    /// <summary>返回有界 ID/分数，正文从权威数据库复验后读取。</summary>
    Task<KnowledgeIndexHit[]> SearchAsync(Guid library, Guid version, float[] query, int topK, CancellationToken ct);
    /// <summary>只删除已获准清理的固定版本集合；不存在视为已完成，不删除命名空间或其他集合。</summary>
    Task DeleteAsync(Guid library, Guid version, CancellationToken ct);
    /// <summary>创建固定命名 dense/bm25 向量和精确标识字段，禁止覆盖旧布局。</summary>
    Task PrepareHybridAsync(Guid library, Guid version, int dimensions, CancellationToken ct);
    /// <summary>只计入同时拥有 dense 与 bm25 向量并属于指定库和版本的点。</summary>
    Task<int> CountHybridAsync(Guid library, Guid version, CancellationToken ct);
    /// <summary>幂等写入同一切片的向量、关键词表示和完整标识；正文不作为权威 Payload 保存。</summary>
    Task WriteHybridAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct);
    /// <summary>混合布局的语义召回，所有强制标识与资源过滤都在检索前生效。</summary>
    Task<KnowledgeIndexHit[]> SearchDenseAsync(Guid library, Guid version, float[] query, int limit, string[] requiredTerms, CancellationToken ct);
    /// <summary>以与入库完全相同的 BM25 分词策略召回关键词候选。</summary>
    Task<KnowledgeIndexHit[]> SearchKeywordsAsync(Guid library, Guid version, string query, int limit, string[] requiredTerms, CancellationToken ct);
    /// <summary>精确标识倒排召回；requireAll 时全部标识必须匹配。</summary>
    Task<KnowledgeIndexHit[]> SearchExactAsync(Guid library, Guid version, string[] terms, int limit, bool requireAll, CancellationToken ct);
}

/// <summary>向量数据库只能返回候选标识与有限分数。</summary>
/// <param name="Id">稳定片段 ID。</param>
/// <param name="Score">相似度分数。</param>
public sealed record KnowledgeIndexHit(Guid Id, double Score);

/// <summary>知识库复用已有模型目录和向量分派器，不创建第二份凭据。</summary>
public interface IKnowledgeEmbedding
{
    /// <summary>捕获非敏感配置版本，要求已保存且已明确维度。</summary>
    Task<string> CaptureAsync(Guid modelId, CancellationToken ct);
    /// <summary>重新核验当前模型配置与索引绑定完全一致，然后执行单批次。</summary>
    Task<EmbeddingResult> EmbedAsync(string snapshot, IReadOnlyList<EmbeddingInput> inputs, string purpose, CancellationToken ct);
}
