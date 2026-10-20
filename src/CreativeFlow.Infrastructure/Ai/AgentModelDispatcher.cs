using System.Runtime.CompilerServices;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

internal sealed class AgentModelDispatcher : IAgentModelDispatcher
{
    /// <summary>按已解析协议索引的已注册上游客户端。</summary>
    private readonly Dictionary<ProviderType, IAgentModelClient> _clients;

    /// <summary>初始化 AgentModelDispatcher 的依赖与实例状态。</summary>
    public AgentModelDispatcher(IEnumerable<IAgentModelClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    /// <summary>按显式协议选择 Agent 客户端并逐条转发流事件；未注册协议时返回失败事件。</summary>
    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        ProviderType protocol,
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            yield return new AgentModelEvent(
                AgentModelEventKind.Failed,
                Error: $"No agent client is registered for protocol '{protocol.ToWire()}'.");
            yield break;
        }

        await foreach (var item in client.StreamAsync(request, ct))
        {
            yield return item;
        }
    }
}
