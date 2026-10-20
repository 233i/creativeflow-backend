using System.Text.Json;

namespace CreativeFlow.Application.Contracts.Admin;

/// <param name="Id">请求或工作流运行标识。</param>
/// <param name="Source">画布或工作流来源。</param>
/// <param name="Kind">生成类型或工作流运行。</param>
/// <param name="Status">当前持久化执行状态。</param>
/// <param name="AccountId">请求用户标识。</param>
/// <param name="UserName">用户显示名，缺失历史归属时为空。</param>
/// <param name="Email">用户邮箱。</param>
/// <param name="ProjectId">关联画布或工作流。</param>
/// <param name="ProjectName">画布或工作流名称。</param>
/// <param name="NodeId">发起画布生成的节点。</param>
/// <param name="ModelKey">生成模型标识，工作流在节点输入中展示。</param>
/// <param name="ProviderName">当前渠道名称。</param>
/// <param name="CreatedAt">请求创建时间。</param>
/// <param name="FinishedAt">请求完成时间。</param>
/// <param name="Error">用户可读的失败说明。</param>
/// <param name="Historical">是否由已有视频任务恢复的历史记录。</param>
public sealed record AdminRequestSummary(string Id, string Source, string Kind, string Status,
    string? AccountId, string? UserName, string? Email, string? ProjectId, string ProjectName,
    string? NodeId, string? ModelKey, string? ProviderName, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt, string? Error, bool Historical);

/// <param name="Items">当前页请求。</param>
/// <param name="Total">符合筛选条件的记录总数。</param>
/// <param name="Offset">当前页起始偏移。</param>
/// <param name="Limit">当前页最多条数。</param>
public sealed record AdminRequestPage(AdminRequestSummary[] Items, long Total, int Offset, int Limit);

/// <param name="Kind">文本、图片或视频。</param>
/// <param name="Text">文本产物内容。</param>
/// <param name="AssetId">已存储的媒体标识。</param>
/// <param name="Url">需要管理员会话的媒体读取地址。</param>
/// <param name="MediaType">实际存储的媒体类型。</param>
/// <param name="Unavailable">素材已删除、不可访问或只留下外部链接。</param>
public sealed record AdminRequestArtifact(string Kind, string? Text, string? AssetId,
    string? Url, string? MediaType, bool Unavailable);

/// <param name="Id">节点标识。</param>
/// <param name="Type">节点类型。</param>
/// <param name="Status">节点执行状态。</param>
/// <param name="Reused">是否复用历史产物。</param>
/// <param name="Input">本次运行图中的节点输入参数。</param>
/// <param name="Artifacts">该节点的文本、图片和视频产物。</param>
/// <param name="Error">节点失败说明。</param>
/// <param name="StartedAt">节点开始时间。</param>
/// <param name="FinishedAt">节点完成时间。</param>
public sealed record AdminRequestNode(string Id, string Type, string Status, bool Reused,
    JsonElement Input, AdminRequestArtifact[] Artifacts, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <param name="Summary">请求基本资料。</param>
/// <param name="Input">生成参数；工作流为运行级输入概览。</param>
/// <param name="Artifacts">请求最终产物，工作流优先展示结果节点。</param>
/// <param name="Nodes">工作流的完整节点明细。</param>
public sealed record AdminRequestDetail(AdminRequestSummary Summary, JsonElement Input,
    AdminRequestArtifact[] Artifacts, AdminRequestNode[] Nodes);
