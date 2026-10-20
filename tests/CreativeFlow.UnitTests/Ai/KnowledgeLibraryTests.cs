using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>解析与真实数据库检查点测试，所有外部调用均为合成替身。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeLibraryTests
{
    /// <summary>没有显式隔离测试库时禁止借用开发配置。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>稳定分段保留行号、中文与 Unicode，不执行 Markdown 链接。</summary>
    [Fact]
    public void PreviewIsStableAndPreservesSource()
    {
        var request = new KnowledgeImportRequest([new("manual.md", "# 合成手册\n保修期限：两年。\n[link](http://169.254.169.254/secret)\n<script>alert(1)</script>" )]);
        var preview = KnowledgeTextParser.Preview(request);
        Assert.Equal(preview.Digest, KnowledgeTextParser.Preview(request).Digest);
        Assert.Equal(preview.Chunks[0].Id, KnowledgeTextParser.Preview(request).Chunks[0].Id);
        Assert.Equal(1, preview.Chunks[0].StartLine); Assert.Equal(4, preview.Chunks[0].EndLine);
        Assert.Contains("<script>", preview.Chunks[0].Text);
        Assert.NotEqual(preview.Digest, KnowledgeTextParser.Preview(request with { Overlap = 50 }).Digest);
    }

    /// <summary>空文档、伪装格式、错误 UTF-8 和非法文件名都在向量化前拒绝。</summary>
    [Theory]
    [InlineData("x.txt", "")]
    [InlineData("x.pdf", "plain text")]
    [InlineData("x.txt", "%PDF-1.7")]
    [InlineData("../x.txt", "data")]
    [InlineData("x.txt", "binary\0data")]
    public void InvalidFilesAreRejected(string name, string text) =>
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(new([new(name, text)])));

    /// <summary>大小、片段数、同名文档和分段参数均有硬上限。</summary>
    [Fact]
    public void ResourceLimitsAndUnicodeAreEnforced()
    {
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(new([new("x.txt", new string('中', 30000))])));
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(new([new("x.txt", "a"), new("X.TXT", "b")])));
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(new([new("x.txt", "a")], 200, 100)));
        Assert.Throws<KnowledgeException>(() => KnowledgeTextParser.Preview(new([new("x.txt", new string('a', 30000))], 200, 0)));
        var text = string.Concat(Enumerable.Repeat("😀", 201));
        var preview = KnowledgeTextParser.Preview(new([new("emoji.txt", text)], 201, 10));
        Assert.All(preview.Chunks, chunk => Assert.Equal(chunk.Text, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(chunk.Text))));
    }

    /// <summary>语义分段保留标题、列表、代码和表格，并覆盖规范化正文的全部偏移区间。</summary>
    [Fact]
    public void SemanticChunksPreserveStructureAndCoverSource()
    {
        var text = "# 产品手册\n\n## 退款规则\n\n退款需要在签收后七日内提出。客服会核验订单号。\n\n- 保留外包装\n- 提供订单截图\n\n```json\n{\"ok\":true}\n```\n\n| 状态 | 处理时限 |\n| --- | --- |\n| 已签收 | 七日 |\n";
        var preview = KnowledgeTextParser.Preview(new([new("manual.md", text)], 200, 40));
        Assert.Contains(preview.Chunks, chunk => chunk.SectionPath.Contains("退款规则", StringComparison.Ordinal));
        Assert.Contains(preview.Chunks, chunk => chunk.BlockType is "list" or "code" or "table" or "mixed");
        Assert.All(preview.Chunks, chunk => Assert.InRange(chunk.EndOffset - chunk.StartOffset, 1, 200));
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var covered = new bool[normalized.Length];
        foreach (var chunk in preview.Chunks)
            for (var i = chunk.StartOffset; i < chunk.EndOffset; i++) covered[i] = true;
        Assert.All(covered, Assert.True);
        var setext = KnowledgeTextParser.Preview(new([new("setext.md", "前言。\n\n退款说明\n===\n正文内容。")], 200, 0));
        Assert.Contains(setext.Chunks, chunk => chunk.SectionPath == "退款说明" && chunk.BlockType is "heading" or "mixed");
    }

    /// <summary>超长无标点段落先进入硬切兜底，代理对不被拆开且续片段带有明确标记。</summary>
    [Fact]
    public void OversizedParagraphUsesSafeContinuationChunks()
    {
        var text = string.Concat(Enumerable.Repeat("连续内容😀", 180));
        var preview = KnowledgeTextParser.Preview(new([new("long.txt", text)], 200, 40));
        Assert.True(preview.Chunks.Length > 1);
        Assert.Contains(preview.Chunks, chunk => chunk.Continuation);
        Assert.All(preview.Chunks, chunk => Assert.DoesNotContain('\uFFFD', chunk.Text));
    }

    /// <summary>重复确认只产生一个索引版本；只有写完且显式激活后才可检索。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ImportDeduplicatesAndRequiresExplicitActivation()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await fixture.ImportAsync(); var duplicate = await fixture.ImportAsync();
        Assert.Equal(first.Id, duplicate.Id); Assert.Equal(0, fixture.Model.Calls);
        Assert.False(await fixture.Repository.ActivateAsync(fixture.Library.Id, first.Id, ct));
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal("ready", (await fixture.Repository.FindVersionAsync(fixture.Library.Id, first.Id, ct))!.Status);
        Assert.Null((await fixture.Repository.FindAsync(fixture.Library.Id, ct))!.ActiveVersion);
        Assert.True(await fixture.Repository.ActivateAsync(fixture.Library.Id, first.Id, ct));
        var response = await fixture.Service.SearchAsync(fixture.Library.Id, new("保修多久？"), ct);
        Assert.Equal(first.Id, response.VersionId); Assert.Contains("两年", Assert.Single(response.Hits).Chunk.Text);
        Assert.Equal(2, fixture.Model.Calls);
        Assert.Null(await fixture.Repository.FindVersionAsync(Guid.NewGuid(), first.Id, ct));
    }

    /// <summary>部分写入失败保留全部向量；恢复只重写稳定点且不破坏当前可用版本。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task FailedCandidateCanRewriteWithoutEmbeddingAgain()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var old = await fixture.ImportAsync(); await fixture.Processor.ProcessNextAsync(ct);
        await fixture.Repository.ActivateAsync(fixture.Library.Id, old.Id, ct);
        var candidate = await fixture.ImportAsync("新版合成手册：保修三年。"); fixture.Storage.FailWrite = true;
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal(2, fixture.Model.Calls);
        Assert.Equal(old.Id, (await fixture.Repository.FindAsync(fixture.Library.Id, ct))!.ActiveVersion);
        Assert.False(await fixture.Repository.ActivateAsync(fixture.Library.Id, candidate.Id, ct));
        fixture.Storage.FailWrite = false;
        await fixture.Service.RecoverAsync(fixture.Library.Id, candidate.Id, "continue", ct);
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal(2, fixture.Model.Calls); Assert.Single(fixture.Storage.Points[candidate.Id]);
        Assert.Equal("ready", (await fixture.Repository.FindVersionAsync(fixture.Library.Id, candidate.Id, ct))!.Status);
    }

    /// <summary>超时批次不自动重发；人工确认后只重新向量化缺少检查点的片段。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task UnknownBatchRequiresExplicitConfirmationAndKeepsCompletedBatches()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var version = await fixture.ImportAsync(new string('a', 450)); fixture.Model.FailOnCall = 2;
        await fixture.Processor.ProcessNextAsync(ct); await fixture.Processor.ProcessNextAsync(ct);
        var saved = (await fixture.Repository.FindVersionAsync(fixture.Library.Id, version.Id, ct))!;
        Assert.Equal("unknown", saved.Status); Assert.Single(KnowledgeLibraryService.Vectors(saved)); Assert.Equal(2, fixture.Model.Calls);
        await Assert.ThrowsAsync<KnowledgeException>(() => fixture.Service.RecoverAsync(fixture.Library.Id, version.Id, "continue", ct));
        await fixture.Service.RecoverAsync(fixture.Library.Id, version.Id, "reembedUnknown", ct);
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal(4, fixture.Model.Calls); Assert.Equal(3, fixture.Storage.Points[version.Id].Count);
    }

    /// <summary>进程丢失发送后的检查点时转换为待核对，不推测上游未受理。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RestartStopsAmbiguousInFlightBatchAndLocksAreExclusive()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var version = await fixture.ImportAsync(); var saved = (await fixture.Repository.FindVersionAsync(fixture.Library.Id, version.Id, ct))!;
        await fixture.Repository.SaveProgressAsync(saved with { Status = "embedding" }, ct);
        await using (var lease = await fixture.Repository.TryWorkerLockAsync(ct))
        { Assert.NotNull(lease); Assert.Null(await fixture.Repository.TryWorkerLockAsync(ct)); await fixture.Processor.ProcessNextAsync(ct); }
        Assert.Equal("embedding", (await fixture.Repository.FindVersionAsync(fixture.Library.Id, version.Id, ct))!.Status);
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal("unknown", (await fixture.Repository.FindVersionAsync(fixture.Library.Id, version.Id, ct))!.Status); Assert.Equal(0, fixture.Model.Calls);
    }

    /// <summary>停用、查询中撤权、跨版本 ID 和模型变化不能产生可用结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RevocationForeignResultsAndModelChangesFailClosed()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var version = await fixture.ImportAsync(); await fixture.Processor.ProcessNextAsync(ct);
        await fixture.Repository.ActivateAsync(fixture.Library.Id, version.Id, ct);
        fixture.Storage.ForeignHit = true;
        await Assert.ThrowsAsync<KnowledgeException>(() => fixture.Service.SearchAsync(fixture.Library.Id, new("问题"), ct));
        fixture.Storage.ForeignHit = false;
        fixture.Storage.OnSearch = () => fixture.Repository.SetEnabledAsync(fixture.Library.Id, false, ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => fixture.Service.SearchAsync(fixture.Library.Id, new("问题"), ct));
        var calls = fixture.Model.Calls;
        await Assert.ThrowsAsync<KnowledgeException>(() => fixture.Service.SearchAsync(fixture.Library.Id, new("问题"), ct)); Assert.Equal(calls, fixture.Model.Calls);
        await fixture.Repository.SetEnabledAsync(fixture.Library.Id, true, ct);
        var newer = await fixture.ImportAsync("new version"); fixture.Model.Revision++;
        await fixture.Processor.ProcessNextAsync(ct);
        Assert.Equal(calls, fixture.Model.Calls); Assert.Equal("failed", (await fixture.Repository.FindVersionAsync(fixture.Library.Id, newer.Id, ct))!.Status);
    }

    /// <summary>新增知识引用不能使原有模型删除接口因外键约束失败，也不能级联销毁知识记录。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KnowledgeReferencesDoNotChangeExistingModelDeletion()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var version = await fixture.ImportAsync();
        await fixture.Database.SqlAsync($"DELETE FROM ai_model WHERE model_id = '{fixture.ModelId}'");
        Assert.NotNull(await fixture.Repository.FindAsync(fixture.Library.Id, ct));
        Assert.NotNull(await fixture.Repository.FindVersionAsync(fixture.Library.Id, version.Id, ct));
        Assert.Equal(0, await fixture.Database.CountAsync($"SELECT count(*) FROM ai_model WHERE model_id = '{fixture.ModelId}'"));
    }
}
