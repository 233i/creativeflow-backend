using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>通过明确的向量协议调用模型；实现不得自动重试未知结果。</summary>
public interface IEmbeddingClient
{
    /// <summary>适配器实际支持的向量协议。</summary>
    ProviderType Protocol { get; }
    /// <summary>发送一个有界批次并恢复输入顺序，任何条目异常都拒绝整个批次。</summary>
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

/// <summary>向量化唯一分派入口，负责参数、协议和并发限制。</summary>
public interface IEmbeddingDispatcher
{
    /// <summary>仅调用所选客户端，不回退聊天或其他云模型。</summary>
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

/// <summary>保留业务标识和原始文本，使响应可以按稳定 ID 恢复关联。</summary>
/// <param name="Id">批次内唯一输入标识。</param>
/// <param name="Text">要向量化的文本。</param>
public sealed record EmbeddingInput(string Id, string Text);

/// <summary>已经由应用解析资源和密钥的向量批次，不直接用于 HTTP 入参。</summary>
/// <param name="Provider">渠道快照及其 Base URL。</param>
/// <param name="Model">明确选择的向量模型。</param>
/// <param name="ApiKey">临时解密的凭据，不记录或持久化。</param>
/// <param name="Settings">经过校验的参数快照。</param>
/// <param name="Inputs">批次输入及稳定标识。</param>
/// <param name="Purpose">document 或 query。</param>
public sealed record EmbeddingRequest(AiProvider Provider, AiModel Model, string ApiKey,
    EmbeddingSettings Settings, IReadOnlyList<EmbeddingInput> Inputs, string Purpose);

/// <summary>内部向量结果，不作为公开 API 输出。</summary>
/// <param name="Id">对应原输入的稳定标识。</param>
/// <param name="Values">全部为有限数值且维度一致的向量。</param>
public sealed record EmbeddingVector(string Id, float[] Values);

/// <summary>完整验证后的批次结果或明确失败，不交付部分有效向量。</summary>
/// <param name="Ok">整个批次是否成功。</param>
/// <param name="Vectors">成功时按输入顺序排列；失败时为空。</param>
/// <param name="Dimensions">实际向量维度，失败时为空。</param>
/// <param name="ConfigurationFingerprint">模型配置摘要，不保证模型权重没有漂移。</param>
/// <param name="TotalTokens">上游报告的用量，未知时为空。</param>
/// <param name="StatusCode">上游状态码，发送前错误为空。</param>
/// <param name="Code">稳定错误类别。</param>
/// <param name="Error">已脱敏的错误提示。</param>
public sealed record EmbeddingResult(bool Ok, IReadOnlyList<EmbeddingVector> Vectors, int? Dimensions,
    string? ConfigurationFingerprint, long? TotalTokens, int? StatusCode, string? Code, string? Error)
{
    /// <summary>构造不包含向量和上游正文的失败结果。</summary>
    public static EmbeddingResult Failure(string code, string error, int? statusCode = null) =>
        new(false, [], null, null, null, statusCode, code, error);
}
