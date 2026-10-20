using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using CreativeFlow.Application.Abstractions.Messaging;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Messaging;

/// <summary>
/// 将集成事件发布到主题交换机，并启用发布确认。
/// </summary>
public sealed class RabbitMqEventPublisher(
    RabbitMqConnectionProvider connections,
    IOptions<RabbitMqOptions> options) : IEventPublisher, IAsyncDisposable
{
    /// <summary>保护共享连接或可变状态的同步原语。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>当前组件在启动时取得的配置快照。</summary>
    private readonly RabbitMqOptions _options = options.Value;
    /// <summary>当前复用的 RabbitMQ 通道。</summary>
    private IChannel? _channel;

    /// <summary>使用给定 JSON 元数据序列化事件，通过复用的 RabbitMQ 通道发布带消息标识的持久消息。</summary>
    public async Task PublishAsync<T>(string routingKey, T payload, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var channel = await GetChannelAsync(ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = Guid.CreateVersion7().ToString("N"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);
    }

    /// <summary>
    /// 通道不是线程安全的，因此需要保护唯一的缓存通道。发布操作会因此串行化——
    /// 对当前数据量可以接受，也是正确的默认选择。
    /// </summary>
    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        // 通道失效时串行重建并重新声明既有交换机，避免并发调用复用已经关闭的发布通道。
        if (_channel is { IsOpen: true } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true } current)
            {
                return current;
            }

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            var connection = await connections.GetConnectionAsync(ct);
            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct);

            await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>异步释放发布通道并清空引用，随后释放通道创建互斥锁。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        _gate.Dispose();
    }
}
