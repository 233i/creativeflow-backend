namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 标识一个浏览器配置。这是系统其他部分绑定的确定性标识：由服务器签发，
/// 存储在第一方 Cookie 中，对使用同一浏览器再次访问的用户识别准确率约为 99%。
/// </summary>
/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct DeviceId(Guid Value)
{
    /// <summary>使用 UUIDv7，使数据行在主键索引中按创建时间聚集。</summary>
    public static DeviceId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out DeviceId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new DeviceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}
