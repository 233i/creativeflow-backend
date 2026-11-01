using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>知识库管理事务与一致读取边界，不向业务服务传递数据库连接。</summary>
public interface IKnowledgeManagementRepository
{
    /// <summary>用乐观锁更新安全元数据，并在同一事务记录前后值。</summary>
    Task<KnowledgeBaseResponse> UpdateMetadataAsync(Guid id, Guid admin, UpdateKnowledgeMetadataRequest request, CancellationToken ct);
    /// <summary>只读计算全库的版本、正文、任务和引用影响。</summary>
    Task<KnowledgeDeletionPreview> PreviewDeletionAsync(Guid id, CancellationToken ct);
    /// <summary>在引用锁内复验影响摘要，然后排队清理或幂等返回删除状态。</summary>
    Task<KnowledgeBaseResponse> DeleteLibraryAsync(Guid id, Guid admin, DeleteKnowledgeRequest request, CancellationToken ct);
    /// <summary>读取最多一百条管理审计，包括已删除知识库的墓碑。</summary>
    Task<KnowledgeManagementAudit[]> AuditAsync(Guid id, CancellationToken ct);
    /// <summary>归档完成后复验可读性并记录导出范围，不记录正文或机密。</summary>
    Task RecordExportAsync(Guid id, Guid admin, Guid? version, CancellationToken ct);
    /// <summary>持有与删除互斥的只读边界，将选定不可变版本逐个交给调用方，防止全库载入内存。</summary>
    Task ReadVersionsAsync(Guid id, Guid? version, Func<KnowledgeBaseResponse, CancellationToken, Task> begin,
        Func<KnowledgeVersionResponse, KnowledgeDocumentInput[], CancellationToken, Task> read, CancellationToken ct);
    /// <summary>在同一只读边界获取两个固定版本及其文档，正文最多为两个受限版本。</summary>
    Task<KnowledgeComparisonSource> ComparisonAsync(Guid id, Guid from, Guid to, CancellationToken ct);
}

/// <summary>版本对比内部输入；文档正文仅交给管理服务，响应由服务明确裁剪。</summary>
/// <param name="From">基准版本公开元数据。</param>
/// <param name="To">目标版本公开元数据。</param>
/// <param name="FromDocuments">基准版本规范化原文。</param>
/// <param name="ToDocuments">目标版本规范化原文。</param>
/// <param name="FromSummaries">基准版本实际字节与切片计数。</param>
/// <param name="ToSummaries">目标版本实际字节与切片计数。</param>
public sealed record KnowledgeComparisonSource(KnowledgeVersionResponse From, KnowledgeVersionResponse To,
    KnowledgeDocumentInput[] FromDocuments, KnowledgeDocumentInput[] ToDocuments,
    KnowledgeDocumentSummary[] FromSummaries, KnowledgeDocumentSummary[] ToSummaries);
