using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Caching;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;
using StackExchange.Redis;

namespace CreativeFlow.Infrastructure.Caching;

/// <summary>
/// 在 Redis 集合中按设备跟踪活动的实时连接。
/// </summary>
/// <remarks>
/// 使用集合而非计数器，因为一个设备可能持有多个连接（多个标签页）；
/// 若进程未执行断开处理器便终止，计数器会永久向上漂移。集合成员关系具有幂等性，
/// 生存时间也能限制清理丢失造成的影响。
/// </remarks>
public sealed class RedisPresenceTracker(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
    : IPresenceTracker
{
    /// <summary>Redis 在线连接记录的有效期。</summary>
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromHours(12);

    /// <summary>当前缓存、存储或密钥格式使用的固定前缀。</summary>
    private readonly string _prefix = options.Value.InstanceName + "presence:";

    /// <summary>将连接加入设备在线集合并刷新集合的存活时间。</summary>
    public async Task MarkOnlineAsync(DeviceId deviceId, string connectionId, CancellationToken ct)
    {
        var db = multiplexer.GetDatabase();
        var key = Key(deviceId);
        await db.SetAddAsync(key, connectionId);
        await db.KeyExpireAsync(key, PresenceTtl);
    }

    /// <summary>从设备在线集合移除断开的连接；最后一个连接离开后删除空集合。</summary>
    public async Task MarkOfflineAsync(DeviceId deviceId, string connectionId, CancellationToken ct)
    {
        var db = multiplexer.GetDatabase();
        var key = Key(deviceId);
        await db.SetRemoveAsync(key, connectionId);

        // 最后一个连接断开后彻底删除键，防止空闲设备积累空集合。
        if (await db.SetLengthAsync(key) == 0)
        {
            await db.KeyDeleteAsync(key);
        }
    }

    /// <summary>根据设备连接集合是否非空判断在线状态。</summary>
    public async Task<bool> IsOnlineAsync(DeviceId deviceId, CancellationToken ct) =>
        await multiplexer.GetDatabase().SetLengthAsync(Key(deviceId)) > 0;

    /// <summary>读取设备当前登记的在线连接数量。</summary>
    public async Task<long> ConnectionCountAsync(DeviceId deviceId, CancellationToken ct) =>
        await multiplexer.GetDatabase().SetLengthAsync(Key(deviceId));

    /// <summary>为当前实例的缓存或在线状态键添加配置前缀。</summary>
    private RedisKey Key(DeviceId deviceId) => _prefix + deviceId.Value.ToString("N");
}
