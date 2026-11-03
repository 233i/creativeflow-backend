using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IWorkflowRunRepository
{
    /// <summary>工作流已有排队中或运行中的执行时返回假。</summary>
    Task<bool> InsertAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes,
        CancellationToken ct);

    /// <summary>
    /// 原子检查工作流的外部排队上限并插入运行，避免多个并发请求同时越过上限检查。
    /// </summary>
    Task<WorkflowRunInsertResult> InsertExternalAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes,
        int maxQueuedRuns,
        CancellationToken ct);

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<WorkflowRun?> FindAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        CancellationToken ct);

    /// <summary>读取该工作流最近一次交互式运行。</summary>
    Task<WorkflowRun?> FindLatestAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>列出最近五十次交互运行的状态和时间，不加载或返回输入、图、模型、节点正文。</summary>
    Task<JsonArray> ListInteractiveAsync(DeviceId deviceId, WorkflowId workflowId, CancellationToken ct);

    /// <summary>读取该工作流当前活动的交互式运行。</summary>
    Task<WorkflowRun?> FindActiveAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>仅在指定工作流的外部触发范围内查找运行。</summary>
    Task<WorkflowRun?> FindExternalAsync(
        WorkflowId workflowId,
        WorkflowRunId runId,
        CancellationToken ct);

    /// <summary>仅供已验证工作流管理权限的后台读取公共创作运行；客户读取使用账户隔离仓储。</summary>
    Task<WorkflowRun?> FindPublicAsync(WorkflowId workflowId, WorkflowRunId runId, CancellationToken ct);

    /// <summary>按 API Key 和幂等键读取已有运行。</summary>
    Task<WorkflowRun?> FindByIdempotencyAsync(
        WorkflowApiKeyId apiKeyId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>读取调用设备可访问工作流的外部运行摘要。</summary>
    Task<IReadOnlyList<WorkflowExternalRunSummary>> ListExternalAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        int limit,
        CancellationToken ct);

    /// <summary>按原有稳定顺序读取运行的节点状态。</summary>
    Task<IReadOnlyList<WorkflowNodeRun>> ListNodesAsync(
        WorkflowRunId runId,
        CancellationToken ct);

    /// <summary>为单节点工作进程以原子方式认领最早排队的运行。</summary>
    Task<WorkflowRun?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>将上一进程实例遗留的进行中运行标记为失败。</summary>
    Task<int> FailInterruptedAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>更新未取消的运行。返回假表示取消或删除操作赢得了竞争。</summary>
    Task<bool> UpdateAsync(WorkflowRun run, CancellationToken ct);

    /// <summary>更新未取消的节点。返回假表示取消或删除操作赢得了竞争。</summary>
    Task<bool> UpdateNodeAsync(WorkflowNodeRun node, CancellationToken ct);

    /// <summary>保存一次节点尝试的状态，用于重试、恢复和运行审计。</summary>
    Task RecordNodeAttemptAsync(WorkflowNodeAttempt attempt, CancellationToken ct);

    /// <summary>判断运行是否已被取消或不再存在。</summary>
    Task<bool> IsCancellationRequestedAsync(WorkflowRunId runId, CancellationToken ct);

    /// <summary>请求取消工作流运行，保持既有的终止与清理规则。</summary>
    Task<bool> CancelAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>按外部工作流范围写入取消状态并处理尚未结束的节点。</summary>
    Task<bool> CancelExternalAsync(
        WorkflowId workflowId,
        WorkflowRunId runId,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>将本次运行尚未执行的节点标记为跳过。</summary>
    Task SkipRemainingNodesAsync(WorkflowRunId runId, DateTimeOffset now, CancellationToken ct);

    /// <summary>将指定的失败或排队节点安全标记为跳过，运行本身保持失败等待续跑。</summary>
    Task<bool> SkipNodeAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        string nodeId,
        string reason,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <param name="Run">当前持久化运行记录。</param>
/// <param name="ApiKeyName">工作流 API Key 的显示名称。</param>
/// <param name="DeploymentVersion">运行关联的发布版本号。</param>
public sealed record WorkflowExternalRunSummary(
    WorkflowRun Run,
    string ApiKeyName,
    int DeploymentVersion);

public enum WorkflowRunInsertResult
{
    /// <summary>已成功写入新记录。</summary>
    Inserted,
    /// <summary>唯一约束表明对应记录已经存在。</summary>
    Duplicate,
    /// <summary>指定工作流已经达到外部排队数量上限。</summary>
    QueueFull,
}
