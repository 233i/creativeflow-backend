using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Skills;

internal sealed class FileSystemSkillStore : ISkillStore
{
    /// <summary>规范化后的 Skill 本地存储根目录，所有包路径都必须限制在该目录内。</summary>
    private readonly string root;

    /// <summary>初始化 FileSystemSkillStore 的依赖与实例状态。</summary>
    public FileSystemSkillStore(IOptions<SkillsOptions> configuredOptions)
    {
        root = Path.GetFullPath(configuredOptions.Value.Root);
        if (configuredOptions.Value.RequireStorageMarker && (!Directory.Exists(root) || !File.Exists(Path.Combine(root, ".creativeflow-storage"))))
            throw new InvalidOperationException("Skill 持久化卷未初始化或未挂载，禁止写入容器临时目录。");
        SecureCreateDirectory(root);
        RejectPathItself(root);
    }

    /// <summary>使用服务专属根目录做有界写入检查，探测后自动删除探针。</summary>
    internal Task ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RejectPathItself(root);
        using var file = new FileStream(Path.Combine(root, $".probe-{Guid.NewGuid():N}"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        file.WriteByte(1); file.Flush(true);
        return Task.CompletedTask;
    }

    /// <summary>在设备与 Skill 专属目录内安全安装包文件，拒绝空包和越界相对路径。</summary>
    public async Task<string> InstallAsync(
        DeviceId deviceId,
        SkillId skillId,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken ct)
    {
        // 包中的相对路径必须约束在当前 Skill 根目录内，解包不能写到其他技能或宿主文件。
        if (files.Count == 0) throw new InvalidDataException("The skill package has no files.");

        var deviceRoot = ContainedPath(root, deviceId.Value.ToString("N"));
        var finalPath = ContainedPath(deviceRoot, skillId.Value.ToString("N"));
        var stagingPath = ContainedPath(deviceRoot, $".{skillId.Value:N}.installing");
        RejectPathItself(root);
        SecureCreateDirectory(deviceRoot);
        RejectReparsePoints(root, deviceRoot);
        if (Directory.Exists(finalPath) || Directory.Exists(stagingPath)
            || File.Exists(finalPath) || File.Exists(stagingPath))
        {
            throw new IOException("The skill storage directory already exists.");
        }

        SecureCreateDirectory(stagingPath);
        RejectReparsePoints(root, stagingPath);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = ValidateRelativePath(file.Path);
                var targetPath = ContainedPath(stagingPath, relativePath);
                var parent = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidDataException($"Invalid skill file path '{file.Path}'.");
                SecureCreateDirectory(parent);
                RejectReparsePoints(stagingPath, parent);
                RejectPathItself(targetPath);
                await using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await output.WriteAsync(file.Bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
                CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(parent);
            }

            Directory.Move(stagingPath, finalPath);
            CreativeFlow.Infrastructure.Common.FileDurability.FlushDirectory(deviceRoot);
            return Path.GetRelativePath(root, finalPath).Replace('\\', '/');
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    /// <summary>验证安装目录、相对路径和文件类型后按字节上限读取 Skill 文本，拒绝符号链接及无效文本。</summary>
    public async Task<string> ReadTextAsync(
        string storagePath,
        string relativePath,
        int maxBytes,
        CancellationToken ct)
    {
        // 读取入口再次验证相对路径、目录归属和文件类型，保持对符号链接与无效文本的现有拒绝规则。
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var installPath = ResolveStoragePath(storagePath);
        var filePath = ContainedPath(installPath, ValidateRelativePath(relativePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Skill file not found.");
        RejectReparsePoints(installPath, filePath);

        var info = new FileInfo(filePath);
        if (info.Length > maxBytes)
        {
            throw new InvalidDataException($"The skill file exceeds {maxBytes} bytes.");
        }

        var bytes = new byte[(int)info.Length];
        await using var input = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException("The skill file ended unexpectedly.");
            offset += read;
        }
        if (await input.ReadAsync(new byte[1], ct) != 0)
        {
            throw new InvalidDataException($"The skill file exceeds {maxBytes} bytes.");
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new InvalidDataException("The skill file is not valid UTF-8 text.", exception);
        }
    }

    /// <summary>列出通过安全路径检查的 Skill 文件，限制条目数并返回统一斜杠格式的相对路径。</summary>
    public Task<IReadOnlyList<string>> ListFilesAsync(
        string storagePath,
        int maxEntries,
        CancellationToken ct)
    {
        var (installPath, paths) = ListSafePaths(storagePath, maxEntries, ct);
        return Task.FromResult<IReadOnlyList<string>>(
            [.. paths.Select(path => Path.GetRelativePath(installPath, path).Replace('\\', '/'))]);
    }

    /// <summary>读取安全文件列表中的 Skill 包内容，并限制文件数量及累计字节数。</summary>
    public async Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(
        string storagePath,
        int maxEntries,
        int maxBytes,
        CancellationToken ct)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var (installPath, paths) = ListSafePaths(storagePath, maxEntries, ct);
        var files = new List<SkillPackageFile>(paths.Length);
        long total = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            RejectReparsePoints(installPath, path);
            var info = new FileInfo(path);
            total = checked(total + info.Length);
            if (total > maxBytes || info.Length > int.MaxValue)
            {
                throw new InvalidDataException("The stored skill exceeds the execution size limit.");
            }
            var bytes = new byte[(int)info.Length];
            await using var input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.ReadExactlyAsync(bytes, ct);
            var relative = Path.GetRelativePath(installPath, path).Replace('\\', '/');
            files.Add(new SkillPackageFile(ValidateRelativePath(relative), bytes));
        }
        return files;
    }

    /// <summary>列出实际位于 Skill 根目录内且符合既有约束的文件。</summary>
    private (string InstallPath, string[] Paths) ListSafePaths(
        string storagePath,
        int maxEntries,
        CancellationToken ct)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        ct.ThrowIfCancellationRequested();
        var installPath = ResolveStoragePath(storagePath);
        if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException("Skill storage is missing.");
        RejectReparsePoints(root, installPath);
        var paths = new List<string>(Math.Min(maxEntries, 128));
        foreach (var path in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (paths.Count >= maxEntries)
            {
                throw new InvalidDataException("The stored skill has too many files.");
            }
            RejectReparsePoints(installPath, path);
            paths.Add(path);
        }
        return (installPath, [.. paths]);
    }

    /// <summary>确认目录位于 Skill 根范围且没有重解析点后删除安装目录；目录不存在时直接完成。</summary>
    public Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var installPath = ResolveStoragePath(storagePath);
        if (!Directory.Exists(installPath)) return Task.CompletedTask;
        RejectReparsePoints(root, installPath);
        Directory.Delete(installPath, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>将已验证的 Skill 存储键解析到受控根目录。</summary>
    private string ResolveStoragePath(string storagePath)
    {
        var relative = ValidateRelativePath(storagePath);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out _)
            || !Guid.TryParseExact(parts[1], "N", out _))
        {
            throw new InvalidDataException("The stored skill path has an invalid shape.");
        }
        return ContainedPath(root, relative);
    }

    /// <summary>验证包内相对路径，拒绝绝对路径和目录穿越。</summary>
    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\')
            || path.StartsWith('/') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Invalid relative skill path '{path}'.");
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." || segment.Length > 255 || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe relative skill path '{path}'.");
        }
        var normalized = string.Join('/', segments);
        if (normalized.Length > 512
            || System.Text.Encoding.UTF8.GetByteCount(normalized) > 1_024)
        {
            throw new InvalidDataException("A relative Skill path exceeds its length limit.");
        }
        return normalized;
    }

    /// <summary>将相对路径解析为根目录内的路径，拒绝越界结果。</summary>
    private static string ContainedPath(string parent, string relativePath)
    {
        var parentFull = Path.GetFullPath(parent);
        var candidate = Path.GetFullPath(Path.Combine(parentFull, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(parentFull) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The skill path escapes its storage directory.");
        }
        return candidate;
    }

    /// <summary>在逐层验证路径后创建 Skill 存储目录。</summary>
    private static void SecureCreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>拒绝路径链中的符号链接或重解析点。</summary>
    private static void RejectReparsePoints(string parent, string candidate)
    {
        RejectPathItself(parent);
        var relative = Path.GetRelativePath(parent, candidate);
        var current = Path.GetFullPath(parent);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectPathItself(current);
        }
    }

    /// <summary>校验目标路径本身，阻止借助链接绕过目录边界。</summary>
    private static void RejectPathItself(string path)
    {
        var info = Directory.Exists(path)
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        try
        {
            // Unix 上路径缺失时 FileSystemInfo.Attributes 为 -1，而 -1 包含包括 ReparsePoint 在内的所有位。
            // 仅当条目确实存在时检查属性；LinkTarget 仍能捕获目标不存在的断裂符号链接。
            if (info.LinkTarget is not null
                || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("Symbolic links are not allowed in skill storage.");
            }
        }
        catch (FileNotFoundException)
        {
            // 尚不存在的路径可以安全地在已验证的父目录下创建。
        }
    }
}
