using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>交付工作流进度，保留敏感正文屏蔽与通知失败隔离策略。</summary>
internal sealed class WorkflowProgressPublisher(IRealtimeNotifier realtime)
{
    /// <summary>发送运行或节点状态通知；临时推送失败不影响已持久化结果，宿主取消仍向上传播。</summary>
    internal async Task PublishAsync(
        WorkflowRun run,
        string status,
        string? nodeId,
        string? nodeStatus,
        double? progress,
        JsonNode? output,
        string? error,
        CancellationToken ct)
    {
        try
        {
            await realtime.SendToDeviceAsync(
                run.DeviceId,
                WorkflowRealtimeMethods.WorkflowRunChanged,
                new WorkflowRunChangedEvent(
                    run.WorkflowId.ToString(),
                    run.Id.ToString(),
                    status,
                    nodeId,
                    nodeStatus,
                    progress,
                    // 含授权资源的运行只推送进度；正文走即时鉴权 HTTP 读取，避免长连接撤权后泄漏。
                    (run.TriggerType == WorkflowRunTriggerType.PublicCreative) || (run.TriggerType == WorkflowRunTriggerType.Api && WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(run.GraphJson)))
                        || KnowledgeWorkflowService.ContainsKnowledge(run.GraphJson) || HttpRequestService.ContainsRequests(run.GraphJson) ? null : output,
                    run.TriggerType == WorkflowRunTriggerType.PublicCreative && error is not null ? "生成未完成，请查看任务详情。" : error),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // PostgreSQL 是事实来源。SignalR 客户端断开后可通过运行状态端点恢复，
            // 因此临时推送失败绝不能让付费任务失败。
        }
    }
}
