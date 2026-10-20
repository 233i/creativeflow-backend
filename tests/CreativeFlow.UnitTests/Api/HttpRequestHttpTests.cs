using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 MVC、PostgreSQL 和 MAF 验证 HTTP 节点及独立凭据的完整运行路径。</summary>
public sealed class HttpRequestHttpTests
{
    /// <summary>只允许显式隔离数据库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>测试取消。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>管理员工作流入口。</summary>
    private const string Workflows = "/api/v1/admin/workflow-apis";
    /// <summary>独立凭据资源入口。</summary>
    private const string Credentials = "/api/v1/admin/http-credentials";

    /// <summary>正常运行、单节点调试和局部重跑共用持久队列；重放调试不再次发请求。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task WholeDebugAndPartialRunsPreserveOutputsAndAvoidDuplicateRequests()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var workflow = await Draft(f, HttpRequestFixtures.Graph()); var path = Workflows + "/" + workflow.Id;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "synthetic" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id);
        Assert.Equal("HTTP: synthetic", result["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "end")!["output"]!["fields"]!["answer"]!.GetValue<string>());
        var partial = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "end" });
        var reused = await f.WaitAsync(path + "/runs/" + partial.Id);
        Assert.True(reused["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "http")!["isReused"]!.GetValue<bool>()); Assert.Equal(1, f.Http.Calls);
        var request = new { requestId = Guid.NewGuid(), inputs = new { question = "debug" } };
        var debug = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/http/debug-runs", request);
        var debugResult = await f.WaitAsync(path + "/runs/" + debug.Id);
        Assert.Single(debugResult["nodes"]!.AsArray()); Assert.Equal("http", debugResult["debugNodeId"]!.GetValue<string>());
        var replay = await f.PostAsync<WorkflowRunResponse>(path + "/nodes/http/debug-runs", request);
        Assert.Equal(debug.Id, replay.Id); Assert.Equal(2, f.Http.Calls); Assert.Equal(0, f.Models.Calls);
        Assert.All(f.Events, entry => Assert.Null(entry.Output));
    }

    /// <summary>分支未选中时不发 HTTP 请求，命中时响应可接入汇合节点。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("hit", 1)][InlineData("miss", 0)]
    public async Task OnlySelectedBranchesSendRequests(string question, int calls)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var graph = WorkflowBranchFixtures.Graph(); graph["nodes"]![1]!["type"] = "httpRequest"; graph["nodes"]![1]!["data"] = HttpRequestFixtures.Data();
        foreach (var edge in graph["edges"]!.AsArray().Where(edge => edge!["source"]!.GetValue<string>() == "hit")) edge!["sourceHandle"] = "body";
        var draft = await Draft(f, graph); var path = $"{Workflows}/{draft.Id}";
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question } });
        await f.WaitAsync(path + "/runs/" + run.Id); Assert.Equal(calls, f.Http.Calls); Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>凭据轮换追加版本；旧引用继续使用旧值，目录和运行记录不含秘密。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CredentialRotationAndRecoveryKeepSecretsOutOfGraphsAndHistory()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var (id, revision) = await Create(f); var workflow = await Draft(f, HttpRequestFixtures.Graph(id, revision));
        using var next = await f.Client.PutAsJsonAsync(Credentials + "/" + id, Form(""), Ct); next.EnsureSuccessStatusCode();
        Assert.NotEqual(revision.ToString(), (await next.Content.ReadFromJsonAsync<JsonObject>(Ct))!["revisionId"]!.GetValue<string>());
        var path = $"{Workflows}/{workflow.Id}";
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "test" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id); Assert.Equal(revision, f.Http.Revision);
        foreach (var text in new[] { await f.Client.GetStringAsync(Credentials + "/" + id, Ct), await f.Client.GetStringAsync(Credentials + "/catalog", Ct), result.ToJsonString() })
        { Assert.DoesNotContain("synthetic-private-token", text); Assert.DoesNotContain("secretCipher", text); }
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE input_json::text LIKE '%synthetic-private-token%' OR graph_snapshot::text LIKE '%synthetic-private-token%'"));
        using var originChanged = await f.Client.PutAsJsonAsync(Credentials + "/" + id, Form("", "https://another.example"), Ct); Assert.Equal(HttpStatusCode.BadRequest, originChanged.StatusCode);
        await f.Db.SqlAsync($"UPDATE workflow_http_credential_revision SET secret_cipher=decode('01','hex') WHERE credential_id='{id}'");
        using var inherited = await f.Client.PutAsJsonAsync(Credentials + "/" + id, Form(""), Ct); Assert.Equal(HttpStatusCode.BadRequest, inherited.StatusCode);
        using var repaired = await f.Client.PutAsJsonAsync(Credentials + "/" + id, Form("Bearer replacement-value"), Ct); repaired.EnsureSuccessStatusCode();
        Assert.Equal(3, await f.Db.CountAsync("SELECT count(*) FROM workflow_http_credential_revision"));
    }

    /// <summary>凭据有独立账号授权，撤权后详情、最新结果和局部重跑均不可读取。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AccountGrantsAreEnforcedAtSubmissionAndEveryResultRead()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var (id, revision) = await Create(f); f.Identity("user");
        var workflow = await f.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "HTTP account test", schemaVersion = 1, graph = HttpRequestFixtures.Graph(id, revision) }); var path = "/api/v1/workflows/" + workflow.Id;
        Assert.Empty((await f.Client.GetFromJsonAsync<JsonArray>("/api/v1/http-credentials/catalog", Ct))!);
        using var denied = await f.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "test" } }, Ct); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        f.Identity("admin"); await Grant(f, id, "account", f.Db.Account, true); f.Identity("user");
        Assert.Single((await f.Client.GetFromJsonAsync<JsonArray>("/api/v1/http-credentials/catalog", Ct))!);
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "test" } }); await f.WaitAsync(path + "/runs/" + run.Id);
        f.Identity("other"); using var foreign = await f.Client.GetAsync(path + "/runs/" + run.Id, Ct); Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        f.Identity("admin"); await Grant(f, id, "account", f.Db.Account, false); f.Identity("user");
        foreach (var suffix in new[] { "/runs/latest", "/runs/" + run.Id, $"/runs/{run.Id}/partial-rerun/preview?fromNodeId=end" })
        { using var response = await f.Client.GetAsync(path + suffix, Ct); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
        using var partial = await f.Client.PostAsJsonAsync(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "end" }, Ct); Assert.Equal(HttpStatusCode.Forbidden, partial.StatusCode);
        Assert.Equal(1, f.Http.Calls); Assert.Equal(2, await f.Db.CountAsync("SELECT count(*) FROM workflow_http_credential_grant_audit"));
    }

    /// <summary>发送中停用后不保存业务正文；超时运行不允许通过继续动作再次调用。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)][InlineData(true)]
    public async Task UnknownOrRevokedInFlightRequestsNeverResumeAutomatically(bool revoke)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var (id, revision) = await Create(f); var workflow = await Draft(f, HttpRequestFixtures.Graph(id, revision)); var path = $"{Workflows}/{workflow.Id}";
        if (revoke) f.Http.BeforeReturn = () => f.Db.SqlAsync($"UPDATE workflow_http_credential SET enabled=false WHERE id='{id}'"); else f.Http.Fail = true;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "test" } });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='failed'") == 0) await Task.Delay(40, timeout.Token);
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_node_run WHERE node_type='httpRequest' AND output IS NOT NULL"));
        if (revoke) await f.Db.SqlAsync($"UPDATE workflow_http_credential SET enabled=true WHERE id='{id}'");
        using var resumed = await f.Client.PostAsJsonAsync(path + $"/runs/{run.Id}/resume", new { }, Ct); Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
        Assert.Equal(1, f.Http.Calls); Assert.All(f.Events, entry => Assert.Null(entry.Output));
    }

    /// <summary>发布固定图和凭据版本，Key 需独立授权；重放保持幂等，撤权立即阻止读取。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task PublicationPinsCredentialAndRequiresApiKeyGrant()
    {
        // 固定旧凭据后轮换当前版本，再用真实 Key 提交，防止执行时悄悄跟随最新配置。
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        var (id, revision) = await Create(f); var draft = await Draft(f, HttpRequestFixtures.Graph(id, revision)); var path = $"{Workflows}/{draft.Id}";
        await f.PostAsync<WorkflowDeploymentResponse>(path + "/publish", new { description = "Synthetic HTTP", maxConcurrency = 1 });
        var key = await f.PostAsync<WorkflowApiKeyResponse>(path + "/api-keys", new { name = "Synthetic HTTP consumer" }); Assert.Equal(0, f.Http.Calls);
        var externalPath = $"/api/external/v1/workflows/{draft.Id}/runs";
        using var denied = await Send(f, key, HttpMethod.Post, externalPath, new { inputs = new { question = "published" } }, "same"); Assert.False(denied.IsSuccessStatusCode);
        await Grant(f, id, "apiKey", Guid.Parse(key.Id), true);
        using var rotated = await f.Client.PutAsJsonAsync(Credentials + "/" + id, Form("Bearer rotated-synthetic-token"), Ct); rotated.EnsureSuccessStatusCode();
        using var accepted = await Send(f, key, HttpMethod.Post, externalPath, new { inputs = new { question = "published" } }, "same");
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(Ct)); var created = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            using var response = await Send(f, key, HttpMethod.Get, created.StatusUrl); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var status = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            if (status["status"]!.GetValue<string>() == "succeeded") { Assert.Equal("HTTP: published", status["outputs"]!["answer"]!.GetValue<string>()); break; }
            Assert.Contains(status["status"]!.GetValue<string>(), new[] { "queued", "running" }); await Task.Delay(40, timeout.Token);
        }
        using var replay = await Send(f, key, HttpMethod.Post, externalPath, new { inputs = new { question = "published" } }, "same"); Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(revision, f.Http.Revision); Assert.Equal(1, f.Http.Calls);
        await Grant(f, id, "apiKey", Guid.Parse(key.Id), false);
        using var revoked = await Send(f, key, HttpMethod.Get, created.StatusUrl); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    /// <summary>管理凭据仅管理员可写，能力关闭阻止新 HTTP 执行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ManagementAuthenticationAndCapabilityGateAreIndependent()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        foreach (var identity in new[] { "anonymous", "user", "device" })
        {
            f.Identity(identity);
            using var list = await f.Client.GetAsync(Credentials, Ct); Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
            using var save = await f.Client.PostAsJsonAsync(Credentials, Form("synthetic"), Ct); Assert.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
        }
        f.Identity("admin"); using var disabled = await f.Client.GetAsync(Credentials, Ct); Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        var workflow = await Draft(f, HttpRequestFixtures.Graph());
        using var run = await f.Client.PostAsJsonAsync($"{Workflows}/{workflow.Id}/runs", new { inputs = new { question = "test" } }, Ct); Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode); Assert.Equal(0, f.Http.Calls);
    }

    /// <summary>完整 1 MiB 通过持久队列、结束节点、复用和外部 API，清理后旧幂等键不再执行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task FullMebibyteSurvivesQueueHistoryPartialRerunAndExternalApiWithoutResending()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        f.Http.Body = new string('中', 349525) + "x"; // UTF-8 恰好 1 MiB，覆盖 JSON 转义和中文长度差异。
        var graph = HttpRequestFixtures.Graph(); graph["nodes"]![1]!["data"]!["http"]!["responseLimitBytes"] = 1048576;
        var workflow = await Draft(f, graph); var path = Workflows + "/" + workflow.Id;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "large" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id);
        Assert.Equal(f.Http.Body, result["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "end")!["output"]!["fields"]!["answer"]!.GetValue<string>());
        var partial = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "end" });
        var reused = await f.WaitAsync(path + "/runs/" + partial.Id);
        Assert.True(reused["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "http")!["isReused"]!.GetValue<bool>());
        Assert.Equal(1, f.Http.Calls);
        await f.PostAsync<WorkflowDeploymentResponse>(path + "/publish", new { description = "Large HTTP body", maxConcurrency = 1 });
        var key = await f.PostAsync<WorkflowApiKeyResponse>(path + "/api-keys", new { name = "Large response test" });
        var externalPath = $"/api/external/v1/workflows/{workflow.Id}/runs";
        using var accepted = await Send(f, key, HttpMethod.Post, externalPath, new { inputs = new { question = "large" } }, "large-response");
        accepted.EnsureSuccessStatusCode(); var created = (await accepted.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var response = await Send(f, key, HttpMethod.Get, created.StatusUrl); response.EnsureSuccessStatusCode();
            var status = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            if (status["status"]!.GetValue<string>() == "succeeded") { Assert.Equal(f.Http.Body, status["outputs"]!["answer"]!.GetValue<string>()); break; }
            Assert.Contains(status["status"]!.GetValue<string>(), new[] { "queued", "running" }); await Task.Delay(40, timeout.Token);
        }
        // 模拟迁移清理：不保留结果，但旧幂等键不得再次产生网络调用。
        await f.Db.SqlAsync($"INSERT INTO workflow_run_retired_idempotency(api_key_id,idempotency_key) VALUES('{key.Id}','large-response'); DELETE FROM workflow_run WHERE api_key_id='{key.Id}'");
        using var replay = await Send(f, key, HttpMethod.Post, externalPath, new { inputs = new { question = "large" } }, "large-response");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode); Assert.Equal(2, f.Http.Calls);
    }

    /// <summary>轮换不能借用既有账号授权扩大路径或方法；旧路由也不再提供操作注册能力。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CredentialScopeCannotBeExpandedAndLegacyRoutesAreGone()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true, http: true);
        var (id, _) = await Create(f);
        foreach (var scope in new[] { new { method = "POST", url = "https://example.com/query" }, new { method = "GET", url = "https://example.com/admin" } })
        {
            using var response = await f.Client.PutAsJsonAsync(Credentials + "/" + id, new { name = "Attempted change", description = "", origin = "https://example.com", headers = new[] { new { name = "Authorization", value = "rotated" } }, requests = new[] { scope } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM workflow_http_credential_revision"));
        using var legacy = await f.Client.GetAsync("/api/v1/admin/external-operations", Ct); Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    /// <summary>只创建当前测试使用的凭据。</summary>
    private static async Task<(Guid Id, Guid Revision)> Create(AdminWorkflowApisHttpTests.Fixture f)
    {
        var result = await f.PostAsync<JsonObject>(Credentials, Form("Bearer synthetic-private-token"));
        return (Guid.Parse(result["id"]!.GetValue<string>()), Guid.Parse(result["revisionId"]!.GetValue<string>()));
    }
    /// <summary>合成凭据表单。</summary>
    private static object Form(string value, string origin = "https://example.com") => new { name = "Synthetic credential", description = "Test only", origin, headers = new[] { new { name = "Authorization", value } }, requests = new[] { new { method = "GET", url = origin + "/query" } } };
    /// <summary>使用真实草稿入口创建工作流。</summary>
    private static Task<WorkflowResponse> Draft(AdminWorkflowApisHttpTests.Fixture f, JsonObject graph) => f.PostAsync<WorkflowResponse>(Workflows, new { name = "HTTP fixture", schemaVersion = 1, graph });
    /// <summary>更新账号或 Key 的独立凭据权限。</summary>
    private static async Task Grant(AdminWorkflowApisHttpTests.Fixture f, Guid id, string kind, Guid subject, bool enabled)
    {
        using var response = await f.Client.PutAsJsonAsync($"{Credentials}/{id}/grants/{kind}/{subject}", new { enabled, reason = "Synthetic grant" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
    /// <summary>使用真实签发的合成 Key 发送公开 API 请求。</summary>
    private static async Task<HttpResponseMessage> Send(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, HttpMethod method, string path, object? body = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key); request.Headers.Add("Test-Identity", "anonymous");
        if (body is not null) request.Content = JsonContent.Create(body);
        if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        return await f.Client.SendAsync(request, Ct);
    }
}
