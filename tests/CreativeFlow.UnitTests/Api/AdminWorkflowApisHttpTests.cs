using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.Infrastructure.Common;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Persistence.Migrations;
using CreativeFlow.Infrastructure.Security;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>用真实 MVC、SQL 和后台执行器验证后台 API 管理及普通用户隔离。</summary>
public sealed class AdminWorkflowApisHttpTests
{
    /// <summary>新版运行使用同一 SQL 队列和 MAF；重复提交被拒绝，结束节点重跑复用 LLM 输出。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task V2RunsPersistInputsAndReuseCompletedOutputs()
    {
        await using var fixture = await Fixture.CreateAsync(worker: true, v2: true);
        fixture.Models.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = WorkflowV2Fixtures.Graph();
        var draft = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "Synthetic AI", schemaVersion = 1, graph });
        var path = Base + "/" + draft.Id;
        using var legacyStart = await fixture.Client.PostAsJsonAsync(path + "/runs", new { }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, legacyStart.StatusCode);
        using var badInput = await fixture.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = 42 } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badInput.StatusCode);
        var first = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "original" } });
        using var duplicate = await fixture.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "duplicate" } }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        fixture.Models.Gate.SetResult(true);
        var completed = await fixture.WaitAsync(path + "/runs/" + first.Id);
        Assert.Equal(1, fixture.Models.Calls);
        Assert.Contains("Synthetic: Answer: original", completed.ToJsonString());
        graph["nodes"]![1]!["data"]!["prompt"]![0]!["text"] = "edited draft: ";
        using var saved = await fixture.Client.PutAsJsonAsync(path, new { schemaVersion = 1, graph }, Ct);
        saved.EnsureSuccessStatusCode();
        var partial = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs/" + first.Id + "/partial-rerun", new { fromNodeId = "end" });
        var partialResult = await fixture.WaitAsync(path + "/runs/" + partial.Id);
        Assert.Equal(1, fixture.Models.Calls);
        Assert.True(partialResult["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "llm")!["isReused"]!.GetValue<bool>());
        var regenerate = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs/" + partial.Id + "/partial-rerun", new { fromNodeId = "llm" });
        await fixture.WaitAsync(path + "/runs/" + regenerate.Id);
        Assert.Equal(2, fixture.Models.Calls); Assert.Equal("Answer: original", fixture.Models.Prompt);
        using var published = await fixture.Client.PostAsJsonAsync(path + "/publish", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var unsupportedVersion = await fixture.Client.PutAsJsonAsync(path, new { schemaVersion = 2 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedVersion.StatusCode);
        fixture.Identity("user");
        using var denied = await fixture.Client.PostAsJsonAsync(path + "/runs", new { inputs = new { question = "denied" } }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>结果不明确的 LLM 不能通过继续运行重复调用；用户显式新建运行才再次执行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task V2UnknownModelOutcomeCannotBeAutomaticallyResumed()
    {
        await using var fixture = await Fixture.CreateAsync(worker: true, v2: true);
        fixture.Models.Fail = true;
        var draft = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "Synthetic failure", schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        var path = Base + "/" + draft.Id;
        var first = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "fail" } });
        await fixture.WaitAsync(path + "/runs/" + first.Id, "failed");
        using var resume = await fixture.Client.PostAsJsonAsync(path + "/runs/" + first.Id + "/resume", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); Assert.Equal(1, fixture.Models.Calls);
        fixture.Models.Fail = false;
        var next = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "explicit new run" } });
        await fixture.WaitAsync(path + "/runs/" + next.Id);
        Assert.Equal(2, fixture.Models.Calls);
    }

    /// <summary>两类草稿共用稳定 schema 1；执行开关关闭时仍可保存，但不能提交模型运行。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task TypedDraftsUseSingleSchemaAndExecutionRequiresCapability()
    {
        await using var fixture = await Fixture.CreateAsync();
        var typed = await fixture.PostAsync<WorkflowResponse>(Base, new { schemaVersion = 1, graph = WorkflowV2Fixtures.Graph() });
        Assert.Equal(1, typed.SchemaVersion);
        var capabilities = await fixture.Client.GetFromJsonAsync<JsonNode>(Base + "/capabilities", Ct);
        Assert.False(capabilities!["enabled"]!.GetValue<bool>());
        Assert.Empty(capabilities["nodeTypes"]!.AsArray());
        using var start = await fixture.Client.PostAsJsonAsync(Base + "/" + typed.Id + "/runs", new { inputs = new { question = "disabled" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Equal(0, fixture.Models.Calls);
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_run"));
        var media = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "Unchanged media" });
        using var saved = await fixture.Client.PutAsJsonAsync(Base + "/" + media.Id, new { name = "Renamed media", schemaVersion = 1 }, Ct);
        saved.EnsureSuccessStatusCode();
        var reread = (await fixture.Client.GetFromJsonAsync<WorkflowResponse>(Base + "/" + media.Id, Ct))!;
        Assert.True(JsonNode.DeepEquals(media.Graph, reread.Graph));
        using var unsupported = await fixture.Client.PostAsJsonAsync(Base, new { schemaVersion = 2, graph = WorkflowV2Fixtures.Graph() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

    /// <summary>不允许回落到开发或生产数据库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>测试取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>管理入口。</summary>
    private const string Base = "/api/v1/admin/workflow-apis";
    /// <summary>独立的有效 PNG 测试图片。</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    /// <summary>普通用户和匿名用户均不能进入管理入口，旧发布路径不再存在。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ManagementAndLegacyEndpointsCannotBeUsedByOrdinaryUsers()
    {
        await using var fixture = await Fixture.CreateAsync();
        // 覆盖所有管理子路由及废弃发布路径，确保不能靠更换 HTTP 方法或 URL 绕过后台边界。
        var id = Guid.NewGuid();
        var run = Guid.NewGuid();
        foreach (var identity in new[] { "user", "anonymous" })
        {
            fixture.Identity(identity);
            foreach (var (method, path) in new (HttpMethod, string)[] {
                (HttpMethod.Get, ""), (HttpMethod.Post, ""), (HttpMethod.Get, $"/{id}"),
                (HttpMethod.Put, $"/{id}"), (HttpMethod.Post, $"/{id}/runs"),
                (HttpMethod.Get, $"/{id}/runs/latest"), (HttpMethod.Get, $"/{id}/runs/{run}"),
                (HttpMethod.Delete, $"/{id}/runs/{run}"), (HttpMethod.Post, $"/{id}/runs/{run}/resume"),
                (HttpMethod.Post, $"/{id}/runs/{run}/partial-rerun"),
                (HttpMethod.Get, $"/{id}/runs/{run}/partial-rerun/preview?fromNodeId=input"),
                (HttpMethod.Post, $"/{id}/publish"), (HttpMethod.Get, $"/{id}/deployments"),
                (HttpMethod.Delete, $"/{id}/deployment"), (HttpMethod.Get, $"/{id}/api-keys"),
                (HttpMethod.Post, $"/{id}/api-keys"), (HttpMethod.Delete, $"/{id}/api-keys/{run}"),
                (HttpMethod.Get, $"/{id}/external-runs"), (HttpMethod.Get, $"/{id}/assets"),
                (HttpMethod.Get, $"/{id}/assets/groups"), (HttpMethod.Get, $"/{id}/assets/{run}"),
                (HttpMethod.Post, $"/{id}/assets"),
            })
            {
                using var request = new HttpRequestMessage(method, Base + path) { Content = method == HttpMethod.Post && path.EndsWith("/assets", StringComparison.Ordinal)
                    ? new MultipartFormDataContent() : JsonContent.Create(new { }) };
                using var response = await fixture.Client.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            foreach (var (method, suffix) in new (HttpMethod, string)[] {
                (HttpMethod.Post, "publish"), (HttpMethod.Get, "deployments"),
                (HttpMethod.Delete, "deployment"), (HttpMethod.Get, "api-keys"),
                (HttpMethod.Post, "api-keys"), (HttpMethod.Delete, $"api-keys/{run}"),
                (HttpMethod.Get, "external-runs"),
            })
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/workflows/{id}/{suffix}") { Content = JsonContent.Create(new { }) };
                using var response = await fixture.Client.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        Assert.Equal(0, await fixture.Db.CountAsync("SELECT count(*) FROM workflow"));
    }

    /// <summary>仅管理员会话即可完成上传、编辑、调试、发布、外部执行、结果下载与密钥撤销。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AdminOnlySessionCanRunTheEntireApiLifecycle()
    {
        await using var fixture = await Fixture.CreateAsync(worker: true);
        var draft = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "Image API", graph = new { nodes = new object[0], edges = new object[0] } });
        var path = Base + "/" + draft.Id;
        var image = await fixture.UploadAsync<GeneratedAsset>(path + "/assets");
        using var preview = await fixture.Client.GetAsync(path + "/assets/" + image.AssetId, Ct);
        Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync(Ct));
        var graph = Graph(image.Url);
        using var save = await fixture.Client.PutAsJsonAsync(path, new { graph, name = "Published image API" }, Ct);
        save.EnsureSuccessStatusCode();
        var debug = await fixture.PostAsync<WorkflowRunResponse>(path + "/runs", new { });
        await fixture.WaitAsync(path + "/runs/" + debug.Id);
        var deployment = await fixture.PostAsync<WorkflowDeploymentResponse>(path + "/publish", new { description = "Image passthrough", maxConcurrency = 1 });
        Assert.Equal(1, deployment.Version);
        var key = await fixture.PostAsync<WorkflowApiKeyResponse>(path + "/api-keys", new { name = "Client" });
        Assert.NotNull(key.Key);
        var list = await fixture.Client.GetFromJsonAsync<AdminWorkflowApiListResponse>(Base, Ct);
        Assert.Equal("published", Assert.Single(list!.Apis).Status);

        // 外部调用仅发送新签发的 Bearer Key，不使用管理员或用户身份。
        fixture.Identity("anonymous");
        fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);
        var externalImage = await fixture.UploadAsync<ExternalImageUploadResponse>("/api/external/v1/assets/images");
        fixture.Client.DefaultRequestHeaders.Add("Idempotency-Key", "request-1");
        var input = new { inputs = new { image = new { assetId = externalImage.AssetId } } };
        var accepted = await fixture.PostAsync<ExternalWorkflowRunCreatedResponse>($"/api/external/v1/workflows/{draft.Id}/runs", input);
        var completed = await fixture.WaitAsync(accepted.StatusUrl);
        var resultUrl = completed["outputs"]!["result"]![0]!["url"]!.GetValue<string>();
        using var result = await fixture.Client.GetAsync(resultUrl, Ct);
        result.EnsureSuccessStatusCode();
        Assert.Equal(Png, await result.Content.ReadAsByteArrayAsync(Ct));

        fixture.Identity("admin");
        graph["externalApi"]!["inputs"]![0]!["key"] = "new_image";
        using var changed = await fixture.Client.PutAsJsonAsync(path, new { graph }, Ct);
        changed.EnsureSuccessStatusCode();
        Assert.Equal(2, (await fixture.PostAsync<WorkflowDeploymentResponse>(path + "/publish", new { })).Version);
        var history = await fixture.Client.GetFromJsonAsync<ExternalWorkflowRunListResponse>(path + "/external-runs", Ct);
        Assert.Equal(accepted.RunId, Assert.Single(history!.Runs).RunId);
        using var disable = await fixture.Client.DeleteAsync(path + "/deployment", Ct);
        disable.EnsureSuccessStatusCode();
        fixture.Identity("anonymous");
        var replay = await fixture.PostAsync<ExternalWorkflowRunCreatedResponse>($"/api/external/v1/workflows/{draft.Id}/runs", input);
        Assert.Equal(accepted.RunId, replay.RunId);
        Assert.Equal(1, replay.Version);
        fixture.Identity("admin");
        using var revoke = await fixture.Client.DeleteAsync(path + "/api-keys/" + key.Id, Ct);
        revoke.EnsureSuccessStatusCode();
        fixture.Identity("anonymous");
        using var denied = await fixture.Client.GetAsync(accepted.StatusUrl, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>历史发布归入后台后保留发布和密钥；原拥有者无法修改、删除或调试 API。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MigrationPreservesLegacyApiAndRemovesAllOrdinaryManagementPaths()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity("user");
        var legacy = await fixture.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "Legacy", graph = Graph("/api/v1/canvas/assets/" + Guid.NewGuid()) });
        using (var scope = fixture.App.Services.CreateScope())
        {
            var now = DateTimeOffset.UtcNow;
            var deployment = new WorkflowDeployment(WorkflowDeploymentId.New(), new(Guid.Parse(legacy.Id)), 0, 1,
                legacy.Graph!.ToJsonString(), "[]", "[]", "legacy", WorkflowDeploymentStatus.Published, 1, now, now, null);
            await scope.ServiceProvider.GetRequiredService<IWorkflowExternalApiRepository>().PublishAsync(new(fixture.Db.Device), deployment, Ct);
            var keyService = scope.ServiceProvider.GetRequiredService<IWorkflowApiKeyService>();
            var material = keyService.Generate();
            await scope.ServiceProvider.GetRequiredService<IWorkflowExternalApiRepository>().InsertApiKeyAsync(new(
                WorkflowApiKeyId.New(), deployment.WorkflowId, "legacy key", material.Prefix, material.Hint, material.Hash,
                true, null, null, now, null), Ct);
        }
        var assembly = typeof(MigrationRunner).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.Contains("0023__", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        await fixture.Db.SqlAsync(await reader.ReadToEndAsync(Ct));
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_deployment WHERE status = 'published'"));
        Assert.Equal(1, await fixture.Db.CountAsync("SELECT count(*) FROM workflow_api_key WHERE enabled"));
        Assert.Empty((await fixture.Client.GetFromJsonAsync<WorkflowListResponse>("/api/v1/workflows", Ct))!.Workflows);
        var path = "/api/v1/workflows/" + legacy.Id;
        var runId = Guid.NewGuid();
        foreach (var (method, suffix) in new (HttpMethod, string)[] {
            (HttpMethod.Get, ""), (HttpMethod.Put, ""), (HttpMethod.Delete, ""),
            (HttpMethod.Post, "/runs"), (HttpMethod.Get, "/runs/latest"),
            (HttpMethod.Get, $"/runs/{runId}"), (HttpMethod.Delete, $"/runs/{runId}"),
            (HttpMethod.Post, $"/runs/{runId}/resume"), (HttpMethod.Post, $"/runs/{runId}/partial-rerun"),
            (HttpMethod.Get, $"/runs/{runId}/partial-rerun/preview?fromNodeId=input"),
        })
        {
            using var request = new HttpRequestMessage(method, path + suffix) { Content = JsonContent.Create(new { }) };
            using var response = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        fixture.Identity("admin");
        var apis = await fixture.Client.GetFromJsonAsync<AdminWorkflowApiListResponse>(Base, Ct);
        Assert.Equal(legacy.Id, Assert.Single(apis!.Apis).Id);
        using var edit = await fixture.Client.PutAsJsonAsync(Base + "/" + legacy.Id, new { name = "Managed legacy" }, Ct);
        edit.EnsureSuccessStatusCode();
        var keys = await fixture.Client.GetFromJsonAsync<WorkflowApiKeyListResponse>(Base + "/" + legacy.Id + "/api-keys", Ct);
        Assert.Equal("legacy key", Assert.Single(keys!.ApiKeys).Name);
    }

    /// <summary>API 素材与普通用户素材隔离，管理员入口也不能用普通工作流标识提升权限。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AdminResourceScopeCannotBeForgedOrSharedAcrossApis()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity("user");
        var ordinary = await fixture.PostAsync<WorkflowResponse>("/api/v1/workflows", new { name = "Personal" });
        fixture.Identity("admin");
        using var forbiddenScope = await fixture.Client.GetAsync(Base + "/" + ordinary.Id, Ct);
        Assert.Equal(HttpStatusCode.NotFound, forbiddenScope.StatusCode);
        var first = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "API A" });
        var second = await fixture.PostAsync<WorkflowResponse>(Base, new { name = "API B" });
        var asset = await fixture.UploadAsync<GeneratedAsset>(Base + "/" + first.Id + "/assets");
        using var wrongApi = await fixture.Client.GetAsync(Base + "/" + second.Id + "/assets/" + asset.AssetId, Ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongApi.StatusCode);
        fixture.Identity("user");
        using var deny = await fixture.Client.GetAsync(Base + "/" + first.Id + "/assets/" + asset.AssetId, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, deny.StatusCode);
        var list = await fixture.Client.GetFromJsonAsync<WorkflowListResponse>("/api/v1/workflows", Ct);
        Assert.Equal(ordinary.Id, Assert.Single(list!.Workflows).Id);
    }

    /// <summary>不调用上游模型的图片输入到输出图，同时覆盖外部图片参数绑定。</summary>
    internal static JsonNode Graph(string url) => JsonNode.Parse($$$"""
        {"nodes":[{"id":"input","type":"image","data":{"assetUrl":"{{{url}}}"}},{"id":"output","type":"imageOutput","data":{}}],
         "edges":[{"id":"edge","source":"input","sourceHandle":"image","target":"output","targetHandle":"image"}],
         "externalApi":{"schemaVersion":1,"inputs":[{"key":"image","label":"Image","type":"image","required":true,"target":{"nodeId":"input","field":"assetUrl"}}],
         "outputs":[{"key":"result","label":"Result","type":"image","nodeId":"output"}]}}
        """)!;

    /// <summary>仅认证标记和素材物理存储替换为测试实现，HTTP、权限、数据库和执行器使用生产实现。</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        /// <summary>隔离数据库。</summary>
        public ImageJobDatabase Db { get; private set; } = null!;
        /// <summary>测试 HTTP 宿主。</summary>
        public WebApplication App { get; private set; } = null!;
        /// <summary>测试客户端。</summary>
        public HttpClient Client { get; private set; } = null!;
        /// <summary>只属于当前测试的受控模型端口。</summary>
        public WorkflowV2Fixtures.Models Models { get; } = new();
        /// <summary>通用 HTTP 节点的合成业务端口。</summary>
        public HttpRequestFixtures.Transport Http { get; } = new();
        /// <summary>合成知识请求计数，不连接模型网络。</summary>
        public KnowledgeFixture.Embeddings Embeddings { get; } = new();
        /// <summary>合成向量索引。</summary>
        public KnowledgeFixture.Index Index { get; } = new();
        /// <summary>知识测试独立管理员。</summary>
        public Guid AdminId { get; } = Guid.NewGuid();
        /// <summary>知识测试库标识。</summary>
        public Guid LibraryId { get; private set; }
        /// <summary>知识测试固定索引版本。</summary>
        public Guid VersionId { get; private set; }
        /// <summary>测试收到的实时事件，用于验证知识正文不通过设备长连接推送。</summary>
        public ConcurrentQueue<WorkflowRunChangedEvent> Events { get; } = new();

        /// <summary>配置真实管理和外部调用依赖。</summary>
        public static async Task<Fixture> CreateAsync(bool worker = false, bool v2 = false, bool knowledge = false, bool http = false,
            Action<Fixture, IServiceCollection>? configure = null)
        {
            // 每个请求和后台执行各自恢复账户范围；测试身份只替换会话认证，不替换权限过滤器。
            var fixture = new Fixture { Db = await ImageJobDatabase.CreateAsync() };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration["Workflow:Enabled"] = v2.ToString();
            builder.Services.AddSingleton<IWorkflowV2Models>(fixture.Models);
            builder.Services.AddSingleton(new WorkflowV2Network(v2, new EmbeddingEndpointPolicy(new EmbeddingOptions(), false)));
            builder.Services.AddSingleton(fixture.Db.Source);
            builder.Services.AddScoped<IAccountContext, TestAccountContext>();
            builder.Services.AddScoped<CreativeFlowConnectionFactory>();
            builder.Services.AddScoped<IAccountRepository, AccountRepository>();
            builder.Services.AddScoped<IWorkflowRepository, WorkflowRepository>();
            builder.Services.AddScoped<IWorkflowCenterRepository, WorkflowCenterRepository>();
            builder.Services.AddScoped<IRequestHistoryRepository, RequestHistoryRepository>();
            builder.Services.AddScoped<CreativeFlow.Application.Features.Administration.RequestHistoryService>();
            builder.Services.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
            builder.Services.AddScoped<IWorkflowRunEventRepository, WorkflowRunEventRepository>();
            builder.Services.AddScoped<IWorkflowRunJournal, WorkflowRunJournal>();
            builder.Services.AddScoped<WorkflowPublicTextStream>();
            builder.Services.AddScoped<IHttpCredentialRepository, HttpCredentialRepository>();
            builder.Services.AddSingleton<IWorkflowHttpTransport>(fixture.Http);
            // 手动测试宿主必须提供 Controller 依赖的脚本端口；禁用实现避免测试执行外部脚本。
            builder.Services.AddSingleton<IWorkflowScriptRunner>(new CreativeFlow.Infrastructure.Skills.OpenSandboxWorkflowScriptRunner(
                new CreativeFlow.Infrastructure.Skills.DisabledSkillScriptRunner(new SkillsOptions()), new SkillsOptions()));
            builder.Services.AddSingleton(new WorkflowHttpOptions(http));
            builder.Services.AddScoped<HttpRequestService>();
            builder.Services.AddScoped<WorkflowV2PublicationService>();
            if (knowledge)
            {
                builder.Services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
                builder.Services.AddScoped<IKnowledgeAccessRepository, KnowledgeAccessRepository>();
                builder.Services.AddSingleton<IKnowledgeEmbedding>(fixture.Embeddings);
                builder.Services.AddSingleton<IKnowledgeIndexStore>(fixture.Index);
                builder.Services.AddSingleton(new CreativeFlow.Application.Features.Knowledge.KnowledgeWorkflowOptions(true));
                builder.Services.AddScoped<CreativeFlow.Application.Features.Knowledge.KnowledgeWorkflowService>();
                builder.Services.AddSingleton<IKnowledgeReranker>(new KnowledgeFixture.Reranker());
                builder.Services.AddScoped<CreativeFlow.Application.Features.Knowledge.KnowledgeRetrievalService>();
                var repository = new KnowledgeRepository(fixture.Db.Source);
var service = new CreativeFlow.Application.Features.Knowledge.KnowledgeLibraryService(repository, fixture.Embeddings, fixture.Index, new(repository, fixture.Embeddings, fixture.Index, new KnowledgeFixture.Reranker()));
                fixture.LibraryId = (await service.CreateAsync(new("合成 RAG", "不含正式资料", Guid.NewGuid()), Ct)).Id;
                var input = new KnowledgeImportRequest([new("manual.md", "合成手册：保修两年。")]);
                fixture.VersionId = (await service.ImportAsync(fixture.LibraryId, input with { PreviewDigest = CreativeFlow.Application.Features.Knowledge.KnowledgeTextParser.Preview(input).Digest }, Ct)).Id;
                await new CreativeFlow.Application.Features.Knowledge.KnowledgeIndexProcessor(repository, fixture.Embeddings, fixture.Index).ProcessNextAsync(Ct);
                Assert.True(await repository.ActivateAsync(fixture.LibraryId, fixture.VersionId, Ct), "Synthetic knowledge fixture must be ready before workflow tests run.");
            }
            if (knowledge || http) await fixture.Db.SqlAsync($"INSERT INTO creativeflow_admin(admin_id,username,password_hash,must_change_password,created_at,updated_at) VALUES ('{fixture.AdminId}','resource-test','synthetic-hash-not-usable',false,now(),now())");
            builder.Services.AddScoped<IWorkflowRuntime, MafWorkflowRuntime>();
            builder.Services.AddScoped<IWorkflowExternalApiRepository, WorkflowExternalApiRepository>();
            builder.Services.AddScoped<ICanvasAssetRepository, CanvasAssetRepository>();
            builder.Services.AddScoped<IWorkflowV2AssetResolver, WorkflowV2AssetResolver>();
            builder.Services.AddScoped<BackgroundAccountScope>();
            builder.Services.AddSingleton<IAssetStore>(fixture.Db.Store);
            builder.Services.AddSingleton(new ExternalWorkflowApiOptions { GlobalConcurrency = 1 });
            var aiOptions = new AiOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            builder.Services.AddSingleton(aiOptions);
            builder.Services.AddSingleton<IApiKeyProtector, AesGcmApiKeyProtector>();
            builder.Services.AddSingleton<IWorkflowApiKeyService, WorkflowApiKeyService>();
            builder.Services.AddSingleton<ExternalWorkflowRateLimiter>();
            builder.Services.AddSingleton<ISignedMediaUrlService>(new SignedMediaUrlService(aiOptions,
                new AuthenticationOptions { PublicOrigin = "http://localhost" }, new SystemClock()));
            builder.Services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IRealtimeNotifier>((_, args) =>
            { if (args is { Length: > 2 } && args[2] is WorkflowRunChangedEvent change) fixture.Events.Enqueue(change); return Task.CompletedTask; }));
            if (worker)
            {
                builder.Services.AddWorkflowExecution();
                builder.Services.AddHostedService<WorkflowRunWorker>();
            }
            builder.Services.AddControllers().AddApplicationPart(typeof(AdminWorkflowApisController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            configure?.Invoke(fixture, builder.Services);
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var identity = context.Request.Headers["Test-Identity"].ToString();
                if (identity is "admin" or "admin-device") context.Items[AdminContextItems.Admin] = new AdminAccount(fixture.AdminId, "admin", "unused", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                if (identity is "user" or "other" or "device" or "admin-device")
                {
                    context.Items[DeviceContextItems.DeviceId] = new DeviceId(identity == "other" ? fixture.Db.OtherDevice : fixture.Db.Device);
                    if (identity != "device")
                    {
                        var account = new AccountId(identity == "other" ? fixture.Db.OtherAccount : fixture.Db.Account);
                        context.Items[DeviceContextItems.AccountId] = account;
                        context.RequestServices.GetRequiredService<IAccountContext>().AccountId = account;
                    }
                }
                await next(context);
            });
            app.MapControllers();
            await app.StartAsync(Ct);
            fixture.App = app;
            fixture.Client = app.GetTestClient();
            fixture.Identity("admin");
            return fixture;
        }

        /// <summary>切换请求认证状态，不使用实际管理员或用户凭据。</summary>
        public void Identity(string identity)
        {
            Client.DefaultRequestHeaders.Remove("Test-Identity");
            Client.DefaultRequestHeaders.Add("Test-Identity", identity);
        }

        /// <summary>提交 JSON 并在错误时保留服务端错误内容。</summary>
        public async Task<T> PostAsync<T>(string path, object body)
        {
            using var response = await Client.PostAsJsonAsync(path, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        /// <summary>提交真实多部分 PNG 上传。</summary>
        public async Task<T> UploadAsync<T>(string path)
        {
            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(Png);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(bytes, "file", "input.png");
            using var response = await Client.PostAsync(path, form, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        /// <summary>等待真实后台工作流到达成功状态，失败时直接报告响应。</summary>
        public async Task<JsonNode> WaitAsync(string path, string expected = "succeeded")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var response = await Client.GetFromJsonAsync<JsonNode>(path, timeout.Token);
                var status = response!["status"]!.GetValue<string>();
                if (status == expected) return response;
                Assert.True(status is "queued" or "running", response.ToJsonString());
                await Task.Delay(50, timeout.Token);
            }
        }

        /// <summary>释放测试客户端，停止并释放独立 API 宿主，最后清理隔离数据库。</summary>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    /// <summary>每个 HTTP 或后台作用域各自保存账户范围。</summary>
    private sealed class TestAccountContext : IAccountContext
    {
        /// <summary>保存测试请求作用域内的账号标识，供后台 API 数据归属断言使用。</summary>
        public AccountId? AccountId { get; set; }
    }

}
