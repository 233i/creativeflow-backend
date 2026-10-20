using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct SkillId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static SkillId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out SkillId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new SkillId(value);
            return true;
        }
        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

public enum SkillRiskLevel
{
    /// <summary>扫描未发现当前规则识别的风险。</summary>
    Safe,
    /// <summary>扫描发现需要注意的风险项。</summary>
    Warning,
    /// <summary>扫描发现高风险脚本或资源访问行为。</summary>
    Danger,
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Slug">Skill 的稳定短名称。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="StoragePath">存储后端的不透明键，不能直接当作用户可控文件路径。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="HasScripts">Skill 包是否包含可执行脚本。</param>
/// <param name="RiskJson">扫描风险结果的持久化 JSON。</param>
/// <param name="MaxRisk">包扫描中发现的最高风险级别。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record Skill(
    SkillId Id,
    DeviceId DeviceId,
    string Slug,
    string Name,
    string Description,
    string StoragePath,
    bool Enabled,
    bool HasScripts,
    string RiskJson,
    SkillRiskLevel MaxRisk,
    long ByteSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
