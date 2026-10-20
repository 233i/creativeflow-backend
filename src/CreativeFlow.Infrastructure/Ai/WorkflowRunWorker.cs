using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Ai.Workflows;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>认领持久化工作流运行，并按依赖顺序执行已验证节点。</summary>
internal sealed class WorkflowRunWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowRunWorker> logger,
    ExternalWorkflowApiOptions externalWorkflowOptions) : BackgroundService
{
    /// <summary>未认领到工作流时的等待间隔。</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    /// <summary>启动时标记重启中断的工作流，再按原全局并发数启动唯一宿主内的认领循环。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var recoveryScope = scopeFactory.CreateScope())
        {
            var recovered = await recoveryScope.ServiceProvider
                .GetRequiredService<IWorkflowRunRepository>()
                .FailInterruptedAsync(DateTimeOffset.UtcNow, stoppingToken);
            if (recovered > 0)
            {
                logger.LogWarning("Failed {Count} workflow runs interrupted by a restart.", recovered);
            }
        }

        var workers = Enumerable.Range(0, externalWorkflowOptions.GlobalConcurrency)
            .Select(_ => RunWorkerLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    /// <summary>持续认领工作流运行，并为每次执行创建独立作用域。</summary>
    private async Task RunWorkerLoopAsync(CancellationToken stoppingToken)
    {
        // 认领和执行各用独立作用域，避免账号上下文跨运行复用；单个运行异常需收尾后继续服务其他任务。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var claimScope = scopeFactory.CreateScope();
                var repository = claimScope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>();
                var run = await repository.ClaimNextAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (run is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                using var runScope = scopeFactory.CreateScope();
                try
                {
                    await runScope.ServiceProvider.GetRequiredService<WorkflowRunOrchestrator>()
                        .RunAsync(run, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Workflow run {RunId} stopped unexpectedly.", run.Id);
                    await runScope.ServiceProvider.GetRequiredService<WorkflowRunFinalizer>()
                        .FailUnhandledRunAsync(run, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Workflow worker iteration failed; continuing.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

}
