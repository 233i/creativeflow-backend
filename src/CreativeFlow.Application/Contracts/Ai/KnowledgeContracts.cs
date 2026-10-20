using System.Text.Json.Serialization;

namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>管理员创建共享库的基本资料，向量模型必须显式选择。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Description">用途说明。</param>
/// <param name="ModelId">现有向量模型标识。</param>
public sealed record CreateKnowledgeBaseRequest(string Name, string Description, Guid ModelId);

/// <summary>本批次支持的 UTF-8 文本文档；不接受 URL 或活动文件。</summary>
/// <param name="Name">带 txt 或 md 后缀的文件名。</param>
/// <param name="Text">严格解码后的正文。</param>
public sealed record KnowledgeDocumentInput(string Name, string Text);

/// <summary>候选版本包含完整文档集；旧版本不被原地修改。</summary>
/// <param name="Documents">完整文档集，数量和 UTF-8 字节预算由部署配额限制。</param>
/// <param name="ChunkSize">每段字符上限。</param>
/// <param name="Overlap">相邻片段的重叠字符数。</param>
/// <param name="PreviewDigest">确认入库时必须提交服务器预览摘要。</param>
public sealed record KnowledgeImportRequest(KnowledgeDocumentInput[] Documents, int ChunkSize = 800,
    int Overlap = 100, string? PreviewDigest = null);

/// <summary>保留文档及行号的分段预览；正文始终按不可信文本展示。</summary>
/// <param name="Id">按文档内容和分段规则生成的稳定标识。</param>
/// <param name="Document">源文档名。</param>
/// <param name="StartLine">起始行，从一开始。</param>
/// <param name="EndLine">结束行，从一开始；不伪造页码。</param>
/// <param name="Text">有界片段正文。</param>
public sealed record KnowledgeChunk(Guid Id, string Document, int StartLine, int EndLine, string Text)
{
    /// <summary>规范化正文中的 UTF-16 起始偏移，结束偏移为排他位置。</summary>
    public int StartOffset { get; init; }
    /// <summary>规范化正文中的 UTF-16 排他偏移。</summary>
    public int EndOffset { get; init; }
    /// <summary>切片所属的 Markdown 标题路径；无标题时为空。</summary>
    public string SectionPath { get; init; } = "";
    /// <summary>语义块类型：标题、段落、列表、表格、代码块或混合块。</summary>
    public string BlockType { get; init; } = "paragraph";
    /// <summary>是否由超长语义块继续硬切得到。</summary>
    public bool Continuation { get; init; }
    /// <summary>数据库内部的来源文档修订 ID，不进入 API 响应。</summary>
    [JsonIgnore]
    public Guid? DocumentId { get; init; }
    /// <summary>数据库内部的全版本顺序，不进入 API 响应。</summary>
    [JsonIgnore]
    public int Ordinal { get; init; }
}

/// <summary>无模型调用的分段预览结果。</summary>
/// <param name="Digest">绑定完整文档和分段版本的摘要。</param>
/// <param name="Chunks">待确认片段。</param>
/// <param name="DocumentCount">文档数量。</param>
public sealed record KnowledgePreview(string Digest, KnowledgeChunk[] Chunks, int DocumentCount);

/// <summary>管理列表只公开资源元数据，不交付向量或模型密钥。</summary>
/// <param name="Id">库标识。</param>
/// <param name="Name">名称。</param>
/// <param name="Description">用途。</param>
/// <param name="ModelId">向量模型标识。</param>
/// <param name="Enabled">停用后立即拒绝检索。</param>
/// <param name="ActiveVersion">当前明确激活的索引版本。</param>
public sealed record KnowledgeBaseResponse(Guid Id, string Name, string Description, Guid ModelId, bool Enabled, Guid? ActiveVersion);

/// <summary>索引任务的有界进度；向量检查点不进入响应。</summary>
/// <param name="Id">索引版本兼任务标识。</param>
/// <param name="Status">排队、向量化、写入、就绪、失败或待核对。</param>
/// <param name="Error">脱敏错误。</param>
/// <param name="DocumentCount">版本中的完整文档数量。</param>
/// <param name="ChunkCount">总片段数。</param>
/// <param name="EmbeddedCount">已经持久化向量的片段数。</param>
/// <param name="CreatedAt">任务创建时间。</param>
public sealed record KnowledgeVersionResponse(Guid Id, string Status, string? Error, int DocumentCount,
    int ChunkCount, int EmbeddedCount, DateTimeOffset CreatedAt)
{
    /// <summary>已被 Qdrant 确认的片段数；索引重写采用幂等 upsert。</summary>
    public int WrittenCount { get; init; }
    /// <summary>从相同模型指纹与片段 ID 的历史版本复用的向量数。</summary>
    public int ReusedCount { get; init; }
    /// <summary>文档变更的明确来源版本，完整替换导入时为空。</summary>
    public Guid? SourceVersionId { get; init; }
    /// <summary>分段长度，旧试点记录未保存此值时为空。</summary>
    public int? ChunkSize { get; init; }
    /// <summary>重叠字符数，旧试点记录未保存此值时为空。</summary>
    public int? Overlap { get; init; }
    /// <summary>本版本预留的逻辑存储预算，不暴露向量。</summary>
    public long ReservedBytes { get; init; }
    /// <summary>库内单调递增的版本序号；清理旧版本后不重新编号。</summary>
    public int Number { get; init; }
    /// <summary>生成该版本分段的解析器版本；旧数据保持 legacy 标识。</summary>
    public string ParserVersion { get; init; } = "utf8-lines-v2";
    /// <summary>dense-v1 仅有向量；hybrid-v1 同时有命名向量、BM25 和精确标识索引。</summary>
    public string IndexSchema { get; init; } = "dense-v1";
}

/// <summary>详情分开返回库元数据和最近版本，正文通过单独预览读取。</summary>
/// <param name="Library">库元数据。</param>
/// <param name="Versions">最多二十个版本。</param>
public sealed record KnowledgeDetailResponse(KnowledgeBaseResponse Library, KnowledgeVersionResponse[] Versions);

/// <summary>显式检索测试仅接受问题及有界 TopK，不能覆盖模型或数据库地址。</summary>
/// <param name="Query">待向量化的问题。</param>
/// <param name="TopK">最多八条候选片段。</param>
public sealed record KnowledgeSearchRequest(string Query, int TopK = 4)
{
    /// <summary>独立原始问题；改写检索词时用它保护精确标识和指导重排序。</summary>
    public string? OriginalQuestion { get; init; }
    /// <summary>显式策略；未提供时保持既有向量检索语义。</summary>
    public KnowledgeRetrievalSettings? Settings { get; init; }
}

/// <summary>按当前可见版本重新核验的检索片段，不含向量。</summary>
/// <param name="Chunk">服务器数据库中的原始片段及来源。</param>
/// <param name="Score">Cosine 相似度分数，不是概率。</param>
public sealed record KnowledgeSearchHit(KnowledgeChunk Chunk, double Score)
{
    /// <summary>primary 为向量直接命中，supporting 为同文档相邻补充片段。</summary>
    public string Role { get; init; } = "primary";
    /// <summary>分数尺度：vector、keyword、fusion 或 rerank，不将其当作概率。</summary>
    public string ScoreType { get; init; } = "vector";
}

/// <summary>检索测试结果与实际使用的固定版本。</summary>
/// <param name="VersionId">本次检索版本。</param>
/// <param name="Hits">按节点或管理测试的交付数量保留完整片段，最多三十二段，正文不做隐藏字符截断。</param>
public sealed record KnowledgeSearchResponse(Guid VersionId, KnowledgeSearchHit[] Hits)
{
    /// <summary>全过程摘要，不含上游秘密或重复正文。</summary>
    public KnowledgeRetrievalTrace? Trace { get; init; }
    /// <summary>主命中和邻接支持合计的最终交付上限。</summary>
    public int MaxContextChunks { get; init; } = 8;
    /// <summary>因交付数量上限而省略的主命中或邻接片段数。</summary>
    public int OmittedCount { get; init; }
    /// <summary>是否按片段数量省略结果，不代表正文字符被截断。</summary>
    public bool Truncated => OmittedCount > 0;
}

/// <summary>即时停用开关，不删除历史索引或数据库内容。</summary>
/// <param name="Enabled">是否允许新索引及检索。</param>
public sealed record SetKnowledgeEnabledRequest(bool Enabled);

/// <summary>明确的失败恢复动作；重写索引不会再次向量化。</summary>
/// <param name="Action">continue 或 reembedUnknown。</param>
public sealed record RecoverKnowledgeVersionRequest(string Action);
