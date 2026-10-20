using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

/// <summary>知识入库专用宿主，每两秒检查持久队列；不接管 MAF 或原媒体任务。</summary>
internal sealed class KnowledgeIndexWorker(IServiceScopeFactory scopes, ILogger<KnowledgeIndexWorker> logger) : BackgroundService
{
    /// <summary>一次处理一个有界任务；非敏感日志不记录异常原文、文档或凭据。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<KnowledgeIndexProcessor>().ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Knowledge index worker stopped this attempt; its durable checkpoint will determine recovery."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
