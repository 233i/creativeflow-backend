namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>版本文档列表不加载正文或向量，修订 ID 不能跨版本任意读取。</summary>
/// <param name="Id">不可变文档修订标识。</param>
/// <param name="Name">源文件名。</param>
/// <param name="ByteCount">UTF-8 正文字节数。</param>
/// <param name="ChunkCount">该版本的片段数。</param>
public sealed record KnowledgeDocumentSummary(Guid Id, string Name, int ByteCount, int ChunkCount);

/// <summary>在一个明确版本上做文档增删改；未提及的文档由服务器保留，旧版本不原地改写。</summary>
/// <param name="SourceVersionId">作为基线的完整版本，不能由当前激活指针隐式替换。</param>
/// <param name="Changes">新增、替换或移除操作；同一文件只能出现一次。</param>
/// <param name="ChunkSize">新版本分段长度。</param>
/// <param name="Overlap">新版本重叠字符。</param>
/// <param name="PreviewDigest">确认时必须提交绑定基线和操作内容的摘要。</param>
public sealed record KnowledgeDocumentChangeRequest(Guid SourceVersionId, KnowledgeDocumentChange[] Changes,
    int ChunkSize = 800, int Overlap = 100, string? PreviewDigest = null);

/// <summary>只接受固定文档操作，不接受路径、URL 或内嵌模型配置。</summary>
/// <param name="Action">add、replace 或 remove。</param>
/// <param name="Name">操作的文件名；replace 必须与原名称一致。</param>
/// <param name="DocumentId">replace/remove 所需的原修订 ID，add 必须为空。</param>
/// <param name="Text">add/replace 的 UTF-8 正文，remove 必须为空。</param>
public sealed record KnowledgeDocumentChange(string Action, string Name, Guid? DocumentId = null, string? Text = null);

/// <summary>需要用户显式确认的版本清理请求；不会删除仍被引用的版本。</summary>
/// <param name="Reason">管理员说明，写入清理审计。</param>
public sealed record KnowledgeCleanupRequest(string Reason);

/// <summary>清理预检只返回阻止原因与引用计数，不泄露运行内容。</summary>
/// <param name="Allowed">当前状态是否可排队清理，提交时仍会在锁内复验。</param>
/// <param name="Reason">不可清理的原因。</param>
/// <param name="ReferenceCount">草稿、模板、发布版本和运行记录的引用总数。</param>
/// <param name="ReservedBytes">完成清理后释放的逻辑预留预算。</param>
public sealed record KnowledgeCleanupPreview(bool Allowed, string? Reason, int ReferenceCount, long ReservedBytes);
