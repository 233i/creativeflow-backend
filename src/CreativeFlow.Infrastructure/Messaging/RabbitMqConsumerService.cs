using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Messaging;

/// <summary>
/// 消费集成事件，并在消息代理或通道断开时按退避策略重连。
/// </summary>
public sealed class RabbitMqConsumerService(
    RabbitMqConnectionProvider connections,
    IntegrationEventHandlerRegistry registry,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    /// <summary>消息连接重试采用的初始等待间隔。</summary>
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    /// <summary>消息连接失败后重连等待的最大间隔。</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>当前组件在启动时取得的配置快照。</summary>
    private readonly RabbitMqOptions _options = options.Value;

    /// <summary>持续消费 RabbitMQ 消息，连接故障按原退避规则恢复，宿主正常取消时停止重连。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 消费循环在连接故障后按既有退避恢复，正常停止应退出，不能被当成失败持续重连。
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ consumer loop failed; retrying in {Backoff}.", backoff);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    /// <summary>声明消息拓扑后消费队列，保持原有确认与预取设置。</summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        var connection = await connections.GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await RabbitMqTopology.DeclareAsync(channel, _options, ct);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, ct);

        await channel.BasicConsumeAsync(
            queue: _options.QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);

        logger.LogInformation(
            "Consuming {Queue} bound to {Exchange} on [{RoutingKeys}].",
            _options.QueueName, _options.Exchange, string.Join(", ", _options.RoutingKeys));

        // 保持通道打开直至关闭；消息投递在消费者回调中完成。
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>分派消息并按既有一次重投与死信规则确认处理结果。</summary>
    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        // 此处理器一旦让出执行，库便会复用 ea.Body 背后的缓冲区，因此必须在任何 await 前复制负载。
        // 持有 ReadOnlyMemory 会在高负载下产生损坏数据，而且几乎无法按需复现。
        var body = ea.Body.ToArray();

        try
        {
            var handled = await registry.TryDispatchAsync(ea.RoutingKey, body, ct);
            if (!handled)
            {
                logger.LogWarning("No handler for routing key {RoutingKey}; discarding.", ea.RoutingKey);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            // 重试一次，然后交给死信交换机。无限制重新入队会让一条毒消息形成死循环，
            // 使队列中的其他消息全部得不到处理。
            var requeue = !ea.Redelivered;
            logger.LogError(
                ex, "Handler for {RoutingKey} failed; requeue={Requeue}.", ea.RoutingKey, requeue);

            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: requeue, ct);
        }
    }
}
