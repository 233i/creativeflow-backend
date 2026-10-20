using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

/// <summary>重排序绑定不可变配置版本；模型禁用、归属或配置变化不能被降级策略绕过。</summary>
internal sealed class KnowledgeReranker(IProviderRepository providers, IRerankSettingsRepository settings,
    IApiKeyProtector protector, IRerankClient client, RerankNetwork network, WorkflowModelCallTrace trace) : IKnowledgeReranker
{
    /// <summary>冻结可用模型身份、端点及配置版本，不保存明文凭据或触发推理。</summary>
    public async Task<string> CaptureAsync(Guid modelId, CancellationToken ct)
    {
        var (model, provider, configuration) = await ResolveAsync(modelId, ct);
        try { network.Policy.ValidateEndpoint(new Uri(provider.BaseUrl.TrimEnd('/') + "/" + configuration.Settings.EndpointPath)); }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException)
        { throw new KnowledgeException("重排序端点未获许可，请配置 Rerank:AllowedEndpoints 完整白名单。"); }
        return new JsonObject { ["modelId"] = model.Id.ToString(), ["providerId"] = provider.Id.ToString(),
            ["modelKey"] = model.ModelKey, ["baseUrl"] = provider.BaseUrl, ["protocol"] = "rerank",
            ["configurationVersion"] = configuration.Version }.ToJsonString();
    }

    /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
    public async Task<RerankResult> RankAsync(string snapshot, string query, IReadOnlyList<string> documents, CancellationToken ct, string? nodeId = null)
    {
        var saved = JsonNode.Parse(snapshot)!;
        var modelId = Guid.Parse(saved["modelId"]!.GetValue<string>());
        if (!JsonNode.DeepEquals(saved, JsonNode.Parse(await CaptureAsync(modelId, ct))))
            throw new KnowledgeException("重排序模型配置已变化，请检查配置后创建新运行。");
        var (model, provider, configuration) = await ResolveAsync(modelId, ct);
        if (configuration.Version != saved["configurationVersion"]!.GetValue<string>() || provider.Id.ToString() != saved["providerId"]!.GetValue<string>()
            || provider.BaseUrl != saved["baseUrl"]!.GetValue<string>()
            || model.ModelKey != saved["modelKey"]!.GetValue<string>()) throw new KnowledgeException("重排序模型配置正在变化，未发送候选正文。");
        string key;
        try { key = provider.ApiKey is null ? "" : protector.Unprotect(provider.ApiKey); }
        catch (CryptographicException) { throw new KnowledgeException("重排序模型密钥无法解密，请在渠道配置中重新保存。"); }
        if (nodeId is not null) await trace.BeginRerankAsync(nodeId, provider, model, documents.Count, ct);
        try
        {
            var result = await client.RankAsync(new(provider, model, key, configuration.Settings, query, documents), ct);
            if (nodeId is not null) await trace.FinishRerankAsync(result, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (nodeId is not null) await trace.FinishRerankAsync(RerankResult.Failure("timeout_unknown", "请求取消，消费状态待核对。"), CancellationToken.None);
            throw;
        }
    }

    /// <summary>拒绝不存在、停用、用途或协议不匹配的模型，冻结配置必须与当前一致。</summary>
    private async Task<(AiModel Model, AiProvider Provider, CreativeFlow.Application.Contracts.Ai.RerankSettingsResponse Settings)> ResolveAsync(Guid id, CancellationToken ct)
    {
        var model = await providers.FindModelAsync(new(id), ct);
        var provider = model is null ? null : await providers.FindAsync(model.ProviderId, ct);
        if (model?.Category != ModelCategory.Rerank || !model.Enabled || provider?.Enabled != true
            || !provider.Models.Any(item => item.Id == model.Id) || provider.ResolveProtocol(model) != ProviderType.Rerank)
            throw new KnowledgeException("请选择已启用的重排序模型，原模型可能已停用或撤权。");
        var configuration = await settings.FindAsync(model.Id, ct);
        if (configuration?.Version is null) throw new KnowledgeException("请先保存重排序模型配置再使用。");
        if (RerankValidation.Settings(configuration.Settings) is { } error) throw new KnowledgeException(error);
        return (model, provider, configuration);
    }
}
