using System.Text.Json.Nodes;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>区分媒体节点端口和通用编排字段，避免相同节点名跨执行语义路由。</summary>
public enum WorkflowNodeFamily
{
    /// <summary>使用 texts、images、videos 输出的媒体创作节点。</summary>
    Media,
    /// <summary>使用类型化 fields 输出的通用编排节点。</summary>
    General,
}

/// <summary>单节点执行的纯数据快照；不携带服务、回调或服务容器，也不拥有调度循环。</summary>
/// <param name="Family">当前图的节点语义。</param>
/// <param name="Run">已持久化的运行身份和图快照。</param>
/// <param name="Node">编译并校验后的当前节点。</param>
/// <param name="Values">此前成功或复用的节点输出，只能按真实连线读取。</param>
/// <param name="Snapshot">本次运行固定的输入、模型和资源授权快照。</param>
/// <param name="Skipped">已跳过的分支节点，用于绑定汇合输入。</param>
/// <param name="DebugInputs">单节点调试的已授权输入；正常运行为空。</param>
/// <param name="NodeRun">节点开始执行时已持久化的状态，用于关联当前节点和进度记录。</param>
public sealed record WorkflowNodeExecutionContext(
    WorkflowNodeFamily Family,
    WorkflowRun Run,
    WorkflowPlanNode Node,
    IReadOnlyDictionary<string, WorkflowNodeValue> Values,
    JsonObject Snapshot,
    IReadOnlySet<string> Skipped,
    JsonObject? DebugInputs,
    WorkflowNodeRun NodeRun)
{
    /// <summary>当前子流程嵌套深度；用于限制循环节点递归消耗资源。</summary>
    public int NestedDepth { get; init; }
}

/// <summary>执行一个已绑定节点的业务行为，不认领运行、不调度下游、不写工作流终态。</summary>
public interface IWorkflowNodeExecutor
{
    /// <summary>消费当前节点上下文，返回原协议的节点输出；失败向编排层传播。</summary>
    Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct);
}
