using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>通过真实 HTTP、PostgreSQL 持久队列与唯一 MAF 验证分支终态和局部重跑。</summary>
[Collection("Knowledge index database lock")]
public sealed class WorkflowBranchHttpTests
{
    /// <summary>没有明确隔离测试库时不接触开发或生产数据。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>传递本条测试的取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>未命中模型分支持久化为 skipped，重跑该分支也不会绕过原条件；下游复用不再次调用。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("hit", 1, "fallback")][InlineData("miss", 0, "hit")]
    public async Task BranchStatesPersistAndPartialRerunRespectsTheDecision(string question, int calls, string skipped)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var draft = await f.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "Branch fixture", schemaVersion = 1, graph = WorkflowBranchFixtures.Graph() });
        var path = "/api/v1/admin/workflow-apis/" + draft.Id;
        var first = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question } });
        var result = await f.WaitAsync(path + "/runs/" + first.Id);
        Assert.Equal(calls, f.Models.Calls); AssertSkipped(result, skipped);
        var partial = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{first.Id}/partial-rerun", new { fromNodeId = "merge" });
        var next = await f.WaitAsync(path + "/runs/" + partial.Id);
        Assert.Equal(calls, f.Models.Calls); AssertSkipped(next, skipped);
        var skippedRerun = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{partial.Id}/partial-rerun", new { fromNodeId = skipped });
        AssertSkipped(await f.WaitAsync(path + "/runs/" + skippedRerun.Id), skipped); Assert.Equal(calls, f.Models.Calls);
        var decisionRerun = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{first.Id}/partial-rerun", new { fromNodeId = "condition" });
        await f.WaitAsync(path + "/runs/" + decisionRerun.Id); Assert.Equal(calls * 2, f.Models.Calls);
        Assert.Contains(f.Events, entry => entry.NodeId == skipped && entry.NodeStatus == "skipped");
    }

    /// <summary>匹配模型失败不切换默认分支，不因继续运行重发；显式新输入可以选择不调用模型的路径。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task FailureDoesNotRunFallbackOrBlindlyRetry()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        f.Models.Fail = true;
        var draft = await f.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "Failure fixture", schemaVersion = 1, graph = WorkflowBranchFixtures.Graph() });
        var path = "/api/v1/admin/workflow-apis/" + draft.Id;
        var first = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "hit" } });
        var failed = await f.WaitAsync(path + "/runs/" + first.Id, "failed");
        AssertSkipped(failed, "fallback"); Assert.Equal(1, f.Models.Calls);
        using var resume = await f.Client.PostAsJsonAsync(path + $"/runs/{first.Id}/resume", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); Assert.Equal(1, f.Models.Calls);
        var fresh = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "miss" } });
        await f.WaitAsync(path + "/runs/" + fresh.Id); Assert.Equal(1, f.Models.Calls);
    }

    /// <summary>真实 MVC/SQL/MAF 链在知识命中时保留引用，空命中跳过问答且只返回默认文本。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(true)][InlineData(false)]
    public async Task KnowledgeBranchKeepsEvidenceAndDoesNotCallTheUnselectedModel(bool hit)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        if (!hit) f.Index.Points[f.VersionId].Clear();
        var graph = WorkflowBranchFixtures.KnowledgeGraph(f.LibraryId, f.VersionId);
        var draft = await f.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "Synthetic knowledge branch", schemaVersion = 1, graph });
        var path = "/api/v1/admin/workflow-apis/" + draft.Id;
        var first = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修多久？" } });
        var result = await f.WaitAsync(path + "/runs/" + first.Id);
        var fields = result["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "end")!["output"]!["fields"]!.AsObject();
        Assert.Equal(hit ? 1 : 0, f.Models.Calls); Assert.Equal(2, f.Embeddings.Calls);
        Assert.Single(fields); AssertSkipped(result, hit ? "fallback" : "answer");
        if (hit) { Assert.True(fields["answer"]!["grounded"]!.GetValue<bool>()); Assert.Single(fields["answer"]!["citations"]!.AsArray()); }
        else Assert.Contains("No knowledge evidence", fields["fallback"]!.GetValue<string>());
        var next = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{first.Id}/partial-rerun", new { fromNodeId = "condition" });
        await f.WaitAsync(path + "/runs/" + next.Id); Assert.Equal(2, f.Embeddings.Calls); Assert.Equal(hit ? 2 : 0, f.Models.Calls);
    }

    /// <summary>跳过状态不能携带成功输出或开始时间，也不能伪称已复用付费结果。</summary>
    private static void AssertSkipped(JsonNode run, string id)
    {
        var node = run["nodes"]!.AsArray().Single(item => item!["nodeId"]!.GetValue<string>() == id)!;
        Assert.Equal("skipped", node["status"]!.GetValue<string>()); Assert.Null(node["startedAt"]); Assert.Null(node["output"]);
        Assert.False(node["isReused"]!.GetValue<bool>());
    }
}
