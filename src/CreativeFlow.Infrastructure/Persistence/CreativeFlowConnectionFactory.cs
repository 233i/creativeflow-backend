using Npgsql;
using CreativeFlow.Application.Abstractions.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>
/// 打开池化连接，并应用此请求已验证的账户作用域。
/// </summary>
/// <remarks>
/// PostgreSQL 连接使用连接池，因此每次取出连接时都会覆盖此设置。空值表示访客或仅设备作用域；
/// 它绝不会残留为上一个使用同一物理连接的请求所设置的值。
/// </remarks>
public sealed class CreativeFlowConnectionFactory(
    NpgsqlDataSource dataSource,
    IAccountContext accountContext)
{
    /// <summary>打开池化连接并覆盖本次已验证账号范围，异常时释放连接。</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT set_config('creativeflow.account_id', $1, false)";
            command.Parameters.AddWithValue(
                accountContext.AccountId?.Value.ToString() ?? string.Empty);
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class AccountContext : IAccountContext
{
    /// <summary>当前依赖注入作用域的账号标识，供数据库访问范围设置使用；未绑定账号时为空。</summary>
    public CreativeFlow.Domain.Identity.AccountId? AccountId { get; set; }
}
