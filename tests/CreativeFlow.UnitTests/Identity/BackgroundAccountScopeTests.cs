using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Identity;

public class BackgroundAccountScopeTests
{
    /// <summary>回归场景：Restore Tracks Current Device Ownership And Clears Previous Account。</summary>
    [Fact]
    public async Task RestoreTracksCurrentDeviceOwnershipAndClearsPreviousAccount()
    {
        var accountId = AccountId.New();
        var accountDevice = DeviceId.New();
        var guestDevice = DeviceId.New();
        var repository = new AccountRepositoryStub(
            new Dictionary<DeviceId, AccountId> { [accountDevice] = accountId });
        var context = new AccountContextStub { AccountId = AccountId.New() };
        var scope = new BackgroundAccountScope(repository, context);

        await scope.RestoreForDeviceAsync(
            accountDevice, TestContext.Current.CancellationToken);
        Assert.Equal(accountId, context.AccountId);

        await scope.RestoreForDeviceAsync(
            guestDevice, TestContext.Current.CancellationToken);
        Assert.Null(context.AccountId);
        Assert.Equal([accountDevice, guestDevice], repository.LookedUpDevices);
    }

    private sealed class AccountContextStub : IAccountContext
    {
        /// <summary>保存测试作用域内当前绑定的账号标识，供后台账号恢复断言使用。</summary>
        public AccountId? AccountId { get; set; }
    }

    /// <summary>初始化 AccountRepositoryStub，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class AccountRepositoryStub(
        IReadOnlyDictionary<DeviceId, AccountId> accounts) : IAccountRepository
    {
        /// <summary>测试查询过归属账号的设备标识，保持实际调用顺序。</summary>
        public List<DeviceId> LookedUpDevices { get; } = [];

        /// <summary>记录被查找的设备，并从预设映射中返回其账号或空值。</summary>
        public Task<AccountId?> FindAccountForDeviceAsync(DeviceId deviceId, CancellationToken ct)
        {
            LookedUpDevices.Add(deviceId);
            return Task.FromResult<AccountId?>(
                accounts.TryGetValue(deviceId, out var accountId) ? accountId : null);
        }

        /// <summary>拒绝后台账号恢复测试意外查询登录令牌。</summary>
        public Task<AccountProfile?> FindBySessionTokenHashAsync(
            byte[] tokenHash, DateTimeOffset now, CancellationToken ct) => Unsupported<AccountProfile?>();

        /// <summary>拒绝后台账号恢复测试读取场景外的账号资料。</summary>
        public Task<AccountProfile?> FindByIdAsync(AccountId accountId, CancellationToken ct) =>
            Unsupported<AccountProfile?>();

        /// <summary>拒绝后台账号恢复测试读取密码凭据。</summary>
        public Task<PasswordCredential?> FindPasswordCredentialAsync(
            string normalizedEmail, CancellationToken ct) => Unsupported<PasswordCredential?>();

        /// <summary>拒绝后台账号恢复测试创建密码账号。</summary>
        public Task<PasswordAccountCreationResult> CreatePasswordAccountAsync(
            PasswordAccountRegistration registration, DeviceId? deviceId, CancellationToken ct) =>
            Unsupported<PasswordAccountCreationResult>();

        /// <summary>拒绝后台账号恢复测试记录密码失败。</summary>
        public Task RecordPasswordFailureAsync(
            AccountId accountId,
            DateTimeOffset now,
            DateTimeOffset lockUntil,
            int maximumAttempts,
            CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试清除密码失败状态。</summary>
        public Task ResetPasswordFailuresAsync(AccountId accountId, CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试更新密码哈希。</summary>
        public Task UpdatePasswordHashAsync(
            AccountId accountId,
            string passwordHash,
            DateTimeOffset now,
            CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试查询外部登录身份。</summary>
        public Task<AccountId?> FindByExternalIdentityAsync(
            string providerKey, string subject, CancellationToken ct) => Unsupported<AccountId?>();

        /// <summary>拒绝后台账号恢复测试通过外部身份创建账号。</summary>
        public Task<AccountProfile> GetOrCreateFromExternalAsync(
            ExternalLoginProfile profile, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<AccountProfile>();

        /// <summary>拒绝后台账号恢复测试绑定外部登录身份。</summary>
        public Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
            AccountId accountId,
            ExternalLoginProfile profile,
            DateTimeOffset now,
            CancellationToken ct) => Unsupported<ExternalIdentityLinkResult>();

        /// <summary>拒绝后台账号恢复测试创建登录会话。</summary>
        public Task CreateSessionAsync(AccountSession session, CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试撤销登录会话。</summary>
        public Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试修改设备归属。</summary>
        public Task<bool> LinkDeviceAsync(
            AccountId accountId, DeviceId deviceId, CancellationToken ct) => Unsupported<bool>();

        /// <summary>拒绝后台账号恢复测试创建 OAuth 事务。</summary>
        public Task CreateOAuthTransactionAsync(
            OAuthTransaction transaction, CancellationToken ct) => Unsupported();

        /// <summary>拒绝后台账号恢复测试消费 OAuth state。</summary>
        public Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
            byte[] stateHash, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<OAuthTransaction?>();

        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task Unsupported() =>
            Task.FromException(new NotSupportedException());

        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task<T> Unsupported<T>() =>
            Task.FromException<T>(new NotSupportedException());
    }
}
