using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct CanvasId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static CanvasId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out CanvasId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new CanvasId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// 已保存的节点图。
/// </summary>
/// <remarks>
/// <see cref="GraphJson"/> 对服务器是不透明的：它由画布客户端生成和使用，服务器只负责存储并返回。
/// 图始终整体读写，为每个节点分配一行并无收益，反而会让数据库架构与变化更频繁的客户端模型耦合。
/// </remarks>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record Canvas(
    CanvasId Id,
    DeviceId DeviceId,
    string Name,
    string GraphJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
