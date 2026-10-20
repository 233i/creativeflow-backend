using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>隔离 schema 验证管理删除的完整 HTTP、并发、历史及权限边界，不访问收费模型。</summary>
public sealed class WorkflowDeletionHttpTests
{
    /// <summary>没有显式测试连接时跳过，不回落到业务 schema。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前用例的取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Center = "/api/v1/admin/workflow-center";

    /// <summary>停用发布不等于结束外部任务；终态 API 运行、幂等键和原 API Key 记录都保留。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ExternalQueueBlocksDeletionAndRetainsIdempotencyHistoryAfterCompletion()
    {
        await using var fixture = await Fixture();
        var (source, id, workflow) = await Create(fixture, "media");
        await SeedDeployment(fixture, workflow, "disabled");
        var key = Guid.NewGuid(); var run = Guid.NewGuid();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_api_key(api_key_id,workflow_id,name,key_prefix,key_hint,key_hash,created_at)
            VALUES ('{{key}}','{{workflow}}','合成客户端','synthetic-key','not-a-secret',decode(repeat('01',32),'hex'),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at,
                trigger_type,deployment_id,api_key_id,request_id,idempotency_key,request_hash,input_json)
            SELECT '{{run}}',w.workflow_id,w.device_id,w.graph,'queued',now(),now(),'api',d.deployment_id,'{{key}}',
                'synthetic-request','keep-idempotency',decode(repeat('02',32),'hex'),'{}'
            FROM workflow w JOIN workflow_deployment d USING(workflow_id) WHERE w.workflow_id='{{workflow}}';
            """);
        var preview = await Preview(fixture, source, id);
        Assert.Equal(1, preview.QueuedRuns); Assert.Equal(1, preview.ApiKeys); Assert.False(preview.CanDelete);
        using var blocked = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await fixture.Db.SqlAsync($"UPDATE workflow_run SET status='succeeded' WHERE run_id='{run}'");
        // 两个同时提交的删除都可安全完成，但只有一个审计事件。
        var responses = await Task.WhenAll(Delete(fixture, source, id, preview), Delete(fixture, source, id, preview));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); response.Dispose(); }
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deletion_audit"));
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE idempotency_key='keep-idempotency'"));
        Assert.Equal(1, await fixture.Db.CountAsync($"SELECT count(*) FROM workflow_api_key WHERE api_key_id='{key}'"));
        using var history = await fixture.Client.GetAsync($"{Center}/{source}/{id}/runs/{run}/detail", Ct);
        history.EnsureSuccessStatusCode();
    }

    /// <summary>旧模板管理入口同样软删除，不级联丢失模板调试历史。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task LegacyTemplateDeleteUsesTheSameAuditAndPreservesDebugGraph()
    {
        await using var fixture = await Fixture();
        var (_, id, workflow) = await Create(fixture, "template");
        using var deleted = await fixture.Client.DeleteAsync($"/api/v1/admin/workflow-templates/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, await fixture.Db.CountAsync($"SELECT count(*) FROM workflow WHERE workflow_id='{workflow}'"));
        Assert.Equal(1, await fixture.Db.CountAsync($"SELECT count(*) FROM workflow_template WHERE template_id='{id}' AND deleted_at IS NOT NULL"));
        Assert.Equal(1, await fixture.Db.CountAsync($"SELECT count(*) FROM workflow_deletion_audit WHERE actor_id='{fixture.AdminId}'"));
    }

    /// <summary>媒体、通用编排和模板共用软删除，并保留节点输出、发布快照、素材、副本及审计。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("media")]
    [InlineData("general")]
    [InlineData("template")]
    public async Task DeletesOnlyTheManagedResourceAndKeepsHistoryAndAssets(string kind)
    {
        await using var fixture = await Fixture();
        var (source, id, workflow) = await Create(fixture, kind);
        var run = Guid.NewGuid(); var asset = Guid.NewGuid();
        var stored = await fixture.App.Services.GetRequiredService<CreativeFlow.Application.Abstractions.Ai.IAssetStore>()
            .SaveAsync([1, 2, 3], "image/png", ".png", Ct);
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT '{{run}}',workflow_id,device_id,'{"nodes":[],"edges":[]}', 'succeeded',now(),now() FROM workflow WHERE workflow_id='{{workflow}}';
            INSERT INTO workflow_node_run(run_id,node_id,node_type,status,output)
            VALUES ('{{run}}','result','text','succeeded','{"texts":["retained-output"]}');
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size)
            SELECT '{{asset}}',device_id,'image','image/png','{{stored.StoragePath}}',3 FROM workflow WHERE workflow_id='{{workflow}}';
            INSERT INTO workflow(workflow_id,device_id,name,is_api,copied_from_source,copied_from_id,created_at,updated_at)
            VALUES (gen_random_uuid(),'{{fixture.Db.Device}}','独立副本',true,'{{source}}','{{id}}',now(),now());
            """);
        if (source == "workflow") await SeedDeployment(fixture, workflow, "disabled");
        var preview = await Preview(fixture, source, id);
        Assert.True(preview.CanDelete); Assert.Equal(1, preview.HistoryRuns); Assert.Equal(1, preview.IndependentCopies);
        using var deleted = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var repeated = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deletion_audit"));
        Assert.Equal(1, await fixture.Db.CountAsync($"SELECT count(*) FROM canvas_asset WHERE asset_id='{asset}'"));
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_node_run WHERE output->'texts'->>0='retained-output'"));
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow WHERE name='独立副本' AND deleted_at IS NULL"));
        var visible = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Center, Ct))!;
        Assert.DoesNotContain(visible.Items, item => item.Id == id.ToString() && item.Source == source);
        var tombstones = (await fixture.Client.GetFromJsonAsync<WorkflowCenterPage>(Center + "?view=deleted", Ct))!;
        Assert.NotNull(Assert.Single(tombstones.Items).DeletedAt);
        var history = (await fixture.Client.GetFromJsonAsync<WorkflowCenterRunPage>($"{Center}/{source}/{id}/runs", Ct))!;
        Assert.Equal(run.ToString(), Assert.Single(history.Items).Id);
        foreach (var view in new[] { "detail", "snapshot" })
        {
            using var response = await fixture.Client.GetAsync($"{Center}/{source}/{id}/runs/{run}/{view}", Ct);
            response.EnsureSuccessStatusCode();
        }
        var resourceUrl = source == "workflow" ? $"/api/v1/admin/workflow-apis/{id}" : $"/api/v1/admin/workflow-templates/{id}";
        fixture.Identity("admin-device");
        using var readDraft = await fixture.Client.GetAsync(resourceUrl, Ct);
        Assert.Equal(HttpStatusCode.NotFound, readDraft.StatusCode);
        using var execute = await fixture.Client.PostAsJsonAsync(resourceUrl + (source == "template" ? "/debug-workflow" : "/runs"), new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, execute.StatusCode);
        using var publish = await fixture.Client.PostAsJsonAsync(resourceUrl + "/publish", new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, publish.StatusCode);
        if (source == "workflow")
        {
            Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deployment"));
            // 已删除 API 只允许读取历史素材，不能通过素材列表或上传重新操作资源。
            using var assetList = await fixture.Client.GetAsync(resourceUrl + "/assets", Ct);
            Assert.Equal(HttpStatusCode.NotFound, assetList.StatusCode);
            using var retainedAsset = await fixture.Client.GetAsync(resourceUrl + "/assets/" + asset, Ct);
            Assert.Equal(HttpStatusCode.OK, retainedAsset.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await retainedAsset.Content.ReadAsByteArrayAsync(Ct));
        }
        // 删除不使历史资源授权校验失效；失去证据授权时仍拒绝交付原输出。
        await fixture.Db.SqlAsync($$"""
            UPDATE workflow_run SET graph_snapshot='{"schemaVersion":1,"nodes":[{"id":"knowledge","type":"knowledgeRetrieve","data":{} }],"edges":[]}',input_json=NULL WHERE run_id='{{run}}';
            """);
        using var revoked = await fixture.Client.GetAsync($"{Center}/{source}/{id}/runs/{run}/detail", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        fixture.Identity("user");
        using var unauthorized = await fixture.Client.GetAsync($"{Center}/{source}/{id}/runs", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    /// <summary>预览通过后新增排队或运行任务也会阻止删除，包括模板调试和 API 触发。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("media", "queued")]
    [InlineData("general", "running")]
    [InlineData("template", "queued")]
    public async Task RechecksActiveRunsInsteadOfTrustingPreview(string kind, string status)
    {
        await using var fixture = await Fixture();
        var (source, id, workflow) = await Create(fixture, kind);
        var preview = await Preview(fixture, source, id);
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT gen_random_uuid(),workflow_id,device_id,graph,'{{status}}',now(),now() FROM workflow WHERE workflow_id='{{workflow}}';
            """);
        using var response = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await Preview(fixture, source, id)).CanDelete);
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deletion_audit"));
        if (source == "template")
        {
            using var legacy = await fixture.Client.DeleteAsync($"/api/v1/admin/workflow-templates/{id}", Ct);
            Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        }
        await fixture.Db.SqlAsync($"UPDATE workflow_run SET status='cancelled' WHERE workflow_id='{workflow}'");
        using var done = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
    }

    /// <summary>所有已发布版本和模板上架状态均是阻断条件，不自动下架或取消任务。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("general")]
    [InlineData("template")]
    public async Task PublishedResourcesMustBeExplicitlyDisabled(string kind)
    {
        await using var fixture = await Fixture();
        var (source, id, workflow) = await Create(fixture, kind);
        if (source == "template") await fixture.Db.SqlAsync($"UPDATE workflow_template SET status='published' WHERE template_id='{id}'");
        else await SeedDeployment(fixture, workflow, "published");
        var preview = await Preview(fixture, source, id);
        Assert.False(preview.CanDelete);
        Assert.Equal(1, preview.PublishedApis + preview.PublishedTemplates);
        using var blocked = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deletion_audit"));
    }

    /// <summary>名称、行版本、来源和管理员身份同时验证，不允许管理员中心删除普通用户私有工作流。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RejectsStaleConfirmationWrongSourceAndNonAdmin()
    {
        await using var fixture = await Fixture();
        var (source, id, _) = await Create(fixture, "media");
        var preview = await Preview(fixture, source, id);
        using var badName = await Delete(fixture, source, id, preview with { Name = "wrong" });
        Assert.Equal(HttpStatusCode.Conflict, badName.StatusCode);
        await fixture.Db.SqlAsync($"UPDATE workflow SET name='他人已修改' WHERE workflow_id='{id}'");
        using var stale = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var wrongSource = await fixture.Client.GetAsync($"{Center}/template/{id}/deletion-preview", Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongSource.StatusCode);
        foreach (var identity in new[] { "user", "anonymous" })
        {
            fixture.Identity(identity);
            using var deniedPreview = await fixture.Client.GetAsync($"{Center}/{source}/{id}/deletion-preview", Ct);
            using var deniedDelete = await Delete(fixture, source, id, preview);
            Assert.Equal(HttpStatusCode.Unauthorized, deniedPreview.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, deniedDelete.StatusCode);
        }
        fixture.Identity("user");
        var personal = await fixture.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "私人图" });
        fixture.Identity("admin");
        using var hidden = await fixture.Client.GetAsync($"{Center}/workflow/{personal.Id}/deletion-preview", Ct);
        using var cannotDelete = await Delete(fixture, "workflow", Guid.Parse(personal.Id), preview);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cannotDelete.StatusCode);
    }

    /// <summary>与未提交的排队事务竞争时，删除等待后能读到新任务；删除成功后禁止迟到提交和重新发布。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("general")]
    [InlineData("template")]
    public async Task SerializesDeletionWithAdmissionAndGuardsLateWrites(string kind)
    {
        await using var fixture = await Fixture();
        var (source, id, workflow) = await Create(fixture, kind);
        using var scope = fixture.App.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var staleDraft = source == "workflow"
            ? await workflows.FindApiAsync(new(workflow), Ct)
            : await workflows.FindAsync(new(fixture.Db.Device), new(workflow), Ct);
        Assert.NotNull(staleDraft);
        var preview = await Preview(fixture, source, id);
        await using var connection = await fixture.Db.Source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
                SELECT gen_random_uuid(),workflow_id,device_id,graph,'queued',now(),now() FROM workflow WHERE workflow_id='{{workflow}}';
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }
        var deletion = Delete(fixture, source, id, preview);
        await Task.Delay(150, Ct);
        Assert.False(deletion.IsCompleted);
        await transaction.CommitAsync(Ct);
        using var blocked = await deletion;
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await fixture.Db.SqlAsync("UPDATE workflow_run SET status='cancelled'");
        using var done = await Delete(fixture, source, id, preview);
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.False(await workflows.UpdateAsync(staleDraft with { Name = "迟到保存不能覆盖历史" }, Ct));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            SELECT gen_random_uuid(),workflow_id,device_id,graph,'queued',now(),now() FROM workflow WHERE workflow_id='{{workflow}}';
            """));
        Assert.Equal("workflow_deleted", exception.ConstraintName);
        var resurrection = await Assert.ThrowsAsync<PostgresException>(() => fixture.Db.SqlAsync("UPDATE workflow_run SET status='queued'"));
        Assert.Equal("workflow_deleted", resurrection.ConstraintName);
        if (source == "workflow")
        {
            var publish = await Assert.ThrowsAsync<PostgresException>(() => SeedDeployment(fixture, workflow, "published"));
            Assert.Equal("workflow_deleted", publish.ConstraintName);
        }
    }

    /// <summary>注册真实模板仓储，测试旧模板删除入口与新入口具有相同保护。</summary>
    private static Task<AdminWorkflowApisHttpTests.Fixture> Fixture() => AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true,
        configure: (_, services) => services.AddScoped<IWorkflowTemplateRepository, WorkflowTemplateRepository>());

    /// <summary>创建测试独占的后台工作流或模板调试图。</summary>
    private static async Task<(string Source, Guid Id, Guid Workflow)> Create(AdminWorkflowApisHttpTests.Fixture fixture, string kind)
    {
        if (kind != "template")
        {
            var doc = await fixture.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new
            { name = "删除测试 " + kind, schemaVersion = 1, graph = kind == "general" ? WorkflowV2Fixtures.Graph() : null });
            return ("workflow", Guid.Parse(doc.Id), Guid.Parse(doc.Id));
        }
        var id = Guid.NewGuid(); var workflow = Guid.NewGuid();
        await fixture.Db.SqlAsync($$"""
            INSERT INTO workflow_template(template_id,name,created_at,updated_at) VALUES ('{{id}}','删除测试 template',now(),now());
            INSERT INTO workflow(workflow_id,device_id,name,debug_template_id,created_at,updated_at)
            VALUES ('{{workflow}}','{{fixture.Db.Device}}','合成调试','{{id}}',now(),now());
            """);
        return ("template", id, workflow);
    }

    /// <summary>读取测试资源的真实预览响应。</summary>
    private static async Task<WorkflowDeletionPreview> Preview(AdminWorkflowApisHttpTests.Fixture fixture, string source, Guid id) =>
        (await fixture.Client.GetFromJsonAsync<WorkflowDeletionPreview>($"{Center}/{source}/{id}/deletion-preview", Ct))!;

    /// <summary>模拟前端显式确认请求，保留预览版本以验证竞态。</summary>
    private static Task<HttpResponseMessage> Delete(AdminWorkflowApisHttpTests.Fixture fixture, string source, Guid id, WorkflowDeletionPreview preview) =>
        fixture.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Center}/{source}/{id}")
        { Content = JsonContent.Create(new DeleteWorkflowRequest(preview.Name, preview.Revision)) }, Ct);

    /// <summary>构造不含外部资源的发布快照，不实际发布收费模型。</summary>
    private static Task SeedDeployment(AdminWorkflowApisHttpTests.Fixture fixture, Guid workflow, string status) => fixture.Db.SqlAsync($$"""
        INSERT INTO workflow_deployment(deployment_id,workflow_id,version,schema_version,graph_snapshot,input_schema,output_schema,status,created_at,published_at)
        SELECT gen_random_uuid(),workflow_id,1,1,graph,'{}','{}','{{status}}',now(),now() FROM workflow WHERE workflow_id='{{workflow}}';
        """);
}
