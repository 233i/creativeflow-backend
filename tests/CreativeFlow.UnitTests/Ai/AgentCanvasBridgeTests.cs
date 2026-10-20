using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;

namespace CreativeFlow.UnitTests.Ai;

public class AgentCanvasBridgeTests
{
    /// <summary>回归场景：An Attached Executor Receives And Completes One Call。</summary>
    [Fact]
    public async Task AnAttachedExecutorReceivesAndCompletesOneCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("connection-1");

        var pending = bridge.ExecuteAsync(
            run, "tool-1", "read_canvas", "{}", TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connection-1", sent.ConnectionId);
        Assert.Equal("tool-1", sent.Payload.ToolUseId);
        Assert.True(bridge.Submit(run.Id, "tool-1", "{\"status\":\"ok\"}", isError: false));

        var result = await pending;
        Assert.False(result.IsError);
        Assert.Equal("{\"status\":\"ok\"}", result.Json);
    }

    /// <summary>回归场景：Attaching After A Wait Resends The Pending Call。</summary>
    [Fact]
    public async Task AttachingAfterAWaitResendsThePendingCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run(connectionId: null);

        var pending = bridge.ExecuteAsync(
            run, "tool-2", "create_node", "{\"kind\":\"text\"}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(sink.HasSent);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("connection-2", sent.ConnectionId);

        bridge.Submit(run.Id, "tool-2", "{}", isError: false);
        await pending;
    }

    /// <summary>回归场景：Reattaching To The Same Connection Does Not Resend The Pending Call。</summary>
    [Fact]
    public async Task ReattachingToTheSameConnectionDoesNotResendThePendingCall()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run(connectionId: null);

        var pending = bridge.ExecuteAsync(
            run, "tool-same-connection", "run_node", "{\"nodeId\":\"video-1\"}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        var sent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tool-same-connection", sent.Payload.ToolUseId);

        await bridge.AttachAsync(run.Id, "connection-2", TestContext.Current.CancellationToken);
        Assert.False(sink.HasSent);

        bridge.Submit(run.Id, "tool-same-connection", "{}", isError: false);
        await pending;
    }

    /// <summary>回归场景：Detach Then Attach Moves An Unfinished Call Without Completing It。</summary>
    [Fact]
    public async Task DetachThenAttachMovesAnUnfinishedCallWithoutCompletingIt()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("old-connection");

        var pending = bridge.ExecuteAsync(
            run, "tool-3", "select_nodes", "{\"nodeIds\":[]}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        _ = await sink.NextAsync(TestContext.Current.CancellationToken);

        Assert.Equal([run.Id], bridge.Detach("old-connection"));
        Assert.False(pending.IsCompleted);

        await bridge.AttachAsync(run.Id, "new-connection", TestContext.Current.CancellationToken);
        var resent = await sink.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("new-connection", resent.ConnectionId);
        Assert.Equal("tool-3", resent.Payload.ToolUseId);

        bridge.Submit(run.Id, "tool-3", "{}", isError: false);
        await pending;
    }

    /// <summary>回归场景：An Approval Can Be Accepted By The Attached Executor。</summary>
    [Fact]
    public async Task AnApprovalCanBeAcceptedByTheAttachedExecutor()
    {
        var sink = new RecordingSink();
        var bridge = new AgentCanvasBridge(sink);
        var run = Run("connection-approval");

        var pending = bridge.RequestApprovalAsync(
            run, "approval-1", "delete_nodes", "Delete nodes?", "{\"nodeIds\":[\"n1\"]}",
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var sent = await sink.NextApprovalAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connection-approval", sent.ConnectionId);
        Assert.Equal("delete_nodes", sent.Payload.ToolName);
        Assert.True(bridge.SubmitApproval(run.Id, "approval-1", approved: true));
        Assert.True(await pending);
    }

    /// <summary>回归场景：A Call Returns A Stable Error When No Canvas Arrives Before Its Deadline。</summary>
    [Fact]
    public async Task ACallReturnsAStableErrorWhenNoCanvasArrivesBeforeItsDeadline()
    {
        var bridge = new AgentCanvasBridge(new RecordingSink());
        var result = await bridge.ExecuteAsync(
            Run(connectionId: null), "tool-4", "read_canvas", "{}",
            TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("{\"error\":\"canvas_tool_timeout\"}", result.Json);
    }

    /// <summary>回归场景：Catalog Names Are Unique And Schemas Are Valid Json Objects。</summary>
    [Fact]
    public void CatalogNamesAreUniqueAndSchemasAreValidJsonObjects()
    {
        Assert.Equal(
            AgentToolCatalog.All.Count,
            AgentToolCatalog.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var tool in AgentToolCatalog.All)
        {
            using var document = System.Text.Json.JsonDocument.Parse(tool.InputSchemaJson);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
        }
        Assert.Equal(
            ["load_skill", "run_skill_script"],
            AgentToolCatalog.All
                .Where(tool => tool.Site == AgentToolSite.Server)
                .Select(tool => tool.Name)
                .ToArray());
        Assert.All(
            AgentToolCatalog.All.Where(tool => tool.Site != AgentToolSite.Server),
            tool => Assert.Equal(AgentToolSite.Canvas, tool.Site));

        var approvals = AgentToolCatalog.All
            .Where(tool => tool.Approval == AgentApprovalPolicy.Always)
            .Select(tool => tool.Name)
            .ToArray();
        Assert.Equal(["delete_nodes", "cancel_node", "run_skill_script"], approvals);
    }

    /// <summary>构造指定设备与会话归属的 Agent 运行测试记录。</summary>
    private static AgentRun Run(string? connectionId) => new(
        AgentRunId.New(),
        AgentSessionId.New(),
        DeviceId.New(),
        CanvasId.New(),
        ProviderId.New(),
        "model",
        AgentRunStatus.Running,
        connectionId,
        Iteration: 0,
        LastSequence: 0,
        Error: null,
        InputTokens: null,
        OutputTokens: null,
        StartedAt: DateTimeOffset.UtcNow,
        FinishedAt: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingSink : IAgentRealtimeSink
    {
        /// <summary>当前测试记录的调用序列。</summary>
        private readonly System.Threading.Channels.Channel<SentCall> _calls =
            System.Threading.Channels.Channel.CreateUnbounded<SentCall>();
        /// <summary>等待浏览器响应的工具审批注册表。</summary>
        private readonly System.Threading.Channels.Channel<SentApproval> _approvals =
            System.Threading.Channels.Channel.CreateUnbounded<SentApproval>();

        /// <summary>测试记录器中是否已有待断言的工具调用。</summary>
        public bool HasSent => _calls.Reader.TryPeek(out _);

        /// <summary>忽略普通 Agent 事件通知，仅为画布桥测试提供已完成任务。</summary>
        public Task SendEventAsync(
            DeviceId deviceId,
            AgentEventEnvelope payload,
            CancellationToken ct) => Task.CompletedTask;

        /// <summary>把目标连接和工具载荷写入内存通道，供测试按发送顺序断言。</summary>
        public Task SendToolCallAsync(
            string connectionId,
            AgentToolCallEnvelope payload,
            CancellationToken ct)
        {
            _calls.Writer.TryWrite(new SentCall(connectionId, payload));
            return Task.CompletedTask;
        }

        /// <summary>把目标连接和审批载荷写入内存通道，供测试等待及校验审批请求。</summary>
        public Task SendApprovalRequestAsync(
            string connectionId,
            AgentApprovalEnvelope payload,
            CancellationToken ct)
        {
            _approvals.Writer.TryWrite(new SentApproval(connectionId, payload));
            return Task.CompletedTask;
        }

        /// <summary>等待并读取测试记录的下一次画布工具调用。</summary>
        public ValueTask<SentCall> NextAsync(CancellationToken ct) => _calls.Reader.ReadAsync(ct);
        /// <summary>等待并读取测试记录的下一次审批请求。</summary>
        public ValueTask<SentApproval> NextApprovalAsync(CancellationToken ct) =>
            _approvals.Reader.ReadAsync(ct);
    }

    /// <param name="ConnectionId">SignalR 浏览器连接标识。</param>
    /// <param name="Payload">事件、缓存或存储项的有效载荷。</param>
    private sealed record SentCall(string ConnectionId, AgentToolCallEnvelope Payload);
    /// <param name="ConnectionId">SignalR 浏览器连接标识。</param>
    /// <param name="Payload">事件、缓存或存储项的有效载荷。</param>
    private sealed record SentApproval(string ConnectionId, AgentApprovalEnvelope Payload);
}
