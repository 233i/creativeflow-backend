using System.Diagnostics;
using System.Security.Cryptography;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>管理向量参数版本并显式测试已保存配置；身份由交付层验证，资源由全局渠道仓储复验。</summary>
public sealed class EmbeddingModelService(IProviderRepository providers,
    IEmbeddingSettingsRepository settings, IApiKeyProtector protector, IEmbeddingDispatcher dispatcher)
{
    /// <summary>只读取全局向量模型的配置，不对聊天、媒体或不可访问模型提供测试入口。</summary>
    public async Task<EmbeddingOperation<EmbeddingSettingsResponse>> GetAsync(ModelId id, CancellationToken ct)
    {
        var (model, provider) = await ResolveAsync(id, ct);
        if (model is null || provider is null) return new(null, "Embedding model not found.", true);
        return new(await settings.FindAsync(id, ct) ?? new(new EmbeddingSettings(), null), null);
    }

    /// <summary>验证新参数后追加版本；保存本身不调用模型，禁用模型仍可修改参数。</summary>
    public async Task<EmbeddingOperation<EmbeddingSettingsResponse>> SaveAsync(
        ModelId id, EmbeddingSettings value, CancellationToken ct)
    {
        var (model, provider) = await ResolveAsync(id, ct);
        if (model is null || provider is null) return new(null, "Embedding model not found.", true);
        var error = EmbeddingValidation.ValidateSettings(value);
        if (error is not null) return new(null, error);
        return new(await settings.SaveAsync(id, value, ct), null);
    }

    /// <summary>使用已保存配置执行一个受限批次，只返回摘要，失败不自动换模型或重发。</summary>
    public async Task<EmbeddingOperation<EmbeddingTestResponse>> TestAsync(
        ModelId id, EmbeddingTestRequest input, CancellationToken ct)
    {
        var (model, provider) = await ResolveAsync(id, ct);
        if (model is null || provider is null) return new(null, "Embedding model not found.", true);
        var configuration = await settings.FindAsync(id, ct);
        if (configuration?.Version is null) return new(null, "Save embedding settings before testing.");
        if (input.Inputs is null || input.Inputs.Length > 32) return new(null, "Provide between 1 and 32 test inputs.");
        var inputs = input.Inputs.Select((text, index) => new EmbeddingInput(index.ToString(System.Globalization.CultureInfo.InvariantCulture), text)).ToArray();
        var error = EmbeddingValidation.ValidateSettings(configuration.Settings)
            ?? EmbeddingValidation.ValidateInputs(configuration.Settings, inputs, input.Purpose);
        if (error is not null) return new(null, error);

        // 密钥仅在参数和归属已确认后解密。不能把受保护字段或异常中的密钥带回浏览器。
        string apiKey;
        try { apiKey = provider.ApiKey is null ? "" : protector.Unprotect(provider.ApiKey); }
        catch (CryptographicException) { return new(null, "The stored key cannot be decrypted. Re-enter it in provider settings."); }
        var started = Stopwatch.GetTimestamp();
        var result = await dispatcher.EmbedAsync(new(provider, model, apiKey, configuration.Settings, inputs, input.Purpose), ct);
        return new(new(result.Ok, result.Code, result.Error, result.StatusCode, result.Dimensions,
            result.Vectors.Count, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            result.ConfigurationFingerprint, configuration.Version, result.TotalTokens), null);
    }

    /// <summary>按全局渠道归属复验模型；不可用记录不泄露其他模型详情。</summary>
    private async Task<(AiModel? Model, AiProvider? Provider)> ResolveAsync(ModelId id, CancellationToken ct)
    {
        var model = await providers.FindModelAsync(id, ct);
        if (model?.Category != ModelCategory.Embedding) return (null, null);
        var provider = await providers.FindAsync(model.ProviderId, ct);
        return provider is null || !provider.Models.Any(candidate => candidate.Id == id)
            ? (null, null) : (model, provider);
    }
}

/// <summary>配置用例的有类型结果，交付层将未找到与无效请求映射为既有 HTTP 错误契约。</summary>
/// <param name="Value">成功时的返回契约。</param>
/// <param name="Error">失败时的非敏感说明。</param>
/// <param name="NotFound">是否应隐藏不存在或不可访问的资源。</param>
public sealed record EmbeddingOperation<T>(T? Value, string? Error, bool NotFound = false) where T : class;
