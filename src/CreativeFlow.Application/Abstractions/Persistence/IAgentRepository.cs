using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>
/// 持久化智能体对话和运行。所有面向请求的操作都携带设备标识，
/// 从而在 SQL 中强制所有权，而不是依赖调用方可能遗漏的查询后检查。
/// </summary>
public interface IAgentRepository
{
    /// <summary>在调用设备可访问范围内查找 Agent 会话。</summary>
    Task<AgentSession?> FindSessionAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    /// <summary>查找给定画布已经建立的 Agent 会话。</summary>
    Task<AgentSession?> FindSessionByCanvasAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct);

    /// <summary>保存新的 Agent 会话。</summary>
    Task InsertSessionAsync(AgentSession session, CancellationToken ct);

    /// <summary>在当前业务访问范围内读取指定运行。</summary>
    Task<AgentRun?> FindRunAsync(DeviceId deviceId, AgentRunId runId, CancellationToken ct);

    /// <summary>供 HTTP 请求之外运行的后台宿主使用的无作用域查询。</summary>
    Task<AgentRun?> FindRunByIdAsync(AgentRunId runId, CancellationToken ct);

    /// <summary>查找当前会话或画布仍然活跃的运行。</summary>
    Task<AgentRun?> FindActiveRunAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct);

    /// <summary>保存新的 Agent 运行，保持原有活动运行约束。</summary>
    Task InsertRunAsync(AgentRun run, CancellationToken ct);

    /// <summary>更新已有运行的状态和统计字段。</summary>
    Task<bool> UpdateRunAsync(AgentRun run, CancellationToken ct);

    /// <summary>按归属与连接规则认领 Agent 的浏览器执行者。</summary>
    Task<bool> ClaimExecutorConnectionAsync(
        DeviceId deviceId,
        AgentRunId runId,
        CanvasId canvasId,
        string connectionId,
        CancellationToken ct);

    /// <summary>清理已经断开的浏览器执行连接关联。</summary>
    Task<int> ClearExecutorConnectionAsync(
        DeviceId deviceId,
        string connectionId,
        CancellationToken ct);

    /// <summary>按原有批次限制读取等待执行的 Agent 运行。</summary>
    Task<IReadOnlyList<AgentRunId>> ListQueuedAsync(int limit, CancellationToken ct);

    /// <summary>按会话顺序读取已保存的 Agent 消息。</summary>
    Task<IReadOnlyList<AgentStoredMessage>> ListMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    /// <summary>清理指定 Agent 会话的历史消息。</summary>
    Task<int> ClearMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    /// <summary>持久化带会话顺序号的 Agent 消息。</summary>
    Task InsertMessageAsync(AgentStoredMessage message, CancellationToken ct);

    /// <summary>获取会话下一条持久消息的顺序号。</summary>
    Task<int> NextMessageOrdinalAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct);

    /// <summary>保存已经开始的模型工具调用。</summary>
    Task InsertToolCallAsync(AgentStoredToolCall toolCall, CancellationToken ct);

    /// <summary>保存工具调用的终止状态、输出和耗时。</summary>
    Task<bool> CompleteToolCallAsync(
        DeviceId deviceId,
        AgentRunId runId,
        string toolUseId,
        string status,
        string resultJson,
        string? error,
        DateTimeOffset finishedAt,
        CancellationToken ct);

    /// <summary>
    /// 以原子方式推进运行序号并插入可重放事件。
    /// </summary>
    Task<AgentStoredEvent?> AppendEventAsync(
        AgentRunId runId,
        DeviceId deviceId,
        string type,
        string payloadJson,
        CancellationToken ct);

    /// <summary>按持久序号增量读取 Agent 运行事件。</summary>
    Task<IReadOnlyList<AgentStoredEvent>> ListEventsAsync(
        DeviceId deviceId,
        AgentRunId runId,
        long afterSequence,
        CancellationToken ct);

    /// <summary>按现有启动恢复规则终止上一进程遗留的 Agent 运行。</summary>
    Task<int> InterruptOrphanedRunsAsync(DateTimeOffset now, CancellationToken ct);
}
