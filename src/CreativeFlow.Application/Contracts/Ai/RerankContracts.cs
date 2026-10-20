namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>已保存的重排序参数；渠道继续拥有唯一的一份 Base URL 和加密凭据。</summary>
public sealed record RerankSettings
{
    /// <summary>相对渠道地址的固定路径，例如 rerank 或 v2/rerank。</summary>
    public string EndpointPath { get; init; } = "rerank";
    /// <summary>管理员声明的模型修订，不冒充已验证的权重指纹。</summary>
    public string ModelRevision { get; init; } = "";
    /// <summary>单次最多发送的候选数，范围 1–64。</summary>
    public int MaxDocuments { get; init; } = 64;
    /// <summary>单篇候选字符上限，超限会拒绝而非截断原文。</summary>
    public int MaxDocumentCharacters { get; init; } = 4096;
    /// <summary>检索问题字符上限。</summary>
    public int MaxQueryCharacters { get; init; } = 4000;
    /// <summary>一次调用含读取响应的总超时，范围 1–300 秒。</summary>
    public int TimeoutSeconds { get; init; } = 60;
    /// <summary>该渠道重排序调用的并发上限，范围 1–4。</summary>
    public int Concurrency { get; init; } = 1;
}

/// <summary>非敏感的不可变配置版本。</summary>
/// <param name="Settings">已保存参数或未保存的默认值。</param>
/// <param name="Version">不可变配置版本，未保存时为空。</param>
public sealed record RerankSettingsResponse(RerankSettings Settings, string? Version);
/// <summary>管理员显式测试已保存模型，不允许临时覆盖端点或密钥。</summary>
/// <param name="Query">管理员显式测试的问题。</param>
/// <param name="Documents">按输入顺序编号的测试候选。</param>
public sealed record RerankTestRequest(string Query, string[] Documents);
/// <summary>只返回原候选的编号及相关性，不信任服务商返回的正文。</summary>
/// <param name="Index">输入候选中的零基编号。</param>
/// <param name="Score">上游返回的有限相关性分数。</param>
public sealed record RerankScore(int Index, double Score);
/// <summary>显式测试的脱敏摘要；失败不会自动重试。</summary>
/// <param name="Ok">是否获得完整有效排名。</param>
/// <param name="Code">安全失败代码。</param>
/// <param name="Error">安全错误说明。</param>
/// <param name="StatusCode">已确认的 HTTP 状态。</param>
/// <param name="Results">候选编号与原始分数。</param>
/// <param name="ElapsedMilliseconds">本次测试耗时。</param>
/// <param name="ConfigurationVersion">实际使用的配置版本。</param>
/// <param name="TotalTokens">上游报告的 token 数。</param>
public sealed record RerankTestResponse(bool Ok, string? Code, string? Error, int? StatusCode,
    RerankScore[] Results, long ElapsedMilliseconds, string ConfigurationVersion, long? TotalTokens);
