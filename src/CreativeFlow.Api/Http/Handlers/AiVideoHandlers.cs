using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>
/// 端到端异步的视频生成。
/// </summary>
/// <remarks>
/// 供应商接受任务后，POST 会立即返回任务标识；后台轮询器推进任务并存储完成文件。
/// 客户端轮询状态路由。因此标签页关闭后任务仍可继续——运行过程不依赖启动请求保持打开。
/// </remarks>
internal static class AiVideoHandlers
{
    /// <summary>单次生成允许携带的参考图片数量上限。</summary>
    private const int MaxReferenceImages = 4;

    /// <summary>
    /// 将任务标记为已取消，使轮询器停止推进。
    /// </summary>
    /// <remarks>
    /// 这不会撤回上游任务——多数供应商不提供取消功能，渲染费用也已产生。
    /// 它只会阻止此任务继续消耗轮询，并避免写入用户已不需要的结果。
    /// <para>
    /// 取消已完成任务时不执行任何操作，也不报错：用户点击停止时任务可能已经完成，
    /// 此时报失败会造成困惑。
    /// </para>
    /// </remarks>
    internal static async Task<IActionResult> CancelAsync(
        string id,
        HttpContext http,
        IVideoJobRepository jobs,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var jobId))
        {
            return ApiResults.NotFound();
        }

        var job = await jobs.FindAsync(deviceId, jobId, ct);
        if (job is null)
        {
            return ApiResults.NotFound();
        }

        if (job.Status is VideoJobStatus.Pending or VideoJobStatus.Running)
        {
            await jobs.UpdateAsync(
                job with { Status = VideoJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow },
                ct);
        }

        return ApiResults.NoContent();
    }

    /// <summary>校验输入并提交异步视频生成任务。</summary>
    internal static async Task<IActionResult> StartAsync(
        StartVideoRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IVideoGenerationDispatcher dispatcher,
        IVideoJobRepository jobs,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        ISignedMediaUrlService signedUrls,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt) && (request.Images?.Length ?? 0) == 0)
        {
            return Invalid("a prompt or at least one reference image is required");
        }

        var duration = request.Duration ?? VideoGenerationLimits.DefaultDurationSeconds;
        if (duration is < VideoGenerationLimits.MinimumDurationSeconds
            or > VideoGenerationLimits.MaximumDurationSeconds)
        {
            return Invalid(
                $"duration must be between {VideoGenerationLimits.MinimumDurationSeconds} and " +
                $"{VideoGenerationLimits.MaximumDurationSeconds} seconds");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return ApiResults.NotFound();
        }

        if (!provider.Enabled)
        {
            return Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(m => m.ModelKey == request.ModelKey);
        if (model is null)
        {
            return Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (!model.Enabled)
        {
            return Invalid($"model '{request.ModelKey}' is disabled");
        }

        if (model.Category != ModelCategory.Video)
        {
            return Invalid($"model '{request.ModelKey}' is not a video model");
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null ? string.Empty : protector.Unprotect(provider.ApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Invalid("The stored API key could not be decrypted. Re-enter it in settings.");
        }

        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } urls)
        {
            if (urls.Length > MaxReferenceImages)
            {
                return Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in urls)
            {
                // 只解析本系统素材路径；任意 URL 会被忽略而不会获取，
                // 因此无法将其用作请求伪造工具。
                if (await ResolveReferenceAsync(
                        url, deviceId, assets, assetStore, signedUrls, ct) is { } image)
                {
                    references.Add(image);
                }
            }
        }

        var protocol = provider.ResolveProtocol(model);
        var inputMode = request.InputMode?.Trim().ToLowerInvariant();
        if (inputMode is not (null or "reference" or "first-last"))
        {
            return Invalid("inputMode must be 'reference' or 'first-last'");
        }

        if (protocol == ProviderType.SeedanceVideo
            && inputMode == "first-last"
            && references.Count > 2)
        {
            return Invalid("first-last mode accepts at most two connected images");
        }

        var submit = await dispatcher.SubmitAsync(
            protocol,
            new VideoGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                references,
                inputMode ?? "reference",
                request.Aspect,
                request.Resolution,
                duration,
                request.Fps,
                request.Seed,
                request.Watermark,
                request.GenerateAudio),
            ct);

        if (!submit.Ok || submit.UpstreamJobId is null)
        {
            return Invalid(submit.Error ?? "the provider rejected the video request");
        }

        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.CreateVersion7();

        // 将原始请求保存到数据行，使配置节点变化后重新加载仍能显示当时请求的内容。
        var requestJson = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["inputMode"] = inputMode ?? "reference",
            ["aspect"] = request.Aspect,
            ["resolution"] = request.Resolution,
            ["duration"] = duration,
        }.ToJsonString();

        await jobs.InsertAsync(
            new VideoJob(
                jobId,
                deviceId,
                providerId.Value,
                request.ModelKey,
                VideoJobStatus.Running,
                submit.UpstreamJobId,
                null,
                null,
                null,
                requestJson,
                now,
                now),
            ct);

        return ApiResults.Ok(new StartVideoResponse(jobId.ToString()));
    }

    /// <summary>读取视频任务当前持久化状态与结果。</summary>
    internal static async Task<IActionResult> GetStatusAsync(
        string id,
        HttpContext http,
        IVideoJobRepository jobs,
        CancellationToken ct)
    {
        // 状态读取以当前设备可访问的本地任务为准，返回结果时沿用原有素材地址规则。
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var jobId))
        {
            return ApiResults.NotFound();
        }

        var job = await jobs.FindAsync(deviceId, jobId, ct);
        if (job is null)
        {
            return ApiResults.NotFound();
        }

        return ApiResults.Ok(new VideoJobStatusResponse(
            job.JobId.ToString(),
            job.Status switch
            {
                VideoJobStatus.Pending => "pending",
                VideoJobStatus.Running => "running",
                VideoJobStatus.Succeeded => "succeeded",
                VideoJobStatus.Failed => "failed",
                _ => "cancelled",
            },
            job.Progress,
            job.AssetId is { } assetId ? $"/api/v1/canvas/assets/{assetId}" : null,
            job.Error));
    }

    /// <summary>只在当前设备可访问的素材中解析视频参考图。</summary>
    private static async Task<ReferenceImage?> ResolveReferenceAsync(
        string url,
        CreativeFlow.Domain.Identity.DeviceId deviceId,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        ISignedMediaUrlService signedUrls,
        CancellationToken ct)
    {
        const string prefix = "/api/v1/canvas/assets/";

        if (!url.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParse(url[prefix.Length..].Split('?')[0], out var assetId))
        {
            return null;
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null || asset.IsRecycled || asset.Kind != "image"
            || !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        var inputUrls = signedUrls.CreateInputUrls(asset);
        return new ReferenceImage(
            buffer.ToArray(),
            asset.MediaType,
            inputUrls.PrimaryUrl,
            inputUrls.FallbackUrl);
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
