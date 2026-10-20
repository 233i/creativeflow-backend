using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>在创建它们的 HTTP 请求之外运行排队的智能体轮次。</summary>
/// <remarks>
/// 单实例假设：进程内取消和执行所有权不会在副本之间共享。启用多个 API 实例前，
/// 排队运行的认领和实时取消必须迁移到分布式租约，正如视频轮询器需要数据行锁一样。
/// </remarks>
internal sealed class AgentRunHost(
    IServiceScopeFactory scopeFactory,
    IAgentCanvasBridge canvasBridge,
    ILogger<AgentRunHost> logger) : BackgroundService, IAgentRunControl
{
    /// <summary>Agent 宿主扫描持久化排队任务的间隔。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    /// <summary>后台扫描每次最多处理的任务数量。</summary>
    private const int BatchSize = 8;
    /// <summary>单个 Agent 宿主允许同时执行的最大运行数。</summary>
    private const int MaxConcurrentRuns = 4;

    /// <summary>唤醒 Agent 运行的有界进程内队列。</summary>
    private readonly Channel<AgentRunId> _queue = Channel.CreateBounded<AgentRunId>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    /// <summary>本进程当前活跃运行与取消源的索引。</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    /// <summary>限制本进程并发运行数量的信号量。</summary>
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRuns, MaxConcurrentRuns);

    /// <summary>将运行标识尝试写入本机待处理队列，由同一宿主统一认领和执行。</summary>
    public void Enqueue(AgentRunId runId) => _queue.Writer.TryWrite(runId);

    /// <summary>向本机正在执行的指定运行请求取消；运行未激活或已请求取消时返回 false。</summary>
    public bool Cancel(AgentRunId runId) =>
        _active.TryGetValue(runId.Value, out var source) && Cancel(source);

    /// <summary>启动时中断遗留活动任务，随后按原周期扫描并消费待执行运行，直至宿主停止。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InterruptOrphansAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
                await DrainAsync(stoppingToken);
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent run host tick failed; continuing.");
            }
        }
    }

    /// <summary>先请求取消本机全部活动运行，再等待后台宿主按原生命周期停止。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _active.Values) Cancel(source);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>在启动时处理上一进程遗留的 Agent 运行。</summary>
    private async Task InterruptOrphansAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var count = await repository.InterruptOrphanedRunsAsync(DateTimeOffset.UtcNow, ct);
        if (count > 0)
        {
            logger.LogWarning("Interrupted {Count} Agent runs left active by the previous process.", count);
        }
    }

    /// <summary>扫描数据库中等待执行的 Agent 运行并唤醒本地队列。</summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        foreach (var runId in await repository.ListQueuedAsync(BatchSize, ct))
        {
            _queue.Writer.TryWrite(runId);
        }
    }

    /// <summary>读取本地运行队列，在原有并发上限内安排执行。</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var runId))
        {
            if (_active.ContainsKey(runId.Value)) continue;
            await _concurrency.WaitAsync(ct);
            _ = RunOneAsync(runId, ct);
        }
    }

    /// <summary>为单个 Agent 运行创建独立作用域与取消源，结束时释放执行所有权。</summary>
    private async Task RunOneAsync(AgentRunId runId, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        linked.CancelAfter(TimeSpan.FromMinutes(15));
        if (!_active.TryAdd(runId.Value, linked))
        {
            _concurrency.Release();
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<AgentTurnRunner>();
            try
            {
                await runner.RunAsync(runId, linked.Token);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent run {RunId} failed.", runId);
                // 使用宿主令牌：用户取消已取消运行令牌，但仍必须持久化终止状态。
                await runner.FailUnhandledAsync(runId, exception, stoppingToken);
            }
        }
        finally
        {
            canvasBridge.Release(runId);
            _active.TryRemove(runId.Value, out _);
            _concurrency.Release();
        }
    }

    /// <summary>仅首次向取消源发出取消信号，已请求取消时返回 false。</summary>
    private static bool Cancel(CancellationTokenSource source)
    {
        if (source.IsCancellationRequested) return false;
        source.Cancel();
        return true;
    }
}
