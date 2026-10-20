using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Hubs;

/// <summary>
/// 分组命名。分组支持背板，因此此寻址方案在单节点和 Redis 支持的集群中行为一致。
/// </summary>
public static class HubGroups
{
    /// <summary>一个浏览器配置——通常有多个连接，每个打开的标签页一个。</summary>
    public static string ForDevice(DeviceId deviceId) => $"device:{deviceId.Value:N}";

    /// <summary>
    /// 所有被认为属于同一物理设备的设备。除首个设备外，成员关系均为概率性判断，
    /// 因此应避免向此分组发送任何敏感内容。
    /// </summary>
    public static string ForVisitor(VisitorId visitorId) => $"visitor:{visitorId.Value:N}";
}

/// <summary>
/// 客户端方法名称。
/// </summary>
/// <remarks>
/// 普通 Hub 不会在编译期检查客户端方法名称，因此集中保存以避免拼写错误在运行时静默失败。
/// </remarks>
public static class RealtimeMethods
{
    /// <summary>通用实时载荷使用的客户端方法名。</summary>
    public const string ReceiveMessage = "ReceiveMessage";
    /// <summary>设备关联完成时发送的客户端事件名。</summary>
    public const string DeviceLinked = "DeviceLinked";
}

/// <summary>在 <c>HttpContext.Items</c> 中存放每请求身份信息所用的键。</summary>
public static class DeviceContextItems
{
    /// <summary>HttpContext.Items 中保存 DeviceId 的固定索引键。</summary>
    public const string DeviceId = "creativeflow.device_id";
    /// <summary>HttpContext.Items 中保存 VisitorId 的固定索引键。</summary>
    public const string VisitorId = "creativeflow.visitor_id";
    /// <summary>请求上下文中当前已授权账号范围的索引键。</summary>
    public const string AccountId = "creativeflow.account_id";
    /// <summary>请求上下文中已经验证的账号 session 标识键。</summary>
    public const string SessionAccountId = "creativeflow.session_account_id";
    /// <summary>请求上下文中设备与账号会话不匹配的阻止标记键。</summary>
    public const string DeviceAccessBlocked = "creativeflow.device_access_blocked";
}
