using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>持久化失败终态与未执行节点，处理认领后的异常收尾；从不重新执行节点。</summary>
internal sealed class WorkflowRunFinalizer(
    IWorkflowRunRepository runs, WorkflowProgressPublisher progress, ILogger<WorkflowRunWorker> logger)
{
    /// <summary>数据库暂时不可用时只重试原有小型终止写入。</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    /// <summary>保存工作流失败并处理尚未执行的节点。</summary>
    internal async Task FailRunAsync(
        WorkflowRun run,
        string error,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var failed = run with
        {
            Status = WorkflowRunStatus.Failed,
            Error = error,
            UpdatedAt = now,
            FinishedAt = now,
        };
        if (!await runs.UpdateAsync(failed, ct))
        {
            await progress.PublishAsync(
                run, "cancelled", null, null, null, null, null, ct);
            return;
        }
        await runs.SkipRemainingNodesAsync(run.Id, now, ct);
        await progress.PublishAsync(failed, "failed", null, null, null, null, error, ct);
    }

    /// <summary>为认领后未处理异常重试小型终止写入，避免运行永久停在 running。</summary>
    internal async Task FailUnhandledRunAsync(
        WorkflowRun run,
        CancellationToken ct)
    {
        // 若异常来自 PostgreSQL，应持续重试这次小型终止写入，而不是让已认领记录永久停留在 `running`。
        // 恢复仍由用户显式选择，确保上游生成绝不会自动重复提交。
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await runs.IsCancellationRequestedAsync(run.Id, ct))
                {
                    await progress.PublishAsync(
                        run, "cancelled", null, null, null, null, null, ct);
                    return;
                }

                await FailRunAsync(
                    run,
                    "The workflow stopped because of an internal service error. " +
                    "Continue the failed run or run it again from the beginning.",
                    ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not persist failure for workflow run {RunId}; retrying.",
                    run.Id);
                await Task.Delay(IdleDelay, ct);
            }
        }
    }
}
