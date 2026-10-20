using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Identity;

public class PasswordAuthServiceTests
{
    /// <summary>本次操作使用的时间基准。</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>回归场景：Registration Normalizes Email Links Guest Device And Creates Session。</summary>
    [Fact]
    public async Task RegistrationNormalizesEmailLinksGuestDeviceAndCreatesSession()
    {
        var repository = new AccountRepositoryStub();
        var service = CreateService(repository);
        var deviceId = DeviceId.New();

        var result = await service.RegisterAsync(
            "  Creator@例子.com ",
            "a long password",
            "  Alice  ",
            deviceId,
            "test-agent",
            "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.Succeeded, result.Status);
        Assert.NotNull(result.Account);
        Assert.NotNull(result.RawSessionToken);
        Assert.Equal("Alice", result.Account.DisplayName);
        Assert.Equal("Creator@xn--fsqu00a.com", result.Account.Email);
        Assert.Equal("creator@xn--fsqu00a.com", repository.Registration?.NormalizedEmail);
        Assert.Equal(deviceId, repository.RegisteredDevice);
        Assert.Equal(Now.AddDays(7), repository.Session?.ExpiresAt);
        Assert.NotEqual(result.RawSessionToken, Convert.ToBase64String(repository.Session!.TokenHash));
    }

    /// <summary>回归场景：Registration Rejects An Existing Email Without Creating Session。</summary>
    [Fact]
    public async Task RegistrationRejectsAnExistingEmailWithoutCreatingSession()
    {
        var repository = new AccountRepositoryStub
        {
            CreationResult = PasswordAccountCreationResult.EmailUnavailable,
        };
        var service = CreateService(repository);

        var result = await service.RegisterAsync(
            "creator@example.com",
            "a long password",
            null,
            null,
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.EmailUnavailable, result.Status);
        Assert.Null(repository.Session);
    }

    /// <summary>回归场景：Login Uses Generic Failure And Records The Attempt。</summary>
    [Fact]
    public async Task LoginUsesGenericFailureAndRecordsTheAttempt()
    {
        var accountId = AccountId.New();
        var repository = new AccountRepositoryStub
        {
            Credential = new PasswordCredential(accountId, "hash:correct password", 0, null),
            Profile = new AccountProfile(accountId, "Alice", "alice@example.com", null, []),
        };
        var service = CreateService(repository);

        var result = await service.LoginAsync(
            "ALICE@example.com",
            "wrong password",
            null,
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.InvalidCredentials, result.Status);
        Assert.Equal(accountId, repository.FailedAccount);
        Assert.Null(repository.Session);
    }

    /// <summary>回归场景：Login Links A New Device And Clears Failures。</summary>
    [Fact]
    public async Task LoginLinksANewDeviceAndClearsFailures()
    {
        var accountId = AccountId.New();
        var deviceId = DeviceId.New();
        var repository = new AccountRepositoryStub
        {
            Credential = new PasswordCredential(accountId, "hash:correct password", 2, null),
            Profile = new AccountProfile(accountId, "Alice", "alice@example.com", null, []),
        };
        var service = CreateService(repository);

        var result = await service.LoginAsync(
            "alice@example.com",
            "correct password",
            deviceId,
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.Succeeded, result.Status);
        Assert.Equal((accountId, deviceId), repository.LinkedDevice);
        Assert.Equal(accountId, repository.ResetAccount);
        Assert.NotNull(repository.Session);
    }

    /// <summary>回归场景：Login Does Not Create A Session While The Account Is Locked。</summary>
    [Fact]
    public async Task LoginDoesNotCreateASessionWhileTheAccountIsLocked()
    {
        var accountId = AccountId.New();
        var repository = new AccountRepositoryStub
        {
            Credential = new PasswordCredential(
                accountId,
                "hash:correct password",
                5,
                Now.AddMinutes(1)),
            Profile = new AccountProfile(accountId, "Alice", "alice@example.com", null, []),
        };
        var service = CreateService(repository);

        var result = await service.LoginAsync(
            "alice@example.com",
            "correct password",
            DeviceId.New(),
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.TemporarilyLocked, result.Status);
        Assert.Null(repository.LinkedDevice);
        Assert.Null(repository.Session);
    }

    /// <summary>回归场景：Login Rejects A Disabled Account Without Creating A Session。</summary>
    [Fact]
    public async Task LoginRejectsADisabledAccountWithoutCreatingASession()
    {
        var accountId = AccountId.New();
        var repository = new AccountRepositoryStub
        {
            Credential = new PasswordCredential(accountId, "hash:correct password", 0, null),
            Profile = new AccountProfile(
                accountId,
                "Alice",
                "alice@example.com",
                null,
                [],
                IsEnabled: false),
        };
        var service = CreateService(repository);

        var result = await service.LoginAsync(
            "alice@example.com",
            "correct password",
            DeviceId.New(),
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordAuthStatus.Disabled, result.Status);
        Assert.Null(repository.Session);
        Assert.Null(repository.LinkedDevice);
    }

    /// <summary>回归场景：Password Hasher Does Not Store Or Accept The Plain Text Password。</summary>
    [Fact]
    public void PasswordHasherDoesNotStoreOrAcceptThePlainTextPassword()
    {
        var hasher = new CreativeFlow.Infrastructure.Security.AspNetCorePasswordCredentialHasher();
        const string password = "a sufficiently long password";

        var hash = hasher.Hash(password);

        Assert.DoesNotContain(password, hash, StringComparison.Ordinal);
        Assert.Equal(PasswordHashVerification.Succeeded, hasher.Verify(hash, password));
        Assert.Equal(PasswordHashVerification.Failed, hasher.Verify(hash, "a different password"));
        Assert.Equal(PasswordHashVerification.Failed, hasher.Verify(null, password));
    }

    /// <summary>用测试配置、时钟与替身依赖组装被测服务。</summary>
    private static PasswordAuthService CreateService(AccountRepositoryStub repository) => new(
        repository,
        new PasswordHasherStub(),
        new ClockStub(),
        Options.Create(new AuthenticationOptions { SessionMaxAgeDays = 7 }));

    private sealed class ClockStub : IClock
    {
        /// <summary>返回密码认证测试预设的当前时间。</summary>
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PasswordHasherStub : IPasswordCredentialHasher
    {
        /// <summary>用固定前缀模拟密码哈希，便于检查注册传入的密码。</summary>
        public string Hash(string password) => $"hash:{password}";

        /// <summary>将测试哈希与带固定前缀的输入密码比较，返回匹配状态。</summary>
        public PasswordHashVerification Verify(string? passwordHash, string providedPassword) =>
            passwordHash == $"hash:{providedPassword}"
                ? PasswordHashVerification.Succeeded
                : PasswordHashVerification.Failed;
    }

    private sealed class AccountRepositoryStub : IAccountRepository
    {
        /// <summary>测试预设的密码账号创建结果。</summary>
        public PasswordAccountCreationResult CreationResult { get; init; } =
            PasswordAccountCreationResult.Created;
        /// <summary>测试供认证读取的密码凭据记录。</summary>
        public PasswordCredential? Credential { get; init; }
        /// <summary>测试供认证返回的账号资料。</summary>
        public AccountProfile? Profile { get; init; }
        /// <summary>测试捕获的账号注册参数。</summary>
        public PasswordAccountRegistration? Registration { get; private set; }
        /// <summary>测试注册操作绑定的设备标识。</summary>
        public DeviceId? RegisteredDevice { get; private set; }
        /// <summary>测试当前保存或供认证读取的会话记录。</summary>
        public AccountSession? Session { get; private set; }
        /// <summary>测试中被记录密码验证失败的账号标识。</summary>
        public AccountId? FailedAccount { get; private set; }
        /// <summary>测试中被重置密码失败计数的账号标识。</summary>
        public AccountId? ResetAccount { get; private set; }
        /// <summary>测试登录操作成功关联的设备标识。</summary>
        public (AccountId AccountId, DeviceId DeviceId)? LinkedDevice { get; private set; }

        /// <summary>记录注册资料及设备，返回夹具配置的账号创建结果。</summary>
        public Task<PasswordAccountCreationResult> CreatePasswordAccountAsync(
            PasswordAccountRegistration registration,
            DeviceId? deviceId,
            CancellationToken ct)
        {
            Registration = registration;
            RegisteredDevice = deviceId;
            return Task.FromResult(CreationResult);
        }

        /// <summary>直接返回预设密码凭据，不按邮箱查询真实数据库。</summary>
        public Task<PasswordCredential?> FindPasswordCredentialAsync(
            string normalizedEmail, CancellationToken ct) => Task.FromResult(Credential);

        /// <summary>直接返回预设账号资料，不访问真实账户表。</summary>
        public Task<AccountProfile?> FindByIdAsync(AccountId accountId, CancellationToken ct) =>
            Task.FromResult(Profile);

        /// <summary>记录拟创建的登录会话，供认证测试断言。</summary>
        public Task CreateSessionAsync(AccountSession session, CancellationToken ct)
        {
            Session = session;
            return Task.CompletedTask;
        }

        /// <summary>记录密码验证失败所属账号，不模拟数据库计数或锁定写入。</summary>
        public Task RecordPasswordFailureAsync(
            AccountId accountId,
            DateTimeOffset now,
            DateTimeOffset lockUntil,
            int maximumAttempts,
            CancellationToken ct)
        {
            FailedAccount = accountId;
            return Task.CompletedTask;
        }

        /// <summary>记录需要清除密码失败状态的账号。</summary>
        public Task ResetPasswordFailuresAsync(AccountId accountId, CancellationToken ct)
        {
            ResetAccount = accountId;
            return Task.CompletedTask;
        }

        /// <summary>接受密码重哈希通知但不写入或记录新哈希，保留该测试替身的空操作行为。</summary>
        public Task UpdatePasswordHashAsync(
            AccountId accountId,
            string passwordHash,
            DateTimeOffset now,
            CancellationToken ct) => Task.CompletedTask;

        /// <summary>固定返回设备尚未关联账号，供注册和登录关联场景使用。</summary>
        public Task<AccountId?> FindAccountForDeviceAsync(DeviceId deviceId, CancellationToken ct) =>
            Task.FromResult<AccountId?>(null);

        /// <summary>记录账号与设备的关联请求并固定返回成功。</summary>
        public Task<bool> LinkDeviceAsync(
            AccountId accountId, DeviceId deviceId, CancellationToken ct)
        {
            LinkedDevice = (accountId, deviceId);
            return Task.FromResult(true);
        }

        /// <summary>拒绝密码认证测试场景外的会话令牌查询。</summary>
        public Task<AccountProfile?> FindBySessionTokenHashAsync(
            byte[] tokenHash, DateTimeOffset now, CancellationToken ct) => Unsupported<AccountProfile?>();
        /// <summary>拒绝密码认证测试查询外部身份关联。</summary>
        public Task<AccountId?> FindByExternalIdentityAsync(
            string providerKey, string subject, CancellationToken ct) => Unsupported<AccountId?>();
        /// <summary>拒绝密码认证测试通过外部身份创建账号。</summary>
        public Task<AccountProfile> GetOrCreateFromExternalAsync(
            ExternalLoginProfile profile, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<AccountProfile>();
        /// <summary>拒绝密码认证测试绑定外部登录身份。</summary>
        public Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
            AccountId accountId, ExternalLoginProfile profile, DateTimeOffset now, CancellationToken ct) =>
            Unsupported<ExternalIdentityLinkResult>();
        /// <summary>拒绝密码认证测试场景外的会话撤销。</summary>
        public Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct) => Unsupported();
        /// <summary>拒绝密码认证测试创建 OAuth 事务。</summary>
        public Task CreateOAuthTransactionAsync(OAuthTransaction transaction, CancellationToken ct) =>
            Unsupported();
        /// <summary>拒绝密码认证测试消费 OAuth state。</summary>
        public Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
            byte[] stateHash, DateTimeOffset now, CancellationToken ct) => Unsupported<OAuthTransaction?>();

        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task Unsupported() => Task.FromException(new NotSupportedException());
        /// <summary>让测试未声明支持的仓储调用立即失败，避免静默掩盖额外副作用。</summary>
        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
