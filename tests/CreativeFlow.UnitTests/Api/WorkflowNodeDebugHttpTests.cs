using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>通过真实 MVC、隔离 PostgreSQL、持久队列和 MAF 验证独立调试，不访问实际上游。</summary>
[Collection("Knowledge index database lock")]
public sealed class WorkflowNodeDebugHttpTests
{
    /// <summary>缺少显式隔离数据库时禁止测试连接开发数据。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>管理员工作流入口。</summary>
    private const string Base = "/api/v1/admin/workflow-apis";

    /// <summary>提取后的检索词与原始问题不同，长证据仍可持久化、独立调试及局部复用，不重新提取或向量化。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task LargeEvidenceCheckpointIsReusedByDebugAndPartialRerun()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        var repository = new KnowledgeRepository(f.Db.Source);
var service = new KnowledgeLibraryService(repository, f.Embeddings, f.Index, new(repository, f.Embeddings, f.Index, new KnowledgeFixture.Reranker()));
        var input = new KnowledgeImportRequest([new("synthetic-long.md", new string('文', 12 * 800))], 800, 0);
        var version = await service.ImportAsync(f.LibraryId, input with { PreviewDigest = service.Preview(input).Digest }, Ct);
        await new KnowledgeIndexProcessor(repository, f.Embeddings, f.Index).ProcessNextAsync(Ct);
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, version.Id);
        graph["nodes"]![3]!["data"]!["topK"] = 12; graph["nodes"]![3]!["data"]!["maxContextChunks"] = 12;
        WorkflowV2Fixtures.AddKnowledgeQueryExtraction(graph);
        f.Models.StructuredResponse = """{"query":"合成检索词"}""";
        var chunks = await repository.ReadChunksAsync(f.LibraryId, version.Id, null, null, 0, 32, Ct);
        f.Models.AnswerResponse = new JsonObject { ["text"] = "合成答案。", ["citations"] = new JsonArray(chunks.Select(chunk => (JsonNode?)JsonValue.Create(chunk.Id.ToString())).ToArray()) }.ToJsonString();
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "Synthetic large checkpoint", schemaVersion = 1, graph });
        var path = Base + "/" + workflow.Id;
        var whole = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "合成问题" } });
        await f.WaitAsync(path + "/runs/" + whole.Id);
        Assert.Equal("合成检索词", f.Embeddings.LastQuery);
        Assert.Equal("合成问题", JsonNode.Parse(f.Models.Prompt!)!["question"]!.GetValue<string>());
        Assert.Equal("合成检索词", JsonNode.Parse(f.Models.Prompt!)!["retrievalQuery"]!.GetValue<string>());
        var embeddings = f.Embeddings.Calls;
        var prepared = await f.Client.GetFromJsonAsync<JsonObject>(path + $"/nodes/answer/debug-inputs?sourceRunId={whole.Id}", Ct);
        Assert.Equal(12, prepared!["inputs"]!["context"]!["hits"]!.AsArray().Count);
        Assert.Equal("合成问题", prepared["inputs"]!["question"]!.GetValue<string>());
        Assert.Equal("合成检索词", prepared["inputs"]!["context"]!["question"]!.GetValue<string>());
        var debug = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/answer/debug-runs", new { requestId = Guid.NewGuid(), sourceRunId = whole.Id, inputs = new { question = "换一种问法的原始问题" } });
        var debugResult = await f.WaitAsync(path + "/runs/" + debug.Id);
        Assert.Equal(12, debugResult["nodes"]![0]!["output"]!["fields"]!["answer"]!["citations"]!.AsArray().Count);
        Assert.Equal("换一种问法的原始问题", JsonNode.Parse(f.Models.Prompt!)!["question"]!.GetValue<string>());
        Assert.Equal("合成检索词", JsonNode.Parse(f.Models.Prompt!)!["retrievalQuery"]!.GetValue<string>());
        var partial = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{whole.Id}/partial-rerun", new { fromNodeId = "answer" });
        var partialResult = await f.WaitAsync(path + "/runs/" + partial.Id);
        var retrieve = partialResult["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "retrieve")!;
        Assert.True(retrieve["isReused"]!.GetValue<bool>());
        Assert.Equal(12, retrieve["output"]!["fields"]!["context"]!["hits"]!.AsArray().Count);
        Assert.Equal("合成问题", JsonNode.Parse(f.Models.Prompt!)!["question"]!.GetValue<string>());
        Assert.Equal("合成检索词", retrieve["output"]!["fields"]!["context"]!["question"]!.GetValue<string>());
        Assert.Equal(embeddings, f.Embeddings.Calls); Assert.Equal(4, f.Models.Calls);
    }

    /// <summary>不完整草稿也可只调试选中节点；任务与历史持久化，同请求重发不再次调用。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DebugIsIndependentDurableAndIdempotent()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var graph = WorkflowV2Fixtures.Graph(); graph["edges"] = new JsonArray();
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "Only one node", schemaVersion = 1, graph });
        var path = Base + "/" + workflow.Id;
        var request = new { requestId = Guid.NewGuid(), inputs = new { question = "test only" } };
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/llm/debug-runs", request);
        var result = await f.WaitAsync(path + "/runs/" + run.Id);
        Assert.Equal("llm", result["debugNodeId"]!.GetValue<string>()); Assert.Single(result["nodes"]!.AsArray()); Assert.Equal(1, f.Models.Calls);
        var replay = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/llm/debug-runs", request);
        Assert.Equal(run.Id, replay.Id); Assert.Equal(1, f.Models.Calls);
        using var conflict = await f.Client.PostAsJsonAsync(path + "/nodes/llm/debug-runs", new { request.requestId, inputs = new { question = "changed" } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var latest = await f.Client.GetAsync(path + "/runs/latest", Ct); Assert.Equal(HttpStatusCode.NotFound, latest.StatusCode);
        var history = await f.Client.GetFromJsonAsync<JsonObject>(path + "/runs", Ct);
        Assert.Equal("llm", history!["runs"]![0]!["debugNodeId"]!.GetValue<string>());
        var center = await f.Client.GetFromJsonAsync<WorkflowCenterRunPage>($"/api/v1/admin/workflow-center/workflow/{workflow.Id}/runs", Ct);
        Assert.Equal("llm", Assert.Single(center!.Items).DebugNodeId);
        using var partial = await f.Client.PostAsJsonAsync(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "llm" }, Ct); Assert.Equal(HttpStatusCode.Conflict, partial.StatusCode);
    }

    /// <summary>历史输入准备不执行模型，独立调试不会覆盖整图最近结果，开始和结束节点也可测试。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task HistoryInputsAndLatestWholeRunStaySeparate()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "History", schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        var path = Base + "/" + workflow.Id;
        var whole = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "original" } });
        await f.WaitAsync(path + "/runs/" + whole.Id);
        var prepared = await f.Client.GetFromJsonAsync<JsonObject>(path + $"/nodes/llm/debug-inputs?sourceRunId={whole.Id}", Ct);
        Assert.Equal("original", prepared!["inputs"]!["question"]!.GetValue<string>()); Assert.Equal(1, f.Models.Calls);
        var debug = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/llm/debug-runs", new { requestId = Guid.NewGuid(), sourceRunId = whole.Id, inputs = new { } });
        await f.WaitAsync(path + "/runs/" + debug.Id); Assert.Equal(2, f.Models.Calls);
        Assert.Equal(whole.Id, (await f.Client.GetFromJsonAsync<WorkflowRunResponse>(path + "/runs/latest", Ct))!.Id);
        var end = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/end/debug-runs", new { requestId = Guid.NewGuid(), inputs = new { answer = "manual end" } });
        var finished = await f.WaitAsync(path + "/runs/" + end.Id); Assert.Single(finished["nodes"]!.AsArray()); Assert.Equal(2, f.Models.Calls);
        f.Identity("user");
        using var denied = await f.Client.GetAsync(path + "/runs/" + debug.Id, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>调试失败不成为整图恢复入口；显式新请求才会再次调用模型。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task FailedDebugCannotBlindlyResume()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true); f.Models.Fail = true;
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "Failure", schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        var path = Base + "/" + workflow.Id;
        var debug = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/llm/debug-runs", new { requestId = Guid.NewGuid(), inputs = new { question = "test" } });
        await f.WaitAsync(path + "/runs/" + debug.Id, "failed");
        using var resume = await f.Client.PostAsJsonAsync(path + $"/runs/{debug.Id}/resume", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); Assert.Equal(1, f.Models.Calls);
    }

    /// <summary>开始节点调试历史保留实际输入且禁止缓存，未派发调试取消后不会发送模型请求。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task StartHistoryAndQueuedCancellationAreSafe()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: false, v2: true);
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "Start debug", schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        var path = Base + "/" + workflow.Id;
        using var response = await f.Client.PostAsJsonAsync(path + "/nodes/start/debug-runs", new { requestId = Guid.NewGuid(), inputs = new { question = "start history" } }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var run = (await response.Content.ReadFromJsonAsync<WorkflowRunResponse>(Ct))!;
        using var cancel = await f.Client.DeleteAsync(path + "/runs/" + run.Id, Ct); cancel.EnsureSuccessStatusCode();
        using var prepared = await f.Client.GetAsync(path + $"/nodes/start/debug-inputs?sourceRunId={run.Id}", Ct);
        prepared.EnsureSuccessStatusCode(); Assert.True(prepared.Headers.CacheControl!.NoStore);
        Assert.Equal("start history", (await prepared.Content.ReadFromJsonAsync<JsonObject>(Ct))!["inputs"]!["question"]!.GetValue<string>());
        Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>问答调试只复用授权历史证据，不重新向量化，并拒绝手工知识对象。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KnowledgeDebugReusesEvidenceWithoutAllowingForgery()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        var workflow = await f.PostAsync<WorkflowResponse>(Base, new { name = "Evidence", schemaVersion = 1, graph = WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, f.VersionId) });
        var path = Base + "/" + workflow.Id;
        var whole = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修多久？" } });
        await f.WaitAsync(path + "/runs/" + whole.Id); var embeddings = f.Embeddings.Calls;
        var debug = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/answer/debug-runs", new { requestId = Guid.NewGuid(), sourceRunId = whole.Id, inputs = new { } });
        var result = await f.WaitAsync(path + "/runs/" + debug.Id);
        Assert.Equal(embeddings, f.Embeddings.Calls); Assert.Equal(2, f.Models.Calls); Assert.Single(result["nodes"]!.AsArray());
        Assert.Equal(1, await f.Db.CountAsync($"SELECT count(*) FROM knowledge_version_pin WHERE owner_kind='workflow_run' AND owner_id='{debug.Id}'"));
        using var forged = await f.Client.PostAsJsonAsync(path + "/nodes/answer/debug-runs", new { requestId = Guid.NewGuid(), sourceRunId = whole.Id, inputs = new { context = new { } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode); Assert.Equal(2, f.Models.Calls);
    }
}
