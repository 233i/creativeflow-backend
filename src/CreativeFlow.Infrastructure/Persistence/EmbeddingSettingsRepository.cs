using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>追加不可变向量参数版本，查询最新保存项；不触碰现有模型或媒体配置。</summary>
public sealed class EmbeddingSettingsRepository(NpgsqlDataSource dataSource) : IEmbeddingSettingsRepository
{
    /// <summary>读取最近保存版本，不存在时由应用显示默认草稿。</summary>
    public async Task<EmbeddingSettingsResponse?> FindAsync(ModelId modelId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<EmbeddingConfigurationRow>(
            """
            SELECT version, settings_json::text AS settings_json
            FROM ai_embedding_configuration WHERE model_id = @ModelId
            ORDER BY created_at DESC, version DESC LIMIT 1
            """, new EmbeddingModelArgs(modelId.Value));
        return row is null ? null : new(
            JsonSerializer.Deserialize(row.SettingsJson, EmbeddingJsonContext.Default.EmbeddingSettings)
                ?? throw new InvalidOperationException("Stored embedding settings are invalid."),
            row.Version.ToString());
    }

    /// <summary>只插入新版本，由模型外键约束资源存在性，不覆盖历史版本。</summary>
    public async Task<EmbeddingSettingsResponse> SaveAsync(ModelId modelId, EmbeddingSettings settings, CancellationToken ct)
    {
        var version = Guid.CreateVersion7();
        var json = JsonSerializer.Serialize(settings, EmbeddingJsonContext.Default.EmbeddingSettings);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO ai_embedding_configuration (model_id, version, settings_json)
            VALUES (@ModelId, @Version, @SettingsJson::jsonb)
            """, new EmbeddingWriteArgs(modelId.Value, version, json));
        return new(settings, version.ToString());
    }
}

/// <summary>查询向量配置的静态参数，支持 Dapper.AOT。</summary>
/// <param name="ModelId">本地模型行标识。</param>
internal sealed record EmbeddingModelArgs(Guid ModelId);

/// <summary>追加版本的静态参数，不携带任何模型密钥。</summary>
/// <param name="ModelId">本地模型行标识。</param>
/// <param name="Version">本次生成的版本标识。</param>
/// <param name="SettingsJson">经过验证的非敏感配置 JSON。</param>
internal sealed record EmbeddingWriteArgs(Guid ModelId, Guid Version, string SettingsJson);

/// <summary>读取向量配置版本及 JSON 的静态数据库行。</summary>
internal sealed class EmbeddingConfigurationRow
{
    /// <summary>配置版本 UUID。</summary>
    public Guid Version { get; init; }
    /// <summary>非敏感参数 JSON。</summary>
    public string SettingsJson { get; init; } = "";
}

/// <summary>向量配置持久化使用静态 JSON 元数据，不引入运行时反射序列化。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmbeddingSettings))]
internal partial class EmbeddingJsonContext : JsonSerializerContext;
