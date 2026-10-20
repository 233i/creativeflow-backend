using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public enum VideoJobStatus
{
    /// <summary>上游已受理，等待处理或后续状态。</summary>
    Pending,
    /// <summary>当前正在执行。</summary>
    Running,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
    /// <summary>已请求或完成取消。</summary>
    Cancelled,
}

/// <param name="JobId">视频任务的本地标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="UpstreamJobId">供应商接受视频生成后返回的任务标识。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="AssetId">素材唯一标识。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="RequestJson">保存或传递的原始请求 JSON。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record VideoJob(
    Guid JobId,
    DeviceId DeviceId,
    Guid? ProviderId,
    string ModelKey,
    VideoJobStatus Status,
    string? UpstreamJobId,
    double? Progress,
    Guid? AssetId,
    string? Error,
    string RequestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IVideoJobRepository
{
    /// <summary>按原有存储规则写入视频生成任务。</summary>
    Task InsertAsync(VideoJob job, CancellationToken ct);

    /// <summary>按设备限定作用域，防止猜中任务标识的人进行轮询。</summary>
    Task<VideoJob?> FindAsync(DeviceId deviceId, Guid jobId, CancellationToken ct);

    /// <summary>供请求之外运行的后台轮询器使用的无作用域查询。</summary>
    Task<VideoJob?> FindByIdAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// 查找生成自有视频素材的供应商任务。在把生成视频串接到另一供应商操作时，
    /// 用于恢复仍有效的供应商 URL。
    /// </summary>
    Task<VideoJob?> FindByAssetAsync(
        DeviceId deviceId,
        Guid assetId,
        CancellationToken ct);

    /// <summary>
    /// 查找本次运行或任一续接祖先中，由工作流节点提交的最新供应商任务。
    /// </summary>
    Task<VideoJob?> FindLatestForWorkflowNodeAsync(
        DeviceId deviceId,
        WorkflowRunId runId,
        string nodeId,
        CancellationToken ct);

    /// <summary>更新视频生成任务，保留原有访问范围和并发结果。</summary>
    Task UpdateAsync(VideoJob job, CancellationToken ct);

    /// <summary>把已经落盘的成品和视频成功状态原子提交，重试不会增加第二份素材。</summary>
    Task CompleteFileAsync(VideoJob job, CreativeFlow.Application.Abstractions.Ai.StoredAsset file, CancellationToken ct);

    /// <summary>
    /// 仍在执行的任务，最旧的排在前面，供后台轮询器推进。
    /// </summary>
    Task<IReadOnlyList<VideoJob>> ListActiveAsync(int limit, CancellationToken ct);

    /// <summary>
    /// 将从 <paramref name="staleBefore"/> 起未更新的任务标记为失败。
    /// </summary>
    /// <remarks>
    /// 若无此限制，供应商悄然丢弃任务会导致记录永久轮询。设置边界可将不可见的挂起转为可报告的失败。
    /// </remarks>
    Task<int> FailStaleAsync(DateTimeOffset staleBefore, CancellationToken ct);
}
