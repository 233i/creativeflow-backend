namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>修改名称和用途，版本号防止覆盖另一位管理员的新修改。</summary>
/// <param name="Name">管理员可见名称，保存前去除首尾空白。</param>
/// <param name="Description">用途说明，允许空字符串。</param>
/// <param name="Revision">读取时的元数据修订，用于拒绝过期覆盖。</param>
public sealed record UpdateKnowledgeMetadataRequest(string Name, string Description, long Revision);
/// <summary>删除确认绑定当前名称、元数据修订和完整影响摘要。</summary>
/// <param name="ConfirmName">必须逐字匹配当前库名。</param>
/// <param name="Reason">管理员填写的删除理由。</param>
/// <param name="Revision">删除预检取得的元数据修订。</param>
/// <param name="ImpactToken">绑定全部引用和版本状态的预检摘要。</param>
public sealed record DeleteKnowledgeRequest(string ConfirmName, string Reason, long Revision, string ImpactToken);
/// <summary>保留的工作流引用；同一资源引用多个版本时只展示一项。</summary>
/// <param name="Kind">草稿、模板、发布版本或运行的资源种类。</param>
/// <param name="Id">引用资源的唯一标识。</param>
/// <param name="Name">资源当前名称，缺失时使用标识。</param>
/// <param name="Versions">该资源引用本库的版本数量。</param>
public sealed record KnowledgeManagementReference(string Kind, Guid Id, string Name, int Versions);
/// <summary>删除的实时影响范围，历史运行引用同样会阻止清理。</summary>
/// <param name="LibraryId">待删除知识库标识。</param>
/// <param name="Name">当前库名，用于显式确认。</param>
/// <param name="Revision">预检时库的管理修订。</param>
/// <param name="ImpactToken">提交时必须复验的引用及版本摘要。</param>
/// <param name="Allowed">当前不存在引用或活动任务阻止删除。</param>
/// <param name="Blockers">阻止删除的可读原因。</param>
/// <param name="Versions">尚未清理的版本数量。</param>
/// <param name="Documents">保留的文档修订总数。</param>
/// <param name="Chunks">待清理的本地切片数量。</param>
/// <param name="ActiveTasks">正在排队、写入或清理的任务数。</param>
/// <param name="ReferenceCount">全部引用资源数，不受展示上限影响。</param>
/// <param name="References">按资源分组的前一百项引用。</param>
public sealed record KnowledgeDeletionPreview(Guid LibraryId, string Name, long Revision, string ImpactToken,
    bool Allowed, string[] Blockers, int Versions, int Documents, int Chunks, int ActiveTasks,
    int ReferenceCount, KnowledgeManagementReference[] References);
/// <summary>管理动作的安全审计记录，不包含文档正文、向量或模型凭据。</summary>
/// <param name="Id">审计记录唯一标识。</param>
/// <param name="AdminId">操作管理员标识，后台清理事件为空。</param>
/// <param name="AdminName">现存管理员名称，账号已移除时为空。</param>
/// <param name="Action">管理动作代码。</param>
/// <param name="Reason">操作理由或安全的导出范围。</param>
/// <param name="BeforeJson">变更前安全元数据，不含正文或凭据。</param>
/// <param name="AfterJson">变更后安全元数据，未记录时为空。</param>
/// <param name="CreatedAt">事件发生的 UTC 时间。</param>
public sealed record KnowledgeManagementAudit(Guid Id, Guid? AdminId, string? AdminName, string Action,
    string Reason, string? BeforeJson, string? AfterJson, DateTimeOffset CreatedAt);
/// <summary>不含正文和向量的文档版本对比条目。</summary>
/// <param name="Name">对比文档的原文件名。</param>
/// <param name="Change">新增、删除、变化或未变的状态代码。</param>
/// <param name="FromId">旧版本文档修订标识，不存在时为空。</param>
/// <param name="ToId">新版本文档修订标识，不存在时为空。</param>
/// <param name="FromBytes">旧版规范化文本的 UTF-8 字节数。</param>
/// <param name="ToBytes">新版规范化文本的 UTF-8 字节数。</param>
/// <param name="FromChunks">旧版本实际切片数量。</param>
/// <param name="ToChunks">新版本实际切片数量。</param>
public sealed record KnowledgeDocumentDifference(string Name, string Change, Guid? FromId, Guid? ToId,
    int FromBytes, int ToBytes, int FromChunks, int ToChunks);
/// <summary>固定版本之间的文档集合和切片配置差异。</summary>
/// <param name="From">作为基准的不可变旧版本。</param>
/// <param name="To">目标不可变版本。</param>
/// <param name="ChunkingChanged">切片长度、重叠或解析器版本发生变化。</param>
/// <param name="IndexChanged">索引模式发生变化。</param>
/// <param name="Documents">按文件名合并的文档差异。</param>
public sealed record KnowledgeVersionComparison(KnowledgeVersionResponse From, KnowledgeVersionResponse To,
    bool ChunkingChanged, bool IndexChanged, KnowledgeDocumentDifference[] Documents);
/// <summary>原文按行对比，行号属于规范化文本，不执行 Markdown 或 HTML。</summary>
/// <param name="Kind">相同、删除或新增的行状态。</param>
/// <param name="FromLine">旧正文的一基行号，新增行为空。</param>
/// <param name="ToLine">新正文的一基行号，删除行为空。</param>
/// <param name="Text">原文行文本，前端必须按纯文本渲染。</param>
public sealed record KnowledgeDiffLine(string Kind, int? FromLine, int? ToLine, string Text);
/// <summary>逐文档分页差异；简化比较明确标记，不把大块替换伪装成精确最短编辑序列。</summary>
/// <param name="Name">文档原文件名。</param>
/// <param name="FromVersion">旧固定版本标识。</param>
/// <param name="ToVersion">新固定版本标识。</param>
/// <param name="TotalLines">全部差异行数量，含相同行。</param>
/// <param name="Simplified">中间大块变化使用替换表示，未计算最短编辑序列。</param>
/// <param name="Offset">当前页的零基差异行偏移。</param>
/// <param name="Lines">有界的当前页差异行。</param>
public sealed record KnowledgeDocumentComparison(string Name, Guid FromVersion, Guid ToVersion,
    int TotalLines, bool Simplified, int Offset, KnowledgeDiffLine[] Lines);
/// <summary>只读导出的文档清单，将原文件名与固定安全归档路径对应。</summary>
/// <param name="Name">原文件名，仅用于清单展示。</param>
/// <param name="Path">服务器生成的安全 ZIP 相对路径。</param>
/// <param name="Sha256">规范化 UTF-8 正文的 SHA-256 小写十六进制。</param>
/// <param name="Bytes">归档正文的未压缩字节数。</param>
public sealed record KnowledgeExportDocument(string Name, string Path, string Sha256, int Bytes);
/// <summary>导出版本只包含公开元数据与原文清单。</summary>
/// <param name="Version">版本标识和公开切片配置。</param>
/// <param name="Documents">正文路径与校验清单。</param>
public sealed record KnowledgeExportVersion(KnowledgeVersionResponse Version, KnowledgeExportDocument[] Documents);
/// <summary>可离线读取的归档清单；不包含向量、模型快照、凭据和授权。</summary>
/// <param name="FormatVersion">归档清单格式编号。</param>
/// <param name="ExportedAt">归档创建的 UTC 时间。</param>
/// <param name="Library">导出时的安全库元数据。</param>
/// <param name="Versions">此次归档包含的版本及原文清单。</param>
public sealed record KnowledgeExportManifest(int FormatVersion, DateTimeOffset ExportedAt,
    KnowledgeBaseResponse Library, KnowledgeExportVersion[] Versions);
