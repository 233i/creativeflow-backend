using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.CreativeApps;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 HTTP、独立 SQL schema 和 MAF 执行；上游仅用合成图片，不访问收费模型。</summary>
public sealed class CreativeAppsHttpTests
{
    /// <summary>仅在显式配置的隔离数据库上运行。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>测试取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>管理员工作流测试路由前缀。</summary>
    private const string Admin = "/api/v1/admin/workflow-apis";
    /// <summary>客户公共创作测试路由前缀。</summary>
    private const string Public = "/api/v1/creative-apps";

    /// <summary>验证版本、幂等、下架和客户隔离，且不创建外部 API 发布。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task PublicationVersionIdempotencyOwnershipAndRetirementAreIndependentOfApi()
    {
        await using var f = await Create();
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Synthetic public", graph = Graph() });
        var path = Admin + "/" + draft.Id + "/creative-app";
        var release = await Publish(f, path);
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM workflow_deployment"));
        f.Identity("user");
        var catalog = await f.Client.GetFromJsonAsync<JsonObject>(Public, Ct);
        Assert.Single(catalog!["items"]!.AsArray());
        Assert.DoesNotContain("target", catalog.ToJsonString()); Assert.DoesNotContain("providerId", catalog.ToJsonString());
        using (var denied = await f.Client.PutAsJsonAsync(path, new { name = "forbidden" }, Ct)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var body = Input(release, "same-key");
        var starts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body)));
        var id = starts[0]["id"]!.GetValue<string>();
        Assert.All(starts, r => Assert.Equal(id, r["id"]!.GetValue<string>()));
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM creative_app_run"));
        var different = Input(release, "same-key", "different");
        using (var conflict = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", different, Ct)) Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        f.Identity("other");
        using (var denied = await f.Client.GetAsync(Public + "/runs/" + id, Ct)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using (var denied = await f.Client.DeleteAsync(Public + "/runs/" + id, Ct)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var second = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body);
        Assert.NotEqual(id, second["id"]!.GetValue<string>());
        f.Identity("admin");
        var next = await Publish(f, path);
        Assert.Equal(2, next["version"]!.GetValue<int>());
        using (var retired = await f.Client.DeleteAsync(path, Ct)) retired.EnsureSuccessStatusCode();
        f.Identity("user");
        var replay = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body);
        Assert.Equal(id, replay["id"]!.GetValue<string>()); Assert.Equal(1, replay["version"]!.GetValue<int>());
        using (var blocked = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", Input(next, "new-key"), Ct)) Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        using (var cancel = await f.Client.DeleteAsync(Public + "/runs/" + id, Ct)) cancel.EnsureSuccessStatusCode();
        var history = await f.Client.GetFromJsonAsync<JsonObject>(Public + "/runs", Ct);
        Assert.Single(history!["items"]!.AsArray());
        Assert.Equal("cancelled", (await f.Client.GetFromJsonAsync<JsonObject>(Public + "/runs/" + id, Ct))!["status"]!.GetValue<string>());
    }

    /// <summary>真实 MAF 执行图片节点，并只向调用客户交付成品。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task RealWorkerUsesFrozenModelOnceAndPersistsCustomerOutput()
    {
        var images = new Images(); await using var f = await Create(true, images);
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "MAF public", graph = Graph() });
        var release = await Publish(f, Admin + "/" + draft.Id + "/creative-app"); f.Identity("user");
        var body = Input(release, "run-once");
        var started = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body);
        var id = started["id"]!.GetValue<string>();
        var completed = await f.WaitAsync(Public + "/runs/" + id);
        Assert.Equal(1, images.Calls); Assert.Equal("public test", images.Prompt);
        Assert.Equal(3, completed["nodes"]!.AsArray().Count);
        var output = Assert.Single(completed["outputs"]!.AsArray());
        Assert.Equal("image", output!["kind"]!.GetValue<string>());
        using (var content = await f.Client.GetAsync(output["url"]!.GetValue<string>(), Ct)) Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        var replay = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body); Assert.Equal(id, replay["id"]!.GetValue<string>()); Assert.Equal(1, images.Calls);
        Assert.DoesNotContain(f.Events, e => e.Output is not null);
        f.Identity("other");
        using (var denied = await f.Client.GetAsync(output["url"]!.GetValue<string>(), Ct)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    /// <summary>必填图片不继承管理员示例，拒绝跨账户素材和超额提交。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task RequiredReferencesCannotFallBackToPublisherSamplesAndLimitsAreEnforced()
    {
        await using var f = await Create();
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "References", graph = Graph() });
        var upload = await f.UploadAsync<JsonObject>(Admin + "/" + draft.Id + "/assets");
        var graph = Graph(); graph["nodes"]!.AsArray().Insert(1, JsonNode.Parse("""{"id":"reference","type":"image","data":{}}""")); graph["nodes"]![1]!["data"]!["assetUrl"] = upload["url"]!.DeepClone();
        graph["edges"]!.AsArray().Add((JsonNode)JsonNode.Parse("""{"id":"r","source":"reference","sourceHandle":"image","target":"generate","targetHandle":"image"}""")!);
        graph["externalApi"]!["inputs"]!.AsArray().Add((JsonNode)JsonNode.Parse("""{"key":"image","label":"Reference","type":"image","required":true,"target":{"nodeId":"reference","field":"assetUrl"}}""")!);
        using (var saved = await f.Client.PutAsJsonAsync(Admin + "/" + draft.Id, new { graph }, Ct)) saved.EnsureSuccessStatusCode();
        var path = Admin + "/" + draft.Id + "/creative-app"; var release = await Publish(f, path, 1); f.Identity("user");
        using (var required = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", Input(release, "required"), Ct)) Assert.Equal(HttpStatusCode.BadRequest, required.StatusCode);
        var bad = Input(release, "foreign"); bad["inputs"]!["image"] = new JsonObject { ["assetId"] = upload["url"]!.GetValue<string>().Split('/').Last() };
        using (var foreign = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", bad, Ct)) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        bad["inputs"]!["image"]!["assetId"] = "not-a-guid";
        using (var invalid = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", bad, Ct)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var own = await f.UploadAsync<JsonObject>("/api/v1/canvas/assets"); bad["inputs"]!["image"]!["assetId"] = own["url"]!.GetValue<string>().Split('/').Last();
        await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", bad);
        bad["requestKey"] = "over-limit";
        using (var quota = await f.Client.PostAsJsonAsync(Public + "/" + draft.Id + "/runs", bad, Ct)) Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
    }

    /// <summary>固定图片需明确授权，复用文件并保护已上架应用引用。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task FixedImagesRequireAnExplicitGrantAndPublicReleasesBlockDeletion()
    {
        await using var f = await Create();
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Shared references", graph = Graph() });
        var asset = await f.UploadAsync<JsonObject>(Admin + "/" + draft.Id + "/assets");
        var graph = Graph(); graph["nodes"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "fixed", ["type"] = "image", ["data"] = new JsonObject { ["assetUrl"] = asset["url"]!.DeepClone() } });
        graph["edges"]!.AsArray().Add((JsonNode)JsonNode.Parse("""{"id":"r","source":"fixed","sourceHandle":"image","target":"generate","targetHandle":"image"}""")!);
        using (var saved = await f.Client.PutAsJsonAsync(Admin + "/" + draft.Id, new { graph }, Ct)) saved.EnsureSuccessStatusCode();
        var path = Admin + "/" + draft.Id + "/creative-app";
        using (var denied = await f.Client.PutAsJsonAsync(path, new { name = "Shared", category = "image" }, Ct)) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var published = await f.Client.PutAsJsonAsync(path, new { name = "Shared", category = "image", shareFixedAssets = true, coverAssetId = asset["url"]!.GetValue<string>().Split('/').Last() }, Ct);
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync(Ct));
        var release = (await published.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var deletionPath = "/api/v1/admin/workflow-center/workflow/" + draft.Id + "/deletion-preview";
        var impact = (await f.Client.GetFromJsonAsync<JsonObject>(deletionPath, Ct))!;
        Assert.False(impact["canDelete"]!.GetValue<bool>()); Assert.Equal(1, impact["publicApps"]!.GetValue<int>());
        f.Identity("user"); var accepted = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", Input(release, "fixed"));
        var id = accepted["id"]!.GetValue<string>();
        Assert.Equal(1, await f.Db.CountAsync($"SELECT count(*) FROM canvas_asset WHERE device_id='{f.Db.Device}' AND name='公共创作参考图'"));
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(DISTINCT storage_path) FROM canvas_asset"));
        Assert.True(await f.Db.CountAsync("SELECT count(*) FROM asset_reference_context WHERE source_kind='creative_app_release' AND protection='protected'") > 0);
        f.Identity("admin"); var detail = await f.Client.GetFromJsonAsync<JsonObject>("/api/v1/admin/workflow-center/workflow/" + draft.Id + "/runs/" + id + "/detail", Ct); Assert.Equal(id, detail!["id"]!.GetValue<string>());
        using (var off = await f.Client.DeleteAsync(path, Ct)) off.EnsureSuccessStatusCode();
        Assert.Equal(0, await f.Db.CountAsync("SELECT count(*) FROM asset_reference_context WHERE source_kind='creative_app_release' AND protection='protected'"));
        using (var activate = await f.Client.PostAsJsonAsync(path + "/versions/" + release["releaseId"]!.GetValue<string>() + "/activate", new { }, Ct)) activate.EnsureSuccessStatusCode();
        Assert.Equal(2, await f.Db.CountAsync("SELECT count(*) FROM creative_app_audit WHERE action IN ('publish','activate')"));
    }

    /// <summary>并行领取任务不能越过应用及客户并发上限。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task ConcurrentWorkersRespectPerApplicationAndPerCustomerLimits()
    {
        await using var f = await Create();
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Concurrency", graph = Graph() });
        var release = await Publish(f, Admin + "/" + draft.Id + "/creative-app"); f.Identity("user");
        for (var i = 0; i < 3; i++) await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", Input(release, "a" + i));
        f.Identity("other"); for (var i = 0; i < 3; i++) await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", Input(release, "b" + i));
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => { using var scope = f.App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().ClaimNextAsync(DateTimeOffset.UtcNow, Ct); }));
        Assert.InRange(claims.Count(r => r is not null), 1, 2);
        for (var i = 0; i < 3; i++) { using var scope = f.App.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().ClaimNextAsync(DateTimeOffset.UtcNow, Ct); }
        Assert.Equal(2, await f.Db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='running'"));
        Assert.Equal(1, await f.Db.CountAsync($"SELECT count(*) FROM workflow_run WHERE status='running' AND device_id='{f.Db.Device}'"));
        Assert.Equal(1, await f.Db.CountAsync($"SELECT count(*) FROM workflow_run WHERE status='running' AND device_id='{f.Db.OtherDevice}'"));
    }

    /// <summary>验证视频模型参数、完成检查点、媒体所有权和 Range 返回。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task VideoUsesPublishedModelAndKeepsOwnedRangeReadableOutput()
    {
        var video = new Videos(); await using var f = await Create(worker: true, video: video);
        var graph = Graph(); graph["nodes"]![1]!["type"] = "videoGenerate"; graph["nodes"]![1]!["data"] = new JsonObject { ["duration"] = 10 };
        graph["nodes"]![2]!["type"] = "videoOutput"; graph["edges"]![1]!["sourceHandle"] = "video"; graph["edges"]![1]!["targetHandle"] = "video";
        graph["externalApi"]!["outputs"]![0]!["type"] = "video";
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Synthetic video", graph });
        using var published = await f.Client.PutAsJsonAsync(Admin + "/" + draft.Id + "/creative-app", new { name = "Video", category = "video" }, Ct);
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync(Ct));
        var release = (await published.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        f.Identity("user"); var body = Input(release, "video-once"); var run = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body);
        using var scope = f.App.Services.CreateScope(); var jobs = scope.ServiceProvider.GetRequiredService<IVideoJobRepository>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        VideoJob? job = null;
        while (job is null) { job = (await jobs.ListActiveAsync(10, timeout.Token)).FirstOrDefault(); if (job is null) await Task.Delay(50, timeout.Token); }
        Assert.Equal(1, video.Calls); Assert.Equal("synthetic-video", video.Request!.ModelKey); Assert.Equal(10, video.Request.DurationSeconds);
        using var content = new MemoryStream([0, 1, 2, 3, 4, 5, 6, 7]);
        var file = await f.Db.Store.SaveAsync(content, "video/mp4", "mp4", 1024, Ct, $"video-job:{job.JobId:N}");
        await jobs.CompleteFileAsync(job, file, Ct);
        var completed = await f.WaitAsync(Public + "/runs/" + run["id"]!.GetValue<string>());
        var output = Assert.Single(completed["outputs"]!.AsArray()); Assert.Equal("video", output!["kind"]!.GetValue<string>());
        using var ranged = new HttpRequestMessage(HttpMethod.Get, output["url"]!.GetValue<string>());
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4);
        using var response = await f.Client.SendAsync(ranged, Ct); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode); Assert.Equal(new byte[] { 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync(Ct));
        await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", body); Assert.Equal(1, video.Calls);
        f.Identity("other"); using var denied = await f.Client.GetAsync(output["url"]!.GetValue<string>(), Ct); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    /// <summary>失败查询及 SSE 不重复计费，也不泄露内部图或上游正文。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task FailedRunAndStatusStreamAreSafeAndNeverAutomaticallyRegenerate()
    {
        var images = new Images { Fail = true }; await using var f = await Create(true, images);
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "Failure", graph = Graph() });
        var release = await Publish(f, Admin + "/" + draft.Id + "/creative-app"); f.Identity("user");
        var input = Input(release, "failed-once"); var accepted = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", input);
        var id = accepted["id"]!.GetValue<string>(); var done = await f.WaitAsync(Public + "/runs/" + id, "failed");
        Assert.DoesNotContain("publisher-hidden", done.ToJsonString()); Assert.Equal(1, images.Calls);
        var events = await f.Client.GetStringAsync(Public + "/runs/" + id + "/events", Ct);
        Assert.Contains("event: progress", events); Assert.Contains("\"status\":\"failed\"", events); Assert.DoesNotContain("graph", events); Assert.DoesNotContain("publisher-hidden", events);
        var replay = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", input); Assert.Equal(id, replay["id"]!.GetValue<string>()); Assert.Equal(1, images.Calls);
        using (var forbidden = await f.Client.GetAsync("/api/v1/workflows/" + draft.Id + "/runs/" + id + "/snapshot", Ct)) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        f.Identity("other"); using var denied = await f.Client.GetAsync(Public + "/runs/" + id + "/events", Ct); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    /// <summary>历史产物按账户和声明输出分页；应用下架后仍可查询，不调用模型。</summary>
    [Fact(Skip = "需要独立数据库", SkipWhen = nameof(NoDatabase))]
    public async Task OutputHistoryFiltersPagesAndPreservesOwnershipAfterRetirement()
    {
        var images = new Images(); await using var f = await Create(true, images);
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "History", graph = Graph() });
        var path = Admin + "/" + draft.Id + "/creative-app";
        var release = await Publish(f, path); f.Identity("user");
        var started = await f.PostAsync<JsonObject>(Public + "/" + draft.Id + "/runs", Input(release, "history-once"));
        var id = started["id"]!.GetValue<string>();
        var completed = await f.WaitAsync(Public + "/runs/" + id);
        var output = Assert.Single(completed["outputs"]!.AsArray())!;
        var assetId = output["url"]!.GetValue<string>().Split('/').Last();
        // Replicate declared output positions to exercise pagination without 25 model requests.
        await f.Db.SqlAsync($"UPDATE workflow_node_run SET output=jsonb_build_object('images',(SELECT jsonb_agg('/api/v1/canvas/assets/{assetId}'::text) FROM generate_series(1,25))) WHERE run_id='{id}' AND node_id='result'");
        var first = (await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs?search=synthetic&kind=image&days=7", Ct))!;
        Assert.Equal(24, first["items"]!.AsArray().Count); Assert.True(first["hasMore"]!.GetValue<bool>());
        Assert.All(first["items"]!.AsArray(), item => {
            Assert.Equal(id, item!["runId"]!.GetValue<string>()); Assert.Equal(assetId, item["assetId"]!.GetValue<string>());
            Assert.Equal(1, item["version"]!.GetValue<int>());
        });
        var last = (await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs?offset=24", Ct))!;
        Assert.Single(last["items"]!.AsArray()); Assert.False(last["hasMore"]!.GetValue<bool>());
        Assert.NotEqual(first["items"]![0]!["id"]!.GetValue<string>(), last["items"]![0]!["id"]!.GetValue<string>());
        foreach (var query in new[] { "kind=video", "search=no-match", "appId=" + Guid.NewGuid(), "offset=48", "search=%25" })
            Assert.Empty((await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs?" + query, Ct))!["items"]!.AsArray());
        foreach (var query in new[] { "days=-1", "offset=-1", "kind=other" })
        { using var invalid = await f.Client.GetAsync(Public + "/outputs?" + query, Ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        f.Identity("other"); Assert.Empty((await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs", Ct))!["items"]!.AsArray());
        f.Identity("admin"); using (var retired = await f.Client.DeleteAsync(path, Ct)) retired.EnsureSuccessStatusCode();
        f.Identity("user");
        Assert.Equal(24, (await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs?appId=" + draft.Id + "&search=" + id, Ct))!["items"]!.AsArray().Count);
        Assert.DoesNotContain("graph", first.ToJsonString()); Assert.DoesNotContain("storage_path", first.ToJsonString()); Assert.Equal(1, images.Calls);
        await f.Db.SqlAsync($"UPDATE canvas_asset SET deleted_at=now() WHERE asset_id='{assetId}'");
        Assert.Empty((await f.Client.GetFromJsonAsync<JsonObject>(Public + "/outputs", Ct))!["items"]!.AsArray());
    }

    private sealed class Videos : IVideoGenerationDispatcher
    {
        /// <summary>视频提交次数，用于断言查询不再次提交。</summary>
        public int Calls;
        /// <summary>捕获冻结模型和视频参数。</summary>
        public VideoGenerationRequest? Request;
        /// <summary>合成视频提交只返回固定上游标识。</summary>
        public Task<VideoSubmitResult> SubmitAsync(ProviderType protocol, VideoGenerationRequest request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Request = request; return Task.FromResult(VideoSubmitResult.Success("synthetic-upstream")); }
        /// <summary>不允许测试意外执行视频续接。</summary>
        public Task<VideoSubmitResult> ExtendAsync(ProviderType protocol, VideoExtensionRequest request, CancellationToken ct) => throw new InvalidOperationException("No extension expected");
        /// <summary>由测试直接保存合成完成结果，禁止外部轮询。</summary>
        public Task<VideoPollResult> PollAsync(ProviderType protocol, AiProvider provider, string apiKey, string jobId, CancellationToken ct) => throw new InvalidOperationException("Synthetic completion is persisted by the test");
    }

    /// <summary>以默认安全限制发布一个合成图片应用。</summary>
    private static async Task<JsonObject> Publish(AdminWorkflowApisHttpTests.Fixture f, string path, int daily = 50)
    {
        using var response = await f.Client.PutAsJsonAsync(path, new { name = "Public synthetic", category = "image", dailyLimit = daily }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }
    /// <summary>生成带明确版本及幂等标识的客户输入。</summary>
    private static JsonObject Input(JsonObject release, string key, string prompt = "public test") => new() { ["releaseId"] = release["releaseId"]!.DeepClone(), ["requestKey"] = key, ["inputs"] = new JsonObject { ["prompt"] = prompt } };
    /// <summary>构造提示词、图片生成及结果节点的合成媒体图。</summary>
    private static JsonObject Graph() => JsonNode.Parse("""
        {"nodes":[{"id":"prompt","type":"text","data":{"text":"private sample"}},{"id":"generate","type":"imageGenerate","data":{"count":1,"aspect":"1:1","quality":"auto"}},{"id":"result","type":"imageOutput","data":{}}],
        "edges":[{"id":"p","source":"prompt","sourceHandle":"text","target":"generate","targetHandle":"prompt"},{"id":"o","source":"generate","sourceHandle":"image","target":"result","targetHandle":"image"}],
        "externalApi":{"schemaVersion":1,"inputs":[{"key":"prompt","label":"Prompt","type":"string","required":true,"target":{"nodeId":"prompt","field":"text"}}],"outputs":[{"key":"result","label":"Image","type":"image","nodeId":"result"}]}}
        """)!.AsObject();
    /// <summary>创建隔离 HTTP、数据库与真实 Worker，替换收费模型端口。</summary>
    private static async Task<AdminWorkflowApisHttpTests.Fixture> Create(bool worker = false, Images? images = null, Videos? video = null)
    {
        var providerId = ProviderId.New(); var modelId = ModelId.New();
        var provider = new AiProvider(providerId, null, "Synthetic", null, null, video is null ? ProviderType.OpenAiImages : ProviderType.OpenAiVideo, null, "https://synthetic.invalid", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        { Models = [new AiModel(modelId, providerId, video is null ? "synthetic-image" : "synthetic-video", "Synthetic", true, null, video is null ? ModelCategory.Image : ModelCategory.Video, null, null, null, true, false, false, 0, DateTimeOffset.UnixEpoch)] };
        var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: worker, configure: (_, services) =>
        {
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IVideoFrameService>((_, _) => throw new InvalidOperationException("Unexpected frame extraction")));
            services.AddScoped<ICreativeAppRepository, CreativeAppRepository>(); services.AddScoped<CreativeAppService>();
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IProviderRepository>((_, _) => Task.FromResult<AiProvider?>(provider)));
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IModelSlotRepository>((_, _) => Task.FromResult<IReadOnlyList<ConfiguredModelSlot>>([new(video is null ? "image" : "video", providerId, modelId, DateTimeOffset.UnixEpoch)])));
            services.AddSingleton<IImageGenerationDispatcher>(images ?? new Images());
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IImageGenerationSettingsRepository>((_, _) => Task.FromResult(new ConfiguredImageGenerationSettings("base64", DateTimeOffset.UnixEpoch))));
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IImageOutputDownloader>((_, _) => throw new InvalidOperationException("Unexpected download")));
            services.AddScoped<IVideoJobRepository, VideoJobRepository>();
            services.AddSingleton<IVideoGenerationDispatcher>(video ?? ExternalWorkflowHandlersTests.RepositoryProxy.Create<IVideoGenerationDispatcher>((_, _) => throw new InvalidOperationException("Unexpected video call")));
        });
        await fixture.Db.SqlAsync($"INSERT INTO ai_provider(provider_id,device_id,name,type,base_url) VALUES('{providerId.Value}','{fixture.Db.Device}','Synthetic','openai-video','https://synthetic.invalid')");
        return fixture;
    }
    private sealed class Images : IImageGenerationDispatcher
    {
        /// <summary>记录上游提交次数，检测重复收费风险。</summary>
        public int Calls;
        /// <summary>保留传给合成模型的提示词，用于绑定验证。</summary>
        public string? Prompt;
        /// <summary>模拟上游失败，不连接外部模型。</summary>
        public bool Fail;
        /// <summary>返回合成 PNG 或受控失败，记录实际模型调用次数。</summary>
        public Task<ImageGenerationResult> GenerateAsync(ProviderType protocol, ImageGenerationRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Prompt = request.Prompt;
            if (Fail) return Task.FromResult(ImageGenerationResult.Failure(500, "publisher-hidden-upstream-details"));
            return Task.FromResult(ImageGenerationResult.Success([new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/6VfWk0AAAAASUVORK5CYII="), "image/png")]));
        }
    }
}
