using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Workflows;
using static CreativeFlow.UnitTests.Api.ExternalWorkflowHandlersTests.RepositoryProxy;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>直接构造进度服务，锁定持久化、取消和通知时序，不创建容器或外部连接。</summary>
public sealed class WorkflowNodeProgressReporterTests
{
    /// <summary>进度更新只改变 Progress，原运行身份、节点状态、正文和开始时间保持不变。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(1d)]
    public async Task PersistsSnapshotBeforePublishingOriginalEvent(double? value)
    {
        var context = WorkflowNodeExecutorRegistryTests.Context("videoGenerate");
        var node = context.NodeRun with { OutputJson = "{\"existing\":true}", Progress = 0.1 };
        var steps = new List<string>();
        WorkflowNodeRun? saved = null;
        (DeviceId Device, string Method, WorkflowRunChangedEvent Event)? sent = null;
        var ct = TestContext.Current.CancellationToken;
        var reporter = Reporter((snapshot, token) =>
        {
            Assert.Equal(ct, token);
            saved = snapshot;
            steps.Add("persist");
            return Task.FromResult(true);
        }, (device, method, payload, _) =>
        {
            sent = (device, method, payload);
            steps.Add("publish");
            return Task.CompletedTask;
        });
        await reporter.ReportAsync(context.Run, node, value, ct);
        Assert.Equal(["persist", "publish"], steps);
        Assert.Equal(node with { Progress = value }, saved);
        Assert.NotNull(sent);
        Assert.Equal(context.Run.DeviceId, sent.Value.Device);
        Assert.Equal(WorkflowRealtimeMethods.WorkflowRunChanged, sent.Value.Method);
        Assert.Equal(new WorkflowRunChangedEvent(context.Run.WorkflowId.ToString(), context.Run.Id.ToString(),
            "running", node.NodeId, "running", value, null, null), sent.Value.Event);
        Assert.Equal(0.1, node.Progress);
    }

    /// <summary>数据库写入未完成时不能提前发送进度，避免前端观察到尚未落库的状态。</summary>
    [Fact]
    public async Task PendingPersistenceBlocksNotification()
    {
        var context = WorkflowNodeExecutorRegistryTests.Context("videoGenerate");
        var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var reporter = Reporter((_, _) => write.Task, (_, _, _, _) => { notifications++; return Task.CompletedTask; });
        var pending = reporter.ReportAsync(context.Run, context.NodeRun, 0.5, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, notifications);
        write.SetResult(true);
        await pending;
        Assert.Equal(1, notifications);
    }

    /// <summary>写入被取消保护拒绝或数据库抛错时，不发送通知，也不自动重试写入。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPersistenceDoesNotPublishOrRetry(bool databaseFailure)
    {
        var context = WorkflowNodeExecutorRegistryTests.Context("videoGenerate");
        var writes = 0;
        var notifications = 0;
        var expected = new InvalidOperationException("synthetic database failure");
        var reporter = Reporter((_, _) =>
        {
            writes++;
            return databaseFailure ? Task.FromException<bool>(expected) : Task.FromResult(false);
        }, (_, _, _, _) => { notifications++; return Task.CompletedTask; });
        /// <summary>对同一合成节点执行一次进度上报，供两种失败断言复用。</summary>
        Task Report() => reporter.ReportAsync(context.Run, context.NodeRun, 0.5, TestContext.Current.CancellationToken);
        if (databaseFailure) Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(Report));
        else await Assert.ThrowsAsync<WorkflowCancelledException>(Report);
        Assert.Equal(1, writes);
        Assert.Equal(0, notifications);
    }

    /// <summary>通知故障，包括非宿主取消异常，不使已落库进度失败或触发重复调用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailureIsIsolated(bool unrelatedCancellation)
    {
        var context = WorkflowNodeExecutorRegistryTests.Context("videoGenerate");
        var writes = 0;
        var notifications = 0;
        var reporter = Reporter((_, _) => { writes++; return Task.FromResult(true); }, (_, _, _, _) =>
        {
            notifications++;
            return Task.FromException(unrelatedCancellation ? new OperationCanceledException() : new IOException("synthetic disconnect"));
        });
        await reporter.ReportAsync(context.Run, context.NodeRun, 0.5, TestContext.Current.CancellationToken);
        Assert.Equal(1, writes);
        Assert.Equal(1, notifications);
    }

    /// <summary>宿主在持久化或推送阶段取消时继续向上传播，不被推送失败隔离吞掉。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCancellationPropagates(bool duringNotification)
    {
        var context = WorkflowNodeExecutorRegistryTests.Context("videoGenerate");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var notifications = 0;
        var reporter = Reporter((_, token) =>
        {
            cancellation.Cancel();
            return duringNotification ? Task.FromResult(true) : Task.FromCanceled<bool>(token);
        }, (_, _, _, token) => { notifications++; return Task.FromCanceled(token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reporter.ReportAsync(context.Run, context.NodeRun, 0.5, cancellation.Token));
        Assert.Equal(duringNotification ? 1 : 0, notifications);
    }

    /// <summary>仅为进度更新的两个端口提供替身，未声明的仓储或通知调用会使测试失败。</summary>
    private static WorkflowNodeProgressReporter Reporter(
        Func<WorkflowNodeRun, CancellationToken, Task<bool>> persist,
        Func<DeviceId, string, WorkflowRunChangedEvent, CancellationToken, Task> notify)
    {
        var runs = Create<IWorkflowRunRepository>((name, args) => name == "UpdateNodeAsync"
            ? persist((WorkflowNodeRun)args![0]!, (CancellationToken)args[1]!)
            : throw new InvalidOperationException($"Unexpected repository call: {name}"));
        var realtime = Create<IRealtimeNotifier>((name, args) => name == "SendToDeviceAsync"
            ? notify((DeviceId)args![0]!, (string)args[1]!, (WorkflowRunChangedEvent)args[2]!, (CancellationToken)args[3]!)
            : throw new InvalidOperationException($"Unexpected notification: {name}"));
        return new WorkflowNodeProgressReporter(runs, new WorkflowProgressPublisher(realtime));
    }
}
