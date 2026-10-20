using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 通过一种供应商协议生成文本。
/// </summary>
/// <remarks>
/// 默认非流式；外部工作流可显式提供文本接收器，同时保留最终完整结果与上游收据。
/// </remarks>
public interface ITextGenerationClient
{
    /// <summary>适配器实现的上游协议。</summary>
    ProviderType Protocol { get; }

    /// <summary>通过本适配器的协议完成非流式文本生成。</summary>
    Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken ct);
}

/// <param name="SystemPrompt">
/// 不属于用户原文的引导内容——改写功能用它描述转换要求，而不会污染正在转换的提示词。
/// </param>
/// <param name="ReferenceImages">
/// 传给支持视觉的模型，以便根据图片编写提示词。协议不包含图片部分的客户端会忽略它。
/// </param>
/// <param name="Provider">当前使用的渠道或身份提供方配置。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="Temperature">文本采样温度选项，由具体协议解释。</param>
public sealed record TextGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    string? SystemPrompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    int? MaxOutputTokens,
    double? Temperature)
{
    /// <summary>显式原生结构化输出格式；未配置时不改变既有文本请求。</summary>
    public System.Text.Json.Nodes.JsonObject? ResponseFormat { get; init; }

    /// <summary>请求使用的命名 HTTP 客户端；默认沿用普通上游通道，工作流会改用受限通道。</summary>
    public string HttpClientName { get; init; } = "upstream";

    /// <summary>显式启用上游流式正文；不包含思考内容，回调失败不自动重发付费请求。</summary>
    public Func<string, CancellationToken, Task>? OnTextDelta { get; init; }
}

/// <param name="Ok">当前调用是否成功。</param>
/// <param name="Text">文本内容或本次流式文本片段。</param>
/// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record TextGenerationResult(bool Ok, string? Text, int? StatusCode, string? Error)
{
    /// <summary>真实收到的 HTTP 状态，独立于旧业务错误映射，供工作流调用收据使用。</summary>
    public int? ResponseStatusCode { get; init; }

    /// <summary>上游实际报告的用量；未返回时保持 null，不能估算成零。</summary>
    public System.Text.Json.Nodes.JsonObject? Usage { get; init; }

    /// <summary>上游请求标识，仅用于排查，不包含认证信息。</summary>
    public string? UpstreamRequestId { get; init; }

    /// <summary>上游结束原因，例如 stop、length 或 content_filter。</summary>
    public string? FinishReason { get; init; }

    /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
    public static TextGenerationResult Success(string text) => new(true, text, 200, null);

    /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
    public static TextGenerationResult Failure(int? statusCode, string error) =>
        new(false, null, statusCode, error);
}

/// <summary>将请求路由到支持解析后协议的客户端。</summary>
public interface ITextGenerationDispatcher
{
    /// <summary>选择指定协议的文本客户端并执行生成。</summary>
    Task<TextGenerationResult> GenerateAsync(
        ProviderType protocol,
        TextGenerationRequest request,
        CancellationToken ct);
}
