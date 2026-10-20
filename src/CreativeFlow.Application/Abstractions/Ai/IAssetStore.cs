namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 将生成的媒体存入持久化素材后端，并返回其存储位置记录。
/// </summary>
/// <remarks>
/// 唯一后端是持久化文件系统；数据库仅记录元数据、引用和恢复状态。
/// </remarks>
public interface IAssetStore
{
    /// <param name="extensionHint">
    /// 根据媒体类型推导的首选文件扩展名。内容寻址后端可能忽略它；提供内容时使用已存储的媒体类型，
    /// 而非文件名扩展名。
    /// </param>
    /// <summary>保存素材字节并返回不透明存储键、媒体类型与长度。</summary>
    Task<StoredAsset> SaveAsync(
        byte[] bytes,
        string mediaType,
        string extensionHint,
        CancellationToken ct);

    /// <summary>有界流式保存；稳定操作键使任务在文件落盘后、业务提交前中断也能复用原文件。</summary>
    Task<StoredAsset> SaveAsync(Stream source, string mediaType, string extensionHint, long maxBytes,
        CancellationToken ct, string? operationKey = null);

    /// <summary>找回已完成或可由磁盘完整检查点恢复的写入；不会访问上游或调用模型。</summary>
    Task<StoredAsset?> FindSavedAsync(string operationKey, CancellationToken ct);

    /// <summary>打开已存储素材以供读取；若内容已丢失则返回空值。</summary>
    /// <remarks>
    /// 文件缺失返回空，不回退到数据库；部署必须保留持久卷，缺失原件需从备份恢复。
    /// </remarks>
    Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct);

    /// <summary>登记文件清理；仍有引用或处于保护期时延迟回收，绝不连带删除其他资产。</summary>
    Task DeleteAsync(string storagePath, CancellationToken ct);
}

/// <param name="StoragePath">不透明的后端键，绝不是由用户控制的文件系统路径。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
public sealed record StoredAsset(string StoragePath, string MediaType, long ByteSize);
