using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>真实隔离 PostgreSQL 验证重排序用途、不可变配置以及模型级级联清理。</summary>
public sealed class RerankSettingsDatabaseTests
{
    /// <summary>未显式提供数据库时不连接任何日常环境。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>版本不能覆盖，最近配置可稳定读取；新用途不改写原聊天模型。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ConfigurationsAppendAndCascadeOnlyOwnModel()
    {
        await using var database = await ImageJobDatabase.CreateAsync();
        var provider = Guid.NewGuid(); var model = new ModelId(Guid.NewGuid()); var ct = TestContext.Current.CancellationToken;
        await database.SqlAsync($$"""
            INSERT INTO ai_provider(provider_id,name,type,base_url) VALUES ('{{provider}}','rerank test','rerank','http://127.0.0.1:1234/v1');
            INSERT INTO ai_model(model_id,provider_id,model_key,name,type,category) VALUES ('{{model}}','{{provider}}','synthetic-rerank','test','rerank','rerank');
            """);
        var repository = new RerankSettingsRepository(database.Source);
        Assert.Null(await repository.FindAsync(model, ct));
        var first = await repository.SaveAsync(model, new(), ct);
        var second = await repository.SaveAsync(model, new() { ModelRevision = "r2" }, ct);
        Assert.NotEqual(first.Version, second.Version); Assert.Equal(second, await repository.FindAsync(model, ct));
        Assert.Equal(2, await database.CountAsync("SELECT count(*) FROM ai_rerank_configuration"));
        await database.SqlAsync($"DELETE FROM ai_model WHERE model_id='{model}'");
        Assert.Null(await repository.FindAsync(model, ct)); Assert.Equal(1, await database.CountAsync("SELECT count(*) FROM ai_provider"));
    }
}
