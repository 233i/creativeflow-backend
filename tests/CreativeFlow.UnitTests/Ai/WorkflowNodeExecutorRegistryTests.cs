using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai.Workflows;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证执行器分派、延迟解析与媒体绑定，不调用模型或外部服务。</summary>
public sealed class WorkflowNodeExecutorRegistryTests
{
    /// <summary>生产注册覆盖所有现有节点，重复装配不增加第二套执行器。</summary>
    [Fact]
    public async Task ProductionRegistrationIsCompleteAndIdempotent()
    {
        var services = new ServiceCollection().AddWorkflowExecution();
        var count = services.Count;
        services.AddWorkflowExecution();
        Assert.Equal(count, services.Count);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var entries = scope.ServiceProvider.GetServices<WorkflowNodeExecutorRegistration>().ToArray();
        Assert.Equal(new[] { "image", "imageGenerate", "imageOutput", "output", "text", "videoExtend", "videoFrame", "videoGenerate", "videoOutput" },
            entries.Where(item => item.Family == WorkflowNodeFamily.Media).Select(item => item.NodeType).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "batch", "condition", "end", "httpRequest", "intentClassify", "knowledgeAnswer", "knowledgeRetrieve", "llm", "loop", "merge", "parameterExtract", "script", "start", "transform" },
            entries.Where(item => item.Family == WorkflowNodeFamily.General).Select(item => item.NodeType).Order(StringComparer.Ordinal));
        Assert.Equal(23, entries.Length);
        Assert.Same(scope.ServiceProvider.GetRequiredService<WorkflowNodeExecutorRegistry>(),
            scope.ServiceProvider.GetRequiredService<WorkflowNodeExecutorRegistry>());
    }

    /// <summary>未执行的节点不解析其依赖，恢复历史输出无需安装或调用模型服务。</summary>
    [Fact]
    public async Task OnlyTheSelectedNodeFactoryIsResolved()
    {
        var calls = 0;
        var executor = new RecordingExecutor();
        var registry = new WorkflowNodeExecutorRegistry([
            new(WorkflowNodeFamily.Media, "text", () => { calls++; return executor; }),
            new(WorkflowNodeFamily.Media, "imageGenerate", () => throw new InvalidOperationException("Model must not resolve.")),
        ]);
        Assert.Equal(0, calls);
        var context = Context("text");
        var output = await registry.ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Same(context, executor.Seen);
        Assert.Equal(["selected"], output.Texts);
    }

    /// <summary>同语义下重复注册必须失败；不同语义的同名节点不会交叉路由。</summary>
    [Fact]
    public async Task RegistrationRejectsDuplicatesAndSeparatesFamilies()
    {
        var executor = new RecordingExecutor();
        var registration = new WorkflowNodeExecutorRegistration(WorkflowNodeFamily.Media, "text", () => executor);
        Assert.Throws<InvalidOperationException>(() => new WorkflowNodeExecutorRegistry([registration, registration]));
        var general = new RecordingExecutor();
        var registry = new WorkflowNodeExecutorRegistry([registration, new(WorkflowNodeFamily.General, "text", () => general)]);
        var context = Context("text") with { Family = WorkflowNodeFamily.General };
        await registry.ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Null(executor.Seen);
        Assert.Same(context, general.Seen);
        await Assert.ThrowsAsync<WorkflowExecutionException>(() => registry.ExecuteAsync(Context("unknown"), TestContext.Current.CancellationToken));
    }

    /// <summary>结果节点保留连线顺序和重复引用，不因提取注册表而合并或重排素材。</summary>
    [Theory]
    [InlineData("imageOutput")]
    [InlineData("videoOutput")]
    [InlineData("output")]
    public async Task MediaOutputPreservesEdgeOrderAndDuplicates(string type)
    {
        var context = Context(type);
        context = context with
        {
            Node = context.Node with
            {
                Inputs = [new("b", "b", "image", "node", "image", WorkflowValueType.Image),
                new("a", "a", "image", "node", "image", WorkflowValueType.Image)]
            },
            Values = new Dictionary<string, WorkflowNodeValue>
            {
                ["a"] = new([], ["a", "same"], []),
                ["b"] = new([], ["b", "same"], []),
            },
        };
        var value = await new MediaOutputNodeExecutor().ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(["b", "same", "a", "same"], value.Images);
    }

    /// <summary>文本仍去除首尾空白，缺失输入的结果节点明确失败而不是交付空结果。</summary>
    [Fact]
    public async Task MediaValueNodesKeepOriginalValidation()
    {
        var context = Context("text");
        context.Node.Data["text"] = "  original prompt  ";
        Assert.Equal(["original prompt"], (await new TextInputNodeExecutor().ExecuteAsync(context, TestContext.Current.CancellationToken)).Texts);
        context = Context("output");
        context = context with { Node = context.Node with { Inputs = [new("missing", "absent", "text", "node", "text", WorkflowValueType.Text)] } };
        var error = await Assert.ThrowsAsync<WorkflowExecutionException>(() => new MediaOutputNodeExecutor().ExecuteAsync(context, TestContext.Current.CancellationToken));
        Assert.Equal("Upstream node 'absent' did not produce a value.", error.Message);
    }

    /// <summary>构造没有持久化或收费副作用的媒体节点执行数据。</summary>
    internal static WorkflowNodeExecutionContext Context(string type)
    {
        var run = WorkflowNodeTestHost.Run();
        var node = new WorkflowPlanNode("node", type, new JsonObject(), []);
        return new(WorkflowNodeFamily.Media, run, node, new Dictionary<string, WorkflowNodeValue>(),
            new JsonObject(), new HashSet<string>(), null, WorkflowNodeTestHost.NodeRun(run, node));
    }

    /// <summary>生产工厂取得的是当前作用域的具体执行器，不在每次调用时创建另一套依赖。</summary>
    [Fact]
    public async Task ProductionFactoriesRespectScopeAndExposeConcreteTypesForValidation()
    {
        var services = new ServiceCollection().AddWorkflowExecution();
        var executors = services.Where(item => typeof(IWorkflowNodeExecutor).IsAssignableFrom(item.ServiceType)).ToArray();
        Assert.Equal(16, executors.Length);
        Assert.All(executors, item =>
        {
            Assert.Equal(ServiceLifetime.Scoped, item.Lifetime);
            Assert.Equal(item.ServiceType, item.ImplementationType);
            Assert.Null(item.ImplementationFactory);
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var a = first.ServiceProvider.GetServices<WorkflowNodeExecutorRegistration>().Single(item => item.NodeType == "text");
        var b = second.ServiceProvider.GetServices<WorkflowNodeExecutorRegistration>().Single(item => item.NodeType == "text");
        Assert.Same(first.ServiceProvider.GetRequiredService<TextInputNodeExecutor>(), a.Resolve());
        Assert.Same(a.Resolve(), a.Resolve());
        Assert.NotSame(a.Resolve(), b.Resolve());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<TextInputNodeExecutor>());
    }

    /// <summary>记录实际接收的上下文，验证注册表不复制或更改执行数据。</summary>
    private sealed class RecordingExecutor : IWorkflowNodeExecutor
    {
        /// <summary>最后一次分派传入的上下文。</summary>
        public WorkflowNodeExecutionContext? Seen { get; private set; }

        /// <summary>返回固定结果，不访问任何基础设施。</summary>
        public Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
        {
            Seen = context;
            return Task.FromResult(WorkflowNodeValue.FromText("selected"));
        }
    }
}
