using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <summary>
/// 用户为设备画布素材库中的媒体创建的文件夹。
/// </summary>
/// <remarks>
/// 分组与素材一样刻意限定在设备范围内。删除分组只会解除素材的分组归属，绝不会删除媒体本身。
/// </remarks>
/// <param name="GroupId">素材分组标识；允许空值的场景表示未分组。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="AssetCount">该分组或结果中的素材数量。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record CanvasAssetGroup(
    Guid GroupId,
    DeviceId DeviceId,
    string Name,
    int AssetCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
