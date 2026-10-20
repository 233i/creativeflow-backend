using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>知识节点端到端经过真实 MVC、权限 SQL、原持久队列及 MAF；不调用真实模型。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeWorkflowHttpTests
{
    /// <summary>未明确指定隔离库时禁止使用共享开发资源。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的中止令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>目录、授权写入、审计幂等及不同主体隔离都由服务器验证。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CatalogAndGrantsRequireExplicitAccountAuthorization()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true, knowledge: true);
        var grantPath = $"/api/v1/admin/knowledge-bases/{f.LibraryId}/grants/{f.Db.Account}";
        foreach (var identity in new[] { "none", "device", "other", "user" })
        {
            f.Identity(identity);
            using var forbidden = await f.Client.PutAsJsonAsync(grantPath, new { enabled = true, reason = "cannot grant self" }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
            using var catalog = await f.Client.GetAsync("/api/v1/knowledge-bases/catalog", Ct);
            if (identity is "none" or "device") Assert.Equal(HttpStatusCode.Unauthorized, catalog.StatusCode);
            else Assert.Empty((await catalog.Content.ReadFromJsonAsync<JsonArray>(Ct))!);
        }
        f.Identity("admin");
        using var invalid = await f.Client.PutAsJsonAsync(grantPath, new { enabled = true, reason = "" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await GrantAsync(f, true); await GrantAsync(f, true);
        var grants = await f.Client.GetFromJsonAsync<JsonObject>($"/api/v1/admin/knowledge-bases/{f.LibraryId}/grants", Ct);
        Assert.Single(grants!["audit"]!.AsArray());
        f.Identity("user");
        var choices = await f.Client.GetFromJsonAsync<JsonArray>("/api/v1/knowledge-bases/catalog", Ct);
        Assert.Single(choices!); Assert.DoesNotContain("vectors", choices!.ToJsonString()); Assert.DoesNotContain("两年", choices.ToJsonString());
        f.Identity("other"); Assert.Empty((await f.Client.GetFromJsonAsync<JsonArray>("/api/v1/knowledge-bases/catalog", Ct))!);
        Assert.Equal(1, f.Embeddings.Calls); Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>成功 RAG 输出可局部重跑答案；复用检索不再次向量化，正文不走 SignalR。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ExecutesRagAndReusesRetrievalThenRevocationBlocksEveryReadAndRerun()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        await GrantAsync(f, true); f.Identity("user");
        var path = await CreateAsync(f);
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修多久？" } });
        var complete = await f.WaitAsync(path + "/runs/" + run.Id);
        var answer = complete["nodes"]!.AsArray().Single(node => node!["nodeType"]!.GetValue<string>() == "knowledgeAnswer")!["output"]!["fields"]!["answer"]!;
        Assert.Contains("保修两年", answer["text"]!.GetValue<string>());
        Assert.True(answer["grounded"]!.GetValue<bool>()); Assert.Single(answer["citations"]!.AsArray());
        Assert.Equal(2, f.Embeddings.Calls); Assert.Equal(1, f.Models.Calls);
        Assert.NotEmpty(f.Events); Assert.All(f.Events, message => Assert.Null(message.Output));
        var next = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "answer" });
        await f.WaitAsync(path + "/runs/" + next.Id);
        Assert.Equal(2, f.Embeddings.Calls); Assert.Equal(2, f.Models.Calls);
        f.Identity("admin"); await GrantAsync(f, false); f.Identity("user");
        foreach (var suffix in new[] { $"/runs/{run.Id}", "/runs/latest", $"/runs/{run.Id}/partial-rerun/preview?fromNodeId=answer" })
        {
            using var hidden = await f.Client.GetAsync(path + suffix, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
            Assert.DoesNotContain("保修两年", await hidden.Content.ReadAsStringAsync(Ct));
            Assert.True(hidden.Headers.CacheControl!.NoStore);
        }
        using var denied = await f.Client.PostAsJsonAsync(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "answer" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var fresh = await f.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "again" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, fresh.StatusCode);
        Assert.Equal(2, f.Embeddings.Calls); Assert.Equal(2, f.Models.Calls);
    }

    /// <summary>管理员调试明确绑定管理员身份；普通用户不能读取或使用这份 API 草稿。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AdminDebugUsesIndependentIdentityAndRemainsPrivateAfterPublication()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        var path = await CreateAsync(f, administrative: true);
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修？" } });
        await f.WaitAsync(path + "/runs/" + run.Id);
        using var publish = await f.Client.PostAsJsonAsync(path + "/publish", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        f.Identity("user");
        using var denied = await f.Client.GetAsync(path + "/runs/" + run.Id, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var id = path.Split('/')[^1];
        using var ordinary = await f.Client.GetAsync($"/api/v1/workflows/{id}/runs/{run.Id}", Ct); Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
    }

    /// <summary>索引查询后立即撤权，不向问答模型发送已不可见资料。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MidRetrievalRevocationPreventsAnswerCall()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        await GrantAsync(f, true); f.Identity("user"); var path = await CreateAsync(f);
        f.Index.OnSearch = () => new KnowledgeAccessRepository(f.Db.Source).SetGrantAsync(f.LibraryId, f.Db.Account, f.AdminId, false, "synthetic race", Ct);
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修？" } });
        await WaitDeniedAsync(f, path + "/runs/" + run.Id);
        Assert.Equal(0, f.Models.Calls);
        Assert.All(f.Events, message => Assert.Null(message.Output));
    }

    /// <summary>已发送问答期间停用库，结果不能交付或在历史读取中恢复。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MidAnswerDisablePreventsDelivery()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        await GrantAsync(f, true); f.Identity("user"); var path = await CreateAsync(f);
        f.Models.BeforeAnswer = () => new KnowledgeRepository(f.Db.Source).SetEnabledAsync(f.LibraryId, false, Ct);
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修？" } });
        await WaitDeniedAsync(f, path + "/runs/" + run.Id);
        Assert.Equal(1, f.Models.Calls); Assert.All(f.Events, message => Assert.Null(message.Output));
    }

    /// <summary>索引无命中时成功返回依据不足，但不会发送第二次模型请求。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task EmptyRetrievalSkipsChatModel()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        await GrantAsync(f, true); f.Identity("user"); var path = await CreateAsync(f); f.Index.Points[f.VersionId].Clear();
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "unknown" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id);
        var answer = result["nodes"]!.AsArray().Single(node => node!["nodeType"]!.GetValue<string>() == "end")!["output"]!["fields"]!["answer"]!;
        Assert.Contains("没有找到足够", answer["text"]!.GetValue<string>()); Assert.Equal(0, f.Models.Calls); Assert.Equal(2, f.Embeddings.Calls);
    }

    /// <summary>非法引用与不合规 JSON 都失败关闭，继续运行不会再次调用模型。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("not json")]
    [InlineData("{\"text\":\"bad\",\"citations\":[\"foreign\"]}")]
    public async Task InvalidAnswersCannotBeResumedAutomatically(string response)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true);
        await GrantAsync(f, true); f.Identity("user"); var path = await CreateAsync(f); f.Models.AnswerResponse = response;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "保修？" } });
        await f.WaitAsync(path + "/runs/" + run.Id, "failed");
        using var resume = await f.Client.PostAsync(path + $"/runs/{run.Id}/resume", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); Assert.Equal(1, f.Models.Calls); Assert.Equal(2, f.Embeddings.Calls);
    }

    /// <summary>未授权用户及匿名设备不能借图内资源 ID 调用模型或伪造服务端快照。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task SubmitRejectsUngrantAndClientSnapshots()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true, knowledge: true);
        f.Identity("user"); var path = await CreateAsync(f);
        using var rejected = await f.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "test" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var forged = await f.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "test" }, knowledge = new { subject = new { kind = "admin", id = f.AdminId } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode); Assert.Equal(1, f.Embeddings.Calls); Assert.Equal(0, f.Models.Calls);
    }

    /// <summary>使用生产授权 HTTP 路由，原因审计由真实 SQL 完成。</summary>
    private static async Task GrantAsync(AdminWorkflowApisHttpTests.Fixture f, bool enabled)
    {
        using var response = await f.Client.PutAsJsonAsync($"/api/v1/admin/knowledge-bases/{f.LibraryId}/grants/{f.Db.Account}", new { enabled, reason = "合成测试授权调整" }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>创建显式 v2 测试图，普通账户图与管理员 API 草稿分别使用原路由。</summary>
    private static async Task<string> CreateAsync(AdminWorkflowApisHttpTests.Fixture f, bool administrative = false)
    {
        var root = administrative ? "/api/v1/admin/workflow-apis" : "/api/v1/workflows";
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, f.VersionId);
        Assert.True(WorkflowV2GraphValidator.Validate(graph).IsValid, string.Join(';', WorkflowV2GraphValidator.Validate(graph).Errors));
        var workflow = await f.PostAsync<WorkflowResponse>(root, new { name = "Synthetic RAG", schemaVersion = 1, graph });
        return root + "/" + workflow.Id;
    }

    /// <summary>等待撤权可见，不把超时当成权限检查通过。</summary>
    private static async Task WaitDeniedAsync(AdminWorkflowApisHttpTests.Fixture f, string path)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            using var response = await f.Client.GetAsync(path, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Forbidden) return;
            Assert.True(response.IsSuccessStatusCode);
            await Task.Delay(50, deadline.Token);
        }
    }
}
