using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>在显式隔离 PostgreSQL 中验证增量迁移、真实 Dapper 仓储与不可变配置版本。</summary>
public sealed class EmbeddingSettingsDatabaseTests
{
    /// <summary>未提供专用测试库时不读取日常开发或生产连接。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>保存两个配置后读到最近版本，原版本仍保留，删除模型时仅级联自身配置。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task VersionsPersistWithoutChangingExistingModels()
    {
        await using var database = await ImageJobDatabase.CreateAsync();
        var providerId = Guid.NewGuid();
        var modelId = new ModelId(Guid.NewGuid());
        await database.SqlAsync($$"""
            INSERT INTO ai_provider(provider_id,name,type,base_url)
            VALUES ('{{providerId}}','Embedding test','openai-embeddings','http://127.0.0.1:1234/v1');
            INSERT INTO ai_model(model_id,provider_id,model_key,name,type,category)
            VALUES ('{{modelId.Value}}','{{providerId}}','test-embedding','Test','openai-embeddings','embedding');
            """);
        var repository = new EmbeddingSettingsRepository(database.Source);
        Assert.Null(await repository.FindAsync(modelId, TestContext.Current.CancellationToken));
        var first = await repository.SaveAsync(modelId, new() { ExpectedDimensions = 4096 }, TestContext.Current.CancellationToken);
        var second = await repository.SaveAsync(modelId, new() { ExpectedDimensions = 4096, QueryPrefix = "query: " }, TestContext.Current.CancellationToken);
        var read = await repository.FindAsync(modelId, TestContext.Current.CancellationToken);
        Assert.Equal(second, read);
        Assert.NotEqual(first.Version, second.Version);
        await using var count = database.Source.CreateCommand($"SELECT count(*) FROM ai_embedding_configuration WHERE model_id = '{modelId.Value}'");
        Assert.Equal(2L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await database.SqlAsync($"DELETE FROM ai_model WHERE model_id = '{modelId.Value}'");
        Assert.Null(await repository.FindAsync(modelId, TestContext.Current.CancellationToken));
    }
}
