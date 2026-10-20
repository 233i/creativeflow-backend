using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Embeddings;

/// <summary>知识索引绑定显式的配置版本、端点、模型和维度；密钥轮换不改变绑定。</summary>
internal sealed class KnowledgeEmbedding(IProviderRepository providers, IEmbeddingSettingsRepository settings,
    IApiKeyProtector protector, IEmbeddingDispatcher dispatcher) : IKnowledgeEmbedding
{
    /// <summary>保存不可含密钥的配置快照；相同维度但不同模型或配置也必须重建索引。</summary>
    public async Task<string> CaptureAsync(Guid modelId, CancellationToken ct)
    {
        var model = await providers.FindModelAsync(new ModelId(modelId), ct);
        var provider = model is null ? null : await providers.FindAsync(model.ProviderId, ct);
        var configuration = model is null ? null : await settings.FindAsync(model.Id, ct);
        if (model?.Category != ModelCategory.Embedding || !model.Enabled || provider?.Enabled != true
            || !provider.Models.Any(item => item.Id == model.Id) || provider.ResolveProtocol(model) != ProviderType.OpenAiEmbeddings)
            throw new KnowledgeException("请选择已启用且使用 openai-embeddings 的向量模型。");
        if (configuration?.Version is null || configuration.Settings.ExpectedDimensions is not (>= 1 and <= 8192))
            throw new KnowledgeException("请先在向量配置中保存实测维度（本批次上限 8192）。");
        if (EmbeddingValidation.ValidateSettings(configuration.Settings) is { } error) throw new KnowledgeException(error);
        return new JsonObject
        {
            ["modelId"] = modelId.ToString(), ["providerId"] = provider.Id.ToString(), ["modelKey"] = model.ModelKey,
            ["baseUrl"] = provider.BaseUrl, ["protocol"] = "openai-embeddings", ["configurationVersion"] = configuration.Version,
            ["dimensions"] = configuration.Settings.ExpectedDimensions, ["batchSize"] = configuration.Settings.MaxBatchSize,
        }.ToJsonString();
    }

    /// <summary>每一批及每次查询重新核验模型与配置，解密仅在真正发送前发生。</summary>
    public async Task<EmbeddingResult> EmbedAsync(string snapshot, IReadOnlyList<EmbeddingInput> inputs, string purpose, CancellationToken ct)
    {
        var modelId = Guid.Parse(JsonNode.Parse(snapshot)!["modelId"]!.GetValue<string>());
        if (!JsonNode.DeepEquals(JsonNode.Parse(await CaptureAsync(modelId, ct)), JsonNode.Parse(snapshot)))
            throw new KnowledgeException("向量模型或配置已改变，不能混用旧索引；请创建新的候选版本。");
        var model = (await providers.FindModelAsync(new ModelId(modelId), ct))!;
        var provider = (await providers.FindAsync(model.ProviderId, ct))!;
        var configuration = (await settings.FindAsync(model.Id, ct))!;
        // 再核验读取出的版本，避免配置正好在前一个检查之后被替换。
        if (configuration.Version != JsonNode.Parse(snapshot)!["configurationVersion"]!.GetValue<string>()
            || provider.BaseUrl != JsonNode.Parse(snapshot)!["baseUrl"]!.GetValue<string>()
            || model.ModelKey != JsonNode.Parse(snapshot)!["modelKey"]!.GetValue<string>())
            throw new KnowledgeException("模型配置正在变化，请重新检查索引配置。");
        string key;
        try { key = provider.ApiKey is null ? "" : protector.Unprotect(provider.ApiKey); }
        catch (CryptographicException) { throw new KnowledgeException("模型密钥无法解密，请在渠道配置中重新保存。"); }
        return await dispatcher.EmbedAsync(new(provider, model, key, configuration.Settings, inputs, purpose), ct);
    }
}
