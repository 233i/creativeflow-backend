using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>统一校验召回和选片配置，区分候选数、主命中数、最终上下文数及各自的分数尺度。</summary>
public static class KnowledgeRetrievalPolicy
{
    /// <summary>单路召回和重排的硬上限，限制正文、响应和网络负担。</summary>
    public const int MaxCandidates = 64;
    /// <summary>非法或冲突配置在任何付费模型调用前拒绝。</summary>
    public static string? Validate(KnowledgeRetrievalSettings s)
    {
        if (s.Mode is not ("vector" or "keyword" or "hybrid")) return "检索方式必须是 vector、keyword 或 hybrid。";
        if (s.DenseCandidates is < 1 or > MaxCandidates || s.KeywordCandidates is < 1 or > MaxCandidates
            || s.RerankCandidates is < 1 or > MaxCandidates) return "每路召回与重排序候选数量必须是 1–64 的整数。";
        if (s.TopK is < 1 or > 32 || s.MaxContextChunks is < 1 or > 32) return "主命中与最终交付片段数量必须是 1–32 的整数。";
        if (s.Mode != "keyword" && s.DenseCandidates < s.TopK || s.Mode != "vector" && s.KeywordCandidates < s.TopK
            || s.RerankModelId is not null && s.RerankCandidates < s.TopK) return "召回或重排序候选数量不能小于 TopK。";
        if (!double.IsFinite(s.MinVectorScore) || s.MinVectorScore is < -1 or > 1) return "Cosine 阈值必须在 -1–1 之间。";
        if (s.MinRerankScore is { } score && (!double.IsFinite(score) || score is < 0 or > 1 || s.RerankModelId is null))
            return "重排序阈值必须在 0–1 之间，且需要选择重排序模型。";
        if (s.NeighborRadius is < 0 or > 2 || s.ExactMatch is not ("prefer" or "require" or "off")) return "相邻窗口或精确匹配策略无效。";
        if (s.RerankModelId == Guid.Empty || s.RerankFailurePolicy is not ("fail" or "fallback")) return "重排序模型或失败策略无效。";
        return null;
    }

    /// <summary>从工作流冻结配置读取策略；缺省值与已有向量工作流保持一致。</summary>
    public static KnowledgeRetrievalSettings FromNode(JsonObject data) => new()
    {
        Mode = data["retrievalMode"] is null ? "vector" : Text(data["retrievalMode"]),
        DenseCandidates = (int)(Number(data["denseCandidates"]) ?? Math.Min(32, Math.Max(8, (Number(data["topK"]) ?? 4) * 4))),
        KeywordCandidates = (int)(Number(data["keywordCandidates"]) ?? 32),
        RerankCandidates = (int)(Number(data["rerankCandidates"]) ?? 24),
        TopK = (int)(Number(data["topK"]) ?? 4), MaxContextChunks = (int)(Number(data["maxContextChunks"]) ?? 8),
        MinVectorScore = Number(data["minScore"]) ?? 0, MinRerankScore = Number(data["minRerankScore"]),
        NeighborRadius = (int)(Number(data["neighborRadius"]) ?? 1),
        ExactMatch = data["exactMatch"] is null ? "prefer" : Text(data["exactMatch"]),
        RerankModelId = Guid.TryParse(Text(data["rerankModelId"]), out var model) ? model : null,
        RerankFailurePolicy = data["rerankFailurePolicy"] is null ? "fail" : Text(data["rerankFailurePolicy"]),
    };
}
