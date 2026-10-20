namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 标识一个推定的物理设备，即由一个或多个 <see cref="DeviceId"/> 组成的集群。
/// 除首个设备外，其他成员关系均为概率性判断，因此访问者是一种假设而非事实。
/// 每个成员关系的置信度参见 <see cref="DeviceLink"/>。
/// </summary>
/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct VisitorId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static VisitorId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out VisitorId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new VisitorId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}
