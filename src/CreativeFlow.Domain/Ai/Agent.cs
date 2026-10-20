using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct AgentSessionId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static AgentSessionId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out AgentSessionId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new AgentSessionId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct AgentRunId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static AgentRunId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out AgentRunId id)
    {
        if (Guid.TryParse(text, out var value) && value != Guid.Empty)
        {
            id = new AgentRunId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

public enum AgentRunStatus
{
    /// <summary>已排队，等待后台执行。</summary>
    Queued,
    /// <summary>当前正在执行。</summary>
    Running,
    /// <summary>等待已绑定浏览器返回画布工具结果。</summary>
    AwaitingCanvas,
    /// <summary>等待用户批准工具执行。</summary>
    AwaitingApproval,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
    /// <summary>已请求或完成取消。</summary>
    Cancelled,
    /// <summary>因进程或执行连接中断而终止。</summary>
    Interrupted,
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="Title">内容、画布或模板的显示标题。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AgentSession(
    AgentSessionId Id,
    DeviceId DeviceId,
    CanvasId CanvasId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="ExecutorConnectionId">认领当前 Agent 执行的浏览器连接标识。</param>
/// <param name="Iteration">Agent 当前执行的轮次编号。</param>
/// <param name="LastSequence">最后一条已持久化或已消费事件的序号。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="InputTokens">模型报告或运行累计的输入 token 数量。</param>
/// <param name="OutputTokens">模型报告或运行累计的输出 token 数量。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AgentRun(
    AgentRunId Id,
    AgentSessionId SessionId,
    DeviceId DeviceId,
    CanvasId CanvasId,
    ProviderId? ProviderId,
    string ModelKey,
    AgentRunStatus Status,
    string? ExecutorConnectionId,
    int Iteration,
    long LastSequence,
    string? Error,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="MessageId">持久化消息的唯一标识。</param>
/// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Ordinal">消息在会话中的持久顺序号。</param>
/// <param name="Role">消息角色或模型角色，含义由所属契约定义。</param>
/// <param name="ContentJson">保留消息块顺序的 JSON 内容。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record AgentStoredMessage(
    Guid MessageId,
    AgentSessionId SessionId,
    AgentRunId? RunId,
    DeviceId DeviceId,
    int Ordinal,
    string Role,
    string ContentJson,
    DateTimeOffset CreatedAt);

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Sequence">持久事件序号；Agent 临时增量事件仍按原约定使用零。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="PayloadJson">事件或缓存载荷的 JSON 表示。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record AgentStoredEvent(
    AgentRunId RunId,
    DeviceId DeviceId,
    long Sequence,
    string Type,
    string PayloadJson,
    DateTimeOffset CreatedAt);

/// <param name="ToolCallId">工具调用的持久标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="ToolUseId">一次模型工具调用的关联标识。</param>
/// <param name="ToolName">模型调用或界面展示的工具名称。</param>
/// <param name="Site">工具在服务器端还是浏览器画布上执行。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="InputJson">执行输入的 JSON 快照。</param>
/// <param name="ResultJson">工具或上游调用结果的 JSON 表示。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
public sealed record AgentStoredToolCall(
    Guid ToolCallId,
    AgentRunId RunId,
    DeviceId DeviceId,
    string ToolUseId,
    string ToolName,
    string Site,
    string Status,
    string InputJson,
    string? ResultJson,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
