using Microsoft.Extensions.Configuration;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Infrastructure.Options;

/// <summary>
/// 手动绑定配置节。
/// </summary>
/// <remarks>
/// <c>services.Configure&lt;T&gt;(IConfiguration)</c> 会反射选项类型，因此标注了
/// <c>RequiresUnreferencedCode</c>/<c>RequiresDynamicCode</c>。Native AOT 下，该绑定可能静默生成
/// 全为默认值的对象，导致应用以空连接字符串启动，并在远离根因的位置失败。
/// 显式读取每个键只多几行代码，却能彻底消除此故障模式。
/// </remarks>
internal static class OptionsBinder
{
    /// <summary>普通画布图片队列。固定上限防止误配置造成服务器资源耗尽。</summary>
    public static CreativeFlow.Application.Abstractions.Persistence.ImageQueueLimits BindImageQueue(IConfiguration configuration)
    {
        var section = configuration.GetSection("Ai:ImageQueue");
        return new(
            Math.Clamp(ReadInt(section["GlobalConcurrency"], 4), 1, 32),
            Math.Clamp(ReadInt(section["UserConcurrency"], 2), 1, 32),
            Math.Clamp(ReadInt(section["ProviderConcurrency"], 2), 1, 32),
            Math.Clamp(ReadInt(section["MaxPendingPerUser"], 20), 1, 1000),
            Math.Clamp(ReadInt(section["MaxPendingGlobal"], 200), 1, 10000));
    }

    /// <summary>按既有默认值读取 PostgreSQL 配置。</summary>
    public static PostgresOptions BindPostgres(IConfiguration configuration)
    {
        var section = configuration.GetSection(PostgresOptions.SectionName);
        return new PostgresOptions
        {
            ConnectionString = section["ConnectionString"] ?? string.Empty,
            RunMigrationsOnStartup = ReadBool(section["RunMigrationsOnStartup"], false),
        };
    }

    /// <summary>按既有默认值读取 Redis 连接和 key 前缀。</summary>
    public static RedisOptions BindRedis(IConfiguration configuration)
    {
        var section = configuration.GetSection(RedisOptions.SectionName);
        return new RedisOptions
        {
            Configuration = section["Configuration"] ?? "localhost:6379",
            InstanceName = section["InstanceName"] ?? "creativeflow:",
        };
    }

    /// <summary>按既有默认值读取消息连接、路由和预取配置。</summary>
    public static RabbitMqOptions BindRabbitMq(IConfiguration configuration)
    {
        var section = configuration.GetSection(RabbitMqOptions.SectionName);
        var routingKeys = section.GetSection("RoutingKeys").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();

        return new RabbitMqOptions
        {
            HostName = section["HostName"] ?? "localhost",
            Port = ReadInt(section["Port"], 5672),
            UserName = section["UserName"] ?? "guest",
            Password = section["Password"] ?? "guest",
            VirtualHost = section["VirtualHost"] ?? "/",
            Exchange = section["Exchange"] ?? "creativeflow.events",
            QueueName = section["QueueName"] ?? "creativeflow.events.api",
            PrefetchCount = (ushort)ReadInt(section["PrefetchCount"], 16),
            RoutingKeys = routingKeys.Length > 0 ? routingKeys : ["device.#"],
        };
    }

    /// <summary>按既有默认值读取设备识别的信号和隐私窗口配置。</summary>
    public static DeviceIdentityOptions BindDeviceIdentity(IConfiguration configuration)
    {
        var section = configuration.GetSection(DeviceIdentityOptions.SectionName);
        return new DeviceIdentityOptions
        {
            Pepper = section["Pepper"] ?? string.Empty,
            SignalVersion = ReadInt(section["SignalVersion"], 1),
            CoarseWindowHours = ReadInt(section["CoarseWindowHours"], 24),
            MaxCoarseFanout = ReadInt(section["MaxCoarseFanout"], 5),
            CookieName = section["CookieName"] ?? "creativeflow_did",
            CookieMaxAgeDays = ReadInt(section["CookieMaxAgeDays"], 730),
        };
    }

    /// <summary>读取账号会话及可选 GitHub OAuth 配置，并保留期限夹取规则。</summary>
    public static AuthenticationOptions BindAuthentication(IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthenticationOptions.SectionName);
        var github = section.GetSection("GitHub");

        return new AuthenticationOptions
        {
            PublicOrigin = section["PublicOrigin"] ?? "http://localhost:5298",
            SessionCookieName = section["SessionCookieName"] ?? "creativeflow_session",
            SessionMaxAgeDays = Math.Clamp(ReadInt(section["SessionMaxAgeDays"], 30), 1, 365),
            OAuthTransactionMinutes = Math.Clamp(
                ReadInt(section["OAuthTransactionMinutes"], 10), 1, 30),
            GitHub = new GitHubAuthenticationOptions
            {
                Enabled = ReadBool(github["Enabled"], false),
                ClientId = github["ClientId"] ?? string.Empty,
                ClientSecret = github["ClientSecret"] ?? string.Empty,
                Scope = github["Scope"] ?? "read:user user:email",
            },
        };
    }

    /// <summary>读取管理员初始化与会话配置；初始化密码缺失时保持为空并由启动校验拒绝。</summary>
    public static AdminOptions BindAdmin(IConfiguration configuration)
    {
        var section = configuration.GetSection(AdminOptions.SectionName);
        return new AdminOptions
        {
            DefaultUsername = section["DefaultUsername"] ?? "admin",
            DefaultPassword = section["DefaultPassword"] ?? string.Empty,
            SessionCookieName = section["SessionCookieName"] ?? "creativeflow_admin_session",
            SessionMaxAgeHours = Math.Clamp(ReadInt(section["SessionMaxAgeHours"], 12), 1, 168),
        };
    }

    /// <summary>读取 Skill 存储、包限制及 OpenSandbox 执行配置。</summary>
    public static SkillsOptions BindSkills(IConfiguration configuration)
    {
        var section = configuration.GetSection(SkillsOptions.SectionName);
        return new SkillsOptions
        {
            SandboxEnabled = ReadBool(section["SandboxEnabled"], false),
            Root = section["Root"] ?? "./storage/skills",
            RequireStorageMarker = ReadBool(section["RequireStorageMarker"], false),
            MaxUploadBytes = Math.Clamp(
                ReadInt(section["MaxUploadBytes"], 25 * 1024 * 1024), 1_048_576, 52_428_800),
            MaxExtractedBytes = Math.Clamp(
                ReadInt(section["MaxExtractedBytes"], 100 * 1024 * 1024), 1_048_576, 209_715_200),
            MaxEntries = Math.Clamp(ReadInt(section["MaxEntries"], 1_000), 10, 5_000),
            OpenSandboxDomain = section["OpenSandboxDomain"] ?? "localhost:8090",
            OpenSandboxApiKey = section["OpenSandboxApiKey"] ?? string.Empty,
            OpenSandboxImage = section["OpenSandboxImage"]
                ?? "opensandbox/code-interpreter:v1.1.0",
            RunnerTimeoutSeconds = Math.Clamp(ReadInt(section["RunnerTimeoutSeconds"], 30), 1, 120),
            RunnerMaxOutputBytes = Math.Clamp(
                ReadInt(section["RunnerMaxOutputBytes"], 256 * 1024), 16_384, 1_048_576),
            // 25 MiB 编码为 base64 后约为 33.4 MiB，可在运行器 40 MiB 请求上限内
            // 为路径、标准输入、参数和 JSON 留出受控空间。
            RunnerMaxPackageBytes = Math.Clamp(
                ReadInt(section["RunnerMaxPackageBytes"], 25 * 1024 * 1024),
                1_048_576, 25 * 1024 * 1024),
        };
    }

    /// <summary>读取 MCP 网络、超时与响应大小配置。</summary>
    public static McpOptions BindMcp(IConfiguration configuration)
    {
        var section = configuration.GetSection(McpOptions.SectionName);
        return new McpOptions
        {
            AllowLoopbackHttp = ReadBool(section["AllowLoopbackHttp"], false),
            RequestTimeoutSeconds = Math.Clamp(ReadInt(section["RequestTimeoutSeconds"], 30), 5, 120),
            MaxResponseBytes = Math.Clamp(
                ReadInt(section["MaxResponseBytes"], 1_048_576), 65_536, 4_194_304),
        };
    }

    /// <summary>读取加密、媒体与上游请求配置，并保留超时下限。</summary>
    public static AiOptions BindAi(IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        return new AiOptions
        {
            EncryptionKey = section["EncryptionKey"] ?? string.Empty,
            AssetRoot = section["AssetRoot"] ?? "./storage/assets",
            RequireStorageMarker = ReadBool(section["RequireStorageMarker"], false),
            MaxAssetBytes = Math.Clamp(long.TryParse(section["MaxAssetBytes"], out var assetBytes) ? assetBytes : 512L * 1024 * 1024, 1, 2L * 1024 * 1024 * 1024),
            AssetWriteTimeoutSeconds = Math.Clamp(ReadInt(section["AssetWriteTimeoutSeconds"], 300), 10, 600),
            RequestTimeoutSeconds = Math.Max(
                AiOptions.MinimumRequestTimeoutSeconds,
                ReadInt(section["RequestTimeoutSeconds"], 1200)),
            SignedMediaUrlLifetimeMinutes = Math.Clamp(
                ReadInt(section["SignedMediaUrlLifetimeMinutes"], 60), 5, 120),
        };
    }

    /// <summary>读取外部工作流开关、Key、配额、并发与限流配置。</summary>
    public static ExternalWorkflowApiOptions BindExternalWorkflowApi(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ExternalWorkflowApiOptions.SectionName);
        return new ExternalWorkflowApiOptions
        {
            Enabled = ReadBool(section["Enabled"], true),
            ApiKeyHashSecret = section["ApiKeyHashSecret"] ?? string.Empty,
            MaxImageBytes = Math.Clamp(
                ReadInt(section["MaxImageBytes"], 20 * 1024 * 1024),
                1_048_576,
                52_428_800),
            MaxImagePixels = Math.Clamp(
                ReadInt(section["MaxImagePixels"], 40_000_000), 1_000_000, 100_000_000),
            MaxImageDimension = Math.Clamp(
                ReadInt(section["MaxImageDimension"], 16_384), 1_024, 65_535),
            MaxQueuedRunsPerWorkflow = Math.Clamp(
                ReadInt(section["MaxQueuedRunsPerWorkflow"], 100), 1, 10_000),
            GlobalConcurrency = Math.Clamp(
                ReadInt(section["GlobalConcurrency"], 4), 1, 32),
            DefaultWorkflowConcurrency = Math.Clamp(
                ReadInt(section["DefaultWorkflowConcurrency"], 2), 1, 32),
            UploadedAssetLifetimeHours = Math.Clamp(
                ReadInt(section["UploadedAssetLifetimeHours"], 24), 1, 720),
            CreateRunPermitLimitPerMinute = Math.Clamp(
                ReadInt(section["CreateRunPermitLimitPerMinute"], 30), 1, 10_000),
            ImageUploadPermitLimitPerMinute = Math.Clamp(
                ReadInt(section["ImageUploadPermitLimitPerMinute"], 30), 1, 10_000),
            StatusPermitLimitPerMinute = Math.Clamp(
                ReadInt(section["StatusPermitLimitPerMinute"], 300), 1, 100_000),
        };
    }

    /// <summary>从 JSON 或配置读取整数，保留原有默认值规则。</summary>
    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>从 JSON 或配置读取布尔值，保留原有默认值规则。</summary>
    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;
}
