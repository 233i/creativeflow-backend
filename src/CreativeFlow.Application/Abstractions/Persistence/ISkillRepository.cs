using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface ISkillRepository
{
    /// <summary>列出Skill 包，保留现有筛选、排序与访问范围。</summary>
    Task<IReadOnlyList<Skill>> ListAsync(DeviceId deviceId, CancellationToken ct);
    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<Skill?> FindAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct);
    /// <summary>按稳定 slug 查找已安装 Skill。</summary>
    Task<Skill?> FindBySlugAsync(DeviceId deviceId, string slug, CancellationToken ct);
    /// <summary>按原有存储规则写入Skill 包。</summary>
    Task<bool> InsertAsync(Skill skill, CancellationToken ct);
    /// <summary>删除Skill 包，保留不存在或不可访问时的返回语义。</summary>
    Task<bool> DeleteAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct);
}
