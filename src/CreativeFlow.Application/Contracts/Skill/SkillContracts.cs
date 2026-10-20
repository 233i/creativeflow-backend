namespace CreativeFlow.Application.Contracts.Skill;

/// <param name="Code">业务错误、风险项或外部认证返回的代码，含义由所属契约定义。</param>
/// <param name="Severity">风险或日志项的严重程度。</param>
/// <param name="Path">文件、资源或请求的路径。</param>
/// <param name="Line">关联源文件或风险项的一基行号。</param>
/// <param name="Message">供用户、模型或调用方阅读的消息正文。</param>
public sealed record SkillRiskFindingResponse(
    string Code,
    string Severity,
    string Path,
    int? Line,
    string Message);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Slug">Skill 的稳定短名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="HasScripts">Skill 包是否包含可执行脚本。</param>
/// <param name="MaxRisk">包扫描中发现的最高风险级别。</param>
/// <param name="Risks">包扫描或校验得到的风险项集合。</param>
/// <param name="Files">包内文件或读取结果集合。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
public sealed record SkillScanResponse(
    string Name,
    string Slug,
    string Description,
    bool HasScripts,
    string MaxRisk,
    SkillRiskFindingResponse[] Risks,
    string[] Files,
    long ByteSize);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Slug">Skill 的稳定短名称。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="HasScripts">Skill 包是否包含可执行脚本。</param>
/// <param name="MaxRisk">包扫描中发现的最高风险级别。</param>
/// <param name="Risks">包扫描或校验得到的风险项集合。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record SkillResponse(
    string Id,
    string Slug,
    string Name,
    string Description,
    bool Enabled,
    bool HasScripts,
    string MaxRisk,
    SkillRiskFindingResponse[] Risks,
    long ByteSize,
    string CreatedAt,
    string UpdatedAt);

/// <param name="SandboxEnabled">是否允许通过已配置的隔离运行器执行 Skill 脚本。</param>
/// <param name="Skills">当前可用的 Skill 集合。</param>
public sealed record SkillListResponse(bool SandboxEnabled, SkillResponse[] Skills);
