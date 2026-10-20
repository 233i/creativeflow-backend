using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>等待已存在的视频任务，传播取消和节点进度；从不提交生成请求。</summary>
internal sealed class WorkflowVideoJobAwaiter(
    IVideoJobRepository videoJobs, IWorkflowRunRepository runs, IWorkflowNodeProgress progress)
{
    /// <summary>保留原本地视频任务查询间隔。</summary>
    private static readonly TimeSpan VideoStatusDelay = TimeSpan.FromSeconds(3);

    /// <summary>等待本地视频任务结束，同步节点进度并传播工作流取消。</summary>
    internal async Task<WorkflowNodeValue> WaitAsync(WorkflowNodeExecutionContext context, Guid jobId, CancellationToken ct)
    {
        var run = context.Run;
        // 等待循环只读取本地视频任务状态；运行取消必须及时中止等待，不能在这里创建新的上游任务。
        double? lastProgress = null;
        while (true)
        {
            await Task.Delay(VideoStatusDelay, ct);
            if (await runs.IsCancellationRequestedAsync(run.Id, ct))
            {
                if (await videoJobs.FindByIdAsync(jobId, ct) is { } active
                    && active.Status is VideoJobStatus.Pending or VideoJobStatus.Running)
                {
                    await videoJobs.UpdateAsync(
                        active with
                        {
                            Status = VideoJobStatus.Cancelled,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        },
                        ct);
                }
                throw new WorkflowCancelledException();
            }

            var job = await videoJobs.FindByIdAsync(jobId, ct)
                ?? throw new WorkflowExecutionException("The workflow video job disappeared.");

            if (job.Progress != lastProgress)
            {
                lastProgress = job.Progress;
                await progress.ReportAsync(run, context.NodeRun, job.Progress, ct);
            }

            switch (job.Status)
            {
                case VideoJobStatus.Succeeded when job.AssetId is { } assetId:
                    return WorkflowNodeValue.FromVideo($"/api/v1/canvas/assets/{assetId}");
                case VideoJobStatus.Failed:
                    throw new WorkflowExecutionException(job.Error ?? "Video generation failed.");
                case VideoJobStatus.Cancelled:
                    throw new WorkflowCancelledException();
            }
        }
    }
}
