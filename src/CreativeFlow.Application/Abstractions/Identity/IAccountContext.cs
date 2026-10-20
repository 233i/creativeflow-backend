using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Identity;

/// <summary>
/// 当当前设备已关联到登录账户时，附加到当前请求的账户作用域。基础设施层打开数据库连接时使用该作用域。
/// 后台任务以空作用域启动，在访问账户级资源前，必须根据持久化的设备所有权显式恢复作用域。
/// </summary>
public interface IAccountContext
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    AccountId? AccountId { get; set; }
}
