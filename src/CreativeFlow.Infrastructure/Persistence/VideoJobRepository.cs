using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 VideoJobRepository 的依赖与实例状态。</summary>
public sealed partial class VideoJobRepository(CreativeFlowConnectionFactory connections) : IVideoJobRepository
{
    /// <summary>该仓储查询使用的固定列清单。</summary>
    private const string Columns = """
        job_id, device_id, provider_id, model_key, status, upstream_job_id,
        progress, asset_id, error, request::text AS request_json, created_at, updated_at
        """;

    /// <summary>保存视频任务的请求快照、渠道、状态及上游关联信息，供后续查询与恢复使用。</summary>
    public async Task InsertAsync(VideoJob job, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO video_job (job_id, device_id, provider_id, model_key, status,
                                   upstream_job_id, progress, asset_id, error, request,
                                   created_at, updated_at)
            VALUES (@JobId, @DeviceId, @ProviderId, @ModelKey, @Status,
                    @UpstreamJobId, @Progress, @AssetId, @Error, @RequestJson::jsonb,
                    @CreatedAt, @UpdatedAt)
            """,
            new InsertVideoJobParams
            {
                JobId = job.JobId,
                DeviceId = job.DeviceId.Value,
                ProviderId = job.ProviderId,
                ModelKey = job.ModelKey,
                Status = ToWire(job.Status),
                UpstreamJobId = job.UpstreamJobId,
                Progress = job.Progress is { } value ? (float)value : null,
                AssetId = job.AssetId,
                Error = job.Error,
                RequestJson = job.RequestJson,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt,
            });
    }

    /// <summary>在当前可访问设备范围内查询视频任务，越权或不存在时返回空。</summary>
    public async Task<VideoJob?> FindAsync(DeviceId deviceId, Guid jobId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"SELECT {Columns} FROM video_job WHERE job_id = @JobId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new VideoJobScopeParams { JobId = jobId, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    /// <summary>供后台轮询按任务标识读取视频任务，不依赖 HTTP 设备作用域。</summary>
    public async Task<VideoJob?> FindByIdAsync(Guid jobId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"SELECT {Columns} FROM video_job WHERE job_id = @JobId",
            new VideoJobIdParam { JobId = jobId });

        return row?.ToDomain();
    }

    /// <summary>在当前可访问设备范围内查找关联指定成品素材的视频任务。</summary>
    public async Task<VideoJob?> FindByAssetAsync(
        DeviceId deviceId,
        Guid assetId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"""
            SELECT {Columns}
            FROM video_job
            WHERE asset_id = @AssetId
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new VideoJobAssetScopeParams
            {
                AssetId = assetId,
                DeviceId = deviceId.Value,
            });

        return row?.ToDomain();
    }

    /// <summary>沿工作流运行的恢复来源链查找同一设备、同一节点最新的视频任务，供续跑复用上游任务。</summary>
    public async Task<VideoJob?> FindLatestForWorkflowNodeAsync(
        DeviceId deviceId,
        WorkflowRunId runId,
        string nodeId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<VideoJobRow>(
            $"""
            WITH RECURSIVE source_runs AS (
                SELECT run_id, resumed_from_run_id
                FROM workflow_run
                WHERE run_id = @RunId AND device_id = @DeviceId

                UNION ALL

                SELECT parent.run_id, parent.resumed_from_run_id
                FROM workflow_run AS parent
                INNER JOIN source_runs AS child
                    ON parent.run_id = child.resumed_from_run_id
            )
            SELECT {Columns}
            FROM video_job
            WHERE device_id = @DeviceId
              AND request ->> 'workflowRunId' IN (
                  SELECT run_id::text FROM source_runs
              )
              AND request ->> 'nodeId' = @NodeId
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new WorkflowVideoJobLookupParams
            {
                DeviceId = deviceId.Value,
                RunId = runId.Value,
                NodeId = nodeId,
            });

        return row?.ToDomain();
    }

    /// <summary>按视频任务标识更新状态、上游任务号、进度和结果信息，不提交上游请求。</summary>
    public async Task UpdateAsync(VideoJob job, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE video_job
            SET status = @Status, upstream_job_id = @UpstreamJobId, progress = @Progress,
                asset_id = @AssetId, error = @Error, updated_at = @UpdatedAt
            WHERE job_id = @JobId
            """,
            new UpdateVideoJobParams
            {
                JobId = job.JobId,
                Status = ToWire(job.Status),
                UpstreamJobId = job.UpstreamJobId,
                Progress = job.Progress is { } value ? (float)value : null,
                AssetId = job.AssetId,
                Error = job.Error,
                UpdatedAt = job.UpdatedAt,
            });
    }

    /// <summary>按原顺序限量读取 pending 或 running 视频任务，优先推进较旧任务。</summary>
    public async Task<IReadOnlyList<VideoJob>> ListActiveAsync(int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 最旧的优先，防止一个繁忙设备饿死其他设备的任务。
        var rows = await connection.QueryAsync<VideoJobRow>(
            $"""
            SELECT {Columns}
            FROM video_job
            WHERE status IN ('pending', 'running')
            ORDER BY updated_at
            LIMIT @Limit
            """,
            new LimitParam { Limit = limit });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>将早于指定活动时间且仍未终止的视频任务标记失败，返回处理数量。</summary>
    public async Task<int> FailStaleAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteAsync(
            """
            UPDATE video_job
            SET status = 'failed',
                error = 'The provider stopped reporting progress for this job.',
                updated_at = now()
            WHERE status IN ('pending', 'running') AND updated_at < @StaleBefore
              AND NOT EXISTS(SELECT 1 FROM storage_write w WHERE w.operation_key='video-job:'||replace(video_job.job_id::text,'-','')
                  AND w.state IN ('writing','ready'))
            """,
            new StaleParam { StaleBefore = staleBefore });
    }

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    private static string ToWire(VideoJobStatus status) => status switch
    {
        VideoJobStatus.Pending => "pending",
        VideoJobStatus.Running => "running",
        VideoJobStatus.Succeeded => "succeeded",
        VideoJobStatus.Failed => "failed",
        VideoJobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    /// <summary>将数据库中的既有状态字符串还原为业务枚举。</summary>
    internal static VideoJobStatus ParseStatus(string value) => value switch
    {
        "pending" => VideoJobStatus.Pending,
        "running" => VideoJobStatus.Running,
        "succeeded" => VideoJobStatus.Succeeded,
        "failed" => VideoJobStatus.Failed,
        "cancelled" => VideoJobStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unrecognised video job status '{value}'."),
    };
}

internal sealed class VideoJobIdParam
{
    /// <summary>视频任务的本地标识。</summary>
    public Guid JobId { get; init; }
}

internal sealed class VideoJobScopeParams
{
    /// <summary>视频任务的本地标识。</summary>
    public Guid JobId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class VideoJobAssetScopeParams
{
    /// <summary>素材唯一标识。</summary>
    public Guid AssetId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class LimitParam
{
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal sealed class WorkflowVideoJobLookupParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>节点图中的稳定节点标识。</summary>
    public string NodeId { get; init; } = string.Empty;
}

internal sealed class StaleParam
{
    /// <summary>将未更新任务视为过期的时间界限。</summary>
    public DateTimeOffset StaleBefore { get; init; }
}

internal sealed class InsertVideoJobParams
{
    /// <summary>视频任务的本地标识。</summary>
    public Guid JobId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "pending";
    /// <summary>供应商接受视频生成后返回的任务标识。</summary>
    public string? UpstreamJobId { get; init; }
    /// <summary>现有协议中的任务完成比例，可空时表示未知。</summary>
    public float? Progress { get; init; }
    /// <summary>素材唯一标识。</summary>
    public Guid? AssetId { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>保存或传递的原始请求 JSON。</summary>
    public string RequestJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateVideoJobParams
{
    /// <summary>视频任务的本地标识。</summary>
    public Guid JobId { get; init; }
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "pending";
    /// <summary>供应商接受视频生成后返回的任务标识。</summary>
    public string? UpstreamJobId { get; init; }
    /// <summary>现有协议中的任务完成比例，可空时表示未知。</summary>
    public float? Progress { get; init; }
    /// <summary>素材唯一标识。</summary>
    public Guid? AssetId { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class VideoJobRow
{
    /// <summary>视频任务的本地标识。</summary>
    public Guid JobId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "pending";
    /// <summary>供应商接受视频生成后返回的任务标识。</summary>
    public string? UpstreamJobId { get; init; }
    /// <summary>现有协议中的任务完成比例，可空时表示未知。</summary>
    public float? Progress { get; init; }
    /// <summary>素材唯一标识。</summary>
    public Guid? AssetId { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>保存或传递的原始请求 JSON。</summary>
    public string RequestJson { get; init; } = "{}";

    // timestamptz 读取为 DateTime，参见 ProviderRow 上的说明。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public VideoJob ToDomain() => new(
        JobId,
        new DeviceId(DeviceId),
        ProviderId,
        ModelKey,
        VideoJobRepository.ParseStatus(Status),
        UpstreamJobId,
        Progress,
        AssetId,
        Error,
        RequestJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}
