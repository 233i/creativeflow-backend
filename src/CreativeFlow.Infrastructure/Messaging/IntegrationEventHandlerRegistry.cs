namespace CreativeFlow.Infrastructure.Messaging;

/// <summary>
/// 将路由键映射到处理器。
/// </summary>
/// <remarks>
/// 在 <c>DependencyInjection</c> 中手动填充。常见的程序集扫描方式在此不可用：
/// Native AOT 下它找不到也不会注册任何内容，同时不报告错误，
/// 导致所有 JIT 模式测试均通过，但生产环境的消费者静默地不处理任何消息。
/// </remarks>
public sealed class IntegrationEventHandlerRegistry
{
    /// <summary>按事件路由索引的显式处理器注册表。</summary>
    private readonly Dictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);

    /// <summary>按路由键显式登记事件反序列化与处理委托。</summary>
    public IntegrationEventHandlerRegistry Register(
        string routingKey, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        _handlers[routingKey] = handler;
        return this;
    }

    /// <summary>判断是否注册了对应消息路由的处理器。</summary>
    public bool CanHandle(string routingKey) => _handlers.ContainsKey(routingKey);

    /// <summary>未注册处理器时返回假，使调用方可以确认消息并继续。</summary>
    public async Task<bool> TryDispatchAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(routingKey, out var handler))
        {
            return false;
        }

        await handler(body, ct);
        return true;
    }
}
