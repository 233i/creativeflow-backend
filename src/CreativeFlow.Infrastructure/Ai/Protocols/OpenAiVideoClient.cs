using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI 风格的视频生成接口（例如兼容 Sora 的端点）。
/// </summary>
/// <remarks>
/// 参数集比 Seedance 小得多：只有时长、画面比例和分辨率。发送帧率、种子或水印会被拒绝，
/// 因此这里会丢弃这些参数而不向上传递；节点界面已经隐藏它们，此处作为最后防线。
/// </remarks>
internal sealed class OpenAiVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiVideoClient> logger) : IVideoGenerationClient
{
    /// <summary>标识当前视频客户端使用 OpenAI Videos 兼容协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiVideo;

    /// <summary>按渠道和模型兼容规则构造视频请求，校验首尾帧能力并仅在原条件下尝试参考媒体地址回退。</summary>
    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        // 参考媒体回退仅在当前判定条件成立时发生，不能把任意提交失败都重试为新的付费任务。
        var usesHttpReferences = request.Provider.BuiltinId == "routin-ai"
            || IsSeedanceCompatibilityModel(request.ModelKey);

        // 只有 Seedance 兼容请求会传递帧角色。不能把不支持的首尾帧请求降级成普通参考图。
        if (IsFirstLastMode(request.InputMode)
            && (request.Provider.BuiltinId == "routin-ai" || !IsSeedanceCompatibilityModel(request.ModelKey)))
        {
            return VideoSubmitResult.Failure(400,
                "This video route does not support first-last mode. Select a Seedance-compatible route or use reference mode.");
        }

        if (IsSeedanceCompatibilityModel(request.ModelKey)
            && IsFirstLastMode(request.InputMode)
            && request.ReferenceImages.Count > 2)
        {
            return VideoSubmitResult.Failure(
                400, "first-last mode accepts at most two connected images");
        }

        if (usesHttpReferences
            && request.ReferenceImages.Any(image =>
                !IsHttpUrl(image.Url) && !IsHttpUrl(image.FallbackUrl)))
        {
            return VideoSubmitResult.Failure(
                400,
                "This video model requires reference images to use a public HTTP(S) " +
                "URL. Configure Authentication:PublicOrigin to an externally reachable address.");
        }

        var useFallbackInitially = usesHttpReferences
            && request.ReferenceImages.Any(image => !IsHttpUrl(image.Url));
        var submitted = await SubmitOnceAsync(request, useFallbackInitially, ct);
        if (!usesHttpReferences
            || useFallbackInitially
            || !CanRetryWithFallback(submitted, request.ReferenceImages))
        {
            return submitted;
        }

        return await SubmitOnceAsync(request, useFallbackUrls: true, ct);
    }

    /// <summary>向供应商执行一次已构造的提交，不在该方法内新增重试。</summary>
    private async Task<VideoSubmitResult> SubmitOnceAsync(
        VideoGenerationRequest request,
        bool useFallbackUrls,
        CancellationToken ct)
    {
        // 普通兼容接口与特殊模型的请求表示不同；单次提交按现有分支构造正文，不修改外层重试次数。
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        var body = BuildRequestBody(request, useFallbackUrls);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/videos")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Video submit failed with {StatusCode}", (int)response.StatusCode);
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

    /// <summary>查询已有 Videos 任务并转换状态及成品地址，不创建新的生成任务。</summary>
    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
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
                // 视为仍在运行，避免短暂故障让正常任务失败；过期任务扫描会限制无响应任务的存续时间。
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
                // 内容从子资源获取，而不是内联返回。
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

    /// <summary>按当前供应商协议组织生成参数，保留省略字段和缺省规则。</summary>
    internal static JsonObject BuildRequestBody(
        VideoGenerationRequest request,
        bool useFallbackUrls = false)
    {
        // 按当前输入模式选择视频与图片字段，并只写入协议支持的可选参数，保持字段缺省语义。
        if (request.Provider.BuiltinId == "routin-ai")
        {
            var content = new JsonArray();
            foreach (var image in request.ReferenceImages)
            {
                content.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image_url",
                    ["role"] = "reference_image",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = SelectHttpUrl(image, useFallbackUrls),
                    },
                });
            }

            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                content.Add((JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.Prompt,
                });
            }

            var routinBody = new JsonObject
            {
                ["model"] = request.ModelKey,
                ["prompt"] = request.Prompt,
                ["content"] = content,
            };

            if (request.Aspect is not null) routinBody["ratio"] = request.Aspect;
            var resolution = FixedResolution(request.ModelKey) ?? request.Resolution;
            if (resolution is not null) routinBody["resolution"] = resolution;
            if (request.DurationSeconds is { } duration) routinBody["duration"] = duration;

            return routinBody;
        }

        if (IsSeedanceCompatibilityModel(request.ModelKey))
        {
            return BuildSeedanceCompatibilityBody(request, useFallbackUrls);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
        };

        if (request.DurationSeconds is { } seconds) body["seconds"] = seconds.ToString();
        if (SizeFor(request.ModelKey, request.Aspect, request.Resolution) is { } size)
        {
            body["size"] = size;
        }

        return body;
    }

    /// <summary>
    /// 通过 New API 的 OpenAI <c>/videos</c> 兼容路由提交 Seedance。
    /// </summary>
    /// <remarks>
    /// New API 会从 <c>metadata.content</c> 还原火山方舟原生请求；顶层
    /// <c>input_reference</c> 用于让兼容层在提交前将任务识别为图生视频。
    /// 参考图不能只放在顶层，否则中转在构造上游请求时会丢失它。
    /// </remarks>
    private static JsonObject BuildSeedanceCompatibilityBody(
        VideoGenerationRequest request,
        bool useFallbackUrls)
    {
        // Seedance 兼容入口沿用其特有的 content 与媒体角色字段，不能套用普通视频接口的字段布局。
        var content = new JsonArray();
        var firstLastMode = IsFirstLastMode(request.InputMode);

        for (var i = 0; i < request.ReferenceImages.Count; i++)
        {
            var reference = request.ReferenceImages[i];
            content.Add((JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["role"] = firstLastMode
                    ? i == 0 ? "first_frame" : "last_frame"
                    : "reference_image",
                ["image_url"] = new JsonObject
                {
                    ["url"] = SelectHttpUrl(reference, useFallbackUrls),
                },
            });
        }

        var metadata = new JsonObject
        {
            ["content"] = content,
        };
        if (request.Aspect is { } aspect) metadata["ratio"] = aspect;
        if (request.Resolution is { } resolution) metadata["resolution"] = resolution;
        if (request.Seed is { } seed) metadata["seed"] = seed;
        if (request.Watermark is { } watermark) metadata["watermark"] = watermark;
        if (request.GenerateAudio is { } audio) metadata["generate_audio"] = audio;

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["metadata"] = metadata,
        };

        if (request.DurationSeconds is { } seconds) body["seconds"] = seconds.ToString();
        if (SizeFor(request.ModelKey, request.Aspect, request.Resolution) is { } size)
        {
            body["size"] = size;
        }

        if (request.ReferenceImages.FirstOrDefault() is { } firstReference)
        {
            body["input_reference"] = SelectHttpUrl(firstReference, useFallbackUrls);
        }

        return body;
    }

    /// <summary>
    /// 将画面比例和分辨率映射为 API 接受的离散尺寸。
    /// </summary>
    /// <remarks>
    /// Sora 接受像素尺寸而非比例，并拒绝支持集合之外的任何值。
    /// </remarks>
    internal static string? SizeFor(string modelKey, string? aspect, string? resolution)
    {
        if (aspect is null) return null;

        var tall = aspect is "9:16" or "3:4" or "2:3";
        var fixedResolution = FixedResolution(modelKey);

        return (fixedResolution ?? resolution) switch
        {
            "1080p" => tall ? "1080x1920" : "1920x1080",
            "480p" => tall ? "480x854" : "854x480",
            _ => tall ? "720x1280" : "1280x720",
        };
    }

    /// <summary>按既有模型兼容规则选取固定视频分辨率。</summary>
    private static string? FixedResolution(string modelKey)
    {
        var normalized = modelKey.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("sd-mini-", StringComparison.Ordinal)
            && !normalized.StartsWith("sd-fast-", StringComparison.Ordinal)
            && !normalized.StartsWith("sd-2.0-", StringComparison.Ordinal))
        {
            return null;
        }

        return normalized.EndsWith("-1080p", StringComparison.Ordinal) ? "1080p"
            : normalized.EndsWith("-720p", StringComparison.Ordinal) ? "720p"
            : normalized.EndsWith("-480p", StringComparison.Ordinal) ? "480p"
            : null;
    }

    /// <summary>识别走 OpenAI 兼容接口的 Seedance 模型。</summary>
    private static bool IsSeedanceCompatibilityModel(string modelKey) =>
        modelKey.Contains("seedance", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断当前输入是否启用首尾帧视频模式。</summary>
    private static bool IsFirstLastMode(string? inputMode) =>
        string.Equals(inputMode, "first-last", StringComparison.OrdinalIgnoreCase);

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
            : image.Url!;

    /// <summary>仅当现有失败条件满足时允许切换已验证的备用来源。</summary>
    private static bool CanRetryWithFallback(
        VideoSubmitResult result,
        IReadOnlyList<ReferenceImage> images) =>
        result is { Ok: false, StatusCode: 400 or 422 }
        && images.Any(image => IsHttpUrl(image.FallbackUrl)
            && !string.Equals(image.Url, image.FallbackUrl, StringComparison.Ordinal));

    /// <summary>判断来源是否为绝对 HTTP(S) 地址。</summary>
    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
