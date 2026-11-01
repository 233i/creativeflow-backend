using System.IO.Compression;
using System.Text.Json;
using Npgsql;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>真实隔离数据库验证管理审计、删除竞态、归档内容与原文差异；外部模型和索引用替身。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeManagementTests
{
    /// <summary>未提供独立数据库时禁止借用开发连接。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>每个测试的取消令牌，停止时不遗留数据库等待。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>重命名在事务中保存前后值；旧修订不能覆盖新值，也不重新调用模型。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task MetadataUsesRevisionAndAuditWithoutChangingVersions()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var admin = await Admin(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var changed = await f.Repository.UpdateMetadataAsync(f.Library.Id, admin, new(" 新名称 ", " 新用途 ", 1), Ct);
        Assert.Equal("新名称", changed.Name); Assert.Equal("新用途", changed.Description); Assert.Equal(2, changed.Revision);
        await Assert.ThrowsAsync<KnowledgeManagementConflict>(() => f.Repository.UpdateMetadataAsync(f.Library.Id, admin, new("覆盖", "", 1), Ct));
        var audit = Assert.Single(await f.Repository.AuditAsync(f.Library.Id, Ct));
        Assert.Equal("metadata_updated", audit.Action); Assert.Contains("新名称", audit.AfterJson!); Assert.Contains("合成手册", audit.BeforeJson!);
        Assert.Equal(version.Id, Assert.Single(await f.Repository.VersionsAsync(f.Library.Id, Ct)).Id); Assert.Equal(1, f.Model.Calls);
    }

    /// <summary>正在索引的版本不能删除；引用在预览后出现也使确认摘要失效。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task QueuedTasksAndReferencesBlockDeletionIncludingStalePreflight()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var admin = await Admin(f);
        var version = await f.ImportAsync(); Assert.False((await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct)).Allowed);
        await f.Processor.ProcessNextAsync(Ct);
        var before = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct); Assert.True(before.Allowed);
        var graph = Graph(version.Id); var workflow = Guid.NewGuid();
        await f.Database.SqlAsync($$"""INSERT INTO workflow(workflow_id,device_id,name,schema_version,graph,created_at,updated_at) VALUES ('{{workflow}}','{{f.Database.Device}}','引用草稿',1,'{{graph}}',now(),now());""");
        var blocked = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct);
        Assert.False(blocked.Allowed); Assert.Equal(1, blocked.ReferenceCount); Assert.Equal("workflow", Assert.Single(blocked.References).Kind);
        await Assert.ThrowsAsync<KnowledgeManagementConflict>(() => f.Repository.DeleteLibraryAsync(f.Library.Id, admin, Request(before), Ct));
        Assert.True((await f.Repository.FindAsync(f.Library.Id, Ct))!.Enabled);
        Assert.Empty(await f.Repository.AuditAsync(f.Library.Id, Ct));
    }

    /// <summary>已完成运行依然固定知识资源；下架或解除草稿不会隐式解除历史引用。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task CompletedRunsRemainDeletionBlockers()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var graph = Graph(version.Id); var workflow = Guid.NewGuid(); var run = Guid.NewGuid();
        await f.Database.SqlAsync($$"""
            INSERT INTO workflow(workflow_id,device_id,name,schema_version,graph,created_at,updated_at) VALUES ('{{workflow}}','{{f.Database.Device}}','历史运行',1,'{"nodes":[]}',now(),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at) VALUES ('{{run}}','{{workflow}}','{{f.Database.Device}}','{{graph}}','succeeded',now(),now());
            """);
        var preview = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct);
        Assert.False(preview.Allowed); Assert.Equal("workflow_run", Assert.Single(preview.References).Kind);
    }

    /// <summary>远端清理确认前保留正文；失败显式重试，完成后只保留元数据和审计墓碑。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task DeletionRecoversFromRemoteFailureAndRetainsAudit()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var admin = await Admin(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct); await f.Repository.ActivateAsync(f.Library.Id, version.Id, Ct);
        var preview = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct);
        var accepted = await f.Repository.DeleteLibraryAsync(f.Library.Id, admin, Request(preview), Ct);
        Assert.Equal("deleting", accepted.DeletionStatus); Assert.False(accepted.Enabled); Assert.Null(accepted.ActiveVersion);
        var sealedVersion = (await f.Repository.FindVersionMetadataAsync(f.Library.Id, version.Id, Ct))!;
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Repository.SaveProgressAsync(sealedVersion with { Status = "queued" }, Ct));
        await Assert.ThrowsAsync<KnowledgeException>(() => f.Repository.SetEnabledAsync(f.Library.Id, true, Ct));
        await Assert.ThrowsAsync<KnowledgeException>(() => f.ImportAsync("新的正文"));
        f.Storage.FailDelete = true; await f.Processor.ProcessNextAsync(Ct);
        Assert.Equal("delete_failed", (await f.Repository.FindAsync(f.Library.Id, Ct))!.DeletionStatus);
        Assert.True(await f.Database.CountAsync("SELECT count(*) FROM knowledge_document") > 0);
        f.Storage.FailDelete = false; preview = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct);
        Assert.True(preview.Allowed); await f.Repository.DeleteLibraryAsync(f.Library.Id, admin, Request(preview), Ct);
        await f.Processor.ProcessNextAsync(Ct);
        var tombstone = (await f.Repository.FindAsync(f.Library.Id, Ct))!;
        Assert.Equal("deleted", tombstone.DeletionStatus); Assert.NotNull(tombstone.DeletedAt);
        Assert.Empty(await f.Repository.ListAsync(Ct)); Assert.Equal(0, await f.Database.CountAsync("SELECT count(*) FROM knowledge_document"));
        Assert.Equal("deleted", Assert.Single(await f.Repository.ListAsync(Ct, includeDeleted: true)).DeletionStatus);
        Assert.Equal(0, await f.Database.CountAsync("SELECT count(*) FROM knowledge_chunk"));
        Assert.False(f.Storage.Points.ContainsKey(version.Id)); Assert.Equal(1, f.Model.Calls);
        Assert.Equal(new[] { "delete_completed", "delete_retried", "delete_failed", "delete_requested" }, (await f.Repository.AuditAsync(f.Library.Id, Ct)).Select(a => a.Action));
    }

    /// <summary>没有文档的库可直接完成删除，重复请求不会重复审计或清理别的库。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task EmptyDeletionIsIdempotentAndNewDraftPinsCannotEnterSealedLibrary()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var admin = await Admin(f);
        var preview = await f.Repository.PreviewDeletionAsync(f.Library.Id, Ct); var request = Request(preview);
        Assert.Equal("deleted", (await f.Repository.DeleteLibraryAsync(f.Library.Id, admin, request, Ct)).DeletionStatus);
        Assert.Equal("deleted", (await f.Repository.DeleteLibraryAsync(f.Library.Id, admin, request, Ct)).DeletionStatus);
        Assert.Equal(2, (await f.Repository.AuditAsync(f.Library.Id, Ct)).Length);
        await using var other = await KnowledgeFixture.CreateAsync(); var otherAdmin = await Admin(other);
        var version = await other.ImportAsync(); await other.Processor.ProcessNextAsync(Ct);
        await other.Repository.DeleteLibraryAsync(other.Library.Id, otherAdmin, Request(await other.Repository.PreviewDeletionAsync(other.Library.Id, Ct)), Ct);
        await Assert.ThrowsAsync<PostgresException>(() => other.Database.SqlAsync($$"""INSERT INTO knowledge_version_pin(version_id,owner_kind,owner_id) VALUES ('{{version.Id}}','workflow','{{Guid.NewGuid()}}');"""));
    }

    /// <summary>归档保留选定版本原文和文件名映射，不包含向量、模型快照或凭据；停用后拒绝导出。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ExportIsReadableScopedAndContainsNoInternalSnapshots()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var first = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var second = await f.ImportAsync("新版合成文档。"); await f.Processor.ProcessNextAsync(Ct);
        var service = new KnowledgeManagementService(f.Repository);
        await using (var stream = await service.ExportAsync(f.Library.Id, first.Id, Ct))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var json = await reader.ReadToEndAsync(Ct); using var manifest = JsonDocument.Parse(json);
            Assert.Single(manifest.RootElement.GetProperty("versions").EnumerateArray());
            Assert.DoesNotContain("modelSnapshot", json); Assert.DoesNotContain("vector", json); Assert.DoesNotContain("apiKey", json);
            var document = manifest.RootElement.GetProperty("versions")[0].GetProperty("documents")[0];
            Assert.Equal("manual.md", document.GetProperty("name").GetString());
            using var body = new StreamReader(archive.GetEntry(document.GetProperty("path").GetString()!)!.Open());
            Assert.Contains("保修期限", await body.ReadToEndAsync(Ct));
            Assert.Equal(2, archive.Entries.Count);
        }
        await using (var all = await service.ExportAsync(f.Library.Id, null, Ct))
        { using var archive = new ZipArchive(all, ZipArchiveMode.Read); Assert.Equal(3, archive.Entries.Count); }
        await Assert.ThrowsAsync<KnowledgeException>(() => service.ExportAsync(f.Library.Id, Guid.NewGuid(), Ct));
        await f.Repository.SetEnabledAsync(f.Library.Id, false, Ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => service.ExportAsync(f.Library.Id, second.Id, Ct));
        Assert.Equal(2, f.Model.Calls);
    }

    /// <summary>文档新增、移除、修改和未变分别展示，不能跨库选择版本或缺失文档。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task VersionComparisonShowsContentAndRealChunkCounts()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var first = await f.ImportAsync("旧第一行\n保留第二行"); await f.Processor.ProcessNextAsync(Ct);
        var input = new KnowledgeImportRequest([new("manual.md", "新第一行\n保留第二行"), new("new.txt", "新增文档")], 200, 0);
        var next = await f.Service.ImportAsync(f.Library.Id, input with { PreviewDigest = f.Service.Preview(input).Digest }, Ct); await f.Processor.ProcessNextAsync(Ct);
        var service = new KnowledgeManagementService(f.Repository); var comparison = await service.CompareAsync(f.Library.Id, first.Id, next.Id, Ct);
        Assert.Equal("changed", comparison.Documents.Single(d => d.Name == "manual.md").Change);
        Assert.Equal("added", comparison.Documents.Single(d => d.Name == "new.txt").Change);
        Assert.Equal(1, comparison.Documents.Single(d => d.Name == "manual.md").FromChunks);
        var diff = await service.CompareDocumentAsync(f.Library.Id, first.Id, next.Id, "manual.md", 0, 100, Ct);
        Assert.Contains(diff.Lines, line => line.Kind == "removed" && line.Text == "旧第一行" && line.FromLine == 1);
        Assert.Contains(diff.Lines, line => line.Kind == "added" && line.Text == "新第一行" && line.ToLine == 1);
        Assert.Contains(diff.Lines, line => line.Kind == "equal" && line.Text == "保留第二行");
        await Assert.ThrowsAsync<KnowledgeException>(() => service.CompareAsync(f.Library.Id, first.Id, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<KnowledgeException>(() => service.CompareDocumentAsync(f.Library.Id, first.Id, next.Id, "foreign.txt", 0, 100, Ct));
        Assert.Equal("removed", (await service.CompareAsync(f.Library.Id, next.Id, first.Id, Ct)).Documents.Single(d => d.Name == "new.txt").Change);
    }

    /// <summary>分页拼接保留旧新原文所有行，包括空行、尾部换行和不执行的 HTML。</summary>
    [Theory]
    [InlineData("a\nb\n", "a\nc\n")]
    [InlineData("<script>test</script>\n\n尾行", "<img>\n\n尾行\n")]
    [InlineData(null, "新增\n")]
    [InlineData("移除\n", null)]
    public void DiffPaginationReconstructsBothSources(string? before, string? after)
    {
        var first = KnowledgeTextDiff.Page(before, after, 0, 1, Ct); var rows = new List<KnowledgeDiffLine>();
        for (var offset = 0; offset < first.Total; offset++) rows.AddRange(KnowledgeTextDiff.Page(before, after, offset, 1, Ct).Lines);
        Assert.Equal(before?.Split('\n') ?? [], rows.Where(row => row.Kind != "added").Select(row => row.Text));
        Assert.Equal(after?.Split('\n') ?? [], rows.Where(row => row.Kind != "removed").Select(row => row.Text));
    }

    /// <summary>大块替换保持分页有界并明确标记，不分配与两篇文档行数乘积相等的矩阵。</summary>
    [Fact]
    public void LargeDiffUsesBoundedReplacementAndRejectsInvalidPages()
    {
        var before = string.Join('\n', Enumerable.Range(0, 3000).Select(i => "before " + i));
        var after = string.Join('\n', Enumerable.Range(0, 3000).Select(i => "after " + i));
        var page = KnowledgeTextDiff.Page(before, after, 2950, 100, Ct);
        Assert.True(page.Simplified); Assert.Equal(6000, page.Total); Assert.Equal(100, page.Lines.Length);
        Assert.Contains(page.Lines, row => row.Kind == "added" && row.ToLine == 1);
        Assert.Throws<KnowledgeException>(() => KnowledgeTextDiff.Page(before, after, -1, 100, Ct));
    }

    /// <summary>从当前预检构造精确确认，不绕过影响令牌。</summary>
    private static DeleteKnowledgeRequest Request(KnowledgeDeletionPreview preview) => new(preview.Name, "合成管理验收", preview.Revision, preview.ImpactToken);
    /// <summary>构造仅引用指定知识版本的最小图，用于引用触发器测试。</summary>
    private static string Graph(Guid version) => $$$"""{"schemaVersion":1,"nodes":[{"type":"knowledgeRetrieve","data":{"versionId":"{{{version}}}"}}]}""";
    /// <summary>在隔离模式下创建合成管理员，供外键及审计核对。</summary>
    private static async Task<Guid> Admin(KnowledgeFixture f)
    {
        var id = Guid.NewGuid(); await f.Database.SqlAsync($"INSERT INTO creativeflow_admin(admin_id,username,password_hash,created_at,updated_at) VALUES ('{id}','management-test','unused-synthetic-password-hash',now(),now())"); return id;
    }
}
