using RabbitMQ.Client;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Messaging;

/// <summary>
/// 声明交换机和队列。声明具有幂等性，因此每次启动和消费者重连时都会执行。
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>按既有命名规则构造死信交换机名称。</summary>
    public static string DeadLetterExchange(RabbitMqOptions options) => options.Exchange + ".dlx";

    /// <summary>按既有命名规则构造死信队列名称。</summary>
    public static string DeadLetterQueue(RabbitMqOptions options) => options.QueueName + ".dlq";

    /// <summary>创建或确认交换机、队列与死信绑定的既有拓扑。</summary>
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(
            exchange: options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        // 持续失败的消息必须进入可检查的位置，而不能永久循环或消失。
        var dlx = DeadLetterExchange(options);
        var dlq = DeadLetterQueue(options);

        await channel.ExchangeDeclareAsync(
            exchange: dlx, type: ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: dlq, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        await channel.QueueBindAsync(queue: dlq, exchange: dlx, routingKey: string.Empty, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = dlx },
            cancellationToken: ct);

        foreach (var routingKey in options.RoutingKeys)
        {
            await channel.QueueBindAsync(
                queue: options.QueueName,
                exchange: options.Exchange,
                routingKey: routingKey,
                cancellationToken: ct);
        }
    }
}
