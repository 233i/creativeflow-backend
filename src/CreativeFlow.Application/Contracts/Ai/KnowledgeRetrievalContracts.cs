namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>管理检索与工作流共用的策略；不包含网络地址、凭据或任意索引集合名。</summary>
public sealed record KnowledgeRetrievalSettings
{
    /// <summary>vector、keyword 或 hybrid；已有未声明策略的工作流继续使用向量检索。</summary>
    public string Mode { get; init; } = "vector";
    /// <summary>向量召回候选上限，启用时不得少于主命中数。</summary>
    public int DenseCandidates { get; init; } = 32;
    /// <summary>关键词及完整标识召回的候选上限。</summary>
    public int KeywordCandidates { get; init; } = 32;
    /// <summary>融合后实际发送到重排模型的候选上限。</summary>
    public int RerankCandidates { get; init; } = 24;
    /// <summary>最终排名保留的主命中数量，不包含邻接支持。</summary>
    public int TopK { get; init; } = 4;
    /// <summary>主命中和邻接支持合计的最终交付上限。</summary>
    public int MaxContextChunks { get; init; } = 8;
    /// <summary>仅作用于向量召回，不用于过滤 BM25、融合或重排序分数。</summary>
    public double MinVectorScore { get; init; }
    /// <summary>仅在启用重排序时使用的相关性阈值。</summary>
    public double? MinRerankScore { get; init; }
    /// <summary>相邻片段窗口，0 表示不补充，最大为前后各两段。</summary>
    public int NeighborRadius { get; init; } = 1;
    /// <summary>prefer 保留精确标识候选名额；require 必须命中全部标识；off 不采用标识策略。</summary>
    public string ExactMatch { get; init; } = "prefer";
    /// <summary>本次使用的重排模型身份，端点与凭据在后台解析。</summary>
    public Guid? RerankModelId { get; init; }
    /// <summary>fail 中止，fallback 明确降级为融合排名；均不自动重试模型。</summary>
    public string RerankFailurePolicy { get; init; } = "fail";
}

/// <summary>一次检索候选的可解释记录，不重复存储正文，不包含模型秘密。</summary>
/// <param name="Id">授权固定版本内的稳定片段身份。</param>
public sealed record KnowledgeCandidateTrace(Guid Id)
{
    /// <summary>向量路径的一基排名，未命中时为空。</summary>
    public int? DenseRank { get; init; }
    /// <summary>关键词路径的一基排名，未命中时为空。</summary>
    public int? KeywordRank { get; init; }
    /// <summary>去重融合后的一基排名。</summary>
    public int? FusionRank { get; init; }
    /// <summary>上游重排的一基排名，未进入候选时为空。</summary>
    public int? RerankRank { get; init; }
    /// <summary>原始 Cosine 分数，只使用向量阈值校验。</summary>
    public double? DenseScore { get; init; }
    /// <summary>原始 BM25 分数，不当作 Cosine 或概率。</summary>
    public double? KeywordScore { get; init; }
    /// <summary>基于召回名次及精确标识提升的 RRF 分数。</summary>
    public double? FusionScore { get; init; }
    /// <summary>上游原始相关性分数，不与其他尺度相加。</summary>
    public double? RerankScore { get; init; }
    /// <summary>是否命中受保护的完整标识；交付前仍需验证真实正文。</summary>
    public bool ExactMatched { get; init; }
    /// <summary>该片段是否进入最终交付。</summary>
    public bool Selected { get; init; }
    /// <summary>交付、邻接补充、阈值或排名上限等选片原因。</summary>
    public string Reason { get; init; } = "candidate";
}

/// <summary>召回、融合、重排和选片全过程摘要，随工作流输出保存，供管理测试与运行详情呈现。</summary>
/// <param name="Mode">当前检索方式。</param>
/// <param name="Query">召回使用的检索词。</param>
/// <param name="OriginalQuestion">可以不同于检索词的原始问题。</param>
public sealed record KnowledgeRetrievalTrace(string Mode, string Query, string? OriginalQuestion)
{
    /// <summary>通过向量阈值的语义候选数量。</summary>
    public int DenseCount { get; init; }
    /// <summary>关键词路径返回的候选数量。</summary>
    public int KeywordCount { get; init; }
    /// <summary>完整标识路径返回的候选数量。</summary>
    public int ExactCount { get; init; }
    /// <summary>去重融合后的总候选数量。</summary>
    public int FusedCount { get; init; }
    /// <summary>已成功获得重排分数的候选数，降级时为零。</summary>
    public int RerankedCount { get; init; }
    /// <summary>最终交付数量，包含邻接支持。</summary>
    public int DeliveredCount { get; init; }
    /// <summary>未启用、完成、无候选或显式降级状态。</summary>
    public string RerankStatus { get; init; } = "disabled";
    /// <summary>安全失败描述，不回显凭据或上游原始错误。</summary>
    public string? RerankError { get; init; }
    /// <summary>本次使用的重排模型身份，端点与凭据在后台解析。</summary>
    public string? RerankModelId { get; init; }
    /// <summary>实际使用的不可变重排配置版本。</summary>
    public string? RerankConfigurationVersion { get; init; }
    /// <summary>上游报告的 token 数，未报告时不估算。</summary>
    public long? RerankTokens { get; init; }
    /// <summary>问题向量化耗时，关键词或复用评测向量时为零。</summary>
    public long EmbeddingMilliseconds { get; init; }
    /// <summary>并行候选召回总耗时。</summary>
    public long RecallMilliseconds { get; init; }
    /// <summary>单次重排调用耗时。</summary>
    public long RerankMilliseconds { get; init; }
    /// <summary>权限校验、召回、排序和选片端到端耗时。</summary>
    public long TotalMilliseconds { get; init; }
    /// <summary>不重复正文的候选名次、分数和选片诊断。</summary>
    public KnowledgeCandidateTrace[] Candidates { get; init; } = [];
}

/// <summary>评测题及该固定版本中人工标注的相关片段 ID，不调用回答模型。</summary>
/// <param name="Query">人工标注的测试问题。</param>
/// <param name="RelevantChunkIds">固定版本中人工确认相关的片段身份。</param>
public sealed record KnowledgeEvaluationCase(string Query, Guid[] RelevantChunkIds);
/// <summary>三种策略对照评测；启用重排序时必须显式选择模型并确认可能产生费用。</summary>
/// <param name="Cases">至多十六道人工标注题。</param>
/// <param name="Settings">各策略共用的候选和选片参数。</param>
/// <param name="IncludeRerank">明确授权额外测试重排序模型。</param>
public sealed record KnowledgeEvaluationRequest(KnowledgeEvaluationCase[] Cases,
    KnowledgeRetrievalSettings Settings, bool IncludeRerank = false);
/// <param name="Strategy">被对照的检索策略。</param>
/// <param name="Recall">人工标注片段的召回率。</param>
/// <param name="Mrr">首个相关主命中的倒数排名。</param>
/// <param name="Ndcg">基于二元标注的归一化折损增益。</param>
/// <param name="ElapsedMilliseconds">各题召回与排序耗时之和。</param>
/// <param name="Results">同版本逐题诊断及完整命中。</param>
public sealed record KnowledgeEvaluationMetric(string Strategy, double Recall, double Mrr,
    double Ndcg, long ElapsedMilliseconds, KnowledgeSearchResponse[] Results);
/// <param name="VersionId">所有题目与策略共同使用的固定版本。</param>
/// <param name="Metrics">向量、混合及可选重排的对照指标。</param>
public sealed record KnowledgeEvaluationResponse(Guid VersionId, KnowledgeEvaluationMetric[] Metrics);
