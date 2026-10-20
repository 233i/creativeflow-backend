using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Skills;

namespace CreativeFlow.Infrastructure.Health;

/// <summary>实际检查文件卷标记、权限和小文件持久写入，不向客户端暴露服务器目录。</summary>
public sealed class FileStorageHealthProbe(AiOptions assets, SkillsOptions skills)
{
    public async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            await new AssetFileSystem(assets).ProbeAsync(ct);
            await new FileSystemSkillStore(Microsoft.Extensions.Options.Options.Create(skills)).ProbeAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new IOException("文件卷不可用，请检查挂载标记、剩余空间和服务账号写权限。");
        }
    }
}
