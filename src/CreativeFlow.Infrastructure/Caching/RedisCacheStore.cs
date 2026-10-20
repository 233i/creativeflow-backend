using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Caching;
using CreativeFlow.Infrastructure.Options;
using StackExchange.Redis;

namespace CreativeFlow.Infrastructure.Caching;

/// <remarks>
/// 刻意避免使用 <c>LuaScript</c> 以及任何经过 <c>ScriptParameterMapper</c> 的路径：
/// 它们通过 <c>Expression.Compile()</c> 构建委托，在 Native AOT 下会抛出
/// <see cref="PlatformNotSupportedException"/>。确实需要脚本时，应改用原始的
/// <c>ScriptEvaluateAsync(string, RedisKey[], RedisValue[])</c> 重载，参见
/// <see cref="RedisDistributedLock"/>。
/// </remarks>
/// <summary>初始化 RedisCacheStore 的依赖与实例状态。</summary>
public sealed class RedisCacheStore(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
    : ICacheStore
{
    /// <summary>当前缓存、存储或密钥格式使用的固定前缀。</summary>
    private readonly string _prefix = options.Value.InstanceName;

    /// <summary>读取带命名空间的 Redis 字符串并使用给定 JSON 元数据反序列化；空值返回默认结果。</summary>
    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var value = await multiplexer.GetDatabase().StringGetAsync(Key(key));
        if (value.IsNullOrEmpty)
        {
            return default;
        }

        return JsonSerializer.Deserialize((string)value!, typeInfo);
    }

    /// <summary>使用给定 JSON 元数据序列化缓存值，并按指定存活时间写入 Redis。</summary>
    public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, TimeSpan ttl, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        await multiplexer.GetDatabase().StringSetAsync(Key(key), json, ttl);
    }

    /// <summary>删除带命名空间的 Redis 缓存键，返回该键是否实际被删除。</summary>
    public async Task<bool> RemoveAsync(string key, CancellationToken ct) =>
        await multiplexer.GetDatabase().KeyDeleteAsync(Key(key));

    /// <summary>为当前实例的缓存或在线状态键添加配置前缀。</summary>
    private string Key(string key) => _prefix + key;
}
