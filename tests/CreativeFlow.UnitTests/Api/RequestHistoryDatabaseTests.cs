using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Persistence.Migrations;

namespace CreativeFlow.UnitTests.Api;

/// <summary>在独立 PostgreSQL schema 中验证完整迁移与真实查询；不读取任何业务数据。</summary>
public sealed class RequestHistoryDatabaseTests
{
    /// <summary>未指定隔离测试数据库时显式跳过集成测试。</summary>
    public static bool NoDatabase => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_POSTGRES"));

    /// <summary>用户筛选、分页及跨账号画布归属必须由数据库查询保证。</summary>
    [Fact(Skip = "设置 CREATIVEFLOW_TEST_POSTGRES 可运行隔离数据库验证。", SkipWhen = nameof(NoDatabase))]
    public async Task CanvasFilteringPaginationAndOwnershipUseRealQueries()
    {
        await using var database = await Database.CreateAsync();
        var request = database.Request("text");
        await database.Repository.StartAsync(request, TestContext.Current.CancellationToken);
        await database.Repository.FinishAsync(request.Id, "succeeded", "{\"text\":\"完整产物\"}", null, null, TestContext.Current.CancellationToken);
        var foreignCanvas = request with { Id = Guid.NewGuid(), CanvasId = database.OtherCanvas, CreatedAt = request.CreatedAt.AddSeconds(-1) };
        await database.Repository.StartAsync(foreignCanvas, TestContext.Current.CancellationToken);
        await database.Repository.FinishAsync(foreignCanvas.Id, "failed", null, "受控失败", null, TestContext.Current.CancellationToken);
        var page = await database.Repository.ListAsync(new("canvas", database.Account, "用户甲", null, 0, 1), TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Total);
        Assert.Equal(request.Id.ToString(), Assert.Single(page.Items).Id);
        Assert.Equal(database.Canvas.ToString(), page.Items[0].ProjectId);
        var second = await database.Repository.ListAsync(new("canvas", database.Account, null, null, 1, 1), TestContext.Current.CancellationToken);
        Assert.Equal(foreignCanvas.Id.ToString(), Assert.Single(second.Items).Id);
        Assert.Null(second.Items[0].ProjectId);
        Assert.Empty((await database.Repository.ListAsync(new("canvas", database.OtherAccount, null, null, 0, 30), TestContext.Current.CancellationToken)).Items);
        var failed = await database.Repository.ListAsync(new("canvas", null, "a@example.invalid", "failed", 0, 30), TestContext.Current.CancellationToken);
        Assert.Equal(foreignCanvas.Id.ToString(), Assert.Single(failed.Items).Id);
        Assert.Empty((await database.Repository.ListAsync(new("canvas", null, "' OR 1=1 --", null, 0, 30), TestContext.Current.CancellationToken)).Items);
        var detail = await database.Repository.FindAsync("canvas", request.Id, TestContext.Current.CancellationToken);
        Assert.Contains("完整产物", detail!.OutputJson);
        Assert.NotNull(detail.Summary.FinishedAt);
    }

    /// <summary>旧视频准确显示一次，工作流内的视频排除，新视频终态跟随原任务。</summary>
    [Fact(Skip = "设置 CREATIVEFLOW_TEST_POSTGRES 可运行隔离数据库验证。", SkipWhen = nameof(NoDatabase))]
    public async Task VideoHistoryDeduplicatesAndTracksTerminalStatus()
    {
        await using var database = await Database.CreateAsync();
        var oldVideo = Guid.NewGuid();
        var workflowVideo = Guid.NewGuid();
        var newVideo = Guid.NewGuid();
        await database.SqlAsync($$"""
            INSERT INTO video_job (job_id, device_id, model_key, status, request) VALUES
            ('{{oldVideo}}', '{{database.Device}}', 'seedance', 'cancelled', '{}'),
            ('{{workflowVideo}}', '{{database.Device}}', 'seedance', 'succeeded', '{"workflowRunId":"test-run"}'),
            ('{{newVideo}}', '{{database.Device}}', 'seedance', 'running', '{}');
            """);
        var request = database.Request("video");
        await database.Repository.StartAsync(request, TestContext.Current.CancellationToken);
        await database.Repository.FinishAsync(request.Id, "running", null, null, newVideo, TestContext.Current.CancellationToken);
        var page = await database.Repository.ListAsync(new("canvas", null, null, null, 0, 30), TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Total);
        Assert.True(page.Items.Single(item => item.Id == oldVideo.ToString()).Historical);
        Assert.DoesNotContain(page.Items, item => item.Id == workflowVideo.ToString() || item.Id == newVideo.ToString());
        await database.SqlAsync($"UPDATE video_job SET status = 'failed', error = '供应商失败', updated_at = now() WHERE job_id = '{newVideo}'");
        var detail = await database.Repository.FindAsync("canvas", request.Id, TestContext.Current.CancellationToken);
        Assert.Equal("failed", detail!.Summary.Status);
        Assert.Equal("供应商失败", detail.Summary.Error);
        Assert.NotNull(detail.Summary.FinishedAt);
        await database.SqlAsync($"UPDATE video_job SET status = 'succeeded', error = NULL, asset_id = '{database.Asset}', updated_at = now() WHERE job_id = '{newVideo}'");
        detail = await database.Repository.FindAsync("canvas", request.Id, TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", detail!.Summary.Status);
        Assert.Contains(database.Asset.ToString(), detail.OutputJson);
    }

    /// <summary>工作流按运行分页并读取节点快照，素材支持同账号跨设备但拒绝其他账号。</summary>
    [Fact(Skip = "设置 CREATIVEFLOW_TEST_POSTGRES 可运行隔离数据库验证。", SkipWhen = nameof(NoDatabase))]
    public async Task WorkflowRunsAndAssetsKeepAccountBoundaries()
    {
        await using var database = await Database.CreateAsync();
        var run = Guid.NewGuid();
        var workflow = Guid.NewGuid();
        await database.SqlAsync($$"""
            INSERT INTO workflow (workflow_id, device_id, name, created_at, updated_at)
                VALUES ('{{workflow}}', '{{database.Device}}', '测试工作流', now(), now());
            INSERT INTO workflow_run (run_id, workflow_id, device_id, graph_snapshot, status, created_at, updated_at, finished_at)
                VALUES ('{{run}}', '{{workflow}}', '{{database.Device}}', '{"nodes":[]}', 'cancelled', now(), now(), now());
            INSERT INTO workflow_node_run (run_id, node_id, node_type, status, is_reused, output)
                VALUES ('{{run}}', 'node-1', 'imageGenerate', 'succeeded', true, '{"texts":["节点结果"]}');
            """);
        var page = await database.Repository.ListAsync(new("workflow", database.Account, "a@example.invalid", "cancelled", 0, 30), TestContext.Current.CancellationToken);
        Assert.Equal(run.ToString(), Assert.Single(page.Items).Id);
        Assert.Empty((await database.Repository.ListAsync(new("canvas", null, null, null, 0, 30), TestContext.Current.CancellationToken)).Items);
        var detail = await database.Repository.FindAsync("workflow", run, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(detail!.Nodes).Reused);
        Assert.Contains("节点结果", detail.Nodes[0].OutputJson);
        var artifacts = await database.Repository.FindArtifactsAsync(database.Device, database.Account,
            [database.Asset, database.OtherAsset, database.SecondDeviceAsset], TestContext.Current.CancellationToken);
        Assert.Equal(2, artifacts.Count);
        Assert.DoesNotContain(artifacts, asset => asset.AssetId == database.OtherAsset);
    }

    /// <summary>每个用例拥有独立 schema，结束时仅清理自己创建的对象。</summary>
    private sealed class Database : IAsyncDisposable
    {
        /// <summary>测试连接池，仅指向当前 schema。</summary>
        private NpgsqlDataSource _source = null!;
        /// <summary>只由随机 UUID 构成的内部 schema 名称。</summary>
        private readonly string _schema = "request_test_" + Guid.NewGuid().ToString("N");
        /// <summary>甲账号。</summary>
        public Guid Account { get; } = Guid.NewGuid();
        /// <summary>乙账号。</summary>
        public Guid OtherAccount { get; } = Guid.NewGuid();
        /// <summary>甲主设备。</summary>
        public Guid Device { get; } = Guid.NewGuid();
        /// <summary>甲第二设备。</summary>
        private Guid SecondDevice { get; } = Guid.NewGuid();
        /// <summary>乙设备。</summary>
        private Guid OtherDevice { get; } = Guid.NewGuid();
        /// <summary>甲画布。</summary>
        public Guid Canvas { get; } = Guid.NewGuid();
        /// <summary>乙画布。</summary>
        public Guid OtherCanvas { get; } = Guid.NewGuid();
        /// <summary>甲主设备素材。</summary>
        public Guid Asset { get; } = Guid.NewGuid();
        /// <summary>甲第二设备素材。</summary>
        public Guid SecondDeviceAsset { get; } = Guid.NewGuid();
        /// <summary>乙素材。</summary>
        public Guid OtherAsset { get; } = Guid.NewGuid();
        /// <summary>被测真实仓储。</summary>
        public RequestHistoryRepository Repository { get; private set; } = null!;

        /// <summary>应用完整嵌入迁移并创建虚构数据；初始化异常时同样释放 schema。</summary>
        public static async Task<Database> CreateAsync()
        {
            var database = new Database();
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_POSTGRES")) { SearchPath = database._schema };
            database._source = NpgsqlDataSourceFactory.Create(connection.ConnectionString);
            try
            {
                await database.SqlAsync($"CREATE SCHEMA {database._schema}");
                // 从发布程序集读取原始迁移，确认新增 view 能与全部既有表结构共存。
                var assembly = typeof(MigrationRunner).Assembly;
                foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("CreativeFlow.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    using var stream = assembly.GetManifestResourceStream(name)!;
                    using var reader = new StreamReader(stream);
                    await database.SqlAsync(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
                }
                database.Repository = new(database._source);
                await database.SeedAsync();
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        /// <summary>仅插入随机标识和虚构用户，不使用真实账号或供应商。</summary>
        private Task SeedAsync() => SqlAsync($$"""
            INSERT INTO creativeflow_account (account_id, display_name, email) VALUES
                ('{{Account}}', '用户甲', 'a@example.invalid'), ('{{OtherAccount}}', '用户乙', 'b@example.invalid');
            INSERT INTO device (device_id, fp_exact, fp_coarse, ip_prefix, signals, sig_version) VALUES
                ('{{Device}}', '\x01', '\x01', '127.0.0.1', '{}', 1),
                ('{{SecondDevice}}', '\x02', '\x02', '127.0.0.1', '{}', 1),
                ('{{OtherDevice}}', '\x03', '\x03', '127.0.0.1', '{}', 1);
            INSERT INTO creativeflow_account_device (account_id, device_id) VALUES
                ('{{Account}}', '{{Device}}'), ('{{Account}}', '{{SecondDevice}}'), ('{{OtherAccount}}', '{{OtherDevice}}');
            INSERT INTO canvas (canvas_id, device_id, name) VALUES
                ('{{Canvas}}', '{{Device}}', '甲画布'), ('{{OtherCanvas}}', '{{OtherDevice}}', '乙画布');
            INSERT INTO storage_object(storage_path,sha256,media_type,byte_size,state,retain_until)
                VALUES('objects/aa/'||repeat('a',64),repeat('a',64),'video/mp4',4,'ready',now()+interval '1 hour');
            INSERT INTO canvas_asset (asset_id, device_id, kind, media_type, storage_path, byte_size) VALUES
                ('{{Asset}}', '{{Device}}', 'video', 'video/mp4', 'objects/aa/'||repeat('a',64), 4),
                ('{{SecondDeviceAsset}}', '{{SecondDevice}}', 'image', 'image/png', 'objects/aa/'||repeat('a',64), 4),
                ('{{OtherAsset}}', '{{OtherDevice}}', 'image', 'image/png', 'objects/aa/'||repeat('a',64), 4);
            """);

        /// <summary>创建带上下文的虚构生成请求。</summary>
        public GenerationHistoryStart Request(string kind) => new(Guid.NewGuid(), Device, Account, Canvas, "node-1", kind,
            null, "test-model", "{\"prompt\":\"测试输入\"}", DateTimeOffset.UtcNow);

        /// <summary>执行仅包含受控标识和常量的测试 SQL。</summary>
        public async Task SqlAsync(string sql)
        {
            await using var command = _source.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>测试终止或取消后仍删除本次 schema 并关闭连接池。</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = _source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally { await _source.DisposeAsync(); }
        }
    }
}
