using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// MiniMax H3 官方 V2 协议：/v2/video_generation 与 /v2/query/video_generation/{task_id}。
/// 与 /v1/videos 兼容协议独立，禁止遇到不支持的路由时降级成不带图片的请求。
/// </summary>
internal sealed class MiniMaxH3VideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<MiniMaxH3VideoClient> logger) : IVideoGenerationClient
{
    /// <summary>外部图片允许的最大字节数。</summary>
    private const int MaxImageBytes = 30 * 1024 * 1024;
    /// <summary>MiniMax H3 提交正文允许的最大字节数。</summary>
    private const int MaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>标识当前客户端使用 MiniMax-H3 原生 V2 视频协议。</summary>
    public ProviderType Protocol => ProviderType.MiniMaxH3;

    /// <summary>校验 H3 输入与请求体大小后提交原生 V2 视频任务，路由不支持时不降级为其他协议。</summary>
    public async Task<VideoSubmitResult> SubmitAsync(VideoGenerationRequest request, CancellationToken ct)
    {
        // H3 仅使用原生 V2 路由；404/405 直接保留失败说明，不能降级为丢失参考图片的其他请求。
        if (Validate(request) is { } error)
        {
            return VideoSubmitResult.Failure(400, error);
        }

        using var message = BuildSubmitRequest(request);
        if (message.Content!.Headers.ContentLength > MaxBodyBytes)
        {
            return VideoSubmitResult.Failure(400,
                "MiniMax-H3 request exceeds 64 MB. Use public HTTP(S) image URLs instead of inline images.");
        }
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        logger.LogInformation(
            "Submitting native MiniMax H3 model {Model} with {ReferenceImageCount} image(s); inputMode={InputMode}",
            request.ModelKey, request.ReferenceImages.Count, request.InputMode ?? "reference");

        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ReadError(text);
                if ((int)response.StatusCode is 404 or 405)
                {
                    detail = "MiniMax-H3 native protocol requires POST /v2/video_generation. "
                        + "This provider may not expose the official H3 route. " + detail;
                }
                return VideoSubmitResult.Failure((int)response.StatusCode, detail);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (StringProperty(root, "task_id") is { Length: > 0 } id)
            {
                return VideoSubmitResult.Success(id);
            }
            return VideoSubmitResult.Failure(null,
                ErrorProperty(root) ?? "MiniMax-H3 returned no task_id for the native V2 request.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoSubmitResult.Failure(null, exception.Message);
        }
    }

    /// <summary>按供应商协议构造视频提交请求及必要请求头。</summary>
    internal static HttpRequestMessage BuildSubmitRequest(VideoGenerationRequest request) => new(
        HttpMethod.Post, $"{BaseUrl(request.Provider)}/v2/video_generation")
    {
        Content = new StringContent(BuildRequestBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
    };

    /// <summary>按当前供应商协议组织生成参数，保留省略字段和缺省规则。</summary>
    internal static JsonObject BuildRequestBody(VideoGenerationRequest request)
    {
        var content = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.Prompt },
        };
        var firstLast = request.InputMode == "first-last";
        for (var index = 0; index < request.ReferenceImages.Count; index++)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = ImageUrl(request.ReferenceImages[index]) },
                ["role"] = firstLast ? index == 0 ? "first_frame" : "last_frame" : "reference_image",
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["content"] = content,
            ["resolution"] = Resolution(request.Resolution),
            ["duration"] = request.DurationSeconds ?? 5,
            // 首尾帧由输入图片决定比例；文生视频不能使用 adaptive。
            ["ratio"] = firstLast && request.ReferenceImages.Count > 0
                ? "adaptive"
                : request.Aspect?.Trim() ?? "16:9",
        };
        if (request.Watermark is { } watermark) body["aigc_watermark"] = watermark;
        return body;
    }

    /// <summary>通过原生 V2 查询已有视频任务，仅将临时上游错误保留为等待后续轮询。</summary>
    public async Task<VideoPollResult> PollAsync(
        AiProvider provider, string apiKey, string upstreamJobId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl(provider)}/v2/query/video_generation/{Uri.EscapeDataString(upstreamJobId)}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            var client = httpClientFactory.CreateClient("upstream");
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // 只有临时错误等待下一轮；鉴权和路由错误需立即显示给用户。
                return (int)response.StatusCode is 408 or 429 or >= 500
                    ? new VideoPollResult(VideoJobState.Running, null, null, null)
                    : Failed(ReadError(text));
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (Property(root, "task") is not { ValueKind: JsonValueKind.Object } task)
            {
                return Failed(ErrorProperty(root) ?? "MiniMax-H3 returned no task in its V2 status response.");
            }

            var status = StringProperty(task, "status");
            if (status == "succeeded")
            {
                var url = Property(task, "content") is { } output ? StringProperty(output, "url") : null;
                if (!IsHttpUrl(url)) return Failed("MiniMax-H3 completed without a valid video URL.");

                if (Property(task, "usage") is { } usage
                    && Property(usage, "input_image_count") is { ValueKind: JsonValueKind.Number } count
                    && count.TryGetInt32(out var imageCount))
                {
                    logger.LogInformation("MiniMax H3 task {TaskId} completed with {InputImageCount} input image(s)",
                        upstreamJobId, imageCount);
                }
                return new VideoPollResult(VideoJobState.Succeeded, 1, url, null);
            }

            return status switch
            {
                "queued" => new VideoPollResult(VideoJobState.Pending, null, null, null),
                "running" => new VideoPollResult(VideoJobState.Running, null, null, null),
                "failed" or "cancelled" => Failed(ErrorProperty(task) ?? $"MiniMax-H3 task {status}."),
                _ => Failed("MiniMax-H3 returned an unknown task status."),
            };
        }
        catch (JsonException)
        {
            return Failed("MiniMax-H3 returned an invalid V2 status response.");
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    /// <summary>按 MiniMax H3 原生协议检查模型、时长、分辨率及图片输入。</summary>
    private static string? Validate(VideoGenerationRequest request)
    {
        // H3 和 H3-Max 对时长、分辨率及参考图模式的限制不同，必须按实际模型与输入模式联合判断。
        if (string.IsNullOrWhiteSpace(request.Prompt)) return "MiniMax-H3 requires a non-empty prompt.";
        if (request.InputMode is not (null or "reference" or "first-last"))
            return "MiniMax-H3 input mode must be reference or first-last.";

        var firstLast = request.InputMode == "first-last";
        var maxModel = request.ModelKey.Equals("MiniMax-H3-Max", StringComparison.OrdinalIgnoreCase);
        var duration = request.DurationSeconds ?? 5;
        if (duration < (maxModel ? 5 : 4) || duration > 15)
            return $"MiniMax-H3 duration must be between {(maxModel ? 5 : 4)} and 15 seconds.";
        if (request.ReferenceImages.Count > (firstLast ? 2 : 9))
            return firstLast ? "MiniMax-H3 first-last mode accepts at most two images."
                : "MiniMax-H3 reference mode accepts at most nine images.";
        if (maxModel && !firstLast && request.ReferenceImages.Count > 0)
            return "MiniMax-H3-Max does not support reference images. Use first-last mode or MiniMax-H3.";

        var resolution = Resolution(request.Resolution);
        if (maxModel ? resolution is not ("480P" or "768P") : resolution is not ("768P" or "2K"))
            return maxModel ? "MiniMax-H3-Max resolution must be 480P or 768P."
                : "MiniMax-H3 resolution must be 768P or 2K (720p and 1080p in existing workflows).";
        var aspect = firstLast && request.ReferenceImages.Count > 0
            ? "adaptive" : request.Aspect?.Trim() ?? "16:9";
        if (aspect is not ("adaptive" or "21:9" or "16:9" or "4:3" or "1:1" or "3:4" or "9:16"))
            return "Unsupported MiniMax-H3 aspect ratio.";
        if (request.ReferenceImages.Count == 0 && aspect == "adaptive")
            return "MiniMax-H3 text-to-video requires an explicit aspect ratio, not adaptive.";

        foreach (var image in request.ReferenceImages)
        {
            if (image.Bytes.Length > MaxImageBytes) return "MiniMax-H3 images must be at most 30 MB each.";
            if (IsHttpUrl(image.Url) || IsHttpUrl(image.FallbackUrl)) continue;
            if (image.Bytes.Length == 0) return "MiniMax-H3 requires image bytes or a public HTTP(S) image URL.";
            if (image.MediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/heic" or "image/heif"))
                return "Unsupported MiniMax-H3 inline image format. Use PNG, JPEG, WEBP, HEIC or HEIF.";
        }
        return null;
    }

    /// <summary>将视频分辨率选项映射为 MiniMax H3 接受的取值。</summary>
    private static string? Resolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "720p" or "768p" => "768P",
        "1080p" or "2k" => "2K",
        "480p" => "480P",
        _ => value,
    };

    /// <summary>优先采用可用的 HTTP(S) 图片地址，否则将现有字节编码为 data URL。</summary>
    private static string ImageUrl(ReferenceImage image) => IsHttpUrl(image.Url) ? image.Url!
        : IsHttpUrl(image.FallbackUrl) ? image.FallbackUrl!
        : $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";

    /// <summary>规范化 MiniMax H3 原生协议使用的基础地址。</summary>
    private static string BaseUrl(AiProvider provider) =>
        AiProvider.NormalizeBaseUrl(provider.BaseUrl, ProviderType.MiniMaxH3);

    /// <summary>判断来源是否为绝对 HTTP(S) 地址。</summary>
    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && !uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>按当前协议的既有规则查找 JSON 字段。</summary>
    private static JsonElement? Property(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;

    /// <summary>从协议响应中读取可用的字符串字段。</summary>
    private static string? StringProperty(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>从当前协议响应字段提取错误信息。</summary>
    private static string? ErrorProperty(JsonElement root) => Property(root, "error") is { } error
        ? error.ValueKind == JsonValueKind.String ? error.GetString() : StringProperty(error, "message")
        : null;

    /// <summary>读取供应商错误详情并保留既有有界回退文本。</summary>
    private static string ReadError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return ErrorProperty(document.RootElement) ?? StringProperty(document.RootElement, "message")
                ?? "MiniMax-H3 request failed.";
        }
        catch (JsonException) { return "MiniMax-H3 returned a non-JSON error response."; }
    }

    /// <summary>构造当前认证、执行或协议处理的失败结果。</summary>
    private static VideoPollResult Failed(string error) => new(VideoJobState.Failed, null, null, error);
}
