using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>普通画布图片任务的持久化队列；认领、限流和幂等判定必须原子化。</summary>
public interface IImageJobRepository
{
    /// <summary>原子检查幂等键、活动重复任务和排队上限，再持久化提交。</summary>
    Task<ImageJobSubmission> SubmitAsync(ImageJobSubmissionInput input, ImageQueueLimits limits, CancellationToken ct);
    /// <summary>仅查询所属账号的任务，不产生任何上游调用。</summary>
    Task<ImageJob?> FindAsync(Guid accountId, Guid jobId, CancellationToken ct);
    /// <summary>按账号内的提交键找回已接受的任务。</summary>
    Task<ImageJob?> FindSubmissionAsync(Guid accountId, Guid key, CancellationToken ct);
    /// <summary>按任务 ID 倒序分页读取账号指定画布的任务。</summary>
    Task<IReadOnlyList<ImageJob>> ListAsync(Guid accountId, Guid canvasId, Guid? before, int limit, CancellationToken ct);
    /// <summary>有检查点时恢复保存，不允许重新调用生成模型。</summary>
    Task<bool> RetryDownloadAsync(Guid accountId, Guid jobId, ImageQueueLimits limits, CancellationToken ct);
    /// <summary>在共享配额内认领最早的可执行任务并签发租约。</summary>
    Task<ImageJob?> ClaimAsync(ImageQueueLimits limits, CancellationToken ct);
    /// <summary>续约当前执行者，返回 false 表示执行权已经失效。</summary>
    Task<bool> HeartbeatAsync(Guid jobId, Guid leaseId, CancellationToken ct);
    /// <summary>下载前保存上游响应，以便独立恢复产物保存阶段。</summary>
    Task CheckpointAsync(ImageJob job, string providerBaseUrl, IReadOnlyList<ImageJobOutputSource> outputs, CancellationToken ct);
    /// <summary>读取固定序号的输出与文件操作键，不读取或保存文件正文。</summary>
    Task<IReadOnlyList<ImageJobOutput>> OutputsAsync(Guid jobId, CancellationToken ct);
    /// <summary>在有效租约下关联已落盘文件与素材，重复执行不新增素材。</summary>
    Task SaveOutputAsync(ImageJob job, ImageJobOutput output, StoredAsset file, CancellationToken ct);
    /// <summary>校验租约并记录终态，同时同步生成历史。</summary>
    Task FinishAsync(ImageJob job, string? error, CancellationToken ct);
}

/// <summary>限制单位是任务，不是输出张数；保存结果仍占用一个执行名额。</summary>
/// <param name="GlobalConcurrency">全局执行并发上限。</param>
/// <param name="UserConcurrency">同账号跨设备的并发上限。</param>
/// <param name="ProviderConcurrency">同服务商跨账号的并发上限。</param>
/// <param name="MaxPendingPerUser">每账号排队和执行中的任务总上限。</param>
/// <param name="MaxPendingGlobal">系统排队和执行中的任务总上限。</param>
public sealed record ImageQueueLimits(int GlobalConcurrency = 4, int UserConcurrency = 2,
    int ProviderConcurrency = 2, int MaxPendingPerUser = 20, int MaxPendingGlobal = 200);

/// <summary>不保存 API Key，只保存用户输入与不可逆请求摘要。</summary>
/// <param name="AccountId">会话解析出的账号。</param>
/// <param name="DeviceId">提交时的设备身份。</param>
/// <param name="Key">账号内稳定的幂等提交标识。</param>
/// <param name="ProviderId">生成使用的服务商。</param>
/// <param name="CanvasId">用于恢复任务的画布。</param>
/// <param name="NodeId">原生成配置节点。</param>
/// <param name="RequestJson">不可变的输入快照。</param>
/// <param name="RequestHash">规范化输入及操作来源的 SHA-256 摘要。</param>
/// <param name="ForceNew">明确重新生成时不合并其他活动任务，仍遵守幂等键。</param>
public sealed record ImageJobSubmissionInput(Guid AccountId, Guid DeviceId, Guid Key,
    Guid ProviderId, Guid? CanvasId, string? NodeId, string RequestJson, string RequestHash,
    bool ForceNew = false);

/// <summary>HTTP 层将冲突映射为 409，队列满映射为 429。</summary>
/// <param name="Job">已接受的新任务或原任务。</param>
/// <param name="Conflict">同一提交键是否绑定其他参数。</param>
/// <param name="QueueFull">是否因待完成任务超限而拒绝。</param>
public sealed record ImageJobSubmission(ImageJob? Job, bool Conflict = false, bool QueueFull = false);

/// <summary>LeaseId 只供后台写入使用，浏览器关闭不会改变任务。</summary>
/// <param name="JobId">持久化任务标识。</param>
/// <param name="AccountId">任务所属账号。</param>
/// <param name="DeviceId">提交设备，用于恢复后台账号上下文。</param>
/// <param name="ProviderId">任务使用的服务商。</param>
/// <param name="CanvasId">关联画布。</param>
/// <param name="NodeId">关联的生成配置节点。</param>
/// <param name="RequestJson">首次接受时固定的输入快照。</param>
/// <param name="Status">排队、生成、保存或终态。</param>
/// <param name="FailureStage">失败发生的阶段。</param>
/// <param name="Error">可展示的失败说明。</param>
/// <param name="CreatedAt">服务器接受任务的时间。</param>
/// <param name="StartedAt">首次认领执行的时间。</param>
/// <param name="FinishedAt">本轮执行完成时间。</param>
/// <param name="LeaseId">当前执行者的写入隔离令牌。</param>
/// <param name="HasCheckpoint">是否已登记上游输出，可尝试恢复文件或重新下载；不保证原件仍然可用。</param>
/// <param name="RequestHash">幂等重放校验使用的请求摘要。</param>
public sealed record ImageJob(Guid JobId, Guid AccountId, Guid DeviceId, Guid ProviderId,
    Guid? CanvasId, string? NodeId, string RequestJson, string Status, string? FailureStage,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    Guid? LeaseId, bool HasCheckpoint, string RequestHash);

/// <summary>上游输出描述不含文件字节；内联内容也必须通过文件存储完成持久化。</summary>
public sealed record ImageJobOutputSource(string MediaType, string? DownloadUrl = null, string? SourceUrl = null,
    DateTimeOffset? SourceUrlExpiresAt = null);

/// <summary>固定输出标识和文件操作键允许恢复落盘结果，不再从数据库读取文件正文。</summary>
/// <param name="Index">从零开始的固定输出序号。</param>
/// <param name="AssetId">提前分配的素材标识，恢复保存时复用。</param>
/// <param name="MediaType">输出的 MIME 类型。</param>
/// <param name="StoragePath">已保存的文件对象键。</param>
/// <param name="DownloadUrl">仅供后台下载的上游地址。</param>
/// <param name="ProviderBaseUrl">首次响应时的服务商地址，用于安全鉴权。</param>
/// <param name="SourceUrl">允许记录到素材上的公开来源地址。</param>
/// <param name="SourceUrlExpiresAt">公开来源地址已知的过期时间。</param>
/// <param name="Saved">素材与文件关联是否已原子保存。</param>
/// <param name="StorageOperationKey">跨进程重试时稳定的文件保存操作标识。</param>
public sealed record ImageJobOutput(int Index, Guid AssetId, string MediaType, string? StoragePath,
    string? DownloadUrl, string? ProviderBaseUrl, string? SourceUrl,
    DateTimeOffset? SourceUrlExpiresAt, bool Saved, string StorageOperationKey);
