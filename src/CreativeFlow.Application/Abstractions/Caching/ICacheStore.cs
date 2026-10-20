using System.Text.Json.Serialization.Metadata;

namespace CreativeFlow.Application.Abstractions.Caching;

/// <summary>
/// 键值缓存。
/// </summary>
/// <remarks>
/// <see cref="JsonTypeInfo{T}"/> 被刻意纳入方法签名。Native AOT 下，基于反射的
/// <c>JsonSerializer</c> 重载会抛出异常，而该问题在 JIT 调试时不会出现，因而很容易遗漏。
/// 要求调用处提供源生成的元数据，可从结构上杜绝不兼容 AOT 的路径。
/// </remarks>
public interface ICacheStore
{
    /// <summary>使用调用方提供的 JSON 元数据读取缓存，键不存在时返回空值。</summary>
    Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct);

    /// <summary>使用显式 JSON 元数据写入缓存，并保留调用方指定的有效期。</summary>
    Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, TimeSpan ttl, CancellationToken ct);

    /// <summary>移除指定缓存键对应的数据。</summary>
    Task<bool> RemoveAsync(string key, CancellationToken ct);
}
