using Npgsql;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>
/// 构建应用的 <see cref="NpgsqlDataSource"/>。
/// </summary>
/// <remarks>
/// 使用 <see cref="NpgsqlSlimDataSourceBuilder"/>，它默认禁用可选类型处理器，必须逐项显式启用。
/// 这样既能缩小原生二进制文件，更重要的是防止意外启用不兼容 AOT 的功能：
/// <c>EnableDynamicJson()</c> 和 <c>MapComposite&lt;T&gt;()</c> 都需要运行时代码生成，
/// 并已据此添加标注。
/// <para>
/// 对调用方的影响：要把普通对象存为 jsonb，必须使用源生成的 <c>JsonSerializerContext</c>
/// 自行序列化，并传递所得字符串。
/// </para>
/// </remarks>
public static class NpgsqlDataSourceFactory
{
    /// <summary>构建显式启用现有网络、数组和静态 JSON 支持的 Npgsql 数据源。</summary>
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlSlimDataSourceBuilder(connectionString);

        // 服务器提供 TLS 时必须启用，而所有托管 PostgreSQL 都提供 TLS。
        builder.EnableTransportSecurity();

        // inet 列（device.ip_prefix）。
        builder.EnableNetworkTypes();

        // 仅支持 JsonDocument/JsonElement，即兼容 AOT 的子集，不支持普通对象映射。
        builder.EnableJsonTypes();

        // 数组参数，供迁移运行器和批量查询使用。
        builder.EnableArrays();

        return builder.Build();
    }
}
