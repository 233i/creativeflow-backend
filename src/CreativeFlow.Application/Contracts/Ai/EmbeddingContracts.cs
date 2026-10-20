namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>一个向量模型的非敏感请求配置；URL 和密钥继续由所属渠道维护。</summary>
public sealed record EmbeddingSettings
{
    /// <summary>相对渠道 Base URL 的固定路径，不接受绝对地址、查询或路径穿越。</summary>
    public string EndpointPath { get; init; } = "embeddings";
    /// <summary>预期响应维度；空值表示首次测试发现，不会自行裁剪向量。</summary>
    public int? ExpectedDimensions { get; init; }
    /// <summary>显式发给上游的 dimensions 参数；仅在服务确认支持时填写。</summary>
    public int? RequestedDimensions { get; init; }
    /// <summary>管理员标记的模型版本或文件摘要；不冒充上游可验证的模型指纹。</summary>
    public string ModelRevision { get; init; } = "";
    /// <summary>文档向量化时逐条添加的前缀，空值不添加。</summary>
    public string DocumentPrefix { get; init; } = "";
    /// <summary>查询向量化时逐条添加的前缀，空值不添加。</summary>
    public string QueryPrefix { get; init; } = "";
    /// <summary>一次请求的最大文本条数，范围 1–32。</summary>
    public int MaxBatchSize { get; init; } = 16;
    /// <summary>每条文本包含前缀后的字符上限，不等同于 token 上限。</summary>
    public int MaxInputCharacters { get; init; } = 12000;
    /// <summary>请求与响应读取的总超时秒数，范围 1–600。</summary>
    public int TimeoutSeconds { get; init; } = 120;
    /// <summary>该模型调用时可占用的渠道并发上限，范围 1–4。</summary>
    public int Concurrency { get; init; } = 1;
}

/// <summary>返回已保存的不可变配置版本；未保存时 Version 为空。</summary>
/// <param name="Settings">向量化参数，不包含凭据。</param>
/// <param name="Version">已保存配置的 UUID，空值表示默认草稿。</param>
public sealed record EmbeddingSettingsResponse(EmbeddingSettings Settings, string? Version);

/// <summary>显式测试已保存的模型配置，不接受 URL、密钥或临时配置覆盖。</summary>
/// <param name="Inputs">测试文本数组，仅发送给所选模型。</param>
/// <param name="Purpose">document 或 query，决定采用哪组前缀。</param>
public sealed record EmbeddingTestRequest(string[] Inputs, string Purpose = "document");

/// <summary>返回向量化测试摘要；不向浏览器暴露向量数组、密钥或上游原文。</summary>
/// <param name="Ok">响应是否通过向量协议校验。</param>
/// <param name="Code">可机器识别的失败类别。</param>
/// <param name="Error">不包含请求文本和密钥的错误说明。</param>
/// <param name="StatusCode">实际上游状态码，发送前失败时为空。</param>
/// <param name="Dimensions">成功响应的向量维度。</param>
/// <param name="Count">通过校验的向量条数。</param>
/// <param name="ElapsedMilliseconds">本次调用耗时，包括读取和校验。</param>
/// <param name="ConfigurationFingerprint">配置摘要，不等同于已验证的模型权重身份。</param>
/// <param name="ConfigurationVersion">本次采用的配置版本。</param>
/// <param name="TotalTokens">上游明确提供的 token 用量，未知时为空。</param>
public sealed record EmbeddingTestResponse(
    bool Ok, string? Code, string? Error, int? StatusCode, int? Dimensions,
    int Count, long ElapsedMilliseconds, string? ConfigurationFingerprint,
    string ConfigurationVersion, long? TotalTokens);
