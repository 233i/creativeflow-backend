using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>保存、测试和工作流调用共用的重排序限额；输入不会隐式截断或拆批。</summary>
public static class RerankValidation
{
    /// <summary>校验相对路径及文本、候选、超时、并发边界，禁止任意端点。</summary>
    public static string? Settings(RerankSettings s)
    {
        var path = EmbeddingValidation.ValidateSettings(new() { EndpointPath = s.EndpointPath });
        if (path is not null) return path;
        if (s.ModelRevision is null || s.ModelRevision.Length > 256 || s.MaxDocuments is < 1 or > 64
            || s.MaxDocumentCharacters is < 1 or > 16000 || s.MaxQueryCharacters is < 1 or > 4000
            || s.TimeoutSeconds is < 1 or > 300 || s.Concurrency is < 1 or > 4)
            return "重排序候选数、文本长度、超时或并发配置超出允许范围。";
        return null;
    }

    /// <summary>校验问题和完整候选长度；超限时拒绝而非隐式截断。</summary>
    public static string? Inputs(RerankSettings s, string query, IReadOnlyList<string>? documents)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > s.MaxQueryCharacters) return "重排序问题为空或超过模型配置的字符上限。";
        if (documents is null || documents.Count < 1 || documents.Count > s.MaxDocuments
            || documents.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > s.MaxDocumentCharacters))
            return "重排序候选数量或完整正文长度超过模型配置；未截断或发送。";
        return null;
    }
}
