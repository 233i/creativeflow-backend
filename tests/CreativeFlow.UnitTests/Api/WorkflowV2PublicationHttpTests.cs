using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 MVC、隔离 PostgreSQL、Key 和 MAF 验证 v2 发布；模型、索引和业务 HTTP 仅使用可计数合成端口。</summary>
[Collection("Knowledge index database lock")]
public sealed class WorkflowV2PublicationHttpTests
{
    /// <summary>未显式配置隔离库时不得访问开发或生产数据。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>测试运行的取消标记。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>工作流管理路径。</summary>
    private const string Admin = "/api/v1/admin/workflow-apis";
    /// <summary>后台固定外部操作路径。</summary>
    private const string Operations = "/api/v1/admin/http-credentials";

    /// <summary>外部 API 通过提取、检索、问答及分支汇合，保留原始问题、不同检索词与十二个长片段，不随草稿变化。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task PublishedLargeEvidencePassesThroughBranchesAndImmutableRun()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        var repository = new KnowledgeRepository(f.Db.Source);
var service = new KnowledgeLibraryService(repository, f.Embeddings, f.Index, new(repository, f.Embeddings, f.Index, new KnowledgeFixture.Reranker()));
        var input = new KnowledgeImportRequest([new("synthetic-long.md", new string('文', 12 * 800))], 800, 0);
        var version = await service.ImportAsync(f.LibraryId, input with { PreviewDigest = service.Preview(input).Digest }, Ct);
        await new KnowledgeIndexProcessor(repository, f.Embeddings, f.Index).ProcessNextAsync(Ct);
        var chunks = await repository.ReadChunksAsync(f.LibraryId, version.Id, null, null, 0, 32, Ct);
        Assert.Equal(12, chunks.Length);
        var graph = WorkflowBranchFixtures.KnowledgeGraph(f.LibraryId, version.Id);
        var retrieve = graph["nodes"]!.AsArray().Single(node => node!["type"]!.GetValue<string>() == "knowledgeRetrieve")!;
        retrieve["data"]!["topK"] = 12; retrieve["data"]!["maxContextChunks"] = 12;
        WorkflowV2Fixtures.AddKnowledgeQueryExtraction(graph);
        var answer = graph["nodes"]!.AsArray().Single(node => node!["type"]!.GetValue<string>() == "knowledgeAnswer")!;
        answer["data"]!["maxOutputTokens"] = 32768;
        f.Models.StructuredResponse = """{"query":"合成检索词"}""";
        var (draft, key) = await Publish(f, graph);
        await KnowledgeGrant(f, key, true);
        f.Models.AnswerResponse = new JsonObject { ["text"] = "合成答案。", ["citations"] = new JsonArray(chunks.Select(chunk => (JsonNode?)JsonValue.Create(chunk.Id.ToString())).ToArray()) }.ToJsonString();
        retrieve["data"]!["maxContextChunks"] = 1;
        answer["data"]!["maxOutputTokens"] = 65536;
        using var save = await f.Client.PutAsJsonAsync(Admin + "/" + draft.Id, new { schemaVersion = 1, graph }, Ct); save.EnsureSuccessStatusCode();
        var saved = await f.Client.GetFromJsonAsync<WorkflowResponse>(Admin + "/" + draft.Id, Ct);
        Assert.Equal(65536, saved!.Graph!["nodes"]!.AsArray().Single(node => node!["type"]!.GetValue<string>() == "knowledgeAnswer")!["data"]!["maxOutputTokens"]!.GetValue<int>());
        using var accepted = await Submit(f, draft.Id, key, "large-evidence", new { question = "合成问题" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        var result = await Wait(f, key, run.StatusUrl);
        Assert.Equal(12, result["outputs"]!["answer"]!["citations"]!.AsArray().Count);
        Assert.Equal(2, f.Models.Calls);
        Assert.Equal(32768, f.Models.LastMaxOutputTokens);
        Assert.Equal("合成检索词", f.Embeddings.LastQuery);
        Assert.Equal("合成问题", JsonNode.Parse(f.Models.Prompt!)!["question"]!.GetValue<string>());
        Assert.Equal("合成检索词", JsonNode.Parse(f.Models.Prompt!)!["retrievalQuery"]!.GetValue<string>());
        var details = await f.Client.GetFromJsonAsync<JsonObject>($"{Admin}/{draft.Id}/external-runs/{run.RunId}", Ct);
        var retrievalRun = details!["nodes"]!.AsArray().Single(node => node!["nodeType"]!.GetValue<string>() == "knowledgeRetrieve")!;
        Assert.Equal(12, retrievalRun["output"]!["fields"]!["context"]!["hits"]!.AsArray().Count);
        Assert.False(retrievalRun["output"]!["fields"]!["context"]!["truncated"]!.GetValue<bool>());
        Assert.Equal("合成检索词", retrievalRun["output"]!["fields"]!["context"]!["question"]!.GetValue<string>());
        using var replay = await Submit(f, draft.Id, key, "large-evidence", new { question = "合成问题" });
        Assert.Equal(run.RunId, (await replay.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!.RunId);
        Assert.Equal(2, f.Models.Calls);
    }

    /// <summary>多个同时到达的相同请求只创建一行且只调用一次模型；新草稿和新版本都不能改变原幂等重放。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ConcurrentIdempotencyUsesImmutablePublishedGraphAndOriginalVersion()
    {
        // 并发请求与发布切换都必须比较首次已受理版本，不能按最新草稿计算幂等冲突。
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var graph = WorkflowV2Fixtures.Graph(); var (draft, key) = await Publish(f, graph);
        graph["nodes"]![1]!["data"]!["prompt"]![0]!["text"] = "new draft: ";
        using var save = await f.Client.PutAsJsonAsync(Admin + "/" + draft.Id, new { schemaVersion = 1, graph }, Ct); save.EnsureSuccessStatusCode();
        var accepted = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Submit(f, draft.Id, key, "order-1", new { question = "hello" })));
        try
        {
            Assert.All(accepted, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var values = await Task.WhenAll(accepted.Select(response => response.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct)));
            var run = values[0]!; Assert.All(values, value => Assert.Equal(run.RunId, value!.RunId));
            var result = await Wait(f, key, run.StatusUrl);
            Assert.Equal("Synthetic: Answer: hello", result["outputs"]!["answer"]!.GetValue<string>());
            Assert.Equal(1, f.Models.Calls);
            await f.PostAsync<WorkflowDeploymentResponse>(Admin + "/" + draft.Id + "/publish", new { });
            using var replay = await Submit(f, draft.Id, key, "order-1", new { question = "hello" });
            var replayed = (await replay.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
            Assert.Equal(run.RunId, replayed.RunId); Assert.Equal(1, replayed.Version);
            using var conflict = await Submit(f, draft.Id, key, "order-1", new { question = "different" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var next = await Submit(f, draft.Id, key, "order-2", new { question = "hello" });
            var second = (await next.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
            Assert.Equal(2, second.Version); var latest = await Wait(f, key, second.StatusUrl);
            Assert.Equal("Synthetic: new draft: hello", latest["outputs"]!["answer"]!.GetValue<string>());
            Assert.Equal(2, f.Models.Calls);
            Assert.Equal(2, await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE trigger_type='api'"));
            Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_deployment WHERE execution_snapshot::text LIKE '%subject%' OR execution_snapshot::text LIKE '%secret%'"));
            Assert.All(f.Events, item => Assert.Null(item.Output));
        }
        finally { foreach (var response in accepted) response.Dispose(); }
    }

    /// <summary>基础类型不转成文本，目录不泄漏内部节点；错误类型、额外参数和伪造知识值在入队前拒绝。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task TypedContractBindsDefaultsAndReturnsOnlyDeclaredEndFields()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var fields = JsonNode.Parse("""[{"key":"quantity","label":"数量","type":"number","required":true,"minimum":1,"maximum":10},{"key":"enabled","type":"boolean","required":true,"default":false},{"key":"payload","type":"object","required":true},{"key":"items","type":"array","required":true},{"key":"optional","type":"string","required":false}]""")!.AsArray();
        var graph = new JsonObject { ["schemaVersion"] = 1, ["nodes"] = new JsonArray(
            WorkflowBranchFixtures.Node("start", "start", new JsonObject { ["fields"] = fields.DeepClone() }),
            WorkflowBranchFixtures.Node("end", "end", new JsonObject { ["fields"] = fields.DeepClone() })),
            ["edges"] = new JsonArray(fields.Select(field => (JsonNode)WorkflowBranchFixtures.Edge(field!["key"]!.GetValue<string>(), "start", field["key"]!.GetValue<string>(), "end", field["key"]!.GetValue<string>())).ToArray()) };
        var (draft, key) = await Publish(f, graph);
        var catalog = await f.Client.GetFromJsonAsync<JsonObject>("/api/external/v1/workflows/" + draft.Id, Ct);
        Assert.Equal(1, catalog!["version"]!.GetValue<int>()); Assert.Null(catalog["schemaVersion"]);
        Assert.DoesNotContain("nodeId", catalog.ToJsonString());
        Assert.DoesNotContain("modelId", catalog.ToJsonString());
        using var accepted = await Submit(f, draft.Id, key, "typed", new { quantity = 2.5, payload = new { ok = true }, items = new[] { 1, 2 } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        var output = (await Wait(f, key, run.StatusUrl))["outputs"]!;
        Assert.Equal(2.5, output["quantity"]!.GetValue<double>()); Assert.False(output["enabled"]!.GetValue<bool>());
        Assert.True(output["payload"]!["ok"]!.GetValue<bool>()); Assert.Equal(2, output["items"]!.AsArray().Count); Assert.Null(output["optional"]);
        foreach (var bad in new object[] { new { quantity = "2", payload = new { }, items = new int[0] },
            new { quantity = 20, payload = new { }, items = new int[0] }, new { quantity = 2, payload = new { }, items = new int[0], models = new { } } })
        { using var rejected = await Submit(f, draft.Id, key, Guid.NewGuid().ToString(), bad); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); }
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM workflow_run")); Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>发布绑定模型配置发生变化时必须重新发布，不在新任务里偷偷替换快照。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ModelConfigurationChangeRequiresExplicitRepublish()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        var (draft, key) = await Publish(f, WorkflowV2Fixtures.Graph()); f.Models.ConfigurationChanged = true;
        using var denied = await Submit(f, draft.Id, key, "before-republish", new { question = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, f.Models.Calls);
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_run"));
        await f.PostAsync<WorkflowDeploymentResponse>(Admin + "/" + draft.Id + "/publish", new { });
        using var accepted = await Submit(f, draft.Id, key, "after-republish", new { question = "test" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    /// <summary>知识/HTTP 都需独立授权，即便未命中分支未执行也不能借发布目录获得资源权限；命中只调用 RAG，未命中只调用 HTTP。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(true)][InlineData(false)]
    public async Task PublishedRagBranchesUseKeyGrantsAndTraceableOutputs(bool hit)
    {
        // 先验证两类资源分别默认拒绝，再检查真实 MAF 路由；撤销未执行分支的资源也不能读取运行。
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true, http: true);
        var operation = await f.PostAsync<JsonObject>(Operations, HttpRequestFixtures.CredentialForm());
        var graph = KnowledgeHttpGraph(f, operation); var (draft, key) = await Publish(f, graph);
        if (!hit) f.Index.Points[f.VersionId].Clear();
        using var noGrants = await Submit(f, draft.Id, key, "no-permission", new { question = "保修？" });
        Assert.Equal(HttpStatusCode.Forbidden, noGrants.StatusCode);
        await KnowledgeGrant(f, key, true);
        using var missingHttpGrant = await Submit(f, draft.Id, key, "no-http", new { question = "保修？" });
        Assert.Equal(HttpStatusCode.Forbidden, missingHttpGrant.StatusCode);
        await HttpGrant(f, operation, key, true);
        using var accepted = await Submit(f, draft.Id, key, "authorized", new { question = "保修？" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        var result = await Wait(f, key, run.StatusUrl);
        Assert.Equal(hit ? 1 : 0, f.Models.Calls); Assert.Equal(hit ? 0 : 1, f.Http.Calls);
        if (hit)
        {
            var answer = result["outputs"]!["answer"]!; Assert.True(answer["grounded"]!.GetValue<bool>());
            Assert.Equal(f.VersionId.ToString(), answer["citations"]![0]!["versionId"]!.GetValue<string>());
            Assert.Contains("保修两年", answer["citations"]![0]!["text"]!.GetValue<string>());
        }
        else Assert.Equal("Synthetic HTTP: 保修？", result["outputs"]!["fallback"]!.GetValue<string>());
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM workflow_node_run WHERE status='skipped'"));
        Assert.All(f.Events, entry => Assert.Null(entry.Output));
        using var adminDetails = await f.Client.GetAsync($"{Admin}/{draft.Id}/external-runs/{run.RunId}", Ct);
        Assert.Equal(HttpStatusCode.OK, adminDetails.StatusCode); Assert.True(adminDetails.Headers.CacheControl!.NoStore);
        Assert.Contains("nodes", await adminDetails.Content.ReadAsStringAsync(Ct));
        await KnowledgeGrant(f, key, false);
        using var hidden = await Send(f, key, HttpMethod.Get, run.StatusUrl); Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.DoesNotContain("保修两年", await hidden.Content.ReadAsStringAsync(Ct)); Assert.True(hidden.Headers.CacheControl!.NoStore);
        using var replay = await Submit(f, draft.Id, key, "authorized", new { question = "保修？" }); Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        using var adminRevoked = await f.Client.GetAsync($"{Admin}/{draft.Id}/external-runs/{run.RunId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, adminRevoked.StatusCode);
        await KnowledgeGrant(f, key, true); await HttpGrant(f, operation, key, false);
        using var hiddenHttp = await Send(f, key, HttpMethod.Get, run.StatusUrl); Assert.Equal(HttpStatusCode.Forbidden, hiddenHttp.StatusCode);
        var grants = await f.Client.GetFromJsonAsync<JsonObject>($"/api/v1/admin/knowledge-bases/{f.LibraryId}/grants", Ct);
        Assert.Single(grants!["apiKeys"]!.AsArray()); Assert.Equal(3, grants["apiKeyAudit"]!.AsArray().Count);
        Assert.DoesNotContain(key.Key!, grants.ToJsonString());
    }

    /// <summary>外部 Key 相互隔离结果与取消权限；停用发布阻止新任务，但原版本状态和幂等查询仍然可用。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KeyIsolationAndPublicationDisablePreserveAcceptedRunSemantics()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var (draft, key) = await Publish(f, WorkflowBranchFixtures.Graph());
        var other = await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{draft.Id}/api-keys", new { name = "Another consumer" });
        using var accepted = await Submit(f, draft.Id, key, "existing", new { question = "miss" });
        var run = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        await Wait(f, key, run.StatusUrl);
        using var stranger = await Send(f, other, HttpMethod.Get, run.StatusUrl); Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        using var cancel = await Send(f, other, HttpMethod.Post, run.StatusUrl + "/cancel", new { }); Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        using var disable = await f.Client.DeleteAsync($"{Admin}/{draft.Id}/deployment", Ct); disable.EnsureSuccessStatusCode();
        using var blocked = await Submit(f, draft.Id, key, "new", new { question = "miss" }); Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        using var replay = await Submit(f, draft.Id, key, "existing", new { question = "miss" }); Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await Wait(f, key, run.StatusUrl); Assert.Equal(0, f.Models.Calls);
        using var revoke = await f.Client.DeleteAsync($"{Admin}/{draft.Id}/api-keys/{key.Id}", Ct); revoke.EnsureSuccessStatusCode();
        using var noKey = await Send(f, key, HttpMethod.Get, run.StatusUrl); Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
    }

    /// <summary>即使是没有知识节点的 LLM 任务，Key 在模型返回前撤销也不得保存或广播结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MidflightKeyRevocationPreventsLlmResultDelivery()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        f.Models.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (draft, key) = await Publish(f, WorkflowV2Fixtures.Graph());
        using var accepted = await Submit(f, draft.Id, key, "revoked", new { question = "private" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (f.Models.Calls == 0) await Task.Delay(25, timeout.Token);
        using var queued = await Submit(f, draft.Id, key, "queued-before-revocation", new { question = "also-private" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='queued'"));
        using var revoked = await f.Client.DeleteAsync($"{Admin}/{draft.Id}/api-keys/{key.Id}", Ct); revoked.EnsureSuccessStatusCode();
        f.Models.Gate.SetResult(true);
        while (await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='failed'") != 2) await Task.Delay(25, timeout.Token);
        Assert.Equal(1, f.Models.Calls);
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_node_run WHERE node_type='llm' AND output IS NOT NULL"));
        Assert.All(f.Events, entry => Assert.Null(entry.Output));
    }

    /// <summary>固定 HTTP 发送后超时进入失败；幂等重放只返回原任务，不自动再请求或执行 ELSE。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task UnknownHttpResultIsNotResentByIdempotencyReplay()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var operation = await f.PostAsync<JsonObject>(Operations, HttpRequestFixtures.CredentialForm());
        var (draft, key) = await Publish(f, HttpRequestFixtures.BranchGraph(Guid.Parse(operation["id"]!.GetValue<string>()), Guid.Parse(operation["revisionId"]!.GetValue<string>())));
        await HttpGrant(f, operation, key, true); f.Http.Fail = true;
        using var accepted = await Submit(f, draft.Id, key, "timeout", new { question = "hit" });
        var run = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        var failed = await Wait(f, key, run.StatusUrl, "failed"); Assert.Contains("HTTP_TIMEOUT", failed["error"]!.GetValue<string>()); Assert.Null(failed["outputs"]);
        using var replay = await Submit(f, draft.Id, key, "timeout", new { question = "hit" }); Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(1, f.Http.Calls); Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM workflow_run"));
    }

    /// <summary>Key 授权端点仅管理员可用，未知 Key 与空原因均拒绝，不产生授权记录。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KnowledgeKeyGrantRequiresAdminRealKeyAndReason()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true, knowledge: true);
        var (_, key) = await Publish(f, WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, f.VersionId));
        var path = $"/api/v1/admin/knowledge-bases/{f.LibraryId}/api-key-grants/";
        foreach (var identity in new[] { "anonymous", "user", "device" })
        { f.Identity(identity); using var denied = await f.Client.PutAsJsonAsync(path + key.Id, new { enabled = true, reason = "attempt" }, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        f.Identity("admin");
        using var noReason = await f.Client.PutAsJsonAsync(path + key.Id, new { enabled = true, reason = " " }, Ct); Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        using var noKey = await f.Client.PutAsJsonAsync(path + Guid.NewGuid(), new { enabled = true, reason = "attempt" }, Ct); Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM knowledge_api_key_grant"));
    }

    /// <summary>历史摘要不带图、入参、模型或正文；管理员和普通用户运行范围仍独立，外部运行另走授权详情。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task HistoryIsBoundedMetadataAndExternalDetailsRequireAdministrativeScope()
    {
        // 分开创建调试和 API 运行，验证两套列表不会混入对方输入，选择详情时仍需独立管理身份。
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        var (draft, key) = await Publish(f, WorkflowBranchFixtures.Graph());
        var path = $"{Admin}/{draft.Id}";
        var first = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "miss-private-input" } });
        await f.WaitAsync(path + "/runs/" + first.Id);
        var second = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "miss-other-input" } });
        await f.WaitAsync(path + "/runs/" + second.Id);
        using var accepted = await Submit(f, draft.Id, key, "external", new { question = "miss-external-input" });
        var external = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        await Wait(f, key, external.StatusUrl);
        var history = await f.Client.GetFromJsonAsync<JsonObject>(path + "/runs", Ct);
        Assert.Equal(2, history!["runs"]!.AsArray().Count); Assert.Equal(second.Id, history["runs"]![0]!["id"]!.GetValue<string>());
        foreach (var forbidden in new[] { "private-input", "other-input", "external-input", "graph", "models", "output" }) Assert.DoesNotContain(forbidden, history.ToJsonString());
        using var detail = await f.Client.GetAsync(path + "/external-runs/" + external.RunId, Ct); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        foreach (var identity in new[] { "user", "device", "anonymous" })
        {
            f.Identity(identity);
            using var deniedHistory = await f.Client.GetAsync(path + "/runs", Ct); Assert.Equal(HttpStatusCode.Unauthorized, deniedHistory.StatusCode);
            using var deniedDetail = await f.Client.GetAsync(path + "/external-runs/" + external.RunId, Ct); Assert.Equal(HttpStatusCode.Unauthorized, deniedDetail.StatusCode);
        }
        f.Identity("user");
        var personal = await f.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "Private debug", schemaVersion = 1, graph = WorkflowBranchFixtures.Graph() });
        var personalPath = "/api/v1/workflows/" + personal.Id;
        var own = await f.PostAsync<WorkflowRunResponse>(personalPath + "/runs", new { inputs = new { question = "miss" } });
        await f.WaitAsync(personalPath + "/runs/" + own.Id);
        Assert.Single((await f.Client.GetFromJsonAsync<JsonObject>(personalPath + "/runs", Ct))!["runs"]!.AsArray());
        f.Identity("other"); using var foreign = await f.Client.GetAsync(personalPath + "/runs", Ct);
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Empty((await foreign.Content.ReadFromJsonAsync<JsonObject>(Ct))!["runs"]!.AsArray());
        Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>从合成检索分支图派生未命中 HTTP 分支，保留类型化答案与不同的可选回退字段。</summary>
    private static JsonObject KnowledgeHttpGraph(AdminWorkflowApisHttpTests.Fixture f, JsonObject operation)
    {
        var graph = WorkflowBranchFixtures.KnowledgeGraph(f.LibraryId, f.VersionId);
        var fallback = graph["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "fallback")!;
        fallback["type"] = "httpRequest";
        fallback["data"] = HttpRequestFixtures.ExtractedData(Guid.Parse(operation["id"]!.GetValue<string>()), Guid.Parse(operation["revisionId"]!.GetValue<string>()));
        graph["edges"]!.AsArray().Single(edge => edge!["id"]!.GetValue<string>() == "fallback-merge")!["sourceHandle"] = "text";
        graph["edges"]!.AsArray().Add((JsonNode)WorkflowBranchFixtures.Edge("http-question", "start", "question", "fallback", "question"));
        return graph;
    }

    /// <summary>走真实后台草稿、发布与 Key 签发接口，不手工插入运行或发布状态。</summary>
    private static async Task<(WorkflowResponse Draft, WorkflowApiKeyResponse Key)> Publish(AdminWorkflowApisHttpTests.Fixture f, JsonObject graph)
    {
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Synthetic workflow publication", schemaVersion = 1, graph });
        var publication = await f.PostAsync<WorkflowDeploymentResponse>($"{Admin}/{draft.Id}/publish", new { description = "合成问答/查询验收", maxConcurrency = 1 });
        Assert.Equal(1, publication.SchemaVersion); Assert.Equal(0, f.Models.Calls);
        var key = await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{draft.Id}/api-keys", new { name = "Synthetic consumer" });
        return (draft, key);
    }

    /// <summary>以每请求独立请求头模拟不同外部客户，可安全并行提交。</summary>
    private static Task<HttpResponseMessage> Submit(AdminWorkflowApisHttpTests.Fixture f, string workflow, WorkflowApiKeyResponse key, string idempotency, object inputs) =>
        Send(f, key, HttpMethod.Post, $"/api/external/v1/workflows/{workflow}/runs", new { inputs }, idempotency);

    /// <summary>只使用新签发的合成 Key，不依赖管理员 Cookie；签发权限仍由后台测试身份保证。</summary>
    private static async Task<HttpResponseMessage> Send(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, HttpMethod method, string path, object? body = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        request.Headers.Add("Test-Identity", "anonymous");
        if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await f.Client.SendAsync(request, Ct);
    }

    /// <summary>轮询持久状态，不重发业务请求，失败详情仅用于测试断言。</summary>
    private static async Task<JsonObject> Wait(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, string path, string status = "succeeded")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            using var response = await Send(f, key, HttpMethod.Get, path); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var value = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            var state = value["status"]!.GetValue<string>(); if (state == status) return value;
            Assert.True(state is "queued" or "running", value.ToJsonString()); await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>调用真实知识 Key 授权接口。</summary>
    private static async Task KnowledgeGrant(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, bool enabled)
    {
        using var response = await f.Client.PutAsJsonAsync($"/api/v1/admin/knowledge-bases/{f.LibraryId}/api-key-grants/{key.Id}", new { enabled, reason = "Synthetic external RAG" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>调用真实 HTTP 操作 Key 授权接口。</summary>
    private static async Task HttpGrant(AdminWorkflowApisHttpTests.Fixture f, JsonObject operation, WorkflowApiKeyResponse key, bool enabled)
    {
        using var response = await f.Client.PutAsJsonAsync($"{Operations}/{operation["id"]!.GetValue<string>()}/grants/apiKey/{key.Id}", new { enabled, reason = "Synthetic external lookup" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}
