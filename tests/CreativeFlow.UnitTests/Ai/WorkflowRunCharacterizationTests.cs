using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>在真实 SQL 与原 Worker 上冻结输出复用、失败、取消和通知语义；没有注册任何收费模型客户端。</summary>
public sealed class WorkflowRunCharacterizationTests
{
    /// <summary>缺少显式测试库时报告跳过。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>分支图的输出连线特意与生成节点排列相反，以固定多图汇合顺序。</summary>
    private const string BranchGraph = """
        {"nodes":[{"id":"text","type":"text","data":{"text":"original prompt"}},
         {"id":"a","type":"imageGenerate","data":{}},{"id":"b","type":"imageGenerate","data":{}},
         {"id":"out","type":"imageOutput","data":{}}],
         "edges":[{"id":"ta","source":"text","sourceHandle":"text","target":"a","targetHandle":"prompt"},
         {"id":"tb","source":"text","sourceHandle":"text","target":"b","targetHandle":"prompt"},
         {"id":"bo","source":"b","sourceHandle":"image","target":"out","targetHandle":"image"},
         {"id":"ao","source":"a","sourceHandle":"image","target":"out","targetHandle":"image"}]}
        """;
    /// <summary>图定义有效但素材不存在，用来验证真正的节点失败而不调用模型。</summary>
    private const string MissingImageGraph = """
        {"nodes":[{"id":"input","type":"image","data":{"assetUrl":"/api/v1/canvas/assets/10000000-0000-0000-0000-000000000001"}},
         {"id":"out","type":"imageOutput","data":{}}],
         "edges":[{"id":"io","source":"input","sourceHandle":"image","target":"out","targetHandle":"image"}]}
        """;

    /// <summary>成功的生成节点直接恢复旧输出，未安装模型服务也应完成；SignalR 失败不影响持久结果。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedGenerationsAreReusedInEdgeOrder(bool dropIntermediateNotifications)
    {
        await using var fixture = await Fixture.CreateAsync(BranchGraph, new Dictionary<string, string>
        {
            ["a"] = Output("asset-a1", "asset-a2"), ["b"] = Output("asset-b"),
        });
        fixture.Notifications.DropIntermediate = dropIntermediateNotifications;
        var terminal = await fixture.ExecuteAsync();
        Assert.Equal("succeeded", terminal.Status);
        var nodes = await fixture.Runs.ListNodesAsync(fixture.Run.Id, TestContext.Current.CancellationToken);
        var output = JsonNode.Parse(Assert.Single(nodes, node => node.NodeId == "out").OutputJson!)!;
        Assert.Equal(["asset-b", "asset-a1", "asset-a2"], output["images"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.DoesNotContain(fixture.Notifications.Events, item => item.NodeId is "a" or "b");
        Assert.All(nodes, node => Assert.Equal(WorkflowNodeRunStatus.Succeeded, node.Status));
        Assert.All(nodes.Where(node => node.NodeId is "a" or "b"), node => Assert.True(node.IsReused));
    }

    /// <summary>已完成输出无法恢复时明确失败，不能回退成再次生成。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CorruptCompletedOutputFailsWithoutRegeneration()
    {
        await using var fixture = await Fixture.CreateAsync(BranchGraph, new Dictionary<string, string>
        {
            ["a"] = "{}", ["b"] = Output("asset-b"),
        });
        var terminal = await fixture.ExecuteAsync();
        Assert.Equal("failed", terminal.Status);
        Assert.Contains("completed output for node 'a' could not be restored", terminal.Error);
        Assert.DoesNotContain(fixture.Notifications.Events, item => item.NodeId is "a" or "b");
    }

    /// <summary>失败节点保存错误、未运行下游标记 skipped，失败运行不会隐式继续。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MissingInputFailsAndSkipsDownstream()
    {
        await using var fixture = await Fixture.CreateAsync(MissingImageGraph, new Dictionary<string, string>());
        var terminal = await fixture.ExecuteAsync();
        Assert.Equal("failed", terminal.Status);
        var nodes = await fixture.Runs.ListNodesAsync(fixture.Run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowNodeRunStatus.Failed, Assert.Single(nodes, node => node.NodeId == "input").Status);
        Assert.Equal(WorkflowNodeRunStatus.Skipped, Assert.Single(nodes, node => node.NodeId == "out").Status);
        Assert.Equal("The input image is no longer available.", terminal.Error);
    }

    /// <summary>节点开始后取消，晚到的本地执行结果不能把取消覆盖为成功。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CancellationBeforeNodeResultIsNeverOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync(BranchGraph, new Dictionary<string, string>
        {
            ["a"] = Output("asset-a"), ["b"] = Output("asset-b"),
        });
        fixture.Notifications.OnEvent = async item =>
        {
            if (item.NodeId == "text" && item.NodeStatus == "running")
                await fixture.Runs.CancelAsync(fixture.Run.DeviceId, fixture.Run.WorkflowId, fixture.Run.Id,
                    DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        };
        Assert.Equal("cancelled", (await fixture.ExecuteAsync()).Status);
        var run = await fixture.Runs.FindAsync(fixture.Run.DeviceId, fixture.Run.WorkflowId, fixture.Run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowRunStatus.Cancelled, run!.Status);
        Assert.DoesNotContain(fixture.Notifications.Events, item => item.NodeStatus == "succeeded");
    }

    /// <summary>分支重新执行只包含指定分支与汇合点，不包含另一分支的成功素材。</summary>
    [Fact]
    public void PartialRerunDoesNotPullInUnaffectedBranch()
    {
        var plan = WorkflowGraphValidator.Validate(JsonNode.Parse(BranchGraph));
        Assert.True(plan.IsValid, string.Join(";", plan.Errors));
        var affected = WorkflowPartialRerunPlanner.FindRerunNodeIds(plan.Plan!, "a");
        Assert.Equal(["a", "out"], affected!.Order(StringComparer.Ordinal));
    }

    /// <summary>按旧节点输出格式构造合成素材引用，不访问任何真实素材。</summary>
    private static string Output(params string[] images) => new JsonObject
    {
        ["texts"] = new JsonArray(), ["images"] = new JsonArray(images.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["videos"] = new JsonArray(),
    }.ToJsonString();

    /// <summary>只启动原 WorkflowRunWorker；SQL 和节点执行保持原实现，模型服务完全不注册。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>当前测试的独立数据库 schema。</summary>
        private ImageJobDatabase database = null!;
        /// <summary>只用于本测试后台作用域的容器。</summary>
        private ServiceProvider services = null!;
        /// <summary>未修改的原工作流宿主。</summary>
        private WorkflowRunWorker worker = null!;
        /// <summary>真实工作流仓储，供测试读取最终状态。</summary>
        public WorkflowRunRepository Runs { get; private set; } = null!;
        /// <summary>合成运行的不可变初始记录。</summary>
        public WorkflowRun Run { get; private set; } = null!;
        /// <summary>记录通知并通过终态事件结束等待。</summary>
        public RecordingNotifications Notifications { get; } = new();

        /// <summary>创建合法图与部分已完成节点，明确断言业务输出复用而非再次调用模型。</summary>
        public static async Task<Fixture> CreateAsync(string graph, IReadOnlyDictionary<string, string> reused)
        {
            var fixture = new Fixture { database = await ImageJobDatabase.CreateAsync() };
            try
            {
                // 使用原校验器产生节点顺序；预置成功节点模拟历史恢复，任何模型访问都必须失败。
                var ct = TestContext.Current.CancellationToken;
                var validation = WorkflowGraphValidator.Validate(JsonNode.Parse(graph));
                Assert.True(validation.IsValid, string.Join(";", validation.Errors));
                var collection = new ServiceCollection();
                collection.AddLogging();
                collection.AddWorkflowExecution();
                collection.AddSingleton(fixture.database.Source);
                collection.AddScoped<IWorkflowRuntime, MafWorkflowRuntime>();
                collection.AddScoped<IAccountContext, AccountContext>();
                collection.AddScoped<CreativeFlowConnectionFactory>();
                collection.AddScoped<IAccountRepository, AccountRepository>();
                collection.AddScoped<BackgroundAccountScope>();
                collection.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
                collection.AddScoped<ICanvasAssetRepository, CanvasAssetRepository>();
                collection.AddSingleton<IRealtimeNotifier>(fixture.Notifications);
                fixture.services = collection.BuildServiceProvider(validateScopes: true);
                fixture.worker = new WorkflowRunWorker(fixture.services.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<WorkflowRunWorker>.Instance, new ExternalWorkflowApiOptions { GlobalConcurrency = 1 });
                var connections = new CreativeFlowConnectionFactory(fixture.database.Source, new AccountContext { AccountId = new AccountId(fixture.database.Account) });
                fixture.Runs = new WorkflowRunRepository(connections);
                var now = DateTimeOffset.UtcNow;
                var id = WorkflowId.New();
                var device = new DeviceId(fixture.database.Device);
                await new WorkflowRepository(connections).InsertAsync(new Workflow(id, device, "MAF business fixture", 1, graph, now, now), ct);
                fixture.Run = new WorkflowRun(WorkflowRunId.New(), id, device, graph, null, WorkflowRunStatus.Queued, null, now, now, null, null);
                var nodes = validation.Plan!.Nodes.Select(node => reused.TryGetValue(node.Id, out var output)
                    ? new WorkflowNodeRun(fixture.Run.Id, node.Id, node.Type, WorkflowNodeRunStatus.Succeeded, true, 1, output, null, now, now)
                    : new WorkflowNodeRun(fixture.Run.Id, node.Id, node.Type, WorkflowNodeRunStatus.Queued, false, null, null, null, null, null)).ToArray();
                Assert.True(await fixture.Runs.InsertAsync(fixture.Run, nodes, ct));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        /// <summary>启动原调度路径并等待真实终态，超时使测试失败而不是无限轮询。</summary>
        public async Task<WorkflowRunChangedEvent> ExecuteAsync()
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            return await Notifications.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        /// <summary>先停止并释放夹具工作流宿主，再释放服务容器和隔离数据库，避免后台任务访问已关闭资源。</summary>
        public async ValueTask DisposeAsync()
        {
            if (worker is not null) { await worker.StopAsync(CancellationToken.None); worker.Dispose(); }
            if (services is not null) await services.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    /// <summary>只收集内存通知；失败注入模拟断线，不改变 SQL 或调用上游。</summary>
    private sealed class RecordingNotifications : IRealtimeNotifier
    {
        /// <summary>实际收到的业务通知。</summary>
        public List<WorkflowRunChangedEvent> Events { get; } = [];
        /// <summary>终态到达后通知测试继续，避免固定时长等待。</summary>
        public TaskCompletionSource<WorkflowRunChangedEvent> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>确定性地注入运行中通知失败。</summary>
        public bool DropIntermediate { get; set; }
        /// <summary>用于注入取消竞争的测试回调。</summary>
        public Func<WorkflowRunChangedEvent, Task>? OnEvent { get; set; }

        /// <summary>记录工作流事件并执行观察钩子，终态完成测试等待；可模拟中间通知时浏览器断线。</summary>
        public async Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct)
        {
            var item = (WorkflowRunChangedEvent)(object)payload!;
            Events.Add(item);
            if (OnEvent is not null) await OnEvent(item);
            if (item.Status is "succeeded" or "failed" or "cancelled") Completed.TrySetResult(item);
            else if (DropIntermediate) throw new IOException("Synthetic disconnected client.");
        }

        /// <summary>拒绝工作流特征测试通过访客分组发送通知。</summary>
        public Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝工作流特征测试向全部连接广播业务事件。</summary>
        public Task BroadcastAsync<T>(string method, T payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
