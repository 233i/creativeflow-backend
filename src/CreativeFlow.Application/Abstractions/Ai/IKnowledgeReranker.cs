using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>捕获和调用固定重排序配置；密钥只在真正发送前由基础设施读取。</summary>
public interface IKnowledgeReranker
{
    /// <summary>冻结可用模型身份、端点及配置版本，不保存明文凭据或触发推理。</summary>
    Task<string> CaptureAsync(Guid modelId, CancellationToken ct);
    /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
    Task<RerankResult> RankAsync(string snapshot, string query, IReadOnlyList<string> documents, CancellationToken ct, string? nodeId = null);
}

/// <summary>已经解析和复验归属的单次重排序请求，不允许客户端构造网络地址。</summary>
/// <param name="Provider">已解析的服务商及协议。</param>
/// <param name="Model">用途为重排的模型。</param>
/// <param name="ApiKey">仅本次请求使用的解密密钥，不写入收据。</param>
/// <param name="Settings">已保存的有界参数。</param>
/// <param name="Query">用于排序的原始问题或检索词。</param>
/// <param name="Documents">按稳定编号排列的完整候选正文。</param>
public sealed record RerankRequest(AiProvider Provider, AiModel Model, string ApiKey,
    RerankSettings Settings, string Query, IReadOnlyList<string> Documents);

/// <summary>无自动重试的重排序传输端口。</summary>
public interface IRerankClient
{
    /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
    Task<RerankResult> RankAsync(RerankRequest request, CancellationToken ct);
}

/// <summary>一次调用的明确结果；只返回候选编号，不信任上游正文。</summary>
/// <param name="Ok">是否获得完整有效排名。</param>
/// <param name="Scores">对应输入编号的原始分数。</param>
/// <param name="Code">安全失败代码。</param>
/// <param name="Error">安全错误说明。</param>
/// <param name="StatusCode">确认的上游状态。</param>
/// <param name="TotalTokens">上游报告的 token 数。</param>
public sealed record RerankResult(bool Ok, RerankScore[] Scores, string? Code, string? Error,
    int? StatusCode, long? TotalTokens)
{
    /// <summary>构造失败收据，保留安全原因和已确认的上游状态。</summary>
    public static RerankResult Failure(string code, string error, int? status = null) => new(false, [], code, error, status, null);
}
