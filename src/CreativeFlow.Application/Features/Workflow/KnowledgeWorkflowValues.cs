using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Knowledge;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>知识节点的固定端口及证据类型，普通 JSON 或开始参数不能伪造检索上下文。</summary>
public static class KnowledgeWorkflowValues
{
    /// <summary>未设置交付数量的节点默认保留八个片段，主命中与相邻片段合计计数。</summary>
    public const int DefaultContextChunks = 8;
    /// <summary>单次检索最多交付三十二个完整片段，问答引用使用相同上限。</summary>
    public const int MaxContextChunks = 32;
    /// <summary>导入切片的正文长度上限；交付时不再截断正文。</summary>
    public const int MaxChunkCharacters = 1600;
    /// <summary>按片段结构推导序列化保护上限，容纳 JSON 最坏六倍转义、来源元数据及八千字符答案；不用于筛选或裁剪证据。</summary>
    public const int MaxValueBytes = MaxContextChunks * (MaxChunkCharacters * 6 + 2048) + 8000 * 6 + 8192 + 131072;
    /// <summary>模型只返回八千字符答案和最多三十二个引用 ID，预留 JSON 转义与封装空间，不包含来源正文。</summary>
    public const int MaxAnswerResponseBytes = 8000 * 6 + MaxContextChunks * 256 + 8192;

    /// <summary>读取已通过配置校验的数量上限；未设置时使用默认值。</summary>
    public static int ContextLimit(JsonObject data) => (int)(Number(data["maxContextChunks"]) ?? DefaultContextChunks);

    /// <summary>知识节点采用显式类型名，不通过模型名或提示词猜测用途。</summary>
    public static bool IsKnowledge(string type) => type is "knowledgeRetrieve" or "knowledgeAnswer";

    /// <summary>检索 question 端口接收检索词，问答 question 端口接收原始问题；保留端口标识但不要求文本相同，context 必须是检索证据。</summary>
    public static JsonArray Inputs(string type) => type == "knowledgeRetrieve"
        ? [Field("question", "string"), new JsonObject { ["key"] = "originalQuestion", ["type"] = "string", ["required"] = false }]
        : [Field("question", "string"), Field("context", "contextChunks")];

    /// <summary>返回固定的领域输出，不允许更名或增加任意内部字段。</summary>
    public static JsonArray Outputs(string type) => type == "knowledgeRetrieve"
        ? [Field("context", "contextChunks")]
        : [Field("answer", "answer")];

    /// <summary>创建单个固定必填端口。</summary>
    private static JsonObject Field(string key, string type) => new() { ["key"] = key, ["type"] = type, ["required"] = true };

    /// <summary>限制知识配置与固定输入结构；草稿只允许暂缺资源选择，不放宽安全字段。</summary>
    public static string? Validate(string type, JsonObject data, bool forExecution)
    {
        // 检索策略与冻结参数共用有界校验；原始问题只用于标识保护和重排，不放宽正文来源及权限。
        var allowed = type == "knowledgeRetrieve"
            ? new HashSet<string> { "title", "fields", "libraryId", "versionId", "topK", "minScore", "maxContextChunks",
                "retrievalMode", "denseCandidates", "keywordCandidates", "rerankCandidates", "minRerankScore", "neighborRadius", "exactMatch", "rerankModelId", "rerankFailurePolicy" }
            : new HashSet<string> { "title", "fields", "modelId", "systemPrompt", "maxOutputTokens", "temperature", "timeoutSeconds" };
        if (data.Any(pair => !allowed.Contains(pair.Key))) return "knowledge node contains unknown configuration";
        if (!JsonNode.DeepEquals(data["fields"], Inputs(type))
            && !(type == "knowledgeRetrieve" && JsonNode.DeepEquals(data["fields"], new JsonArray(Field("question", "string")))))
            return "knowledge node input ports are fixed";
        if (type == "knowledgeRetrieve")
        {
            foreach (var key in new[] { "libraryId", "versionId" })
            {
                if (data[key] is not null && data[key]!.GetValueKind() != System.Text.Json.JsonValueKind.String) return "knowledge resource IDs must be strings";
                if ((forExecution || Text(data[key]).Length != 0) && !Guid.TryParse(Text(data[key]), out _)) return "select a knowledge library and a fixed ready version";
            }
            if (Number(data["topK"]) is not { } k || k < 1 || k > MaxContextChunks || k != Math.Truncate(k)) return "检索 TopK 必须是 1–32 的整数。";
            if (data.ContainsKey("maxContextChunks") && (Number(data["maxContextChunks"]) is not { } limit || limit < 1 || limit > MaxContextChunks || limit != Math.Truncate(limit)))
                return "最终交付片段数必须是 1–32 的整数。";
            if (Number(data["minScore"]) is not { } score || score < -1 || score > 1) return "Cosine threshold must be -1–1";
            foreach (var key in new[] { "denseCandidates", "keywordCandidates", "rerankCandidates", "neighborRadius" })
                if (data.ContainsKey(key) && (Number(data[key]) is not { } count || count != Math.Truncate(count) || count is < 0 or > 64))
                    return "召回、重排候选数和相邻范围必须为合法整数。";
            foreach (var key in new[] { "retrievalMode", "exactMatch", "rerankFailurePolicy", "rerankModelId" })
                if (data[key] is not null && data[key]!.GetValueKind() != System.Text.Json.JsonValueKind.String) return "检索策略与模型 ID 必须是字符串。";
            if (Text(data["rerankModelId"]).Length > 0 && !Guid.TryParse(Text(data["rerankModelId"]), out _)) return "请选择合法的重排序模型。";
            if (data["minRerankScore"] is not null && Number(data["minRerankScore"]) is null) return "重排最低分数必须为数字。";
            if (KnowledgeRetrievalPolicy.Validate(KnowledgeRetrievalPolicy.FromNode(data)) is { } error) return error;
        }
        return null;
    }

    /// <summary>验证领域值的基本结构与有界性；资源归属和正文由执行服务再次与持久化版本比对。</summary>
    public static bool Matches(string type, JsonNode? value)
    {
        if (value is not JsonObject obj || !IsBounded(obj, MaxValueBytes)) return false;
        if (type == "contextChunks") return obj.All(pair => pair.Key is "libraryId" or "versionId" or "retrievalNodeId" or "question" or "originalQuestion" or "trace" or "truncated" or "hits" or "omittedCount" or "maxContextChunks" or "truncationReason")
            && Guid.TryParse(Text(obj["libraryId"]), out _)
            && Guid.TryParse(Text(obj["versionId"]), out _) && Text(obj["retrievalNodeId"]).Length is > 0 and <= 100
            && !string.IsNullOrWhiteSpace(Text(obj["question"])) && Text(obj["question"]).Length <= 2000 && obj["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out _)
            && (obj["originalQuestion"] is null || !string.IsNullOrWhiteSpace(Text(obj["originalQuestion"])) && Text(obj["originalQuestion"]).Length <= 2000)
            && (obj["trace"] is null || obj["trace"] is JsonObject trace && IsBounded(trace, 131072))
            && (obj["omittedCount"] is null || (Number(obj["omittedCount"]) is { } omitted && omitted >= 0 && omitted == Math.Truncate(omitted)))
            && obj["hits"] is JsonArray hits && hits.Count <= MaxContextChunks && hits.All(Hit)
            && (!obj.ContainsKey("maxContextChunks") || Number(obj["maxContextChunks"]) is { } limit && limit >= 1 && limit <= MaxContextChunks && limit == Math.Truncate(limit) && hits.Count <= limit)
            && (!obj.ContainsKey("truncationReason") || Text(obj["truncationReason"]) == "chunk_limit" && truncated.GetValue<bool>()
                && Number(obj["omittedCount"]) is > 0 && Number(obj["maxContextChunks"]) == hits.Count);
        return type == "answer" && obj.Count == 3 && Text(obj["text"]).Length is > 0 and <= 8000
            && obj["grounded"] is JsonValue grounded && grounded.TryGetValue<bool>(out var supported)
            && obj["citations"] is JsonArray citations && citations.Count <= MaxContextChunks
            && (supported ? citations.Count > 0 : citations.Count == 0) && citations.All(Citation);
    }

    /// <summary>片段正文与真实来源只作为纯数据交付，不包含向量或外部链接。</summary>
    private static bool Hit(JsonNode? value) => value is JsonObject hit && (hit.Count is >= 6 and <= 8)
        && hit.All(pair => pair.Key is "id" or "document" or "startLine" or "endLine" or "text" or "score" or "role" or "scoreType")
        && Guid.TryParse(Text(hit["id"]), out _) && Text(hit["document"]).Length is > 0 and <= 120
        && Number(hit["startLine"]) is { } start && start >= 1 && start == Math.Truncate(start)
        && Number(hit["endLine"]) is { } end && end >= start && end == Math.Truncate(end)
        && Text(hit["text"]).Length is > 0 and <= MaxChunkCharacters && Number(hit["score"]) is { } score && double.IsFinite(score)
        && (hit["scoreType"] is null ? score is >= -1 and <= 1 : Text(hit["scoreType"]) switch
            { "vector" => score is >= -1 and <= 1, "rerank" => score is >= 0 and <= 1, "keyword" or "fusion" => score >= 0, _ => false })
        && (hit["role"] is null || Text(hit["role"]) is "primary" or "supporting");

    /// <summary>引用携带固定库与版本，可在权限复验后定位来源。</summary>
    private static bool Citation(JsonNode? value) => value is JsonObject citation && citation.Count is 7 or 8
        && Guid.TryParse(Text(citation["libraryId"]), out _) && Guid.TryParse(Text(citation["versionId"]), out _)
        && Guid.TryParse(Text(citation["id"]), out _) && Text(citation["document"]).Length is > 0 and <= 120
        && Number(citation["startLine"]) is { } start && start >= 1
        && Number(citation["endLine"]) is { } end && end >= start && Text(citation["text"]).Length is > 0 and <= MaxChunkCharacters
        && (citation["role"] is null && citation.Count == 7 || Text(citation["role"]) == "supporting" && citation.Count == 8);
}
