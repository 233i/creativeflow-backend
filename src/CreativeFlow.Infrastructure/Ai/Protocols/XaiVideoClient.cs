using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 兼容 xAI 的异步视频生成接口。
/// </summary>
/// <remarks>
/// xAI 使用 <c>/videos/generations</c> 生成新片段，使用 <c>/videos/extensions</c> 续接视频。
/// 两者都返回 <c>request_id</c>，并在 <c>video.url</c> 中提供完成后的 URL。Routin 将此协议挂载在
/// <c>/xai/v1</c> 下，刻意与通用兼容 OpenAI 的 <c>/v1</c> 命名空间分离。
/// </remarks>
internal sealed class XaiVideoClient(
    IHttpClientFactory httpClientFactory,
    ILogger<XaiVideoClient> logger) : IVideoGenerationClient, IVideoExtensionClient
{
    /// <summary>单次生成允许携带的参考图片数量上限。</summary>
    private const int MaxReferenceImages = 7;
    /// <summary>当前协议允许的视频最大时长，单位为秒。</summary>
    private const int MaxDurationSeconds = 15;
    /// <summary>当前视频协议允许的参考视频时长上限。</summary>
    private const int MaxReferenceDurationSeconds = 10;
    /// <summary>内联视频输入允许的字节数上限。</summary>
    internal const int MaxInlineVideoBytes = 8 * 1024 * 1024;
    /// <summary>采用文件上传的视频内容大小上限。</summary>
    private const int MaxFileVideoBytes = 48 * 1024 * 1024;
    /// <summary>上传视频允许在上游保留的有效期秒数。</summary>
    private const int UploadedVideoTtlSeconds = 3600;
    /// <summary>当前视频续接路径允许的模型标识。</summary>
    internal const string ExtensionModelKey = "grok-imagine-video";

    /// <summary>标识当前客户端使用 xAI 视频生成协议。</summary>
    public ProviderType Protocol => ProviderType.XaiVideo;

    /// <summary>校验提示词、参考图片及输入模式后，构造并提交 xAI 视频生成任务。</summary>
    public async Task<VideoSubmitResult> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        // 生成与续接入口各有输入限制；在发送请求前完成当前模型和素材模式校验。
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return VideoSubmitResult.Failure(400, "xAI video generation requires a prompt.");
        }

        if (request.ReferenceImages.Count > MaxReferenceImages)
        {
            return VideoSubmitResult.Failure(
                400, $"xAI video generation accepts at most {MaxReferenceImages} images.");
        }

        if (string.Equals(request.InputMode, "first-last", StringComparison.OrdinalIgnoreCase))
        {
            return VideoSubmitResult.Failure(
                400, "xAI video generation does not support first-last frame mode.");
        }

        if (request.DurationSeconds is { } duration
            && (duration is < 1 or > MaxDurationSeconds
                || request.ReferenceImages.Count > 1 && duration > MaxReferenceDurationSeconds))
        {
            return VideoSubmitResult.Failure(
                400,
                request.ReferenceImages.Count > 1
                    ? $"xAI reference video duration must be between 1 and {MaxReferenceDurationSeconds} seconds."
                    : $"xAI video duration must be between 1 and {MaxDurationSeconds} seconds.");
        }

        return await SubmitRequestAsync(
            request.Provider,
            request.ApiKey,
            "videos/generations",
            BuildRequestBody(request),
            "generation",
            ct);
    }

    /// <summary>校验续接模型、来源视频与时长后提交 xAI 续接任务，不降级为新的文生视频。</summary>
    public async Task<VideoSubmitResult> ExtendAsync(
        VideoExtensionRequest request,
        CancellationToken ct)
    {
        // 视频续接按已有来源选择与时长规则构造请求，不能把无法续接的输入改成新的文生视频。
        if (!string.Equals(request.ModelKey, ExtensionModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return VideoSubmitResult.Failure(
                400,
                $"xAI video extension requires the '{ExtensionModelKey}' model.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return VideoSubmitResult.Failure(400, "xAI video extension requires a prompt.");
        }

        var hasSourceUrl = Uri.TryCreate(
            request.SourceVideo.Url, UriKind.Absolute, out var sourceUri)
            && sourceUri.Scheme is "http" or "https";
        if ((!hasSourceUrl && request.SourceVideo.Bytes.Length == 0)
            || !request.SourceVideo.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return VideoSubmitResult.Failure(400, "xAI video extension requires a video input.");
        }

        if (!hasSourceUrl && request.SourceVideo.Bytes.Length > MaxFileVideoBytes)
        {
            return VideoSubmitResult.Failure(
                400,
                $"xAI video extension accepts source files up to " +
                $"{MaxFileVideoBytes / 1024 / 1024} MB.");
        }

        if (request.DurationSeconds is < VideoExtensionLimits.MinimumDurationSeconds
            or > VideoExtensionLimits.MaximumDurationSeconds)
        {
            return VideoSubmitResult.Failure(
                400,
                $"xAI video extension duration must be between " +
                $"{VideoExtensionLimits.MinimumDurationSeconds} and " +
                $"{VideoExtensionLimits.MaximumDurationSeconds} seconds.");
        }

        string? fileId = null;
        if (!hasSourceUrl && request.SourceVideo.Bytes.Length > MaxInlineVideoBytes)
        {
            var upload = await UploadVideoAsync(request, ct);
            if (!upload.Ok)
            {
                return VideoSubmitResult.Failure(upload.StatusCode, upload.Error!);
            }
            fileId = upload.FileId;
        }

        var submitted = await SubmitRequestAsync(
            request.Provider,
            request.ApiKey,
            "videos/extensions",
            BuildExtensionRequestBody(request, fileId),
            "extension",
            ct);

        if (!CanRetryVideoUrl(submitted, request.SourceVideo))
        {
            return submitted;
        }

        var retry = request with
        {
            SourceVideo = request.SourceVideo with
            {
                Url = request.SourceVideo.FallbackUrl,
                FallbackUrl = null,
            },
        };
        return await SubmitRequestAsync(
            retry.Provider,
            retry.ApiKey,
            "videos/extensions",
            BuildExtensionRequestBody(retry),
            "extension",
            ct);
    }

    /// <summary>通过 xAI 视频查询端点读取已有任务的状态、成品地址或错误信息。</summary>
    public async Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = ResolveBaseUrl(provider);
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/videos/{upstreamJobId}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return IsTransient(response.StatusCode)
                    ? new VideoPollResult(VideoJobState.Running, null, null, null)
                    : new VideoPollResult(
                        VideoJobState.Failed,
                        null,
                        null,
                        OpenAiImagesClient.ExtractError(text));
            }

            return ParsePollResponse(text);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new VideoPollResult(VideoJobState.Running, null, null, null);
        }
    }

    /// <summary>移除或补齐 xAI 视频协议要求的基础路径。</summary>
    internal static string ResolveBaseUrl(AiProvider provider)
    {
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        if (provider.BuiltinId != "routin-ai") return baseUrl;
        if (baseUrl.EndsWith("/xai/v1", StringComparison.OrdinalIgnoreCase)) return baseUrl;
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];
        return $"{baseUrl}/xai/v1";
    }

    /// <summary>按当前供应商协议组织生成参数，保留省略字段和缺省规则。</summary>
    internal static JsonObject BuildRequestBody(VideoGenerationRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
        };

        if (request.ReferenceImages.Count == 1)
        {
            body["image"] = Image(request.ReferenceImages[0]);
        }
        else if (request.ReferenceImages.Count > 1)
        {
            var references = new JsonArray();
            foreach (var image in request.ReferenceImages)
            {
                references.Add((JsonNode)Image(image));
            }
            body["reference_images"] = references;
        }

        if (request.DurationSeconds is { } duration) body["duration"] = duration;
        if (!string.IsNullOrWhiteSpace(request.Aspect)) body["aspect_ratio"] = request.Aspect;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) body["resolution"] = request.Resolution;
        return body;
    }

    /// <summary>按 xAI 协议构造续接视频的请求参数。</summary>
    internal static JsonObject BuildExtensionRequestBody(
        VideoExtensionRequest request,
        string? fileId = null)
    {
        var video = new JsonObject();
        if (!string.IsNullOrWhiteSpace(fileId))
        {
            video["file_id"] = fileId;
        }
        else
        {
            video["url"] = request.SourceVideo.Url
                ?? $"data:{request.SourceVideo.MediaType};base64," +
                    Convert.ToBase64String(request.SourceVideo.Bytes);
        }

        return new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["duration"] = request.DurationSeconds,
            ["video"] = video,
        };
    }

    /// <summary>为 xAI 续接上传源视频并读取上游文件关联信息。</summary>
    private async Task<VideoUploadResult> UploadVideoAsync(
        VideoExtensionRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = ResolveBaseUrl(request.Provider);
        using var form = new MultipartFormDataContent();
        // xAI 要求 expires_after 位于多部分正文的文件部分之前。
        form.Add(new StringContent(UploadedVideoTtlSeconds.ToString()), "expires_after");
        form.Add(new StringContent("assistants"), "purpose");
        var file = new ByteArrayContent(request.SourceVideo.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.SourceVideo.MediaType);
        form.Add(file, "file", "source.mp4");

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/files")
        {
            Content = form,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return VideoUploadResult.Failure(
                    (int)response.StatusCode,
                    $"The video provider could not upload the source video: " +
                    OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var fileId = ReadString(document.RootElement, "id");
            return string.IsNullOrWhiteSpace(fileId)
                ? VideoUploadResult.Failure(
                    null, "The video provider uploaded the source without returning a file id.")
                : VideoUploadResult.Success(fileId);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoUploadResult.Failure(
                null,
                $"The video provider disconnected while uploading the source video: " +
                exception.Message);
        }
    }

    /// <summary>将 xAI 视频轮询响应映射为当前任务状态和结果地址。</summary>
    internal static VideoPollResult ParsePollResponse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var status = ReadString(root, "status");
        var progress = root.TryGetProperty("progress", out var progressElement)
            && progressElement.ValueKind == JsonValueKind.Number
                ? Math.Clamp(progressElement.GetDouble() / 100, 0, 1)
                : (double?)null;

        return status switch
        {
            "done" when ReadVideoUrl(root) is { Length: > 0 } url =>
                new VideoPollResult(VideoJobState.Succeeded, 1, url, null),
            "done" => new VideoPollResult(
                VideoJobState.Failed, progress, null, "Upstream completed without a video URL."),
            "failed" => new VideoPollResult(
                VideoJobState.Failed, progress, null, ReadError(root) ?? "Generation failed."),
            "expired" => new VideoPollResult(
                VideoJobState.Failed, progress, null, ReadError(root) ?? "Video request expired."),
            "pending" => new VideoPollResult(VideoJobState.Pending, progress, null, null),
            _ => new VideoPollResult(VideoJobState.Running, progress, null, null),
        };
    }

    /// <summary>将参考图片构造为 xAI 接受的输入项。</summary>
    private static JsonObject Image(ReferenceImage image) => new()
    {
        ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
    };

    /// <summary>发送已经构造好的 xAI 视频提交请求并解析任务标识。</summary>
    private async Task<VideoSubmitResult> SubmitRequestAsync(
        AiProvider provider,
        string apiKey,
        string path,
        JsonObject body,
        string operation,
        CancellationToken ct)
    {
        // 提交响应中的上游任务标识用于后续轮询；错误处理必须保留当前失败类别，不能制造成功任务。
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = ResolveBaseUrl(provider);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{path}")
        {
            Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "xAI video {Operation} submit failed with {StatusCode}",
                    operation,
                    (int)response.StatusCode);
                return VideoSubmitResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var requestId = ReadString(document.RootElement, "request_id");
            return string.IsNullOrEmpty(requestId)
                ? VideoSubmitResult.Failure(null, "Upstream returned no request_id.")
                : VideoSubmitResult.Success(requestId);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return VideoSubmitResult.Failure(null, exception.Message);
        }
    }

    /// <summary>按供应商既有响应结构读取可下载的视频地址。</summary>
    private static string? ReadVideoUrl(JsonElement root) =>
        root.TryGetProperty("video", out var video)
        && video.ValueKind == JsonValueKind.Object
        ? ReadString(video, "url")
        : null;

    /// <summary>读取供应商错误详情并保留既有有界回退文本。</summary>
    private static string? ReadError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.ValueKind == JsonValueKind.Object
        ? ReadString(error, "message")
        : null;

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>判断失败是否属于当前允许的临时上游错误。</summary>
    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    /// <summary>判断当前视频来源失败是否满足原有安全回退条件。</summary>
    private static bool CanRetryVideoUrl(
        VideoSubmitResult result,
        ReferenceVideo source) =>
        result is { Ok: false, StatusCode: 400 or 422 }
        && Uri.TryCreate(source.FallbackUrl, UriKind.Absolute, out var fallback)
        && (fallback.Scheme == Uri.UriSchemeHttp || fallback.Scheme == Uri.UriSchemeHttps)
        && !string.Equals(source.Url, source.FallbackUrl, StringComparison.Ordinal);

    /// <param name="Ok">当前调用是否成功。</param>
    /// <param name="FileId">上游返回的文件标识。</param>
    /// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
    /// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
    private sealed record VideoUploadResult(
        bool Ok,
        string? FileId,
        int? StatusCode,
        string? Error)
    {
        /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
        public static VideoUploadResult Success(string fileId) =>
            new(true, fileId, 200, null);

        /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
        public static VideoUploadResult Failure(int? statusCode, string error) =>
            new(false, null, statusCode, error);
    }
}
