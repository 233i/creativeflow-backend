using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>
/// 为在创建它的 HTTP 请求之外运行的持久任务恢复账户访问权限。
/// </summary>
/// <remarks>
/// 账户根据设备当前的持久化所有权解析，而不是从请求复制。因此删除设备关联会立即生效。
/// 分配空结果同样重要：一个有作用域的后台工作进程可能处理多个账户的任务，
/// 绝不能保留上一任务的访问权限。
/// </remarks>
public sealed class BackgroundAccountScope(
    IAccountRepository accounts,
    IAccountContext accountContext)
{
    /// <summary>依据设备当前持久化归属恢复账号范围，同时清空上一任务的旧范围。</summary>
    public async Task RestoreForDeviceAsync(DeviceId deviceId, CancellationToken ct)
    {
        accountContext.AccountId = await accounts.FindAccountForDeviceAsync(deviceId, ct);
    }
}
