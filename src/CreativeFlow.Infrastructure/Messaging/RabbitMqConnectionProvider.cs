using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Messaging;

/// <summary>
/// 管理唯一的长连接 AMQP 连接。
/// </summary>
/// <remarks>
/// 连接创建成本高且线程安全，因此共享使用；通道创建成本低但<em>不是</em>线程安全的，
/// 所以每个发布者和消费者各自创建。连接在信号量保护下延迟打开，而非在构造函数中打开，
/// 这样消息代理短暂不可用只会延迟首次使用，不会阻止应用启动。
/// </remarks>
public sealed class RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    /// <summary>保护共享连接或可变状态的同步原语。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>当前组件在启动时取得的配置快照。</summary>
    private readonly RabbitMqOptions _options = options.Value;
    /// <summary>当前复用的 RabbitMQ 连接。</summary>
    private IConnection? _connection;

    /// <summary>复用仍然有效的 RabbitMQ 连接，并在必要时重新建立。</summary>
    public async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        // 先尝试复用现有连接，重建时进入互斥区并再次检查，避免并发发布各自创建连接。
        if (_connection is { IsOpen: true } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>异步释放持有的 RabbitMQ 连接并清空引用，随后释放连接创建互斥锁。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }
}
