using System.Diagnostics;
using System.Security.Cryptography;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>管理重排序参数和显式连通测试，不将配置页面变成公共推理代理。</summary>
public sealed class RerankModelService(IProviderRepository providers, IRerankSettingsRepository settings,
    IApiKeyProtector protector, IRerankClient client)
{
    /// <summary>校验模型用途并读取已保存参数，未配置时返回安全默认值。</summary>
    public async Task<EmbeddingOperation<RerankSettingsResponse>> GetAsync(ModelId id, CancellationToken ct)
    {
        if (await ResolveAsync(id, ct) is not { }) return new(null, "重排序模型不存在。", true);
        return new(await settings.FindAsync(id, ct) ?? new(new(), null), null);
    }
    /// <summary>追加独立配置版本并返回收据，不覆盖旧记录或调用模型。</summary>
    public async Task<EmbeddingOperation<RerankSettingsResponse>> SaveAsync(ModelId id, RerankSettings value, CancellationToken ct)
    {
        if (await ResolveAsync(id, ct) is not { }) return new(null, "重排序模型不存在。", true);
        if (RerankValidation.Settings(value) is { } error) return new(null, error);
        return new(await settings.SaveAsync(id, value, ct), null);
    }
    /// <summary>仅按当前已保存参数测试一次，返回实际配置版本和无秘密收据。</summary>
    public async Task<EmbeddingOperation<RerankTestResponse>> TestAsync(ModelId id, RerankTestRequest request, CancellationToken ct)
    {
        if (await ResolveAsync(id, ct) is not { } resources) return new(null, "重排序模型不存在。", true);
        var configuration = await settings.FindAsync(id, ct);
        if (configuration?.Version is null) return new(null, "请先保存重排序配置，再发送测试。");
        if (RerankValidation.Inputs(configuration.Settings, request.Query, request.Documents) is { } error) return new(null, error);
        string key;
        try { key = resources.Provider.ApiKey is null ? "" : protector.Unprotect(resources.Provider.ApiKey); }
        catch (CryptographicException) { return new(null, "重排序模型密钥无法解密，请重新保存渠道密钥。"); }
        var started = Stopwatch.GetTimestamp();
        var result = await client.RankAsync(new(resources.Provider, resources.Model, key, configuration.Settings, request.Query, request.Documents), ct);
        return new(new(result.Ok, result.Code, result.Error, result.StatusCode, result.Scores,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, configuration.Version, result.TotalTokens), null);
    }
    /// <summary>仅允许管理所属渠道中的重排序模型；发送测试时由客户端核对启用状态、协议和端点许可。</summary>
    private async Task<(AiModel Model, AiProvider Provider)?> ResolveAsync(ModelId id, CancellationToken ct)
    {
        var model = await providers.FindModelAsync(id, ct);
        var provider = model is null ? null : await providers.FindAsync(model.ProviderId, ct);
        return model?.Category == ModelCategory.Rerank && provider is not null && provider.Models.Any(candidate => candidate.Id == model.Id)
            ? (model, provider) : null;
    }
}
