using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>
/// 将生成请求路由到支持解析后协议的客户端。
/// </summary>
/// <remarks>
/// 根据已注册的 <see cref="IImageGenerationClient"/> 实现构建，而非使用硬编码分支，
/// 因此支持新供应商只需注册，无需在此修改。查询字典在构造时只建立一次；
/// 若改为每次请求扫描列表，会在热点路径上悄然产生开销。
/// </remarks>
internal sealed class ImageGenerationDispatcher : IImageGenerationDispatcher
{
    /// <summary>按已解析协议索引的已注册上游客户端。</summary>
    private readonly Dictionary<ProviderType, IImageGenerationClient> _clients;

    /// <summary>初始化 ImageGenerationDispatcher 的依赖与实例状态。</summary>
    public ImageGenerationDispatcher(IEnumerable<IImageGenerationClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Protocol);
    }

    /// <summary>按显式协议调用图片生成客户端；协议不具备图片客户端时返回配置失败。</summary>
    public Task<ImageGenerationResult> GenerateAsync(
        ProviderType protocol,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(protocol, out var client))
        {
            // 当模型协议覆盖指定了无法生成图片的协议时会进入此处，例如图片模型指定聊天协议。
            // 清晰报错优于为错误 API 组织请求。
            return Task.FromResult(ImageGenerationResult.Failure(
                null,
                $"No image generation client is registered for protocol '{protocol.ToWire()}'."));
        }

        return client.GenerateAsync(request, ct);
    }
}
