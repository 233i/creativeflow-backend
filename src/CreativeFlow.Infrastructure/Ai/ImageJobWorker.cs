using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>PostgreSQL 队列执行器；关闭页面不终止，进程中断后通过租约与检查点安全恢复。</summary>
internal sealed class ImageJobWorker(IServiceScopeFactory scopes, ImageQueueLimits limits,
    ILogger<ImageJobWorker> logger) : BackgroundService
{
    /// <summary>以全局并发上限启动独立认领循环，使用宿主生命周期令牌。</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, limits.GlobalConcurrency).Select(_ => LoopAsync(stoppingToken)));

    /// <summary>认领任务并维护租约，异常只记录失败，不重发生成请求。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        // 每个认领使用独立作用域；浏览器不参与令牌生命周期，租约失败才停止执行。
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IImageJobRepository>();
                var job = await jobs.ClaimAsync(limits, ct);
                if (job is null) { await Task.Delay(1000, ct); continue; }
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var lease = HeartbeatAsync(jobs, job, execution, heartbeat.Token);
                try { await scope.ServiceProvider.GetRequiredService<ImageJobRunner>().RunAsync(job, execution.Token); }
                catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
                catch (Exception error)
                {
                    logger.LogWarning("Image job {JobId} failed ({ErrorType}); generation is not retried.", job.JobId, error.GetType().Name);
                    await jobs.FinishAsync(job, "任务执行异常。若已有上游产物，可重新下载；系统不会自动重新生成。", ct);
                }
                finally
                {
                    await heartbeat.CancelAsync();
                    await lease;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Image queue iteration failed ({ErrorType}).", error.GetType().Name);
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }

    /// <summary>定期续约，无法确认执行权时停止本地工作。</summary>
    private async Task HeartbeatAsync(IImageJobRepository jobs, ImageJob job, CancellationTokenSource execution, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(20_000, ct);
                if (!await jobs.HeartbeatAsync(job.JobId, job.LeaseId!.Value, ct))
                {
                    await execution.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Image job lease refresh failed ({ErrorType}).", error.GetType().Name);
            // 租约无法确认时停止本地工作，过期恢复只允许重试已有产物的保存。
            await execution.CancelAsync();
        }
    }
}
