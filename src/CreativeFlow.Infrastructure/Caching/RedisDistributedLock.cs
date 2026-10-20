using Microsoft.Extensions.Options;
using CreativeFlow.Infrastructure.Options;
using StackExchange.Redis;

namespace CreativeFlow.Infrastructure.Caching;

/// <summary>
/// 基于 Redis（SET NX PX）的尽力而为互斥锁。
/// </summary>
/// <remarks>
/// 适合抑制重复工作，不适合保障正确性：进程暂停超过生存时间后可能已失去锁，却仍以为自己持有它。
/// 当正确性依赖互斥时，应使用数据库事务或 PostgreSQL 咨询锁。
/// </remarks>
public sealed class RedisDistributedLock(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
{
    /// <summary>
    /// 仅在令牌仍匹配时释放，防止旧持有者删除已经过期并被其他位置重新获取的锁。
    /// </summary>
    private const string ReleaseScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    /// <summary>当前缓存、存储或密钥格式使用的固定前缀。</summary>
    private readonly string _prefix = options.Value.InstanceName + "lock:";

    /// <summary>仅在键不存在时写入带有效期的所有者令牌，取得锁后返回令牌，否则返回空值。</summary>
    public async Task<string?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken ct)
    {
        var token = Guid.CreateVersion7().ToString("N");
        var acquired = await multiplexer.GetDatabase()
            .StringSetAsync(_prefix + name, token, ttl, When.NotExists);

        return acquired ? token : null;
    }

    /// <summary>仅当锁中保存的所有者令牌仍匹配时释放 Redis 锁。</summary>
    public async Task<bool> ReleaseAsync(string name, string token, CancellationToken ct)
    {
        // 使用显式键值数组的原始脚本形式。LuaScript.Prepare(...) API 通过
        // Expression.Compile() 构建参数映射器，因此在 Native AOT 下会失败。
        var result = await multiplexer.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript,
            [_prefix + name],
            [token]);

        return (long)result == 1;
    }
}
