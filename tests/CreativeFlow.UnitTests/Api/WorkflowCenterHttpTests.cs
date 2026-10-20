using System.Net;
using System.Net.Http.Json;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>用真实管理鉴权、数据库和 MVC 验证中心分页，不请求模型或修改既有发布。</summary>
public sealed class WorkflowCenterHttpTests
{
    /// <summary>未显式指定隔离数据库时跳过持久化测试。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>工作流中心管理员路由。</summary>
    private const string Route = "/api/v1/admin/workflow-center";

    /// <summary>复制来源经过验证并保存，但不能继承发布状态或资源授权。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CopyProvenanceIsValidatedPersistedAndDoesNotPublishOrGrantResources()
    {
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        var graph = WorkflowV2Fixtures.Graph();
        var original = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "原设计", schemaVersion = 1, graph });
        var copied = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "显式副本", schemaVersion = 1, graph, copyOrigin = new { source = "workflow", id = original.Id } });
        Assert.NotEqual(original.Id, copied.Id);
        Assert.Equal(new WorkflowCopyOrigin("workflow", original.Id), copied.CopyOrigin);
        using var update = await fixture.Client.PutAsJsonAsync($"/api/v1/admin/workflow-apis/{copied.Id}", new { name = "编辑副本", copyOrigin = new { source = "template", id = Guid.NewGuid() } }, Ct);
        update.EnsureSuccessStatusCode();
        await fixture.Db.SqlAsync($"DELETE FROM workflow WHERE workflow_id='{original.Id}'");
        var reread = (await fixture.Client.GetFromJsonAsync<WorkflowResponse>($"/api/v1/admin/workflow-apis/{copied.Id}", Ct))!;
        Assert.Equal("编辑副本", reread.Name); Assert.Equal(copied.CopyOrigin, reread.CopyOrigin);
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_run"));
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deployment"));
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_api_key"));
        using var missing = await fixture.Client.PostAsJsonAsync("/api/v1/admin/workflow-apis", new { name = "无效来源", schemaVersion = 1, graph, copyOrigin = new { source = "workflow", id = original.Id } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        fixture.Identity("user");
        using var forbidden = await fixture.Client.PostAsJsonAsync("/api/v1/workflows", new { name = "伪造来源", schemaVersion = 1, graph, copyOrigin = new { source = "workflow", id = copied.Id } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
    }

    /// <summary>模板历史使用原快照，旧诊断入口也必须遵循资源撤权。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task TemplateHistoryUsesItsOwnSnapshotAndLegacyDiagnosticsCannotBypassResourceRevocation()
    {
        // 先检查历史快照再撤销资源，证明旧诊断路径没有绕过相同授权规则。
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        var template = Guid.NewGuid(); var debug = Guid.NewGuid(); var run = Guid.NewGuid();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_template(template_id,name,created_at,updated_at) VALUES ('{{template}}','历史模板',now(),now());
            INSERT INTO workflow(workflow_id,device_id,name,debug_template_id,graph,created_at,updated_at)
            VALUES ('{{debug}}','{{fixture.Db.Device}}','模板调试','{{template}}','{"nodes":[],"edges":[]}',now(),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            VALUES ('{{run}}','{{debug}}','{{fixture.Db.Device}}','{"nodes":[{"id":"old-node","type":"text","data":{} }],"edges":[]}', 'succeeded',now(),now());
            """);
        var snapshot = $"{Route}/template/{template}/runs/{run}/snapshot";
        using var response = await fixture.Client.GetAsync(snapshot, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Contains("old-node", await response.Content.ReadAsStringAsync(Ct));
        using var wrongSource = await fixture.Client.GetAsync($"{Route}/workflow/{template}/runs/{run}/snapshot", Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongSource.StatusCode);
        using var details = await fixture.Client.GetAsync($"{Route}/template/{template}/runs/{run}/detail", Ct);
        details.EnsureSuccessStatusCode();
        await fixture.Db.SqlAsync($$"""
            UPDATE workflow_run SET graph_snapshot='{"schemaVersion":1,"nodes":[{"id":"knowledge","type":"knowledgeRetrieve","data":{} }],"edges":[]}',input_json=NULL
            WHERE run_id='{{run}}';
            """);
        foreach (var path in new[] { snapshot, $"{Route}/template/{template}/runs/{run}/detail", $"/api/v1/admin/requests/workflow/{run}" })
        {
            using var denied = await fixture.Client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain("old-node", await denied.Content.ReadAsStringAsync(Ct));
        }
        fixture.Identity("user");
        using var nonAdmin = await fixture.Client.GetAsync(snapshot, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, nonAdmin.StatusCode);
    }

    /// <summary>运行分页只返回当前工作流的安全摘要，不泄漏其他图或节点正文。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RunHistoryHasRealPaginationAndDoesNotExposeOtherWorkflowOrSnapshotBodies()
    {
        // 多条独立记录验证真实分页边界；负向检查不依赖前端隐藏字段。
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync();
        var workflow = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "分页历史合成" });
        var other = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "其他来源" });
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT gen_random_uuid(),workflow_id,device_id,'{"nodes":[],"edges":[],"secret-test-marker":"not-in-summary"}'::jsonb,
                CASE WHEN i <= 5 THEN 'failed' ELSE 'succeeded' END,now() + i * interval '1 minute',now()
            FROM workflow CROSS JOIN generate_series(1,25) i WHERE workflow_id='{{workflow.Id}}';
            """);
        var url = $"{Route}/workflow/{workflow.Id}/runs";
        var first = (await fixture.Client.GetFromJsonAsync<WorkflowCenterRunPage>(url, Ct))!;
        var second = (await fixture.Client.GetFromJsonAsync<WorkflowCenterRunPage>(url + "?offset=20", Ct))!;
        Assert.Equal(25, first.Total); Assert.Equal(25, second.Total);
        Assert.Equal(20, first.Items.Length); Assert.Equal(5, second.Items.Length);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        var failed = (await fixture.Client.GetFromJsonAsync<WorkflowCenterRunPage>(url + "?status=failed&limit=50", Ct))!;
        Assert.Equal(5, failed.Total); Assert.All(failed.Items, run => Assert.Equal("failed", run.Status));
        Assert.DoesNotContain("not-in-summary", await fixture.Client.GetStringAsync(url, Ct));
        using var wrongRun = await fixture.Client.GetAsync($"/api/v1/admin/workflow-apis/{other.Id}/runs/{first.Items[0].Id}/snapshot", Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongRun.StatusCode);
        using var wrongSource = await fixture.Client.GetAsync($"{Route}/template/{workflow.Id}/runs", Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongSource.StatusCode);
        using var invalid = await fixture.Client.GetAsync(url + "?status=anything&offset=-1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        fixture.Identity("user");
        using var denied = await fixture.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>历史快照只读且不可变，资源授权失效时拒绝返回内容。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RunSnapshotIsImmutableReadOnlyAndFailsClosedForInvalidResourceAuthorization()
    {
        // 修改草稿不能改写原运行；随后撤权验证快照读取仍要重新鉴权。
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync();
        var workflow = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "历史图合成" });
        var runId = Guid.NewGuid();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT '{{runId}}',workflow_id,device_id,'{"nodes":[{"id":"deleted-node","type":"text","position":{"x":80,"y":60},"data":{"label":"旧节点"} }],"edges":[]}'::jsonb,
                'succeeded',now(),now() FROM workflow WHERE workflow_id='{{workflow.Id}}';
            UPDATE workflow SET graph='{"nodes":[],"edges":[]}'::jsonb WHERE workflow_id='{{workflow.Id}}';
            """);
        var url = $"/api/v1/admin/workflow-apis/{workflow.Id}/runs/{runId}/snapshot";
        using var response = await fixture.Client.GetAsync(url, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var value = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(runId.ToString(), value["runId"]!.GetValue<string>());
        Assert.Equal("deleted-node", value["graph"]!["nodes"]![0]!["id"]!.GetValue<string>());
        var current = (await fixture.Client.GetFromJsonAsync<WorkflowResponse>($"/api/v1/admin/workflow-apis/{workflow.Id}", Ct))!;
        Assert.Empty(current.Graph!["nodes"]!.AsArray());
        await fixture.Db.SqlAsync($$"""
            UPDATE workflow_run SET graph_snapshot='{"schemaVersion":1,"nodes":[{"id":"knowledge","type":"knowledgeRetrieve","data":{} }],"edges":[]}'::jsonb,
                input_json=NULL WHERE run_id='{{runId}}';
            """);
        using var revoked = await fixture.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain("graph", await revoked.Content.ReadAsStringAsync(Ct));
        fixture.Identity("user");
        using var nonAdmin = await fixture.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, nonAdmin.StatusCode);
    }

    /// <summary>聚合两类来源仍保持独立标识，不按同名合并或暴露个人图。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AggregatesBothTypesWithoutMergingNamesOrLeakingPersonalGraphs()
    {
        // 使用同名不同来源与个人图对照，覆盖聚合范围及身份隔离。
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        var media = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "同名合成工作流" });
        var general = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis",
            new { name = "通用合成工作流", schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        var templateId = Guid.NewGuid();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_template(template_id,name,description,status,created_at,updated_at)
            VALUES ('{{templateId}}','同名合成工作流','安全合成用途','published',now(),now());
            """);
        fixture.Identity("user");
        await fixture.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "不可进入后台中心的个人图" });
        using var denied = await fixture.Client.GetAsync(Route, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        fixture.Identity("admin");
        using var response = await fixture.Client.GetAsync(Route, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var page = (await response.Content.ReadFromJsonAsync<WorkflowCenterPage>(Ct))!;
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count(item => item.Name == "同名合成工作流"));
        Assert.Contains(page.Items, item => item.Id == media.Id && item.Source == "workflow" && item.SchemaVersion == 1 && item.Kind == "media");
        Assert.Contains(page.Items, item => item.Id == templateId.ToString() && item.Source == "template" && item.Kind == "media");
        Assert.Contains(page.Items, item => item.Id == general.Id && item.SchemaVersion == 1 && item.Kind == "general");
        Assert.All(page.Items, item => Assert.Null(item.LatestRun));
        var filtered = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?kind=general", Ct))!;
        Assert.Equal(general.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal(1, filtered.Total);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("graph", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyHash", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>分页与总数使用同一筛选，空页和特殊字符搜索也保持字面量语义。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task PaginatesAndCountsTheSameFilterIncludingEmptyPagesAndLiteralSearch()
    {
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow(workflow_id,device_id,name,is_api,created_at,updated_at)
            SELECT gen_random_uuid(),'{{fixture.Db.Device}}','合成 ' || lpad(i::text,2,'0'),true,now(),now() + i * interval '1 minute'
            FROM generate_series(1,27) i;
            INSERT INTO workflow_template(template_id,name,created_at,updated_at)
            VALUES (gen_random_uuid(),'合成 100% literal',now(),now());
            """);
        var first = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?search=" + Uri.EscapeDataString("合成") + "&sort=name_asc", Ct))!;
        var second = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?search=" + Uri.EscapeDataString("合成") + "&sort=name_asc&offset=20", Ct))!;
        Assert.Equal(28, first.Total); Assert.Equal(first.Total, second.Total);
        Assert.Equal(20, first.Items.Length); Assert.Equal(8, second.Items.Length);
        Assert.Empty(first.Items.Select(item => (item.Source, item.Id)).Intersect(second.Items.Select(item => (item.Source, item.Id))));
        var empty = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?offset=200", Ct))!;
        Assert.Equal(28, empty.Total); Assert.Empty(empty.Items);
        var literal = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?search=%25", Ct))!;
        Assert.Equal("合成 100% literal", Assert.Single(literal.Items).Name);
        foreach (var query in new[] { "kind=unknown", "source=personal", "limit=10000", "offset=-1", "id=invalid", "view=unknown", "sort=DROP%20TABLE", "updatedFrom=2027-01-01&updatedTo=2026-01-01" })
        {
            using var invalid = await fixture.Client.GetAsync(Route + "?" + query, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    /// <summary>草稿状态按快照实际变化计算，失败视图只看最近整图运行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task UsesActualSnapshotChangesAndTheLatestRunRatherThanAnyPastFailure()
    {
        await using var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true);
        var graph = WorkflowV2Fixtures.Graph();
        var workflow = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "合成版本", schemaVersion = 1, graph });
        await fixture.PostAsync<WorkflowDeploymentResponse>($"/api/v1/admin/workflow-apis/{workflow.Id}/publish", new { description = "合成用途" });
        var published = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?view=api", Ct))!;
        Assert.Equal("saved", Assert.Single(published.Items).DraftStatus);
        await fixture.Db.SqlAsync($$"""
            UPDATE workflow SET updated_at=now() + interval '1 hour' WHERE workflow_id='{{workflow.Id}}';
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT gen_random_uuid(),workflow_id,device_id,graph,'failed',now() - interval '1 minute',now() FROM workflow WHERE workflow_id='{{workflow.Id}}';
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT gen_random_uuid(),workflow_id,device_id,graph,'succeeded',now(),now() FROM workflow WHERE workflow_id='{{workflow.Id}}';
            """);
        Assert.Empty((await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?view=failed", Ct))!.Items);
        Assert.Empty((await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?view=changed", Ct))!.Items);
        graph["nodes"]![1]!["data"]!["prompt"]![0]!["text"] = "合成变化";
        using var saved = await fixture.Client.PutAsJsonAsync($"/api/v1/admin/workflow-apis/{workflow.Id}", new { schemaVersion = 1, graph }, Ct);
        saved.EnsureSuccessStatusCode();
        var changed = Assert.Single((await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Route + "?view=changed", Ct))!.Items);
        Assert.Equal("changed", changed.DraftStatus); Assert.Equal("succeeded", changed.LatestRun!.Status);
        Assert.Equal(1, changed.ApiVersion);
    }
}
