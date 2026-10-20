using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Workflows;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>阻止节点逻辑重新回流到编排器，保证业务执行器不持有另一套调度器。</summary>
public sealed class WorkflowExecutionBoundaryTests
{
    /// <summary>编排器依赖 MAF 端口和注册表，不直接依赖图片、视频或资产存储客户端。</summary>
    [Fact]
    public void OrchestratorDoesNotOwnNodeBusinessAdapters()
    {
        var parameters = Assert.Single(typeof(WorkflowRunOrchestrator).GetConstructors()).GetParameters();
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IWorkflowRuntime));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(WorkflowNodeExecutorRegistry));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IServiceProvider)
            || parameter.ParameterType == typeof(IImageGenerationDispatcher)
            || parameter.ParameterType == typeof(IVideoGenerationDispatcher)
            || parameter.ParameterType == typeof(IAssetStore));
    }

    /// <summary>所有具体节点依赖显式业务端口，不依赖服务容器、调度引擎或完整编排器。</summary>
    [Fact]
    public void ExecutorsCannotResolveServicesOrScheduleOtherNodes()
    {
        var types = typeof(IWorkflowNodeExecutor).Assembly.GetTypes()
            .Concat(typeof(WorkflowRunOrchestrator).Assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(IWorkflowNodeExecutor).IsAssignableFrom(type)).ToArray();
        Assert.Equal(16, types.Length);
        foreach (var type in types)
            Assert.All(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()), parameter =>
            {
                Assert.NotEqual(typeof(IServiceProvider), parameter.ParameterType);
                Assert.NotEqual(typeof(IServiceScopeFactory), parameter.ParameterType);
                Assert.NotEqual(typeof(IWorkflowRuntime), parameter.ParameterType);
                Assert.NotEqual(typeof(WorkflowRunOrchestrator), parameter.ParameterType);
            });
        Assert.DoesNotContain(typeof(WorkflowRunOrchestrator).Assembly.GetTypes(), type => type.Name == "WorkflowExecution");
        Assert.DoesNotContain(typeof(IWorkflowNodeExecutor).Assembly.GetTypes(), type => type.Name == "WorkflowV2Executor");
    }

    /// <summary>执行上下文只能携带约定的数据快照，禁止服务、工厂、进度回调和任意对象服务袋。</summary>
    [Fact]
    public void ExecutionContextContainsOnlyData()
    {
        var allowed = new[] { typeof(WorkflowNodeFamily), typeof(WorkflowRun), typeof(WorkflowPlanNode),
            typeof(IReadOnlyDictionary<string, WorkflowNodeValue>), typeof(JsonObject), typeof(IReadOnlySet<string>), typeof(WorkflowNodeRun), typeof(int) };
        Assert.All(typeof(WorkflowNodeExecutionContext).GetProperties(), property => Assert.Contains(property.PropertyType, allowed));
        Assert.All(Assert.Single(typeof(WorkflowNodeExecutionContext).GetConstructors()).GetParameters(),
            parameter => Assert.Contains(parameter.ParameterType, allowed));
    }

    /// <summary>业务目录禁止容器解析或创建作用域；Worker 和依赖装配目录保留边界职责。</summary>
    [Theory]
    [InlineData("src/CreativeFlow.Application/Features")]
    [InlineData("src/CreativeFlow.Infrastructure/Ai/Workflows")]
    public void BusinessCodeCannotUseServiceLocators(string directory)
    {
        var forbidden = new[] { "IServiceProvider", "IServiceScopeFactory", "GetRequiredService", "GetService(",
            "GetService<", "GetServices<", "GetKeyedService", "GetRequiredKeyedService", "CreateAsyncScope(", "CreateScope(", "ActivatorUtilities" };
        foreach (var path in Directory.EnumerateFiles(Path.Combine(DependencyBoundaryTests.RepositoryRoot(), directory), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            foreach (var token in forbidden)
                Assert.False(source.Contains(token, StringComparison.Ordinal), $"{path} must not contain {token}.");
        }
    }

    /// <summary>节点行为测试只构造所需依赖，不通过完整 DI 容器掩盖真实依赖关系。</summary>
    [Theory]
    [InlineData("tests/CreativeFlow.UnitTests/Support/WorkflowNodeTestHost.cs")]
    [InlineData("tests/CreativeFlow.UnitTests/Ai/WorkflowMediaNodeExecutorTests.cs")]
    public void NodeUnitTestsDoNotRequireAContainer(string path)
    {
        var source = File.ReadAllText(Path.Combine(DependencyBoundaryTests.RepositoryRoot(), path));
        Assert.DoesNotContain("ServiceCollection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceProvider", source, StringComparison.Ordinal);
    }

    /// <summary>正式环境也检查构造依赖和 scoped 生命周期，而非只在开发环境启用。</summary>
    [Fact]
    public void ApiAlwaysValidatesDependencyGraphsAtStartup()
    {
        var source = File.ReadAllText(Path.Combine(DependencyBoundaryTests.RepositoryRoot(), "src/CreativeFlow.Api/Program.cs"));
        Assert.Contains("builder.Host.UseDefaultServiceProvider(options =>", source, StringComparison.Ordinal);
        Assert.Contains("options.ValidateOnBuild = true;", source, StringComparison.Ordinal);
        Assert.Contains("options.ValidateScopes = true;", source, StringComparison.Ordinal);
    }
}
