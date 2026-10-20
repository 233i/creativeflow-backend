using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct WorkflowId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static WorkflowId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? value, out WorkflowId id)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            id = new WorkflowId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct WorkflowRunId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static WorkflowRunId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? value, out WorkflowRunId id)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            id = new WorkflowRunId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct WorkflowDeploymentId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static WorkflowDeploymentId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? value, out WorkflowDeploymentId id)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            id = new WorkflowDeploymentId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct WorkflowApiKeyId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static WorkflowApiKeyId New() => new(Guid.CreateVersion7());

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

public enum WorkflowRunStatus
{
    /// <summary>已排队，等待后台执行。</summary>
    Queued,
    /// <summary>当前正在执行。</summary>
    Running,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
    /// <summary>已请求或完成取消。</summary>
    Cancelled,
}

public enum WorkflowNodeRunStatus
{
    /// <summary>已排队，等待后台执行。</summary>
    Queued,
    /// <summary>当前正在执行。</summary>
    Running,
    /// <summary>等待本节点的下一次自动重试。</summary>
    Retrying,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
    /// <summary>已请求或完成取消。</summary>
    Cancelled,
    /// <summary>本次运行不再执行该节点。</summary>
    Skipped,
}

public enum WorkflowDeploymentStatus
{
    /// <summary>当前可公开使用的已发布版本。</summary>
    Published,
    /// <summary>当前账号或工作流发布入口已停用。</summary>
    Disabled,
}

public enum WorkflowRunTriggerType
{
    /// <summary>由登录用户的交互操作触发。</summary>
    Interactive,
    /// <summary>由外部工作流 API 触发。</summary>
    Api,
}

/// <summary>轻量级工作流文档，刻意与画布保持独立。</summary>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record Workflow(
    WorkflowId Id,
    DeviceId DeviceId,
    string Name,
    int SchemaVersion,
    string GraphJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>由管理后台独占编辑和调试的 API 工作流。</summary>
    public bool IsApi { get; init; }
    /// <summary>管理员显式复制时的来源种类；不代表共享版本、资源授权或执行依赖。</summary>
    public string? CopiedFromSource { get; init; }
    /// <summary>显式分叉的原对象标识，后续编辑保持不变，原对象删除后仍保留。</summary>
    public Guid? CopiedFromId { get; init; }
}

/// <summary>不可变的工作流快照及其持久执行状态。</summary>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="ResumedFromRunId">当前续跑所基于的原运行标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
public sealed record WorkflowRun(
    WorkflowRunId Id,
    WorkflowId WorkflowId,
    DeviceId DeviceId,
    string GraphJson,
    WorkflowRunId? ResumedFromRunId,
    WorkflowRunStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    /// <summary>运行由交互界面还是外部 API 触发。</summary>
    public WorkflowRunTriggerType TriggerType { get; init; } = WorkflowRunTriggerType.Interactive;
    /// <summary>工作流发布版本的唯一标识。</summary>
    public WorkflowDeploymentId? DeploymentId { get; init; }
    /// <summary>工作流 API Key 的标识。</summary>
    public WorkflowApiKeyId? ApiKeyId { get; init; }
    /// <summary>一次外部 API 请求的关联标识。</summary>
    public string? RequestId { get; init; }
    /// <summary>调用方提供的幂等键，与 API Key 共同确定重放范围。</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>规范化输入与发布版本计算出的摘要，用于判断幂等冲突。</summary>
    public byte[]? RequestHash { get; init; }
    /// <summary>执行输入的 JSON 快照。</summary>
    public string? InputJson { get; init; }
}

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="NodeId">节点图中的稳定节点标识。</param>
/// <param name="NodeType">节点图中既有的节点类型名称。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="IsReused">本次运行是否复用既有成功节点的输出。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="OutputJson">按现有格式保存的节点或工具输出 JSON。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
public sealed record WorkflowNodeRun(
    WorkflowRunId RunId,
    string NodeId,
    string NodeType,
    WorkflowNodeRunStatus Status,
    bool IsReused,
    double? Progress,
    string? OutputJson,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    /// <summary>本次节点执行尝试序号，从一开始递增且不会因重启重置。</summary>
    public int Attempt { get; init; } = 1;
    /// <summary>节点允许的最大尝试次数，由节点 retry 配置冻结到运行状态。</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>下一次自动重试的时间；非 retrying 状态为空。</summary>
    public DateTimeOffset? RetryAt { get; init; }
    /// <summary>跳过节点的稳定原因，供运行详情和审计使用。</summary>
    public string? SkipReason { get; init; }
}

/// <summary>节点每次尝试的轻量审计记录，不保存请求正文或凭据。</summary>
public sealed record WorkflowNodeAttempt(
    WorkflowRunId RunId,
    string NodeId,
    int Attempt,
    WorkflowNodeRunStatus Status,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? RetryAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
/// <param name="OutputSchemaJson">对外输出契约的 JSON schema。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="MaxConcurrency">当前工作流允许同时执行的运行数量上限。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
/// <param name="DisabledAt">账号或资源被停用的时间。</param>
public sealed record WorkflowDeployment(
    WorkflowDeploymentId Id,
    WorkflowId WorkflowId,
    int Version,
    int SchemaVersion,
    string GraphJson,
    string InputSchemaJson,
    string OutputSchemaJson,
    string Description,
    WorkflowDeploymentStatus Status,
    int MaxConcurrency,
    DateTimeOffset CreatedAt,
    DateTimeOffset PublishedAt,
    DateTimeOffset? DisabledAt)
{
    /// <summary>v2 发布时固定的模型配置及知识/外部操作引用；不含使用者身份、API Key 或凭据密文。v1 保持空值。</summary>
    public string? ExecutionSnapshotJson { get; init; }
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="KeyPrefix">用于定位 API Key 记录的公开前缀。</param>
/// <param name="KeyHint">对外展示的 API Key 掩码。</param>
/// <param name="KeyHash">工作流 API Key 的验证摘要，避免存储明文。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
/// <param name="LastUsedAt">API Key 最近一次通过认证并被使用的时间。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="RevokedAt">会话或 API Key 被撤销的时间。</param>
public sealed record WorkflowApiKey(
    WorkflowApiKeyId Id,
    WorkflowId WorkflowId,
    string Name,
    string KeyPrefix,
    string KeyHint,
    byte[] KeyHash,
    bool Enabled,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

/// <param name="AssetId">素材唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="ApiKeyId">工作流 API Key 的标识。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
/// <param name="ConsumedAt">一次性凭据或外部素材被消费的时间。</param>
public sealed record WorkflowExternalAsset(
    Guid AssetId,
    WorkflowId WorkflowId,
    WorkflowApiKeyId ApiKeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ConsumedAt);
