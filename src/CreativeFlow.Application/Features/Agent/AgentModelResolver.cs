using System.Security.Cryptography;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>在已恢复的账号范围内解析启用的聊天模型和密钥，不调用模型、不改变运行状态。</summary>
public sealed class AgentModelResolver(IProviderRepository providers, IApiKeyProtector protector)
{
    /// <summary>保留服务商、模型和解密的原校验顺序，并返回既有用户错误。</summary>
    internal async Task<(AgentModelBinding? Binding, string? Error)> ResolveAsync(AgentRun run, CancellationToken ct)
    {
        var provider = run.ProviderId is { } providerId
            ? await providers.FindAsync(providerId, ct)
            : null;
        if (provider is null)
        {
            return (null, "The selected provider is no longer available.");
        }

        var model = provider.Models.FirstOrDefault(candidate =>
            candidate.ModelKey == run.ModelKey && candidate.Category == ModelCategory.Chat);
        if (model is null || !model.Enabled || !provider.Enabled)
        {
            return (null, "The selected Agent model is unavailable or disabled.");
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null ? string.Empty : protector.Unprotect(provider.ApiKey);
        }
        catch (CryptographicException)
        {
            return (null, "The stored API key could not be decrypted. Re-enter it in settings.");
        }
        return (new AgentModelBinding(provider, model, apiKey), null);
    }
}

/// <param name="Provider">本次运行已校验的服务商配置。</param>
/// <param name="Model">本次运行已校验的启用聊天模型。</param>
/// <param name="ApiKey">仅供本次请求使用的解密密钥，不能写入事件或消息。</param>
internal sealed record AgentModelBinding(AiProvider Provider, AiModel Model, string ApiKey)
{
    /// <summary>模型未配置有效输出上限时使用的原有 token 限制。</summary>
    private const int DefaultMaxOutputTokens = 16_000;

    /// <summary>沿用模型覆盖及服务商协议的解析规则。</summary>
    public ProviderType Protocol => Provider.ResolveProtocol(Model);

    /// <summary>按原提示词、历史和模型参数构造单轮请求，不复制或重排消息。</summary>
    public AgentModelRequest CreateRequest(List<AgentModelMessage> messages, IReadOnlyList<AgentToolDefinition> tools) =>
        new(Provider, ApiKey, Model.ModelKey, AgentSystemPrompt.Text, messages, tools,
            Model.MaxOutputTokens is > 0 and <= 64_000 ? Model.MaxOutputTokens.Value : DefaultMaxOutputTokens,
            SupportsThinking: Model.SupportsThinking);
}
