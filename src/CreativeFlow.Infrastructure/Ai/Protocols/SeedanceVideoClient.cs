using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// Seedance 视频生成接口（Volcengine Ark <c>/contents/generations/tasks</c>）。
/// </summary>
/// <remarks>
/// Seedance 1.x 将参数作为追加到提示词文本后的 <c>--flag value</c> 后缀传递，而不是 JSON 字段；
/// 这一约定虽不常见，却是 API 接受的形式。Seedance 2.x 改用结构化顶层字段，并完全拒绝
/// <c>fps</c>，因此根据模型标识决定发送哪种结构。
/// </remarks>
internal sealed class SeedanceVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<SeedanceVideoClient> logger) : IVideoGenerationClient
{
    /// <summary>标识当前客户端使用 Seedance 视频任务协议。</summary>
    public ProviderType Protocol => ProviderType.SeedanceVideo;

    /// <summary>校验首尾帧数量与结构化模型的图片 URL，按对应 Seedance 请求格式提交视频任务。</summary>
    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        var structured = IsStructuredModel(request.ModelKey);

        if (IsFirstLastMode(request.InputMode) && request.ReferenceImages.Count > 2)
        {
            return VideoSubmitResult.Failure(
                400, "first-last mode accepts at most two connected images");
        }

        // Seedance 2.x 会在启动任务前验证此项，并明确拒绝数据 URL。
        // 在此返回配置错误，比转发已知无效的请求正文更有帮助。
        if (structured && request.ReferenceImages.Any(image => !IsHttpUrl(image.Url)))
        {
            return VideoSubmitResult.Failure(
                400,
                "Seedance 2.x requires reference images to use a public HTTP(S) URL. " +
                "Configure Authentication:PublicOrigin to an externally reachable address.");
        }

        var submitted = await SubmitOnceAsync(request, structured, useFallbackUrls: false, ct);
        if (!structured || !CanRetryWithFallback(submitted, request.ReferenceImages))
        {
            return submitted;
        }

        return await SubmitOnceAsync(request, structured, useFallbackUrls: true, ct);
    }

    /// <summary>向供应商执行一次已构造的提交，不在该方法内新增重试。</summary>
    private async Task<VideoSubmitResult> SubmitOnceAsync(
        VideoGenerationRequest request,
        bool structured,
        bool useFallbackUrls,
        CancellationToken ct)
    {
        // 首尾帧和参考素材分别携带既有角色标记；本次请求保留外层已选择的 URL 或内联回退来源。
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var body = BuildRequestBody(request, structured, useFallbackUrls);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/contents/generations/tasks")
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
                    "Seedance submit failed with {StatusCode}", (int)response.StatusCode);
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

    /// <summary>查询 Seedance contents/generations/tasks 中的已有任务并转换结果，不再次提交生成。</summary>
    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = provider.BaseUrl.TrimEnd('/');

        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/contents/generations/tasks/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // 临时上游错误会报告为仍在运行而非失败：轮询器会重试，
                // 过期任务扫描会限制这种状态的持续时间。
                return new VideoPollResult(VideoJobState.Running, null, null, null);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            return status switch
            {
                "succeeded" => new VideoPollResult(
                    VideoJobState.Succeeded, 1, ReadVideoUrl(root), null),
                "failed" or "cancelled" => new VideoPollResult(
                    VideoJobState.Failed, null, null, ReadError(root) ?? "Generation failed."),
                "running" => new VideoPollResult(VideoJobState.Running, null, null, null),
                _ => new VideoPollResult(VideoJobState.Pending, null, null, null),
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
        bool useFallbackUrls = false) =>
        BuildRequestBody(request, IsStructuredModel(request.ModelKey), useFallbackUrls);

    /// <summary>按当前供应商协议组织生成参数，保留省略字段和缺省规则。</summary>
    private static JsonObject BuildRequestBody(
        VideoGenerationRequest request,
        bool structured,
        bool useFallbackUrls)
    {
        var content = new JsonArray();
        var firstLastMode = IsFirstLastMode(request.InputMode);

        // API 将帧内容和参考媒体视为互斥。画布输入按距离由近到远排序，因此首尾帧模式将前两张图片
        // 映射到两个帧角色；参考模式则把所有图片都保留在参考媒体组中。
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
                    ["url"] = structured
                        ? SelectHttpUrl(reference, useFallbackUrls)
                        : $"data:{reference.MediaType};base64,{Convert.ToBase64String(reference.Bytes)}",
                },
            });
        }

        content.Add((JsonNode)new JsonObject
        {
            ["type"] = "text",
            ["text"] = structured ? request.Prompt : BuildFlaggedPrompt(request),
        });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["content"] = content,
        };

        if (structured)
        {
            // 2.x 将这些值作为真实字段接收。刻意不提供 fps，因为它会被拒绝。
            if (request.Resolution is { } resolution) body["resolution"] = resolution;
            if (request.DurationSeconds is { } duration) body["duration"] = duration;
            if (request.Aspect is { } aspect) body["ratio"] = aspect;
            if (request.Seed is { } seed) body["seed"] = seed;
            if (request.Watermark is { } watermark) body["watermark"] = watermark;
            if (request.GenerateAudio is { } audio) body["generate_audio"] = audio;
        }

        return body;
    }

    /// <summary>
    /// 将 1.x 参数以 <c>--flag value</c> 键值对形式追加到提示词。
    /// </summary>
    private static string BuildFlaggedPrompt(VideoGenerationRequest request)
    {
        var builder = new StringBuilder(request.Prompt);

        if (request.Aspect is { } aspect) builder.Append(" --ratio ").Append(aspect);
        if (request.DurationSeconds is { } duration) builder.Append(" --dur ").Append(duration);
        if (request.Fps is { } fps) builder.Append(" --fps ").Append(fps);
        if (request.Resolution is { } resolution) builder.Append(" --rs ").Append(resolution);
        if (request.Seed is { } seed) builder.Append(" --seed ").Append(seed);
        if (request.Watermark is { } watermark)
        {
            builder.Append(" --wm ").Append(watermark ? "true" : "false");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 检测使用结构化字段而非提示词标志的 Seedance 2.x 模型标识。
    /// </summary>
    private static bool IsStructuredModel(string modelKey) =>
        modelKey.Contains("seedance-2", StringComparison.OrdinalIgnoreCase)
        || modelKey.Contains("seedance-pro-2", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断当前输入是否启用首尾帧视频模式。</summary>
    private static bool IsFirstLastMode(string? inputMode) =>
        string.Equals(inputMode, "first-last", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>按供应商既有响应结构读取可下载的视频地址。</summary>
    private static string? ReadVideoUrl(JsonElement root) =>
        root.TryGetProperty("content", out var content)
        && content.TryGetProperty("video_url", out var url)
        && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;

    /// <summary>读取供应商错误详情并保留既有有界回退文本。</summary>
    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
}
