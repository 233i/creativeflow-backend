using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="MaxConcurrency">当前工作流允许同时执行的运行数量上限。</param>
public sealed record PublishWorkflowRequest(string? Description, int? MaxConcurrency);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="MaxConcurrency">当前工作流允许同时执行的运行数量上限。</param>
/// <param name="Inputs">当前运行或节点的输入集合。</param>
/// <param name="Outputs">当前运行对外发布的输出集合。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
/// <param name="DisabledAt">账号或资源被停用的时间。</param>
public sealed record WorkflowDeploymentResponse(
    string Id,
    string WorkflowId,
    int Version,
    int SchemaVersion,
    string Description,
    string Status,
    int MaxConcurrency,
    JsonNode? Inputs,
    JsonNode? Outputs,
    string PublishedAt,
    string? DisabledAt);

/// <param name="Deployments">工作流已有的发布版本集合。</param>
public sealed record WorkflowDeploymentListResponse(WorkflowDeploymentResponse[] Deployments);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
public sealed record CreateWorkflowApiKeyRequest(string? Name, string? ExpiresAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Prefix">用于命名空间隔离、查找或验证的固定前缀。</param>
/// <param name="Hint">仅用于显示的安全提示或掩码。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
/// <param name="LastUsedAt">API Key 最近一次通过认证并被使用的时间。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="RevokedAt">会话或 API Key 被撤销的时间。</param>
/// <param name="Key">当前查找、签名或映射所使用的键。</param>
public sealed record WorkflowApiKeyResponse(
    string Id,
    string Name,
    string Prefix,
    string Hint,
    bool Enabled,
    string? ExpiresAt,
    string? LastUsedAt,
    string CreatedAt,
    string? RevokedAt,
    string? Key);

/// <param name="ApiKeys">当前工作流的 API Key 元数据集合。</param>
public sealed record WorkflowApiKeyListResponse(WorkflowApiKeyResponse[] ApiKeys);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="Inputs">当前运行或节点的输入集合。</param>
/// <param name="Outputs">当前运行对外发布的输出集合。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
public sealed record ExternalWorkflowCatalogItemResponse(
    string Id,
    string Name,
    string Description,
    int Version,
    JsonNode? Inputs,
    JsonNode? Outputs,
    string PublishedAt)
{
    /// <summary>v2 类型化字段契约的版本标识；v1 省略此扩展字段以保持原响应。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; init; }
}

/// <param name="Workflows">当前查询返回的工作流集合。</param>
public sealed record ExternalWorkflowCatalogResponse(
    ExternalWorkflowCatalogItemResponse[] Workflows);

/// <param name="AssetId">素材唯一标识。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
public sealed record ExternalImageUploadResponse(
    string AssetId,
    string MediaType,
    long ByteSize,
    string CreatedAt,
    string ExpiresAt);

/// <param name="Inputs">当前运行或节点的输入集合。</param>
public sealed record CreateExternalWorkflowRunRequest(JsonObject? Inputs)
{
    /// <summary>保存公开文本增量供 eventsUrl 订阅；提交仍返回 202，不改变持久化队列。</summary>
    public bool Stream { get; init; }
}

/// <param name="RequestId">一次外部 API 请求的关联标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="StatusUrl">查询已受理任务状态的地址。</param>
public sealed record ExternalWorkflowRunCreatedResponse(
    string RequestId,
    string RunId,
    string WorkflowId,
    int Version,
    string Status,
    string StatusUrl)
{
    /// <summary>订阅进度、已保存文本与最终结果的 SSE 地址。</summary>
    public string EventsUrl => StatusUrl + "/events";
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Name">节点在本次固定运行图中的显示名称。</param>
public sealed record ExternalWorkflowCurrentNodeResponse(string Id, string Type, string Name);

/// <param name="RequestId">一次外部 API 请求的关联标识。</param>
/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="CurrentNode">运行当前正在处理的节点。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="Outputs">当前运行对外发布的输出集合。</param>
public sealed record ExternalWorkflowRunResponse(
    string RequestId,
    string RunId,
    string WorkflowId,
    int Version,
    string Status,
    double Progress,
    ExternalWorkflowCurrentNodeResponse? CurrentNode,
    string CreatedAt,
    string? StartedAt,
    string? FinishedAt,
    string? Error,
    JsonNode? Outputs);

/// <param name="RunId">工作流或 Agent 运行的唯一标识。</param>
/// <param name="RequestId">一次外部 API 请求的关联标识。</param>
/// <param name="ApiKeyName">工作流 API Key 的显示名称。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="StartedAt">运行、节点或工具开始执行的时间。</param>
/// <param name="FinishedAt">运行、节点或工具调用到达终止状态的时间。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record ExternalWorkflowRunSummaryResponse(
    string RunId,
    string RequestId,
    string ApiKeyName,
    int Version,
    string Status,
    string CreatedAt,
    string? StartedAt,
    string? FinishedAt,
    string? Error);

/// <param name="Runs">本次查询返回的运行集合。</param>
public sealed record ExternalWorkflowRunListResponse(
    ExternalWorkflowRunSummaryResponse[] Runs);

/// <param name="RequestId">一次外部 API 请求的关联标识。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record ExternalApiErrorBody(
    string RequestId,
    ExternalApiError Error);

/// <param name="Code">业务错误、风险项或外部认证返回的代码，含义由所属契约定义。</param>
/// <param name="Message">供用户、模型或调用方阅读的消息正文。</param>
/// <param name="Details">按当前错误协议返回的详细说明。</param>
public sealed record ExternalApiError(
    string Code,
    string Message,
    ExternalApiErrorDetail[] Details);

/// <param name="Field">关联的请求或契约字段名。</param>
/// <param name="Code">业务错误、风险项或外部认证返回的代码，含义由所属契约定义。</param>
/// <param name="Message">供用户、模型或调用方阅读的消息正文。</param>
public sealed record ExternalApiErrorDetail(string Field, string Code, string Message);
