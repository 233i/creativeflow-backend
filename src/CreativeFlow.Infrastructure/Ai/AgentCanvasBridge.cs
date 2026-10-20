using System.Collections.Concurrent;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>智能体工作进程与单个浏览器标签页之间的进程内反向 RPC 汇合点。</summary>
/// <remarks>
/// 待处理注册表刻意限定在本进程。实现分布式结果中继前，智能体执行采用单实例；
/// 仅使用 Redis SignalR 背板无法在 API 进程之间迁移等待中的
/// <see cref="TaskCompletionSource{TResult}"/>。
/// </remarks>
internal sealed class AgentCanvasBridge(IAgentRealtimeSink realtime) : IAgentCanvasBridge
{
    /// <summary>等待浏览器工具执行结果的注册表。</summary>
    private readonly ConcurrentDictionary<PendingKey, PendingCall> _pending = new();
    /// <summary>等待浏览器响应的工具审批注册表。</summary>
    private readonly ConcurrentDictionary<PendingKey, PendingApproval> _approvals = new();
    /// <summary>Agent 运行到当前浏览器执行连接的映射。</summary>
    private readonly ConcurrentDictionary<Guid, string> _executors = new();
    /// <summary>保护执行连接变更与待处理项关联的同步锁。</summary>
    private readonly object _executorGate = new();

    /// <summary>登记待完成的画布工具调用并发送给当前执行器，等待结果、超时或取消后清理等待项。</summary>
    public async Task<AgentCanvasToolResult> ExecuteAsync(
        AgentRun run,
        string toolUseId,
        string toolName,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // 发送前登记等待项并关联执行器；完成、超时和取消都必须通过原有清理路径释放注册。
        var payload = new AgentToolCallEnvelope(
            run.Id.ToString(),
            run.CanvasId.ToString(),
            toolUseId,
            toolName,
            inputJson,
            checked((int)Math.Clamp(timeout.TotalMilliseconds, 1_000, int.MaxValue)));
        var connectionId = _executors.TryGetValue(run.Id.Value, out var attached)
            ? attached
            : run.ExecutorConnectionId;
        var call = new PendingCall(payload, connectionId);
        var key = new PendingKey(run.Id.Value, toolUseId);
        if (!_pending.TryAdd(key, call))
        {
            return new AgentCanvasToolResult(
                "{\"error\":\"duplicate_tool_use_id\"}", IsError: true);
        }

        try
        {
            await SendIfAttachedAsync(call, ct);
            try
            {
                return await call.Completion.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                return new AgentCanvasToolResult(
                    "{\"error\":\"canvas_tool_timeout\"}", IsError: true);
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>向当前执行器发送审批请求，等待批准或拒绝，并在超时、取消或完成后释放审批等待项。</summary>
    public async Task<bool> RequestApprovalAsync(
        AgentRun run,
        string approvalId,
        string toolName,
        string summary,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var payload = new AgentApprovalEnvelope(
            run.Id.ToString(), approvalId, toolName, summary, inputJson);
        var connectionId = _executors.TryGetValue(run.Id.Value, out var attached)
            ? attached
            : run.ExecutorConnectionId;
        var approval = new PendingApproval(payload, connectionId);
        var key = new PendingKey(run.Id.Value, approvalId);
        if (!_approvals.TryAdd(key, approval)) return false;

        try
        {
            await SendApprovalIfAttachedAsync(approval, ct);
            try
            {
                return await approval.Completion.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _approvals.TryRemove(key, out _);
        }
    }

    /// <summary>绑定运行的画布执行器并向新连接重发未完成请求；同一连接重复绑定仅作为心跳。</summary>
    public async Task AttachAsync(AgentRunId runId, string connectionId, CancellationToken ct)
    {
        lock (_executorGate)
        {
            // 运行活动期间，浏览器会定期重新认领执行器槽位。来自同一 SignalR 连接的重复认领只是心跳；
            // 若在此重放所有待处理调用，会让同一画布变更执行多次。
            if (_executors.TryGetValue(runId.Value, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                return;
            }

            _executors[runId.Value] = connectionId;
        }

        foreach (var pair in _pending)
        {
            if (pair.Key.RunId != runId.Value) continue;
            lock (pair.Value.Gate)
            {
                pair.Value.ConnectionId = connectionId;
            }
            await SendIfAttachedAsync(pair.Value, ct);
        }
        foreach (var pair in _approvals)
        {
            if (pair.Key.RunId != runId.Value) continue;
            lock (pair.Value.Gate)
            {
                pair.Value.ConnectionId = connectionId;
            }
            await SendApprovalIfAttachedAsync(pair.Value, ct);
        }
    }

    /// <summary>解除仍属于断开连接的执行器和等待项，返回受影响的运行，不干扰已重绑到其他连接的任务。</summary>
    public IReadOnlyList<AgentRunId> Detach(string connectionId)
    {
        // 断开的连接只能影响仍归它所有的执行器与等待项，不能取消已经重新绑定到其他连接的运行。
        var runs = new HashSet<Guid>();
        foreach (var executor in _executors)
        {
            if (string.Equals(executor.Value, connectionId, StringComparison.Ordinal)
                && _executors.TryRemove(executor))
            {
                runs.Add(executor.Key);
            }
        }

        foreach (var pair in _pending)
        {
            lock (pair.Value.Gate)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    pair.Value.ConnectionId = null;
                    runs.Add(pair.Key.RunId);
                }
            }
        }
        foreach (var pair in _approvals)
        {
            lock (pair.Value.Gate)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    pair.Value.ConnectionId = null;
                    runs.Add(pair.Key.RunId);
                }
            }
        }
        return runs.Select(value => new AgentRunId(value)).ToList();
    }

    /// <summary>释放指定运行的执行器绑定，不提交工具或审批结果。</summary>
    public void Release(AgentRunId runId) => _executors.TryRemove(runId.Value, out _);

    /// <summary>完成匹配运行与工具调用标识的结果等待项；重复或已结束的回执按幂等成功处理。</summary>
    public bool Submit(
        AgentRunId runId,
        string toolUseId,
        string resultJson,
        bool isError)
    {
        var key = new PendingKey(runId.Value, toolUseId);
        if (!_pending.TryGetValue(key, out var call)) return true;
        _ = call.Completion.TrySetResult(new AgentCanvasToolResult(resultJson, isError));
        return true;
    }

    /// <summary>完成匹配运行与审批标识的决策等待项；重复或已结束的审批回执按幂等成功处理。</summary>
    public bool SubmitApproval(AgentRunId runId, string approvalId, bool approved)
    {
        var key = new PendingKey(runId.Value, approvalId);
        if (!_approvals.TryGetValue(key, out var approval)) return true;
        _ = approval.Completion.TrySetResult(approved);
        return true;
    }

    /// <summary>仅在运行已经绑定浏览器时发送待执行的工具调用。</summary>
    private async Task SendIfAttachedAsync(PendingCall call, CancellationToken ct)
    {
        string? connectionId;
        lock (call.Gate)
        {
            connectionId = call.ConnectionId;
        }
        if (connectionId is null) return;
        await realtime.SendToolCallAsync(connectionId, call.Payload, ct);
    }

    /// <summary>仅在运行已经绑定浏览器时发送待处理的审批请求。</summary>
    private async Task SendApprovalIfAttachedAsync(PendingApproval approval, CancellationToken ct)
    {
        string? connectionId;
        lock (approval.Gate)
        {
            connectionId = approval.ConnectionId;
        }
        if (connectionId is null) return;
        await realtime.SendApprovalRequestAsync(connectionId, approval.Payload, ct);
    }

    /// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
    /// <param name="ToolUseId">一次模型工具调用的关联标识。</param>
    private readonly record struct PendingKey(Guid RunId, string ToolUseId);

    /// <summary>初始化 PendingCall 的依赖与实例状态。</summary>
    private sealed class PendingCall(AgentToolCallEnvelope payload, string? connectionId)
    {
        /// <summary>保护当前共享状态的同步对象。</summary>
        public object Gate { get; } = new();
        /// <summary>事件、缓存或存储项的有效载荷。</summary>
        public AgentToolCallEnvelope Payload { get; } = payload;
        /// <summary>SignalR 浏览器连接标识。</summary>
        public string? ConnectionId { get; set; } = connectionId;
        /// <summary>当前工具调用或审批等待的完成通知。</summary>
        public TaskCompletionSource<AgentCanvasToolResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>初始化 PendingApproval 的依赖与实例状态。</summary>
    private sealed class PendingApproval(AgentApprovalEnvelope payload, string? connectionId)
    {
        /// <summary>保护当前共享状态的同步对象。</summary>
        public object Gate { get; } = new();
        /// <summary>事件、缓存或存储项的有效载荷。</summary>
        public AgentApprovalEnvelope Payload { get; } = payload;
        /// <summary>SignalR 浏览器连接标识。</summary>
        public string? ConnectionId { get; set; } = connectionId;
        /// <summary>当前工具调用或审批等待的完成通知。</summary>
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
