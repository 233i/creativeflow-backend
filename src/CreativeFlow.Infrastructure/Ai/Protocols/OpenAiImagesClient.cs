using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 兼容 OpenAI 的图片生成接口（<c>/images/generations</c> 和 <c>/images/edits</c>）。
/// </summary>
/// <remarks>
/// 同时支持兼容 OpenAI 的中继和聚合商，因此基础 URL 取自供应商配置，而不是硬编码。
/// <para>
/// 使用 <see cref="JsonObject"/> 组织请求，并使用 <see cref="JsonDocument"/> 读取响应。
/// 两者都不依赖反射，因此兼容 AOT；若每个供应商使用一个 DTO，就必须注册到序列化上下文，
/// 且字段一旦移动便会失效。
/// </para>
/// </remarks>
internal sealed class OpenAiImagesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiImagesClient> logger,
    IClock clock) : IImageGenerationClient
{
    /// <summary>
    /// 通过 URL 获取单张图片的上限。字节内容交给素材存储前会整体缓冲，
    /// 因此无上限响应不仅是慢请求，还会放大内存占用。该值略高于 API 强制的 20 MB 上传上限。
    /// </summary>
    private const long MaxDownloadBytes = 25 * 1024 * 1024;
    /// <summary>允许继续采用上游素材地址的时间窗口。</summary>
    private static readonly TimeSpan SourceUrlReuseWindow = TimeSpan.FromMinutes(5);

    /// <summary>标识当前图片客户端使用 OpenAI Images 兼容协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiImages;

    /// <summary>有参考图时使用 multipart 编辑端点，否则使用 JSON 生成端点，并解析上游图片结果。</summary>
    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        // 提供参考图后会切换到编辑端点，该端点使用多部分表单而非 JSON；
        // 这是完全不同的请求结构，而不只是字段不同。
        var isEdit = request.ReferenceImages.Count > 0;

        using var message = isEdit
            ? BuildEditRequest(baseUrl, request)
            : BuildGenerateRequest(baseUrl, request);

        return await SendAsync(message, request, ct);
    }

    /// <summary>共享图片响应解析与安全下载规则；请求路径和正文由各自协议构造，不在失败后尝试其他端点。</summary>
    internal async Task<ImageGenerationResult> SendAsync(HttpRequestMessage message, ImageGenerationRequest request, CancellationToken ct)
    {
        // 不将 HTTP 失败当作可重试生成；只解析这一次响应，空正文也保留状态和请求路径。
        var client = httpClientFactory.CreateClient("upstream");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Image generation failed with {StatusCode} for model {Model}",
                    (int)response.StatusCode,
                    request.ModelKey);

                return ImageGenerationResult.Failure(
                    (int)response.StatusCode,
                    $"Image API returned HTTP {(int)response.StatusCode} ({message.Method} {message.RequestUri?.AbsolutePath}): "
                    + (string.IsNullOrWhiteSpace(body) ? "Upstream returned an empty error body." : ExtractError(body)));
            }

            var images = await ReadImagesAsync(client, body, request, ct);

            return images.Count > 0
                ? ImageGenerationResult.Success(images)
                : ImageGenerationResult.Failure(
                    null,
                    "Upstream returned no usable image data. If it replied with links, they may "
                    + "have expired or been unreachable from the server.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImageGenerationResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return ImageGenerationResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return ImageGenerationResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }

    /// <summary>构造 OpenAI 图片生成 JSON 请求。</summary>
    internal static HttpRequestMessage BuildGenerateRequest(
        string baseUrl,
        ImageGenerationRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count,
            ["output_format"] = ResolveOutputFormat(request.OutputFormat),
            ["response_format"] = ResolveResponseFormat(request.ResponseFormat),
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            body["size"] = request.Size;
        }

        if (!string.IsNullOrWhiteSpace(request.Quality) && request.Quality != "auto")
        {
            body["quality"] = request.Quality;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>构造含参考图与可选蒙版的 OpenAI 图片编辑 multipart 请求。</summary>
    internal static HttpRequestMessage BuildEditRequest(
        string baseUrl,
        ImageGenerationRequest request)
    {
        // 编辑端点接收 multipart/form-data；请求发送前不能释放 MultipartFormDataContent，
        // 其所有权会转交给 HttpRequestMessage。
        var content = new MultipartFormDataContent
        {
            { new StringContent(request.ModelKey), "model" },
            { new StringContent(request.Prompt), "prompt" },
            { new StringContent(request.Count.ToString()), "n" },
            { new StringContent(ResolveOutputFormat(request.OutputFormat)), "output_format" },
            { new StringContent(ResolveResponseFormat(request.ResponseFormat)), "response_format" },
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            content.Add(new StringContent(request.Size), "size");
        }

        // 编辑与生成一样遵守质量参数。若此处省略，接入参考图后质量控件就会失效。
        if (!string.IsNullOrWhiteSpace(request.Quality) && request.Quality != "auto")
        {
            content.Add(new StringContent(request.Quality), "quality");
        }

        for (var i = 0; i < request.ReferenceImages.Count; i++)
        {
            var reference = request.ReferenceImages[i];
            var part = new ByteArrayContent(reference.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(reference.MediaType);

            // API 使用 "image[]" 接收多张参考图；单张也可用 "image" 发送，
            // 而数组形式在两种情况下都能接受。
            content.Add(part, "image[]", $"reference-{i}{ExtensionFor(reference.MediaType)}");
        }

        if (request.Mask is { } mask)
        {
            var part = new ByteArrayContent(mask.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            // OpenAI 编辑协议为蒙版提供独立字段。若把它当作另一张图片，
            // 会在无提示的情况下把局部重绘变成整图变体。
            content.Add(part, "mask", "mask.png");
        }

        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/edits")
        {
            Content = content,
        };
    }

    /// <summary>将请求的编码规范化为 API 接受的值。</summary>
    /// <remarks>
    /// 使用白名单而非直接透传，使未识别值回退到 API 自身默认值，
    /// 而不是得到看似模型故障的 400 响应。
    /// </remarks>
    internal static string ResolveOutputFormat(string? requested) =>
        requested?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "jpeg",
            "webp" => "webp",
            _ => "png",
        };

    /// <summary>
    /// 将调用方期望的字节交付方式映射为 API 使用的名称。
    /// </summary>
    /// <remarks>
    /// 应用使用 <c>base64</c>，API 则称为 <c>b64_json</c>。默认为 <c>url</c>，
    /// 这是大多数兼容 OpenAI 的中继所返回的形式，传输成本也更低；无论哪种方式，
    /// 字节内容最终都会在服务器端获取。
    /// </remarks>
    internal static string ResolveResponseFormat(string? requested) =>
        requested?.Trim().ToLowerInvariant() switch
        {
            "base64" or "b64_json" or "b64json" => "b64_json",
            _ => "url",
        };

    /// <summary>将输出编码选项映射为对应图片 MIME 类型。</summary>
    private static string MediaTypeFor(string outputFormat) => outputFormat switch
    {
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>
    /// 从响应中读取生成图片。
    /// </summary>
    /// <remarks>
    /// 根据请求方式和供应商实际支持情况，每个条目可能内联携带字节（<c>b64_json</c>），
    /// 也可能提供链接（<c>url</c>）。两种形式都会处理；遇到链接时会在此下载，
    /// 确保持久字节进入素材存储。公开链接还会保留数分钟，以优化紧接其后的视频节点；
    /// 需要认证的供应商链接绝不会转发给其他供应商。
    /// </remarks>
    private async Task<List<GeneratedImage>> ReadImagesAsync(
        HttpClient client,
        string body,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // 用作未声明类型条目的预期响应类型。URL 下载会报告自身内容类型，优先级高于此值。
        var expected = MediaTypeFor(ResolveOutputFormat(request.OutputFormat));

        var images = new List<GeneratedImage>(data.GetArrayLength());

        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            if (ReadString(element, "b64_json") is { Length: > 0 } encoded)
            {
                if (Decode(encoded, expected) is { } inline) images.Add(inline);
                continue;
            }

            if (ReadString(element, "url") is not { Length: > 0 } url) continue;

            // 部分中继会用数据 URL 内联字节来响应 URL 请求。
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadDataUrl(url, expected) is { } embedded) images.Add(embedded);
                continue;
            }

            if (request.DeferDownloads && Uri.TryCreate(url, UriKind.Absolute, out var deferred)
                && deferred.Scheme is "http" or "https")
            {
                var authenticated = IsProviderHost(deferred, request.Provider.BaseUrl);
                images.Add(new GeneratedImage([], expected,
                    authenticated ? null : url,
                    authenticated ? null : clock.UtcNow.Add(SourceUrlReuseWindow), DownloadUrl: url));
                continue;
            }

            if (await DownloadAsync(client, url, request, expected, ct) is { } downloaded)
            {
                images.Add(downloaded);
            }
        }

        return images;
    }

    /// <summary>获取供应商以引用形式返回的图片。</summary>
    private async Task<GeneratedImage?> DownloadAsync(
        HttpClient client,
        string url,
        ImageGenerationRequest request,
        string expectedMediaType,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);

            // 密钥只发送到供应商自身主机。返回预签名存储链接的供应商会直接拒绝 Authorization 请求头，
            // 发送该请求头还会向第三方泄露密钥；而从自身 API 主机提供图片的中继则需要它。
            var requiresAuthorization = IsProviderHost(uri, request.Provider.BaseUrl);
            if (requiresAuthorization)
            {
                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", request.ApiKey);
            }

            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Downloading a generated image failed with {StatusCode}",
                    (int)response.StatusCode);

                return null;
            }

            // 读取前先检查，使超大图片只产生一次请求头往返成本，而不占用内存。
            // 由于 Content-Length 可选，读取后会再次检查。
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                logger.LogInformation("A generated image exceeded the download cap and was skipped.");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            if (bytes.Length == 0 || bytes.Length > MaxDownloadBytes)
            {
                return null;
            }

            var reported = response.Content.Headers.ContentType?.MediaType;

            // 从普通文件主机提供图片的供应商可能报告 application/octet-stream；
            // 此时请求的格式是更可靠的推测。
            return new GeneratedImage(
                bytes,
                reported is { Length: > 0 }
                && reported.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? reported
                    : expectedMediaType,
                requiresAuthorization ? null : uri.AbsoluteUri,
                requiresAuthorization ? null : clock.UtcNow.Add(SourceUrlReuseWindow));
        }
        catch (HttpRequestException exception)
        {
            // 单张无法下载的图片不应导致已成功下载的图片被丢弃。
            logger.LogWarning(exception, "Downloading a generated image failed.");
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Downloading a generated image timed out.");
            return null;
        }
    }

    /// <summary>判断下载目的是否属于允许携带供应商凭据的主机。</summary>
    private static bool IsProviderHost(Uri uri, string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var provider)
        && uri.Scheme == provider.Scheme && uri.Port == provider.Port
        && string.Equals(uri.Host, provider.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>从 <c>data:</c> URL 中读取负载，例如 <c>data:image/png;base64,iVBOR…</c>。</summary>
    private static GeneratedImage? ReadDataUrl(string url, string expectedMediaType)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return null;

        // "data:" 与逗号之间的全部内容：先是媒体类型，随后是各项参数。
        var header = url.AsSpan(5, comma - 5);

        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

        var semicolon = header.IndexOf(';');
        var declared = semicolon >= 0 ? header[..semicolon].Trim().ToString() : string.Empty;

        return Decode(
            url[(comma + 1)..],
            declared.Length > 0 ? declared : expectedMediaType);
    }

    /// <summary>将上游 Base64 图片解码为字节并按原规则处理无效数据。</summary>
    private static GeneratedImage? Decode(string base64, string mediaType)
    {
        try
        {
            return new GeneratedImage(Convert.FromBase64String(base64), mediaType);
        }
        catch (FormatException)
        {
            // 单个格式错误条目不应导致已成功解码的图片被丢弃。
            return null;
        }
    }

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>从错误正文中提取人类可读消息，无法提取时回退到原始文本。</summary>
    internal static string ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON，最可能是代理返回的 HTML 错误页。
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    /// <summary>将媒体类型映射为当前支持的文件扩展名，并保留未知类型的默认值。</summary>
    internal static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}
