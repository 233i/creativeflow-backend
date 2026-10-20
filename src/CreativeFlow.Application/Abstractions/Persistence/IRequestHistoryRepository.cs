using CreativeFlow.Application.Contracts.Admin;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>生成记录写入与管理员历史查询的持久化边界。</summary>
public interface IRequestHistoryRepository
{
    /// <summary>保存已验证用户的一次画布请求，画布归属由仓储再次校验。</summary>
    Task StartAsync(GenerationHistoryStart request, CancellationToken ct);
    /// <summary>保存同步结果或关联异步视频任务，不改写原生成任务。</summary>
    Task FinishAsync(Guid id, string status, string? output, string? error, Guid? videoJobId, CancellationToken ct);
    /// <summary>按来源、用户、状态和分页范围读取记录。</summary>
    Task<AdminRequestPage> ListAsync(RequestHistoryQuery query, CancellationToken ct);
    /// <summary>按来源和记录标识读取输入、输出和工作流节点快照。</summary>
    Task<RequestHistoryDocument?> FindAsync(string source, Guid id, CancellationToken ct);
    /// <summary>仅返回与请求执行用户关联的素材元数据。</summary>
    Task<IReadOnlyList<CanvasAsset>> FindArtifactsAsync(Guid deviceId, Guid? accountId, Guid[] ids, CancellationToken ct);
}

/// <param name="Id">本次生成请求标识。</param>
/// <param name="DeviceId">经会话验证的请求设备。</param>
/// <param name="AccountId">请求发生时的已验证用户。</param>
/// <param name="CanvasId">客户端声明的画布，写入前需校验归属。</param>
/// <param name="NodeId">发起生成的画布节点。</param>
/// <param name="Kind">文本、图片或视频生成类型。</param>
/// <param name="ProviderId">请求选择的渠道。</param>
/// <param name="ModelKey">请求选择的模型标识。</param>
/// <param name="InputJson">只包含生成业务参数的快照。</param>
/// <param name="CreatedAt">接收请求的 UTC 时间。</param>
public sealed record GenerationHistoryStart(Guid Id, Guid DeviceId, Guid? AccountId, Guid? CanvasId,
    string? NodeId, string Kind, Guid? ProviderId, string ModelKey, string InputJson, DateTimeOffset CreatedAt);

/// <param name="Source">画布或工作流来源。</param>
/// <param name="AccountId">可选的精确用户筛选。</param>
/// <param name="Search">用户昵称、邮箱或标识搜索。</param>
/// <param name="Status">可选的运行状态筛选。</param>
/// <param name="Offset">分页起始位置。</param>
/// <param name="Limit">分页最大条数。</param>
public sealed record RequestHistoryQuery(string Source, Guid? AccountId, string? Search, string? Status, int Offset, int Limit);

/// <param name="Summary">记录的列表摘要。</param>
/// <param name="DeviceId">执行请求的设备，用于校验产物归属。</param>
/// <param name="InputJson">业务输入或运行图快照。</param>
/// <param name="OutputJson">同步生成结果快照。</param>
/// <param name="Nodes">工作流节点执行快照，画布请求为空集合。</param>
public sealed record RequestHistoryDocument(AdminRequestSummary Summary, Guid DeviceId,
    string InputJson, string? OutputJson, IReadOnlyList<RequestHistoryNode> Nodes);

/// <param name="Id">节点标识。</param>
/// <param name="Type">节点类型。</param>
/// <param name="Status">节点执行状态。</param>
/// <param name="Reused">是否复用此前成功产物。</param>
/// <param name="OutputJson">节点持久化产物。</param>
/// <param name="Error">节点失败说明。</param>
/// <param name="StartedAt">节点开始时间。</param>
/// <param name="FinishedAt">节点结束时间。</param>
public sealed record RequestHistoryNode(string Id, string Type, string Status, bool Reused,
    string? OutputJson, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
