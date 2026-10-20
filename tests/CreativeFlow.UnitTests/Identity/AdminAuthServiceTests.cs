using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Identity;

public class AdminAuthServiceTests
{
    /// <summary>测试专用管理员密码，明确传入而不是依赖仓库默认值。</summary>
    private const string TestPassword = "test-admin-password";
    /// <summary>本次操作使用的时间基准。</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>回归场景：Bootstrap Hashes And Creates The Default Administrator。</summary>
    [Fact]
    public async Task BootstrapHashesAndCreatesTheDefaultAdministrator()
    {
        var repository = new AdminRepositoryStub();
        var service = CreateService(repository);

        await service.EnsureDefaultAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(repository.CreatedDefault);
        Assert.Equal("admin", repository.CreatedDefault.Username);
        Assert.Equal($"hash:{TestPassword}", repository.CreatedDefault.PasswordHash);
        Assert.True(repository.CreatedDefault.MustChangePassword);
    }

    /// <summary>回归场景：Login Creates An Independent Admin Session。</summary>
    [Fact]
    public async Task LoginCreatesAnIndependentAdminSession()
    {
        var repository = new AdminRepositoryStub
        {
            Admin = ExistingAdmin(),
        };
        var service = CreateService(repository);

        var result = await service.LoginAsync(
            " ADMIN ",
            TestPassword,
            "test-agent",
            "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Equal(AdminLoginStatus.Succeeded, result.Status);
        Assert.NotNull(result.RawSessionToken);
        Assert.NotNull(repository.Session);
        Assert.Equal(Now.AddHours(12), repository.Session.ExpiresAt);
        Assert.NotEqual(result.RawSessionToken, Convert.ToBase64String(repository.Session.TokenHash));
    }

    /// <summary>回归场景：Password Change Requires The Current Password And A Strong Replacement。</summary>
    [Fact]
    public async Task PasswordChangeRequiresTheCurrentPasswordAndAStrongReplacement()
    {
        var repository = new AdminRepositoryStub
        {
            Admin = ExistingAdmin(),
        };
        var service = CreateService(repository);

        var wrong = await service.ChangePasswordAsync(
            repository.Admin.Id,
            "wrong",
            "a sufficiently long password",
            TestContext.Current.CancellationToken);
        var shortPassword = await service.ChangePasswordAsync(
            repository.Admin.Id,
            TestPassword,
            "short",
            TestContext.Current.CancellationToken);
        var changed = await service.ChangePasswordAsync(
            repository.Admin.Id,
            TestPassword,
            "a sufficiently long password",
            TestContext.Current.CancellationToken);

        Assert.Equal(AdminPasswordChangeStatus.InvalidCurrentPassword, wrong);
        Assert.Equal(AdminPasswordChangeStatus.InvalidNewPassword, shortPassword);
        Assert.Equal(AdminPasswordChangeStatus.Succeeded, changed);
        Assert.Equal("hash:a sufficiently long password", repository.UpdatedPasswordHash);
    }

    /// <summary>用测试配置、时钟与替身依赖组装被测服务。</summary>
    private static AdminAuthService CreateService(AdminRepositoryStub repository) => new(
        repository,
        new PasswordHasherStub(),
        new ClockStub(),
        Options.Create(new AdminOptions { DefaultPassword = TestPassword }));

    /// <summary>构造已存在且可登录的管理员测试记录。</summary>
    private static AdminAccount ExistingAdmin() => new(
        Guid.CreateVersion7(),
        "admin",
        $"hash:{TestPassword}",
        true,
        Now,
        Now);

    private sealed class ClockStub : IClock
    {
        /// <summary>返回管理员认证测试预设的当前时间。</summary>
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PasswordHasherStub : IPasswordCredentialHasher
    {
        /// <summary>用可读前缀模拟密码哈希，便于断言传入密码，不执行真实密码派生。</summary>
        public string Hash(string password) => $"hash:{password}";

        /// <summary>按固定前缀规则判断测试密码是否匹配，只返回成功或失败。</summary>
        public PasswordHashVerification Verify(string? passwordHash, string providedPassword) =>
            passwordHash == $"hash:{providedPassword}"
                ? PasswordHashVerification.Succeeded
                : PasswordHashVerification.Failed;
    }

    private sealed class AdminRepositoryStub : IAdminRepository
    {
        /// <summary>已验证的管理员资料。</summary>
        public AdminAccount Admin { get; init; } = ExistingAdmin();
        /// <summary>测试是否触发默认管理员创建。</summary>
        public AdminAccount? CreatedDefault { get; private set; }
        /// <summary>测试当前保存或供认证读取的会话记录。</summary>
        public AdminSession? Session { get; private set; }
        /// <summary>测试捕获的更新后密码哈希。</summary>
        public string? UpdatedPasswordHash { get; private set; }

        /// <summary>记录待创建的默认管理员并固定返回成功，不模拟真实空表约束。</summary>
        public Task<bool> CreateDefaultIfEmptyAsync(AdminAccount admin, CancellationToken ct)
        {
            CreatedDefault = admin;
            return Task.FromResult(true);
        }

        /// <summary>仅当用户名匹配预设管理员时返回该账号。</summary>
        public Task<AdminAccount?> FindByUsernameAsync(string username, CancellationToken ct) =>
            Task.FromResult<AdminAccount?>(username == Admin.Username ? Admin : null);

        /// <summary>仅当账号标识匹配预设管理员时返回该账号。</summary>
        public Task<AdminAccount?> FindByIdAsync(Guid adminId, CancellationToken ct) =>
            Task.FromResult<AdminAccount?>(adminId == Admin.Id ? Admin : null);

        /// <summary>记录拟更新的密码哈希，供测试断言改密输入，不修改真实凭据。</summary>
        public Task UpdatePasswordAsync(
            Guid adminId,
            string passwordHash,
            bool clearMustChange,
            DateTimeOffset now,
            CancellationToken ct)
        {
            UpdatedPasswordHash = passwordHash;
            return Task.CompletedTask;
        }

        /// <summary>记录创建的管理员会话，供测试校验令牌及到期时间。</summary>
        public Task CreateSessionAsync(AdminSession session, CancellationToken ct)
        {
            Session = session;
            return Task.CompletedTask;
        }

        /// <summary>拒绝此管理员登录测试之外的会话令牌查询。</summary>
        public Task<AdminAccount?> FindBySessionTokenHashAsync(
            byte[] tokenHash,
            DateTimeOffset now,
            CancellationToken ct) => Unsupported<AdminAccount?>();

        /// <summary>拒绝此管理员登录测试之外的会话撤销操作。</summary>
        public Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct) => Unsupported();

        /// <summary>拒绝管理员认证测试意外读取用户管理列表。</summary>
        public Task<ManagedAccountPage> ListAccountsAsync(
            string? search,
            int offset,
            int limit,
            CancellationToken ct) => Unsupported<ManagedAccountPage>();

        /// <summary>拒绝管理员认证测试意外修改普通用户启用状态。</summary>
        public Task<bool> SetAccountEnabledAsync(
            AccountId accountId,
            bool enabled,
            DateTimeOffset now,
            CancellationToken ct) => Unsupported<bool>();

        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task Unsupported() => Task.FromException(new NotSupportedException());

        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task<T> Unsupported<T>() =>
            Task.FromException<T>(new NotSupportedException());
    }
}
