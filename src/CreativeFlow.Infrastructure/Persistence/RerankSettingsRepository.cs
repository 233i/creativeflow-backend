using Dapper;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>追加重排序配置版本，不修改历史参数或重复保存渠道密钥。</summary>
public sealed class RerankSettingsRepository(NpgsqlDataSource source) : IRerankSettingsRepository
{
    /// <summary>读取指定模型最新参数版本，不覆盖历史记录。</summary>
    public async Task<RerankSettingsResponse?> FindAsync(ModelId id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RerankConfigurationRow>("""
            SELECT version, settings_json::text AS settings_json FROM ai_rerank_configuration
            WHERE model_id = @Id ORDER BY created_at DESC, version DESC LIMIT 1
            """, new RerankModelArgs(id.Value));
        return row is null ? null : new(JsonSerializer.Deserialize(row.SettingsJson, RerankJsonContext.Default.RerankSettings)!, row.Version.ToString());
    }

    /// <summary>追加独立配置版本并返回收据，不覆盖旧记录或调用模型。</summary>
    public async Task<RerankSettingsResponse> SaveAsync(ModelId id, RerankSettings settings, CancellationToken ct)
    {
        var version = Guid.CreateVersion7();
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO ai_rerank_configuration(model_id, version, settings_json) VALUES (@Id, @Version, @Json::jsonb)
            """, new RerankWriteArgs(id.Value, version, JsonSerializer.Serialize(settings, RerankJsonContext.Default.RerankSettings)));
        return new(settings, version.ToString());
    }
}

/// <summary>重排序配置读取行，使用静态映射支持 Dapper.AOT。</summary>
internal sealed class RerankConfigurationRow
{
    /// <summary>不可变参数版本身份。</summary>
    public Guid Version { get; init; }
    /// <summary>数据库中保存的非秘密参数 JSON。</summary>
    public string SettingsJson { get; init; } = "";
}
/// <summary>配置查询参数，不包含密钥。</summary>
/// <param name="Id">需要读取参数的模型身份。</param>
internal sealed record RerankModelArgs(Guid Id);
/// <summary>追加不可变配置版本的静态参数。</summary>
/// <param name="Id">配置所属模型身份。</param>
/// <param name="Version">新追加的参数版本。</param>
/// <param name="Json">不含凭据的序列化参数。</param>
internal sealed record RerankWriteArgs(Guid Id, Guid Version, string Json);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RerankSettings))]
internal partial class RerankJsonContext : JsonSerializerContext;
