using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Serilog;
using CreativeFlow.Api.Auth;
using CreativeFlow.Api.Extensions;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.OpenApi;
using CreativeFlow.Api.Realtime;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence.Migrations;

var builder = WebApplication.CreateBuilder(args);

// 所有环境都检查构造依赖和生命周期；节点仍按需实例化，启动检查不调用模型或创建外部任务。
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// 私有文件只覆盖公共/环境 JSON；User Secrets、环境变量和命令行继续拥有更高优先级。
builder.AddEnvironmentOverrides();

builder.ConfigureSerilog();
builder.ConfigureOpenTelemetry();

// MVC 与 SignalR 共享同一份源生成元数据，确保请求与响应契约完全一致。
builder.Services.AddControllers(options => { options.Filters.Add<GenerationHistoryFilter>(); options.Filters.Add<AssetAvailabilityExceptionFilter>(); })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    });

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SignalRNotifier>();
builder.Services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<SignalRNotifier>());
builder.Services.AddSingleton<IAgentRealtimeSink>(sp => sp.GetRequiredService<SignalRNotifier>());
builder.Services.AddSingleton<ExternalWorkflowRateLimiter>();

var signalR = builder.Services.AddSignalR();

// 插入解析器链，而不是直接赋值 TypeInfoResolver；直接赋值会丢弃 SignalR 为自身协议类型安装的解析器。
builder.Services.Configure<JsonHubProtocolOptions>(options =>
{
    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
});

// 默认关闭：单节点不需要背板，多一个外部依赖只会扩大故障面。
if (string.Equals(builder.Configuration["Realtime:Backplane"], "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConfiguration = builder.Configuration["Redis:Configuration"] ?? "localhost:6379";
    signalR.AddStackExchangeRedis(redisConfiguration, o =>
        o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("creativeflow"));
}

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CreativeFlow API",
        Version = "v1",
        Description = "CreativeFlow 管理、画布、AI 生成与外部工作流 API。",
    });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "CreativeFlow API Key",
        Description = "外部工作流接口请输入 cf_live_ 开头的 API Key。",
    });
    options.OperationFilter<ExternalWorkflowBearerOperationFilter>();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ApiRateLimitPolicies.PasswordAuthentication, context =>
    {
        // 不要信任任意语法有效的设备 Cookie 作为分区键：攻击者可轮换 UUID，
        // 使限流缓存无限增长。此时 DeviceContext 已解析持久化设备；未知 Cookie 回退到 IP。
        var deviceId = context.GetVisitorId() is not null ? context.GetDeviceId() : null;
        var partitionKey = deviceId is { } device
            ? $"device:{device.Value}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            });
    });
});

var app = builder.Build();

DeviceCookie.Configure(app.Services.GetRequiredService<IOptions<DeviceIdentityOptions>>());
AccountSessionCookie.Configure(
    app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value);
AdminSessionCookie.Configure(
    app.Services.GetRequiredService<IOptions<AdminOptions>>().Value);
VerifyCriticalRegistrations(app.Services);

// --migrate-only 使部署可将架构变更作为显式步骤应用，而不是发布的副作用。
if (args.Contains("--migrate-only"))
{
    return await RunMigrationsAsync(app.Services, args.Contains("--discard-database-files"));
}

if (args.Contains("--discard-database-files"))
    throw new InvalidOperationException("--discard-database-files 只能与 --migrate-only 一起用于显式停机切换。");

if (app.Services.GetRequiredService<IOptions<PostgresOptions>>().Value.RunMigrationsOnStartup)
{
    var migrationExitCode = await RunMigrationsAsync(app.Services);
    if (migrationExitCode != 0)
    {
        return migrationExitCode;
    }
}

await EnsureDefaultAdminAsync(app.Services);

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "CreativeFlow API";
    options.SwaggerEndpoint("v1/swagger.json", "CreativeFlow API v1");
    options.DisplayRequestDuration();
    options.EnableTryItOutByDefault();
});

app.UseSerilogRequestLogging();
app.UseDeviceContext();
app.UseAccountContext();
app.UseAdminContext();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapHub<CreativeFlowHub>("/hubs/creativeflow");

app.Run();
return 0;

// 通过现有迁移运行器执行迁移并返回部署退出码。
static async Task<int> RunMigrationsAsync(IServiceProvider services, bool discardDatabaseFiles = false)
{
    await using var scope = services.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var applied = await runner.RunAsync(CancellationToken.None, discardDatabaseFiles);
        logger.LogInformation("Migrations complete ({Applied} applied).", applied);
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Migrations failed.");
        return 1;
    }
}

// 在启动时按既有规则确保初始化管理员存在。
static async Task EnsureDefaultAdminAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AdminAuthService>()
        .EnsureDefaultAsync(CancellationToken.None);
}

/// <summary>
/// 对关键运行时注册进行快速失败。
/// </summary>
/// <remarks>
/// 显式解析交付层依赖，让漏注册在进程接收流量前失败。即使当前 MVC 宿主使用 JIT，
/// 这仍能防止配置分支或重构让验证器、运行控制器和仓储在生产请求中才首次暴露问题。
/// </remarks>
static void VerifyCriticalRegistrations(IServiceProvider services)
{
    using var scope = services.CreateScope();
    _ = scope.ServiceProvider.GetRequiredService<IValidator<DeviceSignalsPayload>>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentModelDispatcher>();
    _ = scope.ServiceProvider.GetRequiredService<IMcpRuntime>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentRunControl>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentRealtimeSink>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentCanvasBridge>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillPackageScanner>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillStore>();
    _ = scope.ServiceProvider.GetRequiredService<IAssetStore>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillScriptRunner>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IAdminRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IModelSlotRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IImageGenerationSettingsRepository>();
    _ = scope.ServiceProvider.GetRequiredService<ISignedMediaUrlService>();
    _ = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IWorkflowTemplateRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IWorkflowExternalApiRepository>();
    _ = scope.ServiceProvider.GetRequiredService<IWorkflowApiKeyService>();
    _ = scope.ServiceProvider.GetRequiredService<AccountAuthService>();
    _ = scope.ServiceProvider.GetRequiredService<PasswordAuthService>();
    _ = scope.ServiceProvider.GetRequiredService<AdminAuthService>();
    _ = scope.ServiceProvider.GetRequiredService<IPasswordCredentialHasher>();
    _ = scope.ServiceProvider.GetRequiredService<IExternalLoginProviderRegistry>();
}

/// <summary>公开此类型，使集成测试可以驱动宿主。</summary>
public partial class Program;
