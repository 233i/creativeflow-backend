using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>在完整迁移后的随机隔离 schema 内验证真实工作流认领事务。</summary>
public sealed class WorkflowExternalApiDatabaseTests
{
    /// <summary>复用已有的完整迁移测试夹具，未显式提供测试库时跳过。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>多轮并发竞争中，已提交运行数始终遵守发布的工作流并发上限。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConcurrentClaimsNeverExceedWorkflowCapacity(int capacity)
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var deployment = await CreateQueueAsync(db, capacity, 100);
        var runs = new WorkflowRunRepository(Connections(db));

        // 同步启动多个认领者并反复恢复相同队列，使语句快照与锁交接的竞争持续出现。
        for (var round = 0; round < 50; round++)
        {
            await db.SqlAsync($"UPDATE workflow_run SET status = 'queued', started_at = NULL WHERE workflow_id = '{deployment.WorkflowId.Value}'");
            var claims = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct)));
            Assert.InRange(claims.Count(run => run is not null), 1, capacity);
            Assert.Equal(claims.Count(run => run is not null),
                await db.CountAsync("SELECT count(*) FROM workflow_run WHERE status = 'running'"));
        }
    }

    /// <summary>其他工作流持有准入锁时仍可执行空闲工作流，取消已认领任务会释放额度。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task LockedWorkflowDoesNotBlockOtherQueuesAndCancellationReleasesCapacity()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var first = await CreateQueueAsync(db, 1, 2);
        var second = await CreateQueueAsync(db, 1, 1);
        var runs = new WorkflowRunRepository(Connections(db));
        await using var connection = await db.Source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended('workflow-external-execution:' || $1::text, 0))";
            command.Parameters.AddWithValue(first.WorkflowId.Value);
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(second.WorkflowId, (await runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct))!.WorkflowId);
        await transaction.CommitAsync(Ct);
        var claimed = (await runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct))!;
        Assert.Equal(first.WorkflowId, claimed.WorkflowId);
        Assert.Null(await runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct));
        Assert.True(await runs.CancelExternalAsync(first.WorkflowId, claimed.Id, DateTimeOffset.UtcNow, Ct));
        Assert.Equal(first.WorkflowId, (await runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct))!.WorkflowId);
    }

    /// <summary>交互式运行继续使用自己的活动运行规则，不被已满的外部额度阻塞。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task InteractiveRunsRemainIndependentOfExternalCapacity()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var deployment = await CreateQueueAsync(db, 1, 2);
        var runs = new WorkflowRunRepository(Connections(db));
        Assert.NotNull(await runs.ClaimNextAsync(DateTimeOffset.UtcNow, Ct));
        var now = DateTimeOffset.UtcNow;
        var interactive = new WorkflowRun(WorkflowRunId.New(), deployment.WorkflowId, new DeviceId(db.Device), "{}",
            null, WorkflowRunStatus.Queued, null, now, now, null, null);
        Assert.True(await runs.InsertAsync(interactive, [], Ct));

        Assert.Equal(interactive.Id, (await runs.ClaimNextAsync(now, Ct))!.Id);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_run WHERE status = 'running' AND trigger_type = 'api'"));
    }

    /// <summary>播种完整发布元数据及未执行的队列，不访问模型或业务数据库。</summary>
    private static async Task<WorkflowDeployment> CreateQueueAsync(ImageJobDatabase db, int capacity, int count)
    {
        var factory = Connections(db);
        var device = new DeviceId(db.Device);
        var workflowId = WorkflowId.New();
        var now = DateTimeOffset.UtcNow;
        await new WorkflowRepository(factory).InsertAsync(new Workflow(workflowId, device, "queue test", 1, "{}", now, now), Ct);
        var external = new WorkflowExternalApiRepository(factory);
        var deployment = (await external.PublishAsync(device,
            new WorkflowDeployment(WorkflowDeploymentId.New(), workflowId, 0, 1, "{}", "[]", "[]", "test",
                WorkflowDeploymentStatus.Published, capacity, now, now, null), Ct))!;
        var key = new WorkflowApiKey(WorkflowApiKeyId.New(), workflowId, "test", Guid.NewGuid().ToString("N"), "test",
            new byte[32], true, null, null, now, null);
        await external.InsertApiKeyAsync(key, Ct);
        await db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at,
                trigger_type,deployment_id,api_key_id,request_id,idempotency_key,request_hash,input_json)
            SELECT gen_random_uuid(),'{{workflowId.Value}}','{{device.Value}}','{}','queued',now(),now(),
                'api','{{deployment.Id.Value}}','{{key.Id.Value}}','req_test',gen_random_uuid()::text,decode('00','hex'),'{}'
            FROM generate_series(1,{{count}});
            """);
        return deployment;
    }

    /// <summary>为当前隔离测试账号创建与生产一致的连接工厂。</summary>
    private static CreativeFlowConnectionFactory Connections(ImageJobDatabase db) =>
        new(db.Source, new AccountContextStub { AccountId = new AccountId(db.Account) });

    /// <summary>只保存本次隔离测试账号范围。</summary>
    private sealed class AccountContextStub : IAccountContext
    {
        /// <summary>保存数据库测试当前作用域的账号标识，供可访问设备范围验证使用。</summary>
        public AccountId? AccountId { get; set; }
    }
}
