using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Caching;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Infrastructure.Caching;
using CreativeFlow.Infrastructure.Options;
using StackExchange.Redis;

namespace CreativeFlow.Infrastructure;

/// <summary>按功能组织基础设施注册，保留同一公共装配入口。</summary>
public static partial class DependencyInjection
{
    /// <summary>注册共享 Redis 连接以及缓存、在线状态与锁实现。</summary>
    private static void AddCaching(IServiceCollection services)
    {
        // 多路复用器内部已实现复用且创建成本较高——每个进程一个，绝不按请求创建。
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            var config = ConfigurationOptions.Parse(options.Configuration);
            config.AbortOnConnectFail = false;
            // 刻意保持为假：此处从不需要管理命令，且 3.x 版本会在 Execute 时强制检查该标志。
            config.AllowAdmin = false;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<ICacheStore, RedisCacheStore>();
        services.AddSingleton<IPresenceTracker, RedisPresenceTracker>();
        services.AddSingleton<RedisDistributedLock>();
    }
}
