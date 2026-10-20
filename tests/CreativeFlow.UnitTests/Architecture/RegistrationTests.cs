using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.Infrastructure.Ai.Workflows;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>验证注册职责迁移后用例作用域和后台执行所有权仍然一致。</summary>
public sealed class RegistrationTests
{
    /// <summary>应用注册幂等，验证器单例、依赖账号范围的用例仍为 scoped。</summary>
    [Fact]
    public void ApplicationRegistrationIsExplicitAndIdempotent()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        var count = services.Count;
        services.AddApplication();
        Assert.Equal(count, services.Count);
        foreach (var type in new[]
        {
            typeof(BackgroundAccountScope), typeof(ResolveDeviceIdentity),
            typeof(AccountAuthService), typeof(PasswordAuthService), typeof(AdminAuthService),
            typeof(AgentTurnRunner), typeof(GenerateImagesUseCase), typeof(GenerateTextUseCase),
            typeof(AgentModelResolver), typeof(AgentModelMessageConverter), typeof(AgentRunEventRecorder),
            typeof(AgentToolExecutionService), typeof(AgentToolDiscovery), typeof(AgentToolApprovalPolicy), typeof(AgentSkillTools),
        })
        {
            var registration = Assert.Single(services, descriptor => descriptor.ServiceType == type);
            Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        }
        Assert.Equal(ServiceLifetime.Singleton,
            Assert.Single(services, descriptor =>
                descriptor.ServiceType == typeof(IValidator<DeviceSignalsPayload>)).Lifetime);
    }

    /// <summary>残留迁移配置无法改变 MAF 唯一注册；原业务作用域、宿主与取消所有权保持一致。</summary>
    [Theory]
    [InlineData("Legacy", "Legacy")]
    [InlineData("Maf", "Legacy")]
    [InlineData("Legacy", "Maf")]
    [InlineData("Maf", "Maf")]
    [InlineData("invalid", "auto")]
    public async Task InfrastructureFacadePreservesScopesAndTheSingleAgentHost(string agent, string workflow)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentRealtimeSink, SilentRealtime>();
        services.AddSingleton<IRealtimeNotifier, SilentRealtime>();
        services.AddInfrastructure(TestConfiguration(agent, workflow));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        // 只解析服务，不启动后台宿主，也不打开数据库/Redis/RabbitMQ 连接。
        var host = provider.GetRequiredService<AgentRunHost>();
        Assert.Same(host, provider.GetRequiredService<IAgentRunControl>());
        Assert.Same(host, Assert.Single(provider.GetServices<IHostedService>().OfType<AgentRunHost>()));
        Assert.Single(provider.GetServices<IHostedService>().OfType<WorkflowRunWorker>());
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.IsType<MafAgentRuntime>(first.ServiceProvider.GetRequiredService<IAgentRuntime>());
        Assert.IsType<MafWorkflowRuntime>(first.ServiceProvider.GetRequiredService<IWorkflowRuntime>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAgentRuntime));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IWorkflowRuntime));
        foreach (var type in new[] { typeof(GenerateImagesUseCase), typeof(GenerateTextUseCase), typeof(IAgentRuntime), typeof(IWorkflowRuntime),
            typeof(WorkflowRunOrchestrator), typeof(WorkflowNodeExecutorRegistry), typeof(IWorkflowNodeProgress), typeof(WorkflowVideoJobAwaiter),
            typeof(AgentTurnRunner), typeof(AgentModelResolver), typeof(AgentModelMessageConverter), typeof(AgentRunEventRecorder),
            typeof(AgentToolExecutionService), typeof(AgentToolDiscovery), typeof(AgentToolApprovalPolicy), typeof(AgentSkillTools) })
        {
            var useCase = first.ServiceProvider.GetRequiredService(type);
            Assert.Same(useCase, first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(useCase, second.ServiceProvider.GetRequiredService(type));
        }
    }

    /// <summary>即便节点通过延迟工厂分派，缺失必需业务依赖也必须在构建容器时报告。</summary>
    [Theory]
    [InlineData(typeof(IWorkflowV2Models))]
    [InlineData(typeof(IVideoGenerationDispatcher))]
    [InlineData(typeof(IAssetStore))]
    [InlineData(typeof(IVideoFrameService))]
    [InlineData(typeof(IWorkflowNodeProgress))]
    public void MissingWorkflowDependenciesFailBeforeAJobCanRun(Type dependency)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentRealtimeSink, SilentRealtime>();
        services.AddSingleton<IRealtimeNotifier, SilentRealtime>();
        services.AddInfrastructure(TestConfiguration("Maf", "Maf"));
        services.RemoveAll(dependency);
        var error = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        }));
        Assert.Contains(dependency.FullName!, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>使用无业务意义的隔离配置验证注册，禁止借用机器上的真实连接或密钥。</summary>
    private static IConfiguration TestConfiguration(string agent, string workflow) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=creativeflow_governance_test;Username=test;Password=test",
            ["DeviceIdentity:Pepper"] = "governance-test-only-pepper",
            ["RuntimeMigration:Agent"] = agent,
            ["RuntimeMigration:Workflow"] = workflow,
            ["RuntimeMigration:AllowAllAccounts"] = "true",
        }).Build();

    /// <summary>服务解析时需要的实时端口；本测试不会发送任何外部消息。</summary>
    private sealed class SilentRealtime : IRealtimeNotifier, IAgentRealtimeSink
    {
        /// <summary>忽略设备分组消息，使依赖注册测试不启动 SignalR。</summary>
        public Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>忽略访客分组消息，使依赖注册测试不依赖实时连接。</summary>
        public Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>忽略全局广播，仅为依赖解析提供无副作用实现。</summary>
        public Task BroadcastAsync<T>(string method, T payload, CancellationToken ct) => Task.CompletedTask;

        /// <summary>忽略 Agent 事件，仅满足宿主依赖装配，不持久化或发送事件。</summary>
        public Task SendEventAsync(DeviceId deviceId, AgentEventEnvelope payload, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>忽略画布工具消息，注册测试不执行浏览器工具。</summary>
        public Task SendToolCallAsync(string connectionId, AgentToolCallEnvelope payload, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>忽略审批通知，注册测试不触发审批或生成决策。</summary>
        public Task SendApprovalRequestAsync(string connectionId, AgentApprovalEnvelope payload, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
