using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

internal sealed class VideoGenerationDispatcher : IVideoGenerationDispatcher
{
    /// <summary>按已解析协议索引的已注册上游客户端。</summary>
    private readonly Dictionary<ProviderType, IVideoGenerationClient> _clients;

    /// <summary>初始化 VideoGenerationDispatcher 的依赖与实例状态。</summary>
    public VideoGenerationDispatcher(IEnumerable<IVideoGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    /// <summary>按显式协议提交视频生成；未注册客户端时返回失败而不尝试其他协议。</summary>
    public Task<VideoSubmitResult> SubmitAsync(
        ProviderType protocol,
        VideoGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            return Task.FromResult(VideoSubmitResult.Failure(
                null,
                $"No video generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.SubmitAsync(request, ct);
    }

    /// <summary>仅向实现视频续接端口的协议客户端提交续接请求；缺少客户端或能力时返回失败。</summary>
    public Task<VideoSubmitResult> ExtendAsync(
        ProviderType protocol,
        VideoExtensionRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            return Task.FromResult(VideoSubmitResult.Failure(
                null,
                $"No video client is registered for protocol '{protocol.ToWire()}'."));
        }

        if (client is not IVideoExtensionClient extensionClient)
        {
            return Task.FromResult(VideoSubmitResult.Failure(
                400,
                $"Video extension is not supported by protocol '{protocol.ToWire()}'."));
        }

        return extensionClient.ExtendAsync(request, ct);
    }

    /// <summary>按任务协议查询已有上游视频任务，协议未注册时返回失败状态，不重新提交生成。</summary>
    public Task<VideoPollResult> PollAsync(
        ProviderType protocol,
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            return Task.FromResult(new VideoPollResult(
                VideoJobState.Failed,
                null,
                null,
                $"No video generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.PollAsync(provider, apiKey, upstreamJobId, ct);
    }
}
