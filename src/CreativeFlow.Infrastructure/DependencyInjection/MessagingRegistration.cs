using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Messaging;
using CreativeFlow.Infrastructure.Messaging;

namespace CreativeFlow.Infrastructure;

/// <summary>按功能组织基础设施注册，保留同一公共装配入口。</summary>
public static partial class DependencyInjection
{
    /// <summary>注册 RabbitMQ 连接、发布者、消费者及显式事件处理器。</summary>
    private static void AddMessaging(IServiceCollection services)
    {
        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();

        // 显式注册处理器。程序集扫描在 AOT 下会静默地不注册任何内容，
        // 从而导致消费者仍在运行却不处理消息。
        services.AddSingleton(_ => new IntegrationEventHandlerRegistry());

        services.AddHostedService<RabbitMqConsumerService>();
    }
}
