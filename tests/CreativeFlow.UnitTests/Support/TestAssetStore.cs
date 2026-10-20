using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.UnitTests.Support;

/// <summary>仅供小型单元测试使用的流转字节适配；持久化与 HTTP 集成测试使用真实文件存储。</summary>
internal abstract class TestAssetStore : IAssetStore
{
    public abstract Task<StoredAsset> SaveAsync(byte[] bytes, string mediaType, string extensionHint, CancellationToken ct);
    public virtual async Task<StoredAsset> SaveAsync(Stream source, string mediaType, string extensionHint, long maxBytes,
        CancellationToken ct, string? operationKey = null)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        if (buffer.Length > maxBytes) throw new InvalidDataException();
        return await SaveAsync(buffer.ToArray(), mediaType, extensionHint, ct);
    }
    public virtual Task<StoredAsset?> FindSavedAsync(string operationKey, CancellationToken ct) => Task.FromResult<StoredAsset?>(null);
    public abstract Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct);
    public abstract Task DeleteAsync(string storagePath, CancellationToken ct);
}
