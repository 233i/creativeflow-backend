namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>工作流中心的服务端筛选；只允许明确的来源、状态和排序，不接收图正文。</summary>
/// <param name="Search">名称和说明中的字面量搜索文本。</param>
/// <param name="Id">限定一个原始工作流或模板标识。</param>
/// <param name="Kind">媒体创作或通用编排类型。</param>
/// <param name="Source">工作流或历史模板来源。</param>
/// <param name="View">发布、待修改或最近失败等预设视图。</param>
/// <param name="ApiStatus">API 发布状态筛选。</param>
/// <param name="TemplateStatus">模板发布状态筛选。</param>
/// <param name="UpdatedFrom">更新时间范围下界。</param>
/// <param name="UpdatedTo">更新时间范围上界。</param>
/// <param name="Sort">白名单中的排序方式。</param>
/// <param name="Offset">服务端跳过的记录数。</param>
/// <param name="Limit">每页返回条数。</param>
/// <param name="IncludeDeleted">管理员读取指定历史资源时包含软删除对象；默认列表隐藏。</param>
public sealed record WorkflowCenterQuery(
    string? Search = null,
    string? Id = null,
    string? Kind = null,
    string? Source = null,
    string? View = null,
    string? ApiStatus = null,
    string? TemplateStatus = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    string Sort = "updated_desc",
    int Offset = 0,
    int Limit = 20,
    bool IncludeDeleted = false);

/// <summary>最近一次真实运行的安全摘要；不包含输入、结果、错误正文或调用凭证。</summary>
/// <param name="Id">真实运行标识。</param>
/// <param name="WorkflowId">运行所属工作流标识。</param>
/// <param name="TriggerType">交互提交或外部 API 触发。</param>
/// <param name="Status">持久化运行状态。</param>
/// <param name="CreatedAt">任务提交时间。</param>
public sealed record WorkflowCenterRun(
    string Id, string WorkflowId, string TriggerType, string Status, DateTimeOffset CreatedAt)
{
    /// <summary>独立节点调试的目标标识；为空表示整图运行，不把局部成功当成整图成功。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DebugNodeId { get; init; }
}

/// <summary>后台工作流或历史模板；来源与原 ID 一起标识记录，同名对象不自动合并。</summary>
/// <param name="Id">来源对象的原始标识。</param>
/// <param name="Source">工作流或历史模板来源。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Description">已保存的用途说明。</param>
/// <param name="SchemaVersion">工作流图的存储格式版本；业务类别见 Kind。</param>
/// <param name="Kind">根据图中节点类型计算的工作流类别；不会把业务类别错误地当作模式版本。</param>
/// <param name="ApiStatus">最近 API 发布状态。</param>
/// <param name="ApiVersion">最近 API 发布版本号。</param>
/// <param name="TemplateStatus">模板生命周期状态。</param>
/// <param name="TemplateVersion">模板版本号。</param>
/// <param name="DraftStatus">草稿与当前发布快照的差异状态。</param>
/// <param name="NodeCount">草稿图节点数量。</param>
/// <param name="UpdatedAt">来源对象最后更新时间。</param>
/// <param name="LatestRun">最近一次整图运行摘要，不使用单节点调试覆盖。</param>
public sealed record WorkflowCenterItem(
    string Id, string Source, string Name, string Description, int SchemaVersion,
    string Kind,
    string ApiStatus, int? ApiVersion, string TemplateStatus, int? TemplateVersion,
    string DraftStatus, int NodeCount, DateTimeOffset UpdatedAt, WorkflowCenterRun? LatestRun)
{
    /// <summary>软删除时间；非空时只允许查询历史，不再编辑、发布或运行。</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>与相同筛选、同一数据库快照一致的工作流中心分页结果。</summary>
/// <param name="Items">当前页工作流摘要。</param>
/// <param name="Total">相同筛选下的总记录数。</param>
/// <param name="Offset">当前页偏移量。</param>
/// <param name="Limit">请求的每页条数。</param>
public sealed record WorkflowCenterPage(WorkflowCenterItem[] Items, long Total, int Offset, int Limit);

/// <summary>同一工作流全部运行的服务端分页摘要，不包含任何节点输入输出。</summary>
/// <param name="Items">当前页运行摘要。</param>
/// <param name="Total">该工作流符合筛选的运行总数。</param>
/// <param name="Offset">当前页偏移量。</param>
/// <param name="Limit">请求的每页条数。</param>
public sealed record WorkflowCenterRunPage(WorkflowCenterRun[] Items, long Total, int Offset, int Limit);
