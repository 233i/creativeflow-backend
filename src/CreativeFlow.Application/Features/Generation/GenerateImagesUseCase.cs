using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Generation;

/// <summary>校验图片生成策略、调用现有协议端口并保存素材；不依赖 HTTP 交付机制。</summary>
public sealed class GenerateImagesUseCase(
    IProviderRepository providers,
    IApiKeyProtector protector,
    IImageGenerationDispatcher dispatcher,
    IAssetStore assetStore,
    ICanvasAssetRepository assets,
    IImageGenerationSettingsRepository generationSettings,
    IClock clock,
    IImageOutputDownloader downloader)
{
    /// <summary>单次生成最多四张图片，保持画布与既有端点的数量边界。</summary>
    private const int MaxImageCount = 4;

    /// <summary>限制需要加载并重新发送给上游的参考图片数量。</summary>
    private const int MaxReferenceImages = 8;

    /// <summary>按已验证设备范围执行生成，保留原有校验顺序、容错与保存顺序。</summary>
    /// <param name="deviceId">交付层已验证的设备标识，仓储在当前账号范围内解析素材归属。</param>
    /// <param name="request">原有图片生成输入，字段与缺省值保持不变。</param>
    /// <param name="ct">本次请求的取消令牌，向上游与存储操作传递。</param>
    public Task<GenerateImagesResult> HandleAsync(
        DeviceId deviceId, GenerateImageRequest request, CancellationToken ct) =>
        HandleCoreAsync(deviceId, request, null, ct);

    /// <summary>后台任务在下载前持久化上游输出，沿用同步接口的输入校验和协议绑定。</summary>
    public Task<GenerateImagesResult> GenerateAndCheckpointAsync(DeviceId deviceId,
        GenerateImageRequest request,
        Func<string, IReadOnlyList<GeneratedImage>, CancellationToken, Task> checkpoint,
        CancellationToken ct) => HandleCoreAsync(deviceId, request, checkpoint, ct);

    /// <summary>复用同步入口的校验及协议分发，后台模式先保存输出检查点。</summary>
    private async Task<GenerateImagesResult> HandleCoreAsync(DeviceId deviceId, GenerateImageRequest request,
        Func<string, IReadOnlyList<GeneratedImage>, CancellationToken, Task>? checkpoint, CancellationToken ct)
    {
        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return GenerateImagesResult.Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt) && (request.Images?.Length ?? 0) == 0)
        {
            return GenerateImagesResult.Invalid("a prompt or at least one reference image is required");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return GenerateImagesResult.NotFound();
        }

        if (!provider.Enabled)
        {
            return GenerateImagesResult.Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(
            candidate => candidate.ModelKey == request.ModelKey);

        if (model is null)
        {
            return GenerateImagesResult.Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (!model.Enabled)
        {
            return GenerateImagesResult.Invalid($"model '{request.ModelKey}' is disabled");
        }

        if (model.Category != ModelCategory.Image)
        {
            return GenerateImagesResult.Invalid($"model '{request.ModelKey}' is not an image model");
        }

        string apiKey;
        if (!TryUnprotectKey(provider, protector, out apiKey, out var keyError))
        {
            return GenerateImagesResult.Invalid(keyError);
        }

        // 参考图以素材 URL 指定；在此解析可避免客户端再次上传相同字节。
        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } imageUrls)
        {
            if (imageUrls.Length > MaxReferenceImages)
            {
                return GenerateImagesResult.Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in imageUrls)
            {
                var reference = await LoadReferenceAsync(url, deviceId, assets, assetStore, ct);
                if (reference is not null)
                {
                    references.Add(reference);
                }
                // 无法解析的参考图会被跳过，而不会使运行失败：通常是节点图片从未上传，
                // 仅根据提示词生成比直接报错更有用。
            }
        }

        var protocol = provider.ResolveProtocol(model);

        ReferenceImage? mask = null;
        if (!string.IsNullOrWhiteSpace(request.MaskUrl))
        {
            if (protocol != ProviderType.OpenAiImages)
            {
                return GenerateImagesResult.Invalid("the selected image protocol does not support inpaint masks");
            }

            if (references.Count == 0)
            {
                return GenerateImagesResult.Invalid("an inpaint mask requires at least one reference image");
            }

            mask = await LoadReferenceAsync(request.MaskUrl, deviceId, assets, assetStore, ct);
            if (mask is null)
            {
                // 与普通参考图不同，静默丢弃蒙版会把局部重绘变成整图变体。
                // 这种破坏性足以要求明确失败。
                return GenerateImagesResult.Invalid("the inpaint mask is not an available canvas asset");
            }

            if (!string.Equals(mask.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                return GenerateImagesResult.Invalid("the inpaint mask must be a PNG image");
            }
        }

        var count = Math.Clamp(request.Count ?? 1, 1, MaxImageCount);
        // 每次运行都刻意在服务器端读取此值。公开请求契约不含响应格式字段，
        // 因此画布或直接 API 调用方无法覆盖管理员策略。
        var policy = await generationSettings.GetAsync(ct);

        var result = await dispatcher.GenerateAsync(
            protocol,
            new ImageGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                references,
                mask,
                request.Size,
                request.Quality,
                request.OutputFormat,
                policy.ResponseFormat,
                count,
                request.Seed,
                DeferDownloads: true),
            ct);

        if (!result.Ok)
        {
            return GenerateImagesResult.Invalid(result.Error ?? "image generation failed");
        }

        if (checkpoint is not null)
        {
            await checkpoint(provider.BaseUrl, result.Images, ct);
            return GenerateImagesResult.Success(new GenerateImageResponse([]));
        }

        var stored = new List<GeneratedAsset>(result.Images.Count);

        foreach (var image in result.Images)
        {
            var saved = image.DownloadUrl is { } downloadUrl && image.Bytes.Length == 0
                ? await downloader.DownloadAsync(downloadUrl, provider.BaseUrl, apiKey, image.MediaType, ct)
                : await assetStore.SaveAsync(image.Bytes, image.MediaType, ExtensionFor(image.MediaType), ct);

            var assetId = Guid.CreateVersion7();

            await assets.InsertAsync(
                new CanvasAsset(
                    assetId,
                    deviceId,
                    "image",
                    saved.MediaType,
                    saved.StoragePath,
                    saved.ByteSize,
                    request.Prompt,
                    clock.UtcNow,
                    SourceUrl: image.SourceUrl,
                    SourceUrlExpiresAt: image.SourceUrlExpiresAt),
                ct);

            stored.Add(new GeneratedAsset(
                $"/api/v1/canvas/assets/{assetId}", saved.MediaType, assetId.ToString()));
        }

        return GenerateImagesResult.Success(new GenerateImageResponse([.. stored]));
    }

    /// <summary>
    /// 将素材 URL 解析回字节内容。
    /// </summary>
    /// <remarks>
    /// 只接受本系统素材路径。若获取客户端提供的任意 URL，
    /// 此端点会变成任何能向其提交请求的人都可利用的服务器端请求伪造工具。
    /// </remarks>
    private static async Task<ReferenceImage?> LoadReferenceAsync(
        string url,
        DeviceId deviceId,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        // 仅加载当前设备可访问的站内素材；返回空值交给调用方区分普通参考图与必须有效的蒙版。
        const string prefix = "/api/v1/canvas/assets/";

        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var idSegment = url[prefix.Length..].Split('?')[0];
        if (!Guid.TryParse(idSegment, out var assetId))
        {
            return null;
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null || asset.IsRecycled)
        {
            return null;
        }

        await using var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return new ReferenceImage(buffer.ToArray(), asset.MediaType);
    }

    /// <summary>
    /// 恢复供应商的 API 密钥。
    /// </summary>
    /// <remarks>
    /// 配置的加密密钥变化或数据行被篡改时，解密会失败。两种情况都无法通过重试解决，
    /// 因此消息应提示重新输入密钥，而不是表现为临时错误。
    /// </remarks>
    private static bool TryUnprotectKey(
        CreativeFlow.Domain.Ai.AiProvider provider,
        IApiKeyProtector protector,
        out string apiKey,
        out string error)
    {
        if (provider.ApiKey is null)
        {
            apiKey = string.Empty;
            error = string.Empty;
            return true;
        }

        try
        {
            apiKey = protector.Unprotect(provider.ApiKey);
            error = string.Empty;
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            apiKey = string.Empty;
            error = "The stored API key could not be decrypted. Re-enter it in settings.";
            return false;
        }
    }

    /// <summary>将媒体类型映射为当前支持的文件扩展名，并保留未知类型的默认值。</summary>
    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        _ => ".bin",
    };

}
