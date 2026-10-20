using System.Security.Cryptography;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>读取当前账号可访问的媒体输入；集中处理签名地址和 xAI 源视频地址回退。</summary>
internal sealed class WorkflowMediaAssetReader(
    ICanvasAssetRepository assets,
    IAssetStore assetStore,
    ISignedMediaUrlService signedUrls,
    IVideoJobRepository videoJobs,
    IProviderRepository providers,
    IApiKeyProtector protector,
    IVideoGenerationDispatcher dispatcher)
{
    /// <summary>在运行恢复的账号范围内读取参考图片内容。</summary>
    internal async Task<IReadOnlyList<ReferenceImage>> LoadReferencesAsync(
        WorkflowRun run,
        IReadOnlyList<string> urls,
        int max,
        CancellationToken ct)
    {
        var references = new List<ReferenceImage>();

        foreach (var url in urls.Take(max))
        {
            if (!TryAssetId(url, out var assetId)) continue;
            var asset = await assets.FindAsync(run.DeviceId, assetId, ct);
            if (asset is null || asset.IsRecycled || asset.Kind != "image"
                || !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            await using var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
            if (stream is null) continue;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var inputUrls = signedUrls.CreateInputUrls(asset);
            references.Add(new ReferenceImage(
                buffer.ToArray(),
                asset.MediaType,
                inputUrls.PrimaryUrl,
                inputUrls.FallbackUrl));
        }

        return references;
    }

    /// <summary>在当前范围内解析源视频元数据与可供上游使用的内容。</summary>
    internal async Task<ReferenceVideo> LoadVideoAsync(
        WorkflowRun run,
        string url,
        CancellationToken ct)
    {
        // 确认源素材归属与类型后，依次尝试上游地址、签名地址和内容流，读取到空内容时明确失败。
        if (!TryAssetId(url, out var assetId))
        {
            throw new WorkflowExecutionException("The input video is no longer available.");
        }

        var asset = await assets
            .FindAsync(run.DeviceId, assetId, ct);
        if (asset is not { Kind: "video" })
        {
            throw new WorkflowExecutionException("The input video is no longer available.");
        }

        var mediaType = asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? asset.MediaType
            : "video/mp4";
        var signedInputUrls = signedUrls
            .CreateInputUrls(asset);
        var reusableUrl = await TryResolveReusableVideoUrlAsync(
            run, assetId, ct);
        if (reusableUrl is not null)
        {
            return new ReferenceVideo(
                [], mediaType, reusableUrl, signedInputUrls.PrimaryUrl);
        }

        if (signedInputUrls.PrimaryUrl is not null)
        {
            return new ReferenceVideo(
                [], mediaType, signedInputUrls.PrimaryUrl, signedInputUrls.FallbackUrl);
        }

        await using var stream = await assetStore
            .OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            throw new WorkflowExecutionException("The input video is no longer available.");
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            throw new WorkflowExecutionException("The input video is empty.");
        }

        return new ReferenceVideo(buffer.ToArray(), mediaType);
    }

    /// <summary>为已成功的 xAI 视频任务重新查询可复用地址，不可用时返回空值交给调用方回退。</summary>
    private async Task<string?> TryResolveReusableVideoUrlAsync(
        WorkflowRun run,
        Guid assetId,
        CancellationToken ct)
    {
        // 仅使用成功的 xAI 任务重新查询结果；当前上游仍返回成功且地址为 HTTP(S) 时才交给续接使用。
        var job = await videoJobs
            .FindByAssetAsync(run.DeviceId, assetId, ct);
        if (job is not
            {
                Status: VideoJobStatus.Succeeded,
                ProviderId: { } providerId,
                UpstreamJobId: { Length: > 0 } upstreamJobId,
            })
        {
            return null;
        }

        var provider = await providers
            .FindAsync(new ProviderId(providerId), ct);
        var model = provider?.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.ModelKey, job.ModelKey, StringComparison.Ordinal));
        if (provider is null || model is null || !provider.Enabled || !model.Enabled
            || provider.ResolveProtocol(model) != ProviderType.XaiVideo)
        {
            return null;
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null
                ? string.Empty
                : protector.Unprotect(provider.ApiKey);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var result = await dispatcher
            .PollAsync(ProviderType.XaiVideo, provider, apiKey, upstreamJobId, ct);
        return result.State == VideoJobState.Succeeded
            && Uri.TryCreate(result.VideoUrl, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
                ? result.VideoUrl
                : null;
    }
}
