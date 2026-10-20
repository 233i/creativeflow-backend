namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>同一个提交或网络重试必须复用 Key；只有明确重新生成才使用新 Key。</summary>
/// <param name="Key">提交前持久化的幂等 UUID。</param>
/// <param name="Input">需要固定保存的图片生成参数。</param>
public sealed record SubmitImageJobRequest(Guid Key, GenerateImageRequest Input);

/// <summary>重新生成会产生新的上游调用；原任务和参数快照保留。</summary>
/// <param name="Key">本次重新生成的标识，网络重试时复用。</param>
public sealed record RegenerateImageJobRequest(Guid Key);

/// <summary>公开状态不包含密钥或带鉴权参数的上游地址。</summary>
/// <param name="JobId">用于后续查询的任务 ID。</param>
/// <param name="Status">任务当前排队、生成、保存或终态。</param>
/// <param name="FailureStage">失败阶段，其余状态为空。</param>
/// <param name="Error">可展示的失败详情。</param>
/// <param name="CreatedAt">服务器接受时间，用于计算等待时长。</param>
/// <param name="StartedAt">首次开始执行时间。</param>
/// <param name="FinishedAt">本轮完成时间，用于固定最终耗时。</param>
/// <param name="CanRetryDownload">是否有可单独恢复保存的失败产物。</param>
/// <param name="CanvasId">对应的画布。</param>
/// <param name="NodeId">对应的生成配置节点。</param>
/// <param name="Input">不可变的原始输入。</param>
/// <param name="Assets">已经保存到本站的全部或部分输出。</param>
public sealed record ImageJobResponse(
    string JobId, string Status, string? FailureStage, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    bool CanRetryDownload, string? CanvasId, string? NodeId,
    GenerateImageRequest Input, ImageJobAsset[] Assets);

/// <summary>固定输出序号让轮询、刷新和下载重试不会产生重复结果节点。</summary>
/// <param name="Index">从零开始的输出序号。</param>
/// <param name="Url">本站受账号访问控制的素材地址。</param>
/// <param name="MediaType">图片 MIME 类型。</param>
/// <param name="AssetId">素材的稳定标识。</param>
public sealed record ImageJobAsset(int Index, string Url, string MediaType, string AssetId);

/// <summary>可分页恢复画布任务，游标是上一页最后一个任务 ID。</summary>
/// <param name="Items">当前页的任务快照。</param>
/// <param name="NextCursor">下一页游标，没有更多任务时为空。</param>
public sealed record ImageJobPage(ImageJobResponse[] Items, string? NextCursor);
