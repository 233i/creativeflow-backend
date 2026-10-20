using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>独立知识资源及持久索引检查点；仅由管理员用例和专用 Worker 使用。</summary>
public interface IKnowledgeRepository
{
    /// <summary>读取有界管理列表。</summary>
    Task<KnowledgeBaseResponse[]> ListAsync(CancellationToken ct);
    /// <summary>按库 ID 读取元数据。</summary>
    Task<KnowledgeBaseResponse?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>在配额内创建新库，不调用模型。</summary>
    Task<KnowledgeBaseResponse> CreateAsync(CreateKnowledgeBaseRequest request, CancellationToken ct);
    /// <summary>立即改变可用性；历史版本不被删除。</summary>
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct);
    /// <summary>读取该库的有界版本列表。</summary>
    Task<KnowledgeVersionResponse[]> VersionsAsync(Guid id, CancellationToken ct);
    /// <summary>读取属于指定库的单个版本及内部检查点。</summary>
    Task<KnowledgeVersion?> FindVersionAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>管理恢复只读状态与计数，不加载旧的完整向量检查点。</summary>
    Task<KnowledgeVersion?> FindVersionMetadataAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>检索只加载模型指纹与片段，不读取入库文档集或持久向量数组。</summary>
    Task<KnowledgeVersion?> FindReadableVersionAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>执行及读取授权复验只读就绪版本摘要，避免反复加载大型索引内容。</summary>
    Task<string?> FindReadyDigestAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>追加幂等候选版本；同库最多一个活跃索引任务。</summary>
    Task<KnowledgeVersion> InsertVersionAsync(KnowledgeVersion version, CancellationToken ct);
    /// <summary>在当前事务中只激活已经就绪的同库版本。</summary>
    Task<bool> ActivateAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>获取独立索引宿主的数据库会话锁，多进程也只有一个正在写的索引任务。</summary>
    Task<IAsyncDisposable?> TryWorkerLockAsync(CancellationToken ct);
    /// <summary>读取最早的待处理索引，不消费媒体或工作流队列。</summary>
    Task<KnowledgeVersion?> NextAsync(CancellationToken ct);
    /// <summary>检查点和状态一起原子保存；与手工恢复使用同一宿主锁。</summary>
    Task SaveProgressAsync(KnowledgeVersion version, CancellationToken ct);
    /// <summary>读取版本文档元数据，不加载整库分段或向量。</summary>
    Task<KnowledgeDocumentSummary[]> DocumentsAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>限定库、版本和修订 ID 后读取单篇正文。</summary>
    Task<KnowledgeDocumentInput?> DocumentAsync(Guid id, Guid version, Guid document, CancellationToken ct);
    /// <summary>有界读取分段页或检索命中的特定 ID，不加载向量检查点。</summary>
    Task<KnowledgeChunk[]> ReadChunksAsync(Guid id, Guid version, Guid[]? ids, Guid? document, int offset, int limit, CancellationToken ct);
    /// <summary>按同一文档顺序读取检索命中的相邻窗口，不跨文档或版本扩展。</summary>
    Task<KnowledgeChunk[]> ReadAdjacentChunksAsync(Guid id, Guid version, Guid[] seedIds, int radius, CancellationToken ct);
    /// <summary>索引 Worker 每次只读取尚无向量的一个小批次。</summary>
    Task<KnowledgeChunk[]> PendingChunksAsync(Guid id, Guid version, int limit, CancellationToken ct);
    /// <summary>完整批次的向量和队列状态同事务保存，未确认批次绝不部分提交。</summary>
    Task SaveVectorBatchAsync(KnowledgeVersion version, IReadOnlyList<EmbeddingVector> vectors, CancellationToken ct);
    /// <summary>只载入尚未写入 Qdrant 的一个向量批次。</summary>
    Task<EmbeddingVector[]> PendingWritesAsync(Guid id, Guid version, int limit, CancellationToken ct);
    /// <summary>Qdrant 确认幂等写入后保存写检查点。</summary>
    Task MarkWrittenAsync(Guid id, Guid version, Guid[] chunks, CancellationToken ct);
    /// <summary>预检激活指针、运行、草稿及发布引用，提交清理时仍在锁内复验。</summary>
    Task<KnowledgeCleanupPreview> CleanupPreviewAsync(Guid id, Guid version, CancellationToken ct);
    /// <summary>经管理员确认后标记清理；提交不直接删除远端数据。</summary>
    Task RequestCleanupAsync(Guid id, Guid version, Guid admin, string reason, CancellationToken ct);
    /// <summary>返回已显式授权清理的任务；共享索引 Worker 锁保证不会与写入并行。</summary>
    Task<KnowledgeVersion?> NextCleanupAsync(CancellationToken ct);
    /// <summary>向量集合确认已删除后移除分段及无引用文档，保留版本墓碑与审计。</summary>
    Task FinishCleanupAsync(Guid id, Guid version, CancellationToken ct);
}

/// <summary>索引内部记录；VectorsJson 和模型快照绝不直接进入 HTTP 响应。</summary>
/// <param name="Id">候选版本及任务 ID。</param>
/// <param name="BaseId">归属知识库。</param>
/// <param name="Digest">文档分段和模型配置的组合摘要。</param>
/// <param name="ModelSnapshot">不包含密钥的模型配置摘要及版本。</param>
/// <param name="DocumentsJson">完整的文本文档集。</param>
/// <param name="ChunksJson">稳定片段及来源。</param>
/// <param name="VectorsJson">已完成批次的内部向量检查点。</param>
/// <param name="Status">当前索引状态。</param>
/// <param name="Error">安全错误说明。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record KnowledgeVersion(Guid Id, Guid BaseId, string Digest, string ModelSnapshot,
    string DocumentsJson, string ChunksJson, string VectorsJson, string Status, string? Error, DateTimeOffset CreatedAt)
{
    /// <summary>单独保存的小进度记录；仅显式完整读取兼容方法组装上面的 JSON，不再作为数据库存储方式。</summary>
    public KnowledgeVersionResponse? Progress { get; init; }
    /// <summary>新候选所基于的不可变版本。</summary>
    public Guid? SourceVersionId { get; init; }
    /// <summary>新候选的分段字符数。</summary>
    public int? ChunkSize { get; init; }
    /// <summary>新候选的重叠字符数。</summary>
    public int? Overlap { get; init; }
    /// <summary>生成该版本切片的解析器版本；用于旧版本兼容和新算法隔离。</summary>
    public string ParserVersion { get; init; } = "utf8-lines-v2";
    /// <summary>不可变索引布局；新版本构建混合索引，旧的固定版本不会被原地替换。</summary>
    public string IndexSchema { get; init; } = "hybrid-v1";
}
