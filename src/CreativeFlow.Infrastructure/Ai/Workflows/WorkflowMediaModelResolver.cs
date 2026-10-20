using System.Security.Cryptography;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>解析媒体节点使用的管理员模型角色与续接能力，不更改模型绑定规则。</summary>
internal sealed class WorkflowMediaModelResolver(
    IModelSlotRepository slots, IProviderRepository providers, IApiKeyProtector protector)
{
    /// <summary>原视频续接模型标识，仅允许已启用的 xAI 协议。</summary>
    private const string XaiVideoExtensionModelKey = "grok-imagine-video";

    /// <summary>按模型角色、类别和启用状态解析工作流使用的渠道与模型。</summary>
    internal async Task<WorkflowManagedModel> ResolveAsync(
        string slotKind,
        ModelCategory category,
        CancellationToken ct,
        string? requestedProviderId = null,
        string? requestedModelId = null)
    {
        // 图片创作节点可以固定选择一个已启用模型；没有显式选择时继续使用角色默认值，
        // 这样新图可以在多个启用模型之间选择，旧图仍沿用后台的 image 槽位。
        var hasRequestedSelection = !string.IsNullOrWhiteSpace(requestedProviderId)
            || !string.IsNullOrWhiteSpace(requestedModelId);
        ProviderId providerId;
        ModelId modelId;
        if (hasRequestedSelection)
        {
            if (!ProviderId.TryParse(requestedProviderId, out providerId)
                || !ModelId.TryParse(requestedModelId, out modelId))
            {
                throw new WorkflowExecutionException(
                    $"The workflow {category.ToString().ToLowerInvariant()} model selection is invalid.");
            }
        }
        else
        {
            var slot = (await slots.ListAsync(ct))
                .FirstOrDefault(candidate => candidate.Kind == slotKind);
            if (slot?.ProviderId is not { } configuredProviderId
                || slot.ModelId is not { } configuredModelId)
            {
                throw new WorkflowExecutionException(
                    $"The administrator has not configured the {slotKind} model.");
            }

            providerId = configuredProviderId;
            modelId = configuredModelId;
        }

        if (providerId == default || modelId == default)
        {
            throw new WorkflowExecutionException(
                $"The workflow {category.ToString().ToLowerInvariant()} model selection is invalid.");
        }

        var provider = await providers
            .FindAsync(providerId, ct);
        var model = provider?.Models.FirstOrDefault(candidate => candidate.Id == modelId);
        if (provider is null || model is null || !provider.Enabled || !model.Enabled
            || model.Category != category)
        {
            throw new WorkflowExecutionException(
                $"The administrator-configured {slotKind} model is unavailable.");
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
            throw new WorkflowExecutionException("The configured model credential cannot be read.");
        }

        return new WorkflowManagedModel(provider, model, apiKey);
    }

    /// <summary>选择满足现有续接能力约束的视频模型。</summary>
    internal async Task<WorkflowManagedModel> ResolveVideoExtensionAsync(
        CancellationToken ct)
    {
        var configured = await ResolveAsync("video", ModelCategory.Video, ct);
        var extensionModel = configured.Provider.Models.FirstOrDefault(candidate =>
            candidate.Enabled
            && candidate.Category == ModelCategory.Video
            && string.Equals(
                candidate.ModelKey, XaiVideoExtensionModelKey, StringComparison.OrdinalIgnoreCase)
            && configured.Provider.ResolveProtocol(candidate) == ProviderType.XaiVideo);

        if (extensionModel is null)
        {
            throw new WorkflowExecutionException(
                $"The selected video provider does not have an enabled " +
                $"'{XaiVideoExtensionModelKey}' xAI extension model.");
        }

        return configured with { Model = extensionModel };
    }
}

/// <summary>一次执行解析出的服务商、模型与临时明文凭据，不直接写入快照。</summary>
/// <param name="Provider">当前授权范围内的服务商。</param>
/// <param name="Model">已启用且类别匹配的模型。</param>
/// <param name="ApiKey">仅用于本次上游请求的解密凭据。</param>
internal sealed record WorkflowManagedModel(AiProvider Provider, AiModel Model, string ApiKey);
