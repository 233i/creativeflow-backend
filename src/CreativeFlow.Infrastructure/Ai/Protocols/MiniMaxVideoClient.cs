using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 通过 New API 的 <c>/videos</c> 端点调用 MiniMax 视频模型。
/// </summary>
/// <remarks>
/// New API 的 Hailuo 适配器从 <c>metadata.first_frame_image</c> 构造 MiniMax-H3
/// <c>/v2/video_generation</c> 所需的多模态 <c>content</c>。同时发送字符串形式的
/// <c>input_reference</c>，兼容尚未升级 H3 插件、但能识别 OpenAI 视频参考图字段的中转。
/// </remarks>
internal sealed class MiniMaxVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<MiniMaxVideoClient> logger) : IVideoGenerationClient
{
    /// <summary>标识当前客户端使用 MiniMax 的 Videos 兼容请求格式。</summary>
    public ProviderType Protocol => ProviderType.MiniMaxVideo;

    /// <summary>校验参考首图的可访问 HTTP(S) 地址，按 MiniMax 兼容格式提交视频并保留原地址回退条件。</summary>
    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        var firstFrame = request.ReferenceImages.FirstOrDefault();
        var useFallbackInitially = firstFrame is not null
            && !IsHttpUrl(firstFrame.Url)
            && IsHttpUrl(firstFrame.FallbackUrl);

        if (firstFrame is not null
            && !IsHttpUrl(firstFrame.Url)
            && !IsHttpUrl(firstFrame.FallbackUrl))
        {
            return VideoSubmitResult.Failure(
                400,
                "MiniMax-H3 first-frame generation requires a public HTTP(S) image URL. "
                + "Configure Authentication:PublicOrigin to an externally reachable address.");
        }

        var submitted = await SubmitOnceAsync(request, useFallbackInitially, ct);
        if (useFallbackInitially
            || firstFrame is null
            || !CanRetryWithFallback(submitted, firstFrame))
        {
            return submitted;
        }

        return await SubmitOnceAsync(request, useFallbackUrl: true, ct);
    }

    /// <summary>向供应商执行一次已构造的提交，不在该方法内新增重试。</summary>
    private async Task<VideoSubmitResult> SubmitOnceAsync(
        VideoGenerationRequest request,
        bool useFallbackUrl,
        CancellationToken ct)
    {
        // 本方法只构造并发送单次提交，是否改用备用媒体地址由外层回退策略决定。
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        using var message = BuildSubmitRequest(baseUrl, request, useFallbackUrl);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        logger.LogInformation(
            "Submitting MiniMax video model {Model} with {ReferenceImageCount} reference image(s); "
            + "transport={Transport}; fallback={UseFallbackUrl}",
            request.ModelKey,
            request.ReferenceImages.Count,
            request.ReferenceImages.Count > 0 ? "json-h3-http-first-frame" : "json-h3",
            useFallbackUrl);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "MiniMax video submit failed with {StatusCode}", (int)response.StatusCode);
                return VideoSubmitResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var id = document.RootElement.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            return string.IsNullOrEmpty(id)
                ? VideoSubmitResult.Failure(null, "Upstream returned no job id.")
                : VideoSubmitResult.Success(id);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoSubmitResult.Failure(null, exception.Message);
        }
    }

    /// <summary>按供应商协议构造视频提交请求及必要请求头。</summary>
    internal static HttpRequestMessage BuildSubmitRequest(
        string baseUrl,
        VideoGenerationRequest request,
        bool useFallbackUrl = false)
    {
        var body = BuildRequestBody(request, useFallbackUrl);
        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/videos")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>按当前供应商协议组织生成参数，保留省略字段和缺省规则。</summary>
    internal static JsonObject BuildRequestBody(
        VideoGenerationRequest request,
        bool useFallbackUrl = false)
    {
        var metadata = new JsonObject
        {
            ["resolution"] = H3Resolution(request.Resolution),
        };

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["metadata"] = metadata,
        };

        if (request.DurationSeconds is { } seconds)
        {
            body["duration"] = seconds;
        }

        // 当前产品语义是“首帧参考图”：多条图片连线时仍只取遍历顺序中的第一张。
        // Westmoon 链路会过滤 data: URL，继而把 adaptive 请求误判成文生视频，因此这里
        // 使用可公开获取的 HTTPS URL。input_reference 是旧中转的兼容字段，metadata 字段
        // 则由新版 Hailuo 插件转换为 H3 content 中 role=first_frame 的 image_url 条目。
        if (request.ReferenceImages.FirstOrDefault() is { } firstFrame)
        {
            var firstFrameUrl = SelectHttpUrl(firstFrame, useFallbackUrl);
            body["input_reference"] = firstFrameUrl;
            metadata["first_frame_image"] = firstFrameUrl;
            metadata["ratio"] = "adaptive";
        }
        else
        {
            metadata["ratio"] = H3Ratio(request.Aspect);
        }

        return body;
    }

    /// <summary>查询 MiniMax 兼容 Videos 任务并区分轮询错误与最终失败，不重新生成视频。</summary>
    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        // 轮询错误与最终失败按当前协议分别处理，读取任务状态不能重新提交生成请求。
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = provider.BaseUrl.TrimEnd('/');

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/videos/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new VideoPollResult(VideoJobState.Running, null, null, null);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var progress = root.TryGetProperty("progress", out var progressElement)
                && progressElement.ValueKind == JsonValueKind.Number
                    ? progressElement.GetDouble() / 100
                    : (double?)null;

            return status switch
            {
                "completed" => new VideoPollResult(
                    VideoJobState.Succeeded, 1, $"{baseUrl}/videos/{upstreamJobId}/content", null),
                "failed" or "cancelled" => new VideoPollResult(
                    VideoJobState.Failed, null, null, ReadError(root) ?? "Generation failed."),
                "in_progress" or "processing" => new VideoPollResult(
                    VideoJobState.Running, progress, null, null),
                _ => new VideoPollResult(VideoJobState.Pending, progress, null, null),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    /// <summary>将本地视频分辨率选项转换为 H3 参数。</summary>
    private static string H3Resolution(string? resolution) =>
        resolution?.Trim().ToLowerInvariant() switch
        {
            "1080p" or "2k" => "2K",
            _ => "768P",
        };

    /// <summary>将本地宽高比选项转换为 H3 参数。</summary>
    private static string H3Ratio(string? aspect) => aspect?.Trim() switch
    {
        "21:9" or "16:9" or "4:3" or "1:1" or "3:4" or "9:16" => aspect,
        _ => "16:9",
    };

    /// <summary>读取供应商错误详情并保留既有有界回退文本。</summary>
    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;

    /// <summary>按既有优先级选择可供上游读取的公开媒体地址。</summary>
    private static string SelectHttpUrl(ReferenceImage image, bool useFallbackUrl) =>
        useFallbackUrl && IsHttpUrl(image.FallbackUrl)
            ? image.FallbackUrl!
            : IsHttpUrl(image.Url)
                ? image.Url!
                : image.FallbackUrl!;

    /// <summary>仅当现有失败条件满足时允许切换已验证的备用来源。</summary>
    private static bool CanRetryWithFallback(
        VideoSubmitResult result,
        ReferenceImage image) =>
        result is { Ok: false, StatusCode: 400 or 422 }
        && IsHttpUrl(image.FallbackUrl)
        && !string.Equals(image.Url, image.FallbackUrl, StringComparison.Ordinal);

    /// <summary>判断来源是否为绝对 HTTP(S) 地址。</summary>
    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
