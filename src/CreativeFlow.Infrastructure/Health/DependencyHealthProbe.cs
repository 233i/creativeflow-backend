using Npgsql;
using RabbitMQ.Client;
using CreativeFlow.Infrastructure.Messaging;
using StackExchange.Redis;

namespace CreativeFlow.Infrastructure.Health;

/// <summary>探测单个依赖项的结果。</summary>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Healthy">该服务或整体依赖是否通过健康检查。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="ElapsedMs">以毫秒计的耗时。</param>
public sealed record DependencyHealth(string Name, bool Healthy, string? Error, double ElapsedMs);

/// <summary>
/// 探测后端支撑服务。
/// </summary>
/// <remarks>
/// 手动实现而不使用社区的 <c>AspNetCore.HealthChecks.*</c> 包：其 AOT 兼容性未经验证，
/// 且每个包都会为本质上只有三条单行查询的功能引入传递依赖。
/// </remarks>
public sealed class DependencyHealthProbe(
    NpgsqlDataSource dataSource,
    IConnectionMultiplexer redis,
    RabbitMqConnectionProvider rabbit,
    FileStorageHealthProbe storage)
{
    /// <summary>并行检查当前配置的基础设施依赖健康状态。</summary>
    public async Task<IReadOnlyList<DependencyHealth>> ProbeAllAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(
            ProbeAsync("postgres", async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(token);
            }, ct),
            ProbeAsync("redis", async _ =>
            {
                // 仅执行 PING。StackExchange.Redis 3.x 会在 Execute 时强制检查 AllowAdmin，
                // 带管理标志的健康检查需要提升权限，却没有任何收益。
                await redis.GetDatabase().PingAsync();
            }, ct),
            ProbeAsync("rabbitmq", async token =>
            {
                var connection = await rabbit.GetConnectionAsync(token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
            }, ct),
            ProbeAsync("file-storage", storage.ProbeAsync, ct));

        return results;
    }

    /// <summary>测量单个依赖探测的成功、错误和耗时。</summary>
    private static async Task<DependencyHealth> ProbeAsync(
        string name, Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        var start = TimeProvider.System.GetTimestamp();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await probe(timeout.Token);
            return new DependencyHealth(name, true, null, Elapsed(start));
        }
        catch (Exception ex)
        {
            return new DependencyHealth(name, false, ex.Message, Elapsed(start));
        }
    }

    /// <summary>将单次依赖探测耗时转换为现有计时结果。</summary>
    private static double Elapsed(long start) =>
        TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
}
