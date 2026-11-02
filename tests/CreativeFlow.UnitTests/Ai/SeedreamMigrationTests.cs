using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>原生协议升级只修复明确的 Ark Seedream 推导配置，保留人工覆盖与中转渠道。</summary>
public sealed class SeedreamMigrationTests
{
    /// <summary>仅运行在显式配置的隔离数据库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>迁移旧模型后仍能持久化专用协议；旧作业、快照、密钥和地址不会改写。</summary>
    [Fact(Skip = "需要隔离测试数据库", SkipWhen = nameof(NoDatabase))]
    public async Task MigratesOnlyUnambiguousNativeModelsAndPreservesExplicitOverrides()
    {
        await using var db = await ImageJobDatabase.CreateAsync(47);
        var native = Guid.NewGuid(); var relay = Guid.NewGuid(); var lookalike = Guid.NewGuid();
        // 固定官方主机边界，避免把使用相同模型名的第三方或相似域名改成原生协议。
        await db.SqlAsync($$"""
            INSERT INTO ai_provider(provider_id,name,type,base_url) VALUES
              ('{{native}}','Ark','openai-chat','https://ark.cn-beijing.volces.com/api/plan/v3'),
              ('{{relay}}','Relay','openai-images','https://proxy.example.test/v1'),
              ('{{lookalike}}','Other','openai-chat','https://ark.cn-beijing.volces.com.example.test/v1');
            INSERT INTO ai_model(model_id,provider_id,model_key,name,category,type) VALUES
              ('{{Guid.NewGuid()}}','{{native}}','doubao-seedream-5.0-pro','auto','image',NULL),
              ('{{Guid.NewGuid()}}','{{native}}','doubao-seedream-5.0-pro-explicit','explicit','image','openai-images'),
              ('{{Guid.NewGuid()}}','{{relay}}','doubao-seedream-5.0-pro','relay','image',NULL),
              ('{{Guid.NewGuid()}}','{{lookalike}}','doubao-seedream-5.0-pro','other','image',NULL);
            """);
        await db.ApplyRemainingAsync(47);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM ai_model WHERE type='seedream-images' AND name='auto'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM ai_model WHERE type='openai-images' AND name='explicit'"));
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM ai_model WHERE type IS NULL"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM ai_provider WHERE name='Ark' AND type='openai-chat' AND base_url='https://ark.cn-beijing.volces.com/api/plan/v3'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM image_job"));
    }
}
