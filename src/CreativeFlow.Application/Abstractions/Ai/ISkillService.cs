using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Ai;

public interface ISkillPackageScanner
{
    /// <summary>在当前上传限制内扫描 Skill 包结构与风险。</summary>
    Task<ScannedSkillPackage> ScanAsync(
        Stream stream,
        string fileName,
        CancellationToken ct);
}

public interface ISkillStore
{
    /// <summary>保存 Skill 包的文件内容并返回存储路径；业务元数据由 Skill 仓储另行保存。</summary>
    Task<string> InstallAsync(
        DeviceId deviceId,
        SkillId skillId,
        IReadOnlyList<SkillPackageFile> files,
        CancellationToken ct);

    /// <summary>在既有路径与大小限制内读取 Skill 的 UTF-8 文本资源。</summary>
    Task<string> ReadTextAsync(
        string storagePath,
        string relativePath,
        int maxBytes,
        CancellationToken ct);

    /// <summary>列出 Skill 包内可访问的文件路径。</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(
        string storagePath,
        int maxEntries,
        CancellationToken ct);

    /// <summary>在数量和字节限制内读取完整 Skill 包文件。</summary>
    Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(
        string storagePath,
        int maxEntries,
        int maxBytes,
        CancellationToken ct);

    /// <summary>按存储路径删除已安装 Skill 包的文件内容。</summary>
    Task DeleteAsync(string storagePath, CancellationToken ct);
}

/// <param name="Code">业务错误、风险项或外部认证返回的代码，含义由所属契约定义。</param>
/// <param name="Severity">风险或日志项的严重程度。</param>
/// <param name="Path">文件、资源或请求的路径。</param>
/// <param name="Line">关联源文件或风险项的一基行号。</param>
/// <param name="Message">供用户、模型或调用方阅读的消息正文。</param>
public sealed record SkillRiskFinding(
    string Code,
    SkillRiskLevel Severity,
    string Path,
    int? Line,
    string Message);

/// <param name="Path">文件、资源或请求的路径。</param>
/// <param name="Bytes">原始二进制内容。</param>
public sealed record SkillPackageFile(string Path, byte[] Bytes);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Slug">Skill 的稳定短名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Instructions">Skill 或运行器使用的说明正文。</param>
/// <param name="Files">包内文件或读取结果集合。</param>
/// <param name="Risks">包扫描或校验得到的风险项集合。</param>
/// <param name="HasScripts">Skill 包是否包含可执行脚本。</param>
/// <param name="MaxRisk">包扫描中发现的最高风险级别。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
public sealed record ScannedSkillPackage(
    string Name,
    string Slug,
    string Description,
    string Instructions,
    IReadOnlyList<SkillPackageFile> Files,
    IReadOnlyList<SkillRiskFinding> Risks,
    bool HasScripts,
    SkillRiskLevel MaxRisk,
    long ByteSize);
