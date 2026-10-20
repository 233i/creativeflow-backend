using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record WorkflowResponse(
    string Id,
    string Name,
    int SchemaVersion,
    JsonNode? Graph,
    string CreatedAt,
    string UpdatedAt)
{
    /// <summary>显式复制的来源，不用于推断当前发布或资源权限。</summary>
    public WorkflowCopyOrigin? CopyOrigin { get; init; }
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="NodeCount">节点图包含的节点数量。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record WorkflowSummaryResponse(
    string Id,
    string Name,
    int NodeCount,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Workflows">当前查询返回的工作流集合。</param>
public sealed record WorkflowListResponse(WorkflowSummaryResponse[] Workflows);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
public sealed record CreateWorkflowRequest(string? Name, int? SchemaVersion, JsonNode? Graph)
{
    /// <summary>只允许管理员从可管理来源显式创建新副本时指定。</summary>
    public WorkflowCopyOrigin? CopyOrigin { get; init; }
}

/// <param name="Source">被复制的后台工作流或历史模板来源。</param>
/// <param name="Id">被复制对象的完整标识，不是新工作流 ID。</param>
public sealed record WorkflowCopyOrigin(string Source, string Id);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
public sealed record UpdateWorkflowRequest(string? Name, int? SchemaVersion, JsonNode? Graph);

/// <param name="FromNodeId">开始执行或局部重跑的源节点标识。</param>
public sealed record PartialRerunWorkflowRequest(string? FromNodeId);

/// <param name="Reason">跳过节点的审计原因。</param>
public sealed record SkipWorkflowNodeRequest(string? Reason);

/// <param name="SourceRunId">局部重跑或继续执行所基于的运行标识。</param>
/// <param name="FromNodeId">开始执行或局部重跑的源节点标识。</param>
/// <param name="ReusedNodeIds">本次可以复用已有输出的节点标识集合。</param>
/// <param name="RerunNodeIds">本次需要重新执行的节点标识集合。</param>
public sealed record WorkflowPartialRerunPreviewResponse(
    string SourceRunId,
    string FromNodeId,
    string[] ReusedNodeIds,
    string[] RerunNodeIds);

/// <param name="NodeId">节点图中的稳定节点标识。</param>
/// <param name="NodeType">节点图中既有的节点类型名称。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="IsReused">本次运行是否复用既有成功节点的输出。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="Output">节点或运行产生的输出。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
public sealed record WorkflowNodeRunResponse(
    string NodeId,
    string NodeType,
    string Status,
    bool IsReused,
    double? Progress,
    JsonNode? Output,
    string? Error,
    string? StartedAt,
    string? FinishedAt)
{
    /// <summary>当前尝试序号和节点总尝试上限。</summary>
    public int Attempt { get; init; } = 1;
    public int MaxAttempts { get; init; } = 1;
    /// <summary>下一次自动重试时间，仅 retrying 状态返回。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryAt { get; init; }
    /// <summary>用户或分支跳过节点的原因。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipReason { get; init; }
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="ResumedFromRunId">当前续跑所基于的原运行标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
/// <param name="Nodes">图计划或运行中的节点集合。</param>
public sealed record WorkflowRunResponse(
    string Id,
    string WorkflowId,
    string? ResumedFromRunId,
    string Status,
    string? Error,
    string CreatedAt,
    string UpdatedAt,
    string? StartedAt,
    string? FinishedAt,
    WorkflowNodeRunResponse[] Nodes)
{
    /// <summary>独立调试的目标节点；普通整图运行不输出此字段。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DebugNodeId { get; init; }
}

/// <summary>精简的 SignalR 更新；客户端重连后会获取持久化运行。</summary>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="NodeId">节点图中的稳定节点标识。</param>
/// <param name="NodeStatus">当前节点的执行状态。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="Output">节点或运行产生的输出。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record WorkflowRunChangedEvent(
    string WorkflowId,
    string RunId,
    string Status,
    string? NodeId,
    string? NodeStatus,
    double? Progress,
    JsonNode? Output,
    string? Error);

public static class WorkflowRealtimeMethods
{
    /// <summary>工作流状态变化使用的客户端事件方法名。</summary>
    public const string WorkflowRunChanged = "WorkflowRunChanged";
}
