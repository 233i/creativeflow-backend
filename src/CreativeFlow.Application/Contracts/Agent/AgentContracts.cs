namespace CreativeFlow.Application.Contracts.Agent;

/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="Title">内容、画布或模板的显示标题。</param>
public sealed record CreateAgentSessionRequest(string CanvasId, string? Title);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="Title">内容、画布或模板的显示标题。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AgentSessionResponse(
    string Id,
    string CanvasId,
    string? Title,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Sessions">当前查询返回的 Agent 会话集合。</param>
public sealed record AgentSessionListResponse(AgentSessionResponse[] Sessions);

/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="ExecutorConnectionId">认领当前 Agent 执行的浏览器连接标识。</param>
/// <param name="Images">图片内容或引用地址集合，元素类型由所属契约定义。</param>
public sealed record CreateAgentRunRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string? ExecutorConnectionId,
    string[]? Images = null);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Iteration">Agent 当前执行的轮次编号。</param>
/// <param name="LastSequence">最后一条已持久化或已消费事件的序号。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AgentRunResponse(
    string Id,
    string SessionId,
    string CanvasId,
    string? ProviderId,
    string ModelKey,
    string Status,
    int Iteration,
    long LastSequence,
    string? Error,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="Ordinal">消息在会话中的持久顺序号。</param>
/// <param name="Role">消息角色或模型角色，含义由所属契约定义。</param>
/// <param name="ContentJson">保留消息块顺序的 JSON 内容。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record AgentMessageResponse(
    string Id,
    string? RunId,
    int Ordinal,
    string Role,
    string ContentJson,
    string CreatedAt);

/// <param name="Messages">按会话顺序排列的消息集合。</param>
public sealed record AgentMessageListResponse(AgentMessageResponse[] Messages);

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="Sequence">持久事件序号；Agent 临时增量事件仍按原约定使用零。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="PayloadJson">事件或缓存载荷的 JSON 表示。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record AgentEventResponse(
    string RunId,
    long Sequence,
    string Type,
    string PayloadJson,
    string CreatedAt);

/// <param name="Events">按既有顺序返回的事件集合。</param>
public sealed record AgentEventListResponse(AgentEventResponse[] Events);

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="ToolUseId">一次模型工具调用的关联标识。</param>
/// <param name="ToolName">模型调用或界面展示的工具名称。</param>
/// <param name="InputJson">执行输入的 JSON 快照。</param>
/// <param name="TimeoutMilliseconds">工具执行超时，单位为毫秒。</param>
public sealed record AgentToolCallEnvelope(
    string RunId,
    string CanvasId,
    string ToolUseId,
    string ToolName,
    string InputJson,
    int TimeoutMilliseconds);

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="ApprovalId">一次审批请求的关联标识。</param>
/// <param name="ToolName">模型调用或界面展示的工具名称。</param>
/// <param name="Summary">供界面、审批或日志显示的简短摘要。</param>
/// <param name="InputJson">执行输入的 JSON 快照。</param>
public sealed record AgentApprovalEnvelope(
    string RunId,
    string ApprovalId,
    string ToolName,
    string Summary,
    string InputJson);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Site">工具在服务器端还是浏览器画布上执行。</param>
/// <param name="Approval">工具执行采用的审批策略。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
/// <param name="TimeoutMilliseconds">工具执行超时，单位为毫秒。</param>
public sealed record AgentToolCatalogEntryResponse(
    string Name,
    string Site,
    string Approval,
    string Description,
    string InputSchemaJson,
    int TimeoutMilliseconds);

/// <param name="Tools">模型可用的工具或 MCP 工具目录。</param>
public sealed record AgentToolCatalogResponse(AgentToolCatalogEntryResponse[] Tools);

/// <summary>
/// 实时输出。未持久化增量的序号为零，可重放事件的序号为正数。
/// 客户端使用正数检测缺口，并将零视为临时显示数据。
/// </summary>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="Sequence">持久事件序号；Agent 临时增量事件仍按原约定使用零。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Text">文本内容或本次流式文本片段。</param>
/// <param name="Json">按既有契约保存或传递的 JSON 内容。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="SentAt">实时消息发送时间。</param>
public sealed record AgentEventEnvelope(
    string RunId,
    long Sequence,
    string Type,
    string? Text,
    string? Json,
    string? Error,
    string SentAt);

public static class AgentRealtimeMethods
{
    /// <summary>发送 Agent 事件信封时使用的客户端方法名。</summary>
    public const string AgentEvent = "AgentEvent";
    /// <summary>发送画布工具调用时使用的客户端方法名。</summary>
    public const string AgentToolCall = "AgentToolCall";
    /// <summary>发送给浏览器的 Agent 审批方法名。</summary>
    public const string AgentApprovalRequest = "AgentApprovalRequest";
}
