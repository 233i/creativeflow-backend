namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 标识一个已登录的 CreativeFlow 账户。账户身份刻意与 <see cref="DeviceId"/> 分离：
/// 浏览器可以成为账户设备，而无需更改现有数据模型已经使用的所有权键。</summary>
/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct AccountId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static AccountId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out AccountId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new AccountId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}
