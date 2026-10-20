using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Agent;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>防止工具、消息与通知职责重新回流到 Agent 轮次编排器。</summary>
public sealed class AgentExecutionBoundaryTests
{
    /// <summary>轮次运行器只依赖业务服务和唯一调度端口，不直接访问工具、素材、密钥或推送端。</summary>
    [Fact]
    public void RunnerCannotCallToolOrMessageAdaptersDirectly()
    {
        var parameters = Assert.Single(typeof(AgentTurnRunner).GetConstructors()).GetParameters();
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IAgentRuntime));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(AgentToolExecutionService));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(AgentModelMessageConverter));
        var forbidden = new[] { typeof(IServiceProvider), typeof(IMcpRuntime), typeof(IMcpServerRepository),
            typeof(ISkillRepository), typeof(ISkillStore), typeof(ISkillScriptRunner), typeof(IAgentCanvasBridge),
            typeof(ICanvasAssetRepository), typeof(IAssetStore), typeof(IAgentRealtimeSink), typeof(IApiKeyProtector), typeof(IProviderRepository) };
        Assert.DoesNotContain(parameters, parameter => forbidden.Contains(parameter.ParameterType));
        var source = File.ReadAllText(Path.Combine(DependencyBoundaryTests.RepositoryRoot(),
            "src/CreativeFlow.Application/Features/Agent/AgentTurnRunner.cs"));
        foreach (var call in new[] { "CallToolAsync(", "RequestApprovalAsync(", "OpenReadAsync(", "SendEventAsync(",
                     "ParseBlocks(", "SerializeBlocks(", "InsertToolCallAsync(" })
            Assert.DoesNotContain(call, source, StringComparison.Ordinal);
    }

    /// <summary>拆分服务不得获取容器、再次调度轮次，工具服务也不得持有模型客户端。</summary>
    [Fact]
    public void ServicesCannotScheduleOrReplayModelTurns()
    {
        var services = new[] { typeof(AgentToolExecutionService), typeof(AgentToolDiscovery), typeof(AgentSkillTools),
            typeof(AgentModelMessageConverter), typeof(AgentRunEventRecorder), typeof(AgentModelResolver), typeof(AgentToolApprovalPolicy) };
        foreach (var service in services)
            Assert.All(service.GetConstructors().SelectMany(constructor => constructor.GetParameters()), parameter =>
            {
                Assert.NotEqual(typeof(IServiceProvider), parameter.ParameterType);
                Assert.NotEqual(typeof(IAgentRuntime), parameter.ParameterType);
                Assert.NotEqual(typeof(AgentTurnRunner), parameter.ParameterType);
                Assert.NotEqual(typeof(IAgentModelDispatcher), parameter.ParameterType);
            });
        Assert.Empty(Assert.Single(typeof(AgentToolApprovalPolicy).GetConstructors()).GetParameters());
    }
}
