using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>在隔离数据库验证单引擎不依赖旧绑定，且原受理约束和原子写入继续生效。</summary>
public sealed class SingleEnginePersistenceTests
{
    /// <summary>未指定隔离 PostgreSQL 时跳过真实数据库测试。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>全新会话及运行不写绑定；即使旧元数据无法写入也不影响业务受理与执行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AgentDoesNotReadOrWriteRetiredBindings()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        var db = fixture.Database;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM agent_session_runtime"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM agent_run_runtime"));
        // 旧记录故意标为另一引擎及未知版本；执行只由组合根的 MAF 端口决定。
        await db.SqlAsync($"INSERT INTO agent_run_runtime VALUES ('{fixture.Run.Id}', 'legacy', 999)");
        await db.SqlAsync("ALTER TABLE agent_run_runtime ADD CONSTRAINT no_new_binding CHECK (false) NOT VALID");
        await fixture.ExecuteAsync();
        Assert.Equal(AgentRunStatus.Succeeded, (await fixture.ReadRunAsync()).Status);
        Assert.Single(fixture.Models.Calls);
        var followup = fixture.Run with { Id = AgentRunId.New() };
        await fixture.Agents.InsertRunAsync(followup, ct);
        Assert.NotNull(await fixture.Agents.FindRunByIdAsync(followup.Id, ct));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM agent_run_runtime"));
    }

    /// <summary>工作流继续运行保存来源和节点复用信息，活动运行唯一约束仍拒绝重复受理。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task WorkflowKeepsResumeAndDuplicateSemanticsWithoutBindings()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var connection = new CreativeFlowConnectionFactory(db.Source, new AccountContext { AccountId = new AccountId(db.Account) });
        var runs = new WorkflowRunRepository(connection);
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(WorkflowId.New(), new DeviceId(db.Device), "Single engine", 1, "{}", now, now);
        await new WorkflowRepository(connection).InsertAsync(workflow, ct);
        var source = new WorkflowRun(WorkflowRunId.New(), workflow.Id, workflow.DeviceId, "{}", null, WorkflowRunStatus.Succeeded, null, now, now, now, now);
        Assert.True(await runs.InsertAsync(source, [], ct));
        await db.SqlAsync("ALTER TABLE workflow_run_runtime ADD CONSTRAINT no_new_binding CHECK (false) NOT VALID");
        var continuation = source with { Id = WorkflowRunId.New(), ResumedFromRunId = source.Id, Status = WorkflowRunStatus.Queued, FinishedAt = null };
        var node = new WorkflowNodeRun(continuation.Id, "reused", "text", WorkflowNodeRunStatus.Succeeded, true, 1,
            "{\"texts\":[\"saved\"],\"images\":[],\"videos\":[]}", null, now, now);
        Assert.True(await runs.InsertAsync(continuation, [node], ct));
        var saved = await runs.FindAsync(workflow.DeviceId, workflow.Id, continuation.Id, ct);
        Assert.Equal(source.Id, saved!.ResumedFromRunId);
        Assert.True(Assert.Single(await runs.ListNodesAsync(continuation.Id, ct)).IsReused);
        var duplicate = continuation with { Id = WorkflowRunId.New() };
        Assert.False(await runs.InsertAsync(duplicate, [], ct));
        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM workflow_run WHERE run_id='{duplicate.Id}'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM workflow_run_runtime"));
    }

    /// <summary>节点插入中途失败时，原事务整体回滚运行及已插入节点，不留下半个已受理任务。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task NodeFailureStillRollsBackWorkflowAcceptance()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var connection = new CreativeFlowConnectionFactory(db.Source, new AccountContext());
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(WorkflowId.New(), new DeviceId(db.Device), "Atomic fixture", 1, "{}", now, now);
        await new WorkflowRepository(connection).InsertAsync(workflow, ct);
        var run = new WorkflowRun(WorkflowRunId.New(), workflow.Id, workflow.DeviceId, "{}", null, WorkflowRunStatus.Queued, null, now, now, null, null);
        var node = new WorkflowNodeRun(run.Id, "duplicate", "text", WorkflowNodeRunStatus.Queued, false, null, null, null, null, null);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => new WorkflowRunRepository(connection).InsertAsync(run, [node, node], ct));
        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM workflow_run WHERE run_id='{run.Id}'"));
        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM workflow_node_run WHERE run_id='{run.Id}'"));
    }
}
