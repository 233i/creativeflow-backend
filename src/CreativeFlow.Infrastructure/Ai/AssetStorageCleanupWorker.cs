using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>恢复完整文件检查点并回收无引用内容；失败保留数据库状态，下轮重试，不调用任何模型。</summary>
internal sealed class AssetStorageCleanupWorker(FileSystemAssetStore store, ILogger<AssetStorageCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "File recovery/cleanup is incomplete; durable state will be retried."); }
        }
    }
}
