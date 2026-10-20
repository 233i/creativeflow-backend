using System.Security.Cryptography;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>只负责受控目录内的有界文件操作；不接收用户提供的路径，也不读写业务数据库。</summary>
internal sealed class AssetFileSystem
{
    internal string Root { get; }
    private readonly bool requireMarker;

    public AssetFileSystem(AiOptions options)
    {
        requireMarker = options.RequireStorageMarker;
        Root = Path.GetFullPath(options.AssetRoot);
        if (options.RequireStorageMarker && (!Directory.Exists(Root) || !File.Exists(Path.Combine(Root, ".creativeflow-storage"))))
            throw new InvalidOperationException("持久化文件卷未初始化或未挂载：缺少 .creativeflow-storage 标记。禁止回退到容器临时目录。");
        CreatePrivateDirectory(Root);
        RejectLink(Root);
        CreatePrivateDirectory(Resolve("pending"));
        CreatePrivateDirectory(Resolve("objects"));
        CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Root);
    }

    /// <summary>逐级拒绝符号链接；文件根目录必须由服务专用账号独占写权限。</summary>
    internal string Resolve(string key)
    {
        if (string.IsNullOrEmpty(key) || Path.IsPathRooted(key) || key.Contains('\\')
            || key.Split('/').Any(p => p is "" or "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.')))
            throw new InvalidDataException("无效的文件存储键。");
        RejectLink(Root);
        // 卷在进程运行中丢失时也拒绝在同名空目录继续写入。
        if (requireMarker && !File.Exists(Path.Combine(Root, ".creativeflow-storage")))
            throw new IOException("持久化文件卷标记丢失，停止文件读写。");
        var current = Root;
        foreach (var part in key.Split('/'))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return current;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string PreparePath(string key)
    {
        var path = Resolve(key);
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent))
        {
            CreatePrivateDirectory(parent);
            CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Path.GetDirectoryName(parent)!);
        }
        Resolve(key);
        return path;
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("文件存储目录不能包含符号链接或重解析点。");
    }

    internal static string PendingKey(Guid id, Guid lease, string suffix) => $"pending/{id:N}/{lease:N}.{suffix}";
    internal static string ObjectKey(string sha256) => $"objects/{sha256[..2]}/{sha256}";
    internal static bool IsObjectKey(string key) => key.Length == 75 && key.StartsWith("objects/", StringComparison.Ordinal)
        && key[10] == '/' && key[8..10] == key[11..13] && key[11..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>完整文件仅在 Flush(true) 成功后发布为 .complete，半文件永远不能用于恢复。</summary>
    internal async Task<(string Hash, long Size)> WriteAsync(StorageWrite write, Stream source, CancellationToken ct)
    {
        var part = PreparePath(PendingKey(write.Id, write.Lease, "part"));
        var complete = Resolve(PendingKey(write.Id, write.Lease, "complete"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[131072];
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                {
                    size += count;
                    if (size > write.MaxBytes) throw new InvalidDataException("文件超过允许的大小限制。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (size == 0) throw new InvalidDataException("不能保存空文件。");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.Move(part, complete);
            CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Path.GetDirectoryName(complete)!);
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        }
        catch
        {
            if (File.Exists(part)) File.Delete(part);
            throw;
        }
    }

    /// <summary>按固定缓冲区读取，不随文件大小申请内存；恢复与去重发布时核验实际内容。</summary>
    internal async Task<(string Hash, long Size)> InspectAsync(string key, long maxBytes, CancellationToken ct)
    {
        await using var file = Open(key) ?? throw new FileNotFoundException("文件检查点不存在。");
        if (file.Length is <= 0 || file.Length > maxBytes) throw new InvalidDataException("文件检查点大小无效。");
        return (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)), file.Length);
    }

    internal FileStream? Open(string key)
    {
        var path = Resolve(key);
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal bool Exists(string key) => File.Exists(Resolve(key));
    internal void Delete(string key)
    {
        var path = Resolve(key);
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { return; }
        if (Directory.Exists(Path.GetDirectoryName(path))) CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>同一文件系统内发布不可变对象；已有对象由调用方核验后复用。</summary>
    internal void Publish(string completeKey, string objectKey)
    {
        var target = PreparePath(objectKey);
        if (File.Exists(target)) { Delete(completeKey); return; }
        var complete = Resolve(completeKey);
        File.Move(complete, target);
        CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Path.GetDirectoryName(target)!);
        CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(Path.GetDirectoryName(complete)!);
    }

    internal IEnumerable<(Guid Id, Guid Lease, string Key)> ExpiredPending(DateTime cutoff)
    {
        foreach (var directory in Directory.EnumerateDirectories(Resolve("pending")))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var id)) continue;
            RejectLink(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                RejectLink(file);
                var extension = Path.GetExtension(file);
                if (extension is not (".part" or ".complete") || File.GetLastWriteTimeUtc(file) > cutoff
                    || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var lease)) continue;
                yield return (id, lease, $"pending/{name}/{Path.GetFileName(file)}");
            }
            RemoveEmptyPending(id);
        }
    }

    internal void RemoveEmptyPending(Guid id)
    {
        var directory = Resolve($"pending/{id:N}");
        try { Directory.Delete(directory, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 非空、尚有写入者或目录已由其他实例清理时保留。 */ }
    }

    /// <summary>健康检查实际执行受控小文件写入；错误由健康接口统一脱敏。</summary>
    internal Task ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve($"pending/probe-{Guid.NewGuid():N}");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        file.WriteByte(1); file.Flush(true);
        return Task.CompletedTask;
    }
}

/// <summary>文件写入的元数据租约；不包含文件正文。</summary>
internal sealed record StorageWrite(Guid Id, Guid Lease, string Operation, string MediaType, long MaxBytes,
    string State, DateTimeOffset Expires, string? Path, string? Hash, long? Size);
