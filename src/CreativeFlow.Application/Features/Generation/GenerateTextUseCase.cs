using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Generation;

/// <summary>装配文本生成的渠道、密钥、参考素材和上游端口，执行不依赖 HTTP 的生成规则。</summary>
public sealed class GenerateTextUseCase(
    IProviderRepository providers,
    IApiKeyProtector protector,
    ITextGenerationDispatcher dispatcher,
    IAssetStore assetStore,
    ICanvasAssetRepository assets)
{
    /// <summary>单次请求最多携带八个参考图 URL，在过滤不可用素材之前计算。</summary>
    private const int MaxReferenceImages = 8;

    /// <summary>在已验证设备范围内生成文本，保持原有校验顺序、素材读取及上游结果规则。</summary>
    /// <param name="deviceId">交付层已验证的设备，仓储在当前账号范围内解析可访问素材。</param>
    /// <param name="request">原有文本生成 DTO，不改变字段、缺省值或字符串内容。</param>
    /// <param name="ct">本次请求的取消令牌，传给仓储、读流与上游分派器。</param>
    /// <remarks>仅把既有业务失败映射为结果；基础设施异常和请求取消继续向调用方传播。</remarks>
    public async Task<GenerateTextResult> HandleAsync(
        DeviceId deviceId, GenerateTextRequest request, CancellationToken ct)
    {
        // 校验顺序决定调用方先收到哪个错误；即使有参考图，文本入口也仍然要求提示词。
        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return GenerateTextResult.Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return GenerateTextResult.Invalid("a prompt is required");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return GenerateTextResult.NotFound();
        }

        if (!provider.Enabled)
        {
            return GenerateTextResult.Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(
            candidate => candidate.ModelKey == request.ModelKey);

        if (model is null)
        {
            return GenerateTextResult.Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (!model.Enabled)
        {
            return GenerateTextResult.Invalid($"model '{request.ModelKey}' is disabled");
        }

        if (model.Category != ModelCategory.Chat)
        {
            return GenerateTextResult.Invalid($"model '{request.ModelKey}' is not a chat model");
        }

        if (!TryUnprotectKey(provider, protector, out var apiKey, out var keyError))
        {
            return GenerateTextResult.Invalid(keyError);
        }

        // 数量限制先于过滤；缺失素材可以跳过，但读流异常不等同于素材缺失。
        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } urls)
        {
            if (urls.Length > MaxReferenceImages)
            {
                return GenerateTextResult.Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in urls)
            {
                if (await LoadReferenceAsync(url, deviceId, assets, assetStore, ct) is { } image)
                {
                    references.Add(image);
                }
            }
        }

        // 只在请求未指定时采用模型 token 上限；参数不在这里夹取，协议支持仍由分派器决定。
        var result = await dispatcher.GenerateAsync(
            provider.ResolveProtocol(model),
            new TextGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                request.SystemPrompt,
                references,
                request.MaxOutputTokens ?? model.MaxOutputTokens,
                request.Temperature),
            ct);

        // 成功的空串仍是有效文本；上游状态码不直接成为交付层的响应状态。
        return result.Ok && result.Text is not null
            ? GenerateTextResult.Success(new GenerateTextResponse(result.Text))
            : GenerateTextResult.Invalid(result.Error ?? "text generation failed");
    }

    /// <summary>将站内素材 URL 解析为可访问的字节和原有媒体类型，保留查询串与缺失素材的处理。</summary>
    /// <remarks>不发起外部 URL 下载，也不新增媒体类型、视觉能力或重复项过滤。</remarks>
    private static async Task<ReferenceImage?> LoadReferenceAsync(
        string url,
        DeviceId deviceId,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        // 仅解析站内素材路径并检查可访问记录；任意外部 URL 不在这里发起下载。
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

    /// <summary>恢复渠道密钥；未配置时保留空串，仅将密码学异常转为原有重填提示。</summary>
    /// <remarks>加密密钥变化或记录损坏不能通过重试恢复，其他异常继续由调用方处理。</remarks>
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
            // 只映射可识别的密钥损坏，其他异常保留原样，避免误导调用方重新配置或重试。
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
}
