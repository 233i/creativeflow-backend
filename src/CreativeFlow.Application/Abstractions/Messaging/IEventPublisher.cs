using System.Text.Json.Serialization.Metadata;

namespace CreativeFlow.Application.Abstractions.Messaging;

/// <summary>
/// 将集成事件发布到消息代理。
/// </summary>
/// <remarks>
/// 与 <see cref="Caching.ICacheStore"/> 一样，由调用方提供源生成的 JSON 元数据，
/// 确保 AOT 下不存在回退到反射序列化的路径。
/// </remarks>
public interface IEventPublisher
{
    /// <summary>使用调用方明确提供的 JSON 元数据发布集成事件。</summary>
    Task PublishAsync<T>(string routingKey, T payload, JsonTypeInfo<T> typeInfo, CancellationToken ct);
}
