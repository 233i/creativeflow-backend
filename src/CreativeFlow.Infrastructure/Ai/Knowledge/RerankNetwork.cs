using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

/// <summary>重排序独立的端点授权，不因持有渠道密钥就开放整个站点；连接仍固定经过校验的公网 IP。</summary>
internal sealed class RerankNetwork(EmbeddingOptions options, bool development)
{
    /// <summary>重排独立的端点许可策略，不继承工作流或向量模型的白名单。</summary>
    public EmbeddingEndpointPolicy Policy { get; } = new(options, development, allowPublicHttpsWildcard: true);
}
