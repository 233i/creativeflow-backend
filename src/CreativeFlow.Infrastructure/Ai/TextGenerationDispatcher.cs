using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

internal sealed class TextGenerationDispatcher : ITextGenerationDispatcher
{
    /// <summary>按已解析协议索引的已注册上游客户端。</summary>
    private readonly Dictionary<ProviderType, ITextGenerationClient> _clients;

    /// <summary>初始化 TextGenerationDispatcher 的依赖与实例状态。</summary>
    public TextGenerationDispatcher(IEnumerable<ITextGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    /// <summary>按显式协议调用文本生成客户端；协议未注册文本能力时返回配置失败。</summary>
    public Task<TextGenerationResult> GenerateAsync(
        ProviderType protocol,
        TextGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            // 聊天模型的协议覆盖指定图片或视频协议时会进入此处。
            return Task.FromResult(TextGenerationResult.Failure(
                null,
                $"No text generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.GenerateAsync(request, ct);
    }
}
