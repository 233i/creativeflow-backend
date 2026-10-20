using System.Text.Json;
using Npgsql;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>真实隔离 PostgreSQL 验证文档版本、配额、分批检查点及引用保护；模型和向量网络使用替身。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeDocumentTests
{
    /// <summary>禁止没有显式测试数据库时借用开发环境。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>旧 JSONB 资料迁移保持片段 ID、摘要、激活指针及向量值不变。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task MigrationPreservesExistingPilotDataAndActivatedVersion()
    {
        await using var f = await KnowledgeFixture.CreateAsync(migrationsThrough: 30);
        var version = Guid.NewGuid(); var chunk = Guid.NewGuid();
        await f.Database.SqlAsync($$"""
            INSERT INTO knowledge_version(id,base_id,digest,model_snapshot,documents_json,chunks_json,vectors_json,status)
            VALUES ('{{version}}','{{f.Library.Id}}','old-digest','{"dimensions":3,"batchSize":1,"revision":1}',
                '[{"name":"legacy.md","text":"历史合成正文"}]',
                '[{"id":"{{chunk}}","document":"legacy.md","startLine":1,"endLine":1,"text":"历史合成正文"}]',
                '{"{{chunk}}":[1,0,0]}','ready');
            UPDATE knowledge_base SET active_version='{{version}}' WHERE id='{{f.Library.Id}}';
            """);
        var assembly = typeof(CreativeFlow.Infrastructure.Persistence.Migrations.MigrationRunner).Assembly;
        await using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.Contains("0031__", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream); await f.Database.SqlAsync(await reader.ReadToEndAsync(Ct));
        // 旧试点数据先经过拆表迁移，再应用语义切片元数据迁移；默认值保证历史片段仍可读取。
        await using var semanticStream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.Contains("0034__", StringComparison.Ordinal)))!;
        using var semanticReader = new StreamReader(semanticStream); await f.Database.SqlAsync(await semanticReader.ReadToEndAsync(Ct));
        await using var hybridStream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.Contains("0042__", StringComparison.Ordinal)))!;
        using var hybridReader = new StreamReader(hybridStream); await f.Database.SqlAsync(await hybridReader.ReadToEndAsync(Ct));
        var restored = (await f.Repository.FindVersionAsync(f.Library.Id, version, Ct))!;
        Assert.Equal("dense-v1", restored.IndexSchema);
        Assert.Equal("old-digest", restored.Digest); Assert.Equal("ready", restored.Status);
        Assert.Equal(chunk, Assert.Single(KnowledgeLibraryService.Chunks(restored)).Id);
        Assert.Equal(new float[] { 1, 0, 0 }, KnowledgeLibraryService.Vectors(restored)[chunk.ToString()]);
        Assert.Equal(version, (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion);
        Assert.Equal(1, restored.Progress!.WrittenCount); Assert.Equal(1, restored.Progress.Number);
        Assert.Equal("历史合成正文", Assert.Single(JsonSerializer.Deserialize(restored.DocumentsJson, KnowledgeJsonContext.Default.KnowledgeDocumentInputArray)!).Text);
    }

    /// <summary>新增保留未变文档及向量，替换保留原修订，移除只改变新版本并保持激活指针。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task AddReplaceRemoveAreImmutableAndReuseConfirmedVectors()
    {
        await using var f = await KnowledgeFixture.CreateAsync();
        var original = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        await f.Repository.ActivateAsync(f.Library.Id, original.Id, Ct);
        var add = new KnowledgeDocumentChangeRequest(original.Id, [new("add", "faq.md", Text: "合成 FAQ：周一营业。")], 200, 0);
        var preview = await f.Service.PreviewChangesAsync(f.Library.Id, add, Ct);
        Assert.Equal(1, f.Model.Calls); Assert.Equal(2, preview.DocumentCount);
        var added = await f.Service.ChangeDocumentsAsync(f.Library.Id, add with { PreviewDigest = preview.Digest }, Ct);
        Assert.Equal(added.Id, (await f.Service.ChangeDocumentsAsync(f.Library.Id, add with { PreviewDigest = preview.Digest }, Ct)).Id);
        Assert.Equal(1, added.ReusedCount); Assert.Equal(original.Id, added.SourceVersionId);
        await f.Processor.ProcessNextAsync(Ct); Assert.Equal(2, f.Model.Calls);
        var docs = await f.Repository.DocumentsAsync(f.Library.Id, added.Id, Ct);
        var faq = docs.Single(d => d.Name == "faq.md");
        var replace = new KnowledgeDocumentChangeRequest(added.Id, [new("replace", faq.Name, faq.Id, "合成 FAQ：周二营业。")], 200, 0);
        var replaced = await Apply(f, replace); await f.Processor.ProcessNextAsync(Ct);
        Assert.Equal(3, f.Model.Calls); Assert.Equal(1, replaced.ReusedCount);
        Assert.Contains("周一", (await f.Repository.DocumentAsync(f.Library.Id, added.Id, faq.Id, Ct))!.Text);
        var newFaq = (await f.Repository.DocumentsAsync(f.Library.Id, replaced.Id, Ct)).Single(d => d.Name == "faq.md");
        Assert.NotEqual(faq.Id, newFaq.Id);
        Assert.Null(await f.Repository.DocumentAsync(f.Library.Id, added.Id, newFaq.Id, Ct));
        var removed = await Apply(f, new(replaced.Id, [new("remove", newFaq.Name, newFaq.Id)], 200, 0));
        await f.Processor.ProcessNextAsync(Ct);
        Assert.Equal(3, f.Model.Calls); Assert.Single(await f.Repository.DocumentsAsync(f.Library.Id, removed.Id, Ct));
        Assert.Equal(original.Id, (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion);
        Assert.Equal(4, (await f.Repository.VersionsAsync(f.Library.Id, Ct)).Max(v => v.Number));
        Assert.Equal(0, await f.Database.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'knowledge_version' AND column_name IN ('documents_json','chunks_json','vectors_json')"));
        Assert.Equal(3, await f.Database.CountAsync("SELECT count(*) FROM knowledge_document"));
    }

    /// <summary>错误基线、伪造修订、重名和清空库均在受理前拒绝；预览摘要不能换操作使用。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ChangesRejectStalePreviewCrossVersionAndInvalidActions()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var original = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var doc = Assert.Single(await f.Repository.DocumentsAsync(f.Library.Id, original.Id, Ct));
        foreach (var changes in new KnowledgeDocumentChange[][] {
            [new("add", doc.Name, Text: "覆盖")], [new("replace", doc.Name, Guid.NewGuid(), "伪造")],
            [new("remove", doc.Name, doc.Id)], [new("rename", doc.Name, doc.Id)], [new("remove", doc.Name, doc.Id, "不允许正文")],
            [new("add", "a.txt", Text:"a"), new("add", "A.TXT", Text:"b")] })
            await Assert.ThrowsAsync<KnowledgeException>(() => f.Service.PreviewChangesAsync(f.Library.Id, new(original.Id, changes), Ct));
        var add = new KnowledgeDocumentChangeRequest(original.Id, [new("add", "a.md", Text: "新增合成内容")]);
        var preview = await f.Service.PreviewChangesAsync(f.Library.Id, add, Ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Service.ChangeDocumentsAsync(f.Library.Id, add with { Overlap = 50, PreviewDigest = preview.Digest }, Ct));
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Service.PreviewChangesAsync(f.Library.Id, add with { SourceVersionId = Guid.NewGuid() }, Ct));
        Assert.Single(await f.Repository.VersionsAsync(f.Library.Id, Ct)); Assert.Equal(1, f.Model.Calls);
    }

    /// <summary>非默认容量经过配置后可超过旧试点常量；列表、读路径与 Worker 仍按批次访问规范化行。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ConfigurableQuotasSupportLargerCorpusWithoutWholeVectorJson()
    {
        var quotas = new KnowledgeQuotas { MaxDocuments = 16, MaxDocumentBytes = 131072, MaxVersionBytes = 262144, MaxChunks = 1024 };
        await using var f = await KnowledgeFixture.CreateAsync(quotas);
        var input = new KnowledgeImportRequest(Enumerable.Range(1, 9).Select(i => new KnowledgeDocumentInput($"manual-{i}.txt", new string((char)('a' + i), 8000))).ToArray(), 200, 0);
        var preview = f.Service.Preview(input); Assert.Equal(360, preview.Chunks.Length);
        var version = await f.Service.ImportAsync(f.Library.Id, input with { PreviewDigest = preview.Digest }, Ct);
        await f.Processor.ProcessNextAsync(Ct);
        var progress = Assert.Single(await f.Repository.VersionsAsync(f.Library.Id, Ct));
        Assert.Equal(360, progress.EmbeddedCount); Assert.Equal(360, progress.WrittenCount);
        var metadata = (await f.Repository.FindReadableVersionAsync(f.Library.Id, version.Id, Ct))!;
        Assert.Equal("[]", metadata.ChunksJson); Assert.Equal("{}", metadata.VectorsJson);
        var documents = await f.Repository.DocumentsAsync(f.Library.Id, version.Id, Ct);
        Assert.Equal(9, documents.Length);
        Assert.Equal(32, (await f.Repository.ReadChunksAsync(f.Library.Id, version.Id, null, null, 128, 32, Ct)).Length);
        Assert.Single(await f.Repository.ReadChunksAsync(f.Library.Id, version.Id, [preview.Chunks[3].Id], null, 0, 8, Ct));
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(input));
    }

    /// <summary>版本数和存储预留在数据库事务中限制，并发受理无法突破额度。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task VersionAndStorageBudgetsAreEnforcedBeforeNewModelCalls()
    {
        await using var f = await KnowledgeFixture.CreateAsync(new() { MaxVersions = 1 });
        await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => f.ImportAsync("另一个候选版本"));
        Assert.Equal(1, f.Model.Calls);
        await using var limited = await KnowledgeFixture.CreateAsync(new() { MaxLibraryReservedBytes = 65536 });
        await Assert.ThrowsAsync<KnowledgeException>(() => limited.ImportAsync(new string('x', 20000)));
        Assert.Equal(0, limited.Model.Calls); Assert.Empty(await limited.Repository.VersionsAsync(limited.Library.Id, Ct));
    }

    /// <summary>模型配置改变不得复用旧向量；已经完成的片段只有同模型指纹时复用。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ChangedEmbeddingConfigurationDoesNotReuseVectors()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var original = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        f.Model.Revision = 2;
        var next = await Apply(f, new(original.Id, [new("add", "new.md", Text: "新增资料")], 200, 0));
        Assert.Equal(0, next.ReusedCount); await f.Processor.ProcessNextAsync(Ct); Assert.Equal(3, f.Model.Calls);
    }

    /// <summary>活跃及引用中的版本不能清理；清理失败保留数据，明确重试后仅删除无引用的修订。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task CleanupProtectsActiveVersionsAndLeavesRecoverableDataOnFailure()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var original = await f.ImportAsync();
        var admin = await Admin(f);
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Repository.RequestCleanupAsync(f.Library.Id, original.Id, admin, "仍在处理", Ct));
        await f.Processor.ProcessNextAsync(Ct); await f.Repository.ActivateAsync(f.Library.Id, original.Id, Ct);
        Assert.False((await f.Repository.CleanupPreviewAsync(f.Library.Id, original.Id, Ct)).Allowed);
        var next = await Apply(f, new(original.Id, [new("add", "new.md", Text: "新增资料")], 200, 0)); await f.Processor.ProcessNextAsync(Ct);
        await f.Repository.ActivateAsync(f.Library.Id, next.Id, Ct);
        await f.Repository.RequestCleanupAsync(f.Library.Id, original.Id, admin, "清理无引用开发候选", Ct);
        f.Storage.FailDelete = true; await f.Processor.ProcessNextAsync(Ct);
        Assert.Equal("cleanup_failed", (await f.Repository.FindVersionAsync(f.Library.Id, original.Id, Ct))!.Status);
        Assert.Equal(3, await f.Database.CountAsync("SELECT count(*) FROM knowledge_chunk"));
        Assert.Null(await f.Repository.FindReadyDigestAsync(f.Library.Id, original.Id, Ct));
        f.Storage.FailDelete = false;
        await f.Repository.RequestCleanupAsync(f.Library.Id, original.Id, admin, "重试无副作用集合删除", Ct); await f.Processor.ProcessNextAsync(Ct);
        Assert.Equal("deleted", (await f.Repository.FindVersionAsync(f.Library.Id, original.Id, Ct))!.Status);
        Assert.Equal(2, await f.Database.CountAsync("SELECT count(*) FROM knowledge_document"));
        Assert.Equal(2, await f.Database.CountAsync("SELECT count(*) FROM knowledge_cleanup_audit WHERE completed_at IS NOT NULL"));
        Assert.False(f.Storage.Points.ContainsKey(original.Id)); Assert.Equal(2, f.Model.Calls);
        Assert.Equal(2, Assert.Single(await f.Repository.VersionsAsync(f.Library.Id, Ct)).Number);
    }

    /// <summary>真实图引用触发器保护旧发布与运行；清理先受理时阻止之后创建悬空运行。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task PersistentPinsPreventCleanupAndRejectLateRunReferences()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var admin = await Admin(f); var workflow = Guid.NewGuid();
        var graph = $$$"""{"schemaVersion":1,"nodes":[{"type":"knowledgeRetrieve","data":{"versionId":"{{{version.Id}}}"}}]}""";
        await f.Database.SqlAsync($$"""INSERT INTO workflow(workflow_id,device_id,name,schema_version,graph,created_at,updated_at) VALUES ('{{workflow}}','{{f.Database.Device}}','test',1,'{{graph}}',now(),now());""");
        Assert.Equal(1, (await f.Repository.CleanupPreviewAsync(f.Library.Id, version.Id, Ct)).ReferenceCount);
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Repository.RequestCleanupAsync(f.Library.Id, version.Id, admin, "不应该删除", Ct));
        await f.Database.SqlAsync($$"""UPDATE workflow SET graph='{"schemaVersion":1,"nodes":[]}' WHERE workflow_id='{{workflow}}';""");
        await f.Repository.RequestCleanupAsync(f.Library.Id, version.Id, admin, "无引用后清理", Ct);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => f.Database.SqlAsync($$"""INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at) VALUES ('{{Guid.NewGuid()}}','{{workflow}}','{{f.Database.Device}}','{{graph}}','queued',now(),now());"""));
        Assert.Equal("knowledge_version_unavailable", failure.ConstraintName);
        Assert.False(await f.Repository.ActivateAsync(f.Library.Id, version.Id, Ct));
        await f.Processor.ProcessNextAsync(Ct); Assert.Equal("deleted", (await f.Repository.FindVersionAsync(f.Library.Id, version.Id, Ct))!.Status);
    }

    /// <summary>读取并确认真实预览，测试不绕过摘要检查。</summary>
    private static async Task<KnowledgeVersionResponse> Apply(KnowledgeFixture fixture, KnowledgeDocumentChangeRequest request)
    {
        var preview = await fixture.Service.PreviewChangesAsync(fixture.Library.Id, request, Ct);
        return await fixture.Service.ChangeDocumentsAsync(fixture.Library.Id, request with { PreviewDigest = preview.Digest }, Ct);
    }

    /// <summary>只在当前随机测试 schema 创建审计用管理员。</summary>
    private static async Task<Guid> Admin(KnowledgeFixture fixture)
    {
        var id = Guid.NewGuid();
        await fixture.Database.SqlAsync($"INSERT INTO sol_admin(admin_id,username,password_hash,created_at,updated_at) VALUES ('{id}','test','unused-synthetic-password-hash',now(),now())");
        return id;
    }
}
