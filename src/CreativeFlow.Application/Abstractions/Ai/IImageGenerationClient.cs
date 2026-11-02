using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 通过一种供应商协议生成图片。
/// </summary>
/// <remarks>
/// 每种 <see cref="ProviderType"/> 对应一个实现；<c>IImageGenerationDispatcher</c> 根据解析出的协议
/// 选择实现。新增供应商意味着新增实现，而不是修改端点中的分支语句。
/// </remarks>
public interface IImageGenerationClient
{
    /// <summary>该客户端处理的协议。</summary>
    ProviderType Protocol { get; }

    /// <summary>通过本适配器的协议完成一次图片生成。</summary>
    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct);
}

/// <param name="ReferenceImages">
/// 用作生成条件的现有图片。始终保留原始字节，以支持多部分表单和数据 URL 协议；
/// 可选的签名 HTTP(S) URL 允许限制更严格的视频供应商获取同一素材。
/// </param>
/// <param name="Mask">
/// 可选的 PNG 编辑蒙版。透明像素会被替换；仅带显式蒙版部分的协议支持它。
/// 它与 <paramref name="ReferenceImages"/> 分开，因为上游多部分表单为其分配了不同语义的字段。
/// </param>
/// <param name="Count">要生成的图片数量。调用方会将其限制在 1～4。</param>
/// <param name="OutputFormat">
/// 请求供应商使用的编码——<c>png</c>、<c>jpeg</c> 或 <c>webp</c>。此处不做规范化：
/// 哪些拼写合法由协议决定，没有对应参数的协议会忽略它。
/// </param>
/// <param name="ResponseFormat">
/// 图片的返回方式：<c>url</c> 或 <c>base64</c>。这是应用层词汇，协议客户端会将其映射为供应商使用的名称。
/// </param>
/// <param name="Provider">当前使用的渠道或身份提供方配置。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="Size">图片尺寸选项，由具体协议解释。</param>
/// <param name="Quality">图片生成质量选项，由具体协议解释。</param>
/// <param name="Seed">上游生成使用的随机种子选项。</param>
/// <param name="DeferDownloads">返回上游输出供后台先保存检查点，不在适配器内下载。</param>
public sealed record ImageGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    ReferenceImage? Mask,
    string? Size,
    string? Quality,
    string? OutputFormat,
    string? ResponseFormat,
    int Count,
    int? Seed,
    bool DeferDownloads = false);

/// <param name="Url">
/// 对拒绝内联数据的协议，优先使用此公开 HTTP(S) 来源。多部分表单和数据 URL 协议仍可使用字节内容。
/// </param>
/// <param name="FallbackUrl">
/// 当供应商的短期 URL 过期时，用于一次安全重试的 CreativeFlow 签名 URL。
/// </param>
/// <param name="Bytes">原始二进制内容。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
public sealed record ReferenceImage(
    byte[] Bytes,
    string MediaType,
    string? Url = null,
    string? FallbackUrl = null);

/// <param name="Bytes">原始二进制内容。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="SourceUrl">上游返回的原始素材地址，保留供后续来源选择使用。</param>
/// <param name="SourceUrlExpiresAt">上游原始素材地址的已知失效时间。</param>
/// <param name="DownloadUrl">延迟下载时仅供后台访问的地址，不作为公开素材来源。</param>
public sealed record GeneratedImage(
    byte[] Bytes,
    string MediaType,
    string? SourceUrl = null,
    DateTimeOffset? SourceUrlExpiresAt = null,
    string? DownloadUrl = null);

/// <param name="Ok">当前调用是否成功。</param>
/// <param name="Images">图片内容或引用地址集合，元素类型由所属契约定义。</param>
/// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record ImageGenerationResult(
    bool Ok,
    IReadOnlyList<GeneratedImage> Images,
    int? StatusCode,
    string? Error)
{
    /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
    public static ImageGenerationResult Success(IReadOnlyList<GeneratedImage> images) =>
        new(true, images, 200, null);

    /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
    public static ImageGenerationResult Failure(int? statusCode, string error) =>
        new(false, [], statusCode, string.IsNullOrWhiteSpace(error)
            ? statusCode is { } code ? $"Image API returned HTTP {code} with an empty error body." : "Image generation failed without error details."
            : error);
}

/// <summary>将请求路由到支持解析后协议的客户端。</summary>
public interface IImageGenerationDispatcher
{
    /// <summary>选择指定协议的图片客户端并执行生成。</summary>
    Task<ImageGenerationResult> GenerateAsync(
        ProviderType protocol,
        ImageGenerationRequest request,
        CancellationToken ct);
}
