using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Http;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 MVC、事务日志和 MAF Worker 的 AG-UI 闭环；仅使用合成输入及受控流式模型。</summary>
[Collection("Knowledge index database lock")]
public sealed class AgUiWorkflowHttpTests
{
    /// <summary>测试库必须显式指定，不能回落到开发或正式连接。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>所有 HTTP、流和数据库等待共享测试运行器的取消标记。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>真实后台发布端点，用合成管理员创建独立工作流。</summary>
    private const string Admin = "/api/v1/admin/workflow-apis";

    /// <summary>首个增量早于完成，客户端身份稳定；子帧续读、幂等重试与 SDK 解析不重新调用模型。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RealWorkerStreamsBeforeEndAndPreciselyResumesWithoutDuplicateModelCall()
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, frames.Last);
        await using var f = await WorkflowStreamHttpTests.Fixture(upstream);
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var submitted = await Submit(f, workflow.Id, key, Input("client-one"));
        if (!submitted.IsSuccessStatusCode) Assert.Fail(await submitted.Content.ReadAsStringAsync(Ct)); Assert.Equal("text/event-stream", submitted.Content.Headers.ContentType!.MediaType);
        Assert.True(submitted.Headers.CacheControl!.NoStore); Assert.Equal("no", submitted.Headers.GetValues("X-Accel-Buffering").Single());
        var internalId = submitted.Headers.GetValues("X-Workflow-Run-ID").Single();
        var url = submitted.Headers.GetValues("X-Workflow-Events-URL").Single();
        using var reader = new StreamReader(await submitted.Content.ReadAsStreamAsync(Ct));
        var start = await Next(reader, "RUN_STARTED");
        Assert.Equal("client-one", start.Event["runId"]!.GetValue<string>()); Assert.Equal("thread-one", start.Event["threadId"]!.GetValue<string>());
        Assert.NotEqual(internalId, start.Event["runId"]!.GetValue<string>());
        var accepted = await Next(reader, "CUSTOM"); Assert.Equal(internalId, accepted.Event["value"]!["internalRunId"]!.GetValue<string>());
        var first = await Next(reader, "TEXT_MESSAGE_CONTENT"); Assert.Equal("合成", first.Event["delta"]!.GetValue<string>());
        Assert.False(upstream.Gate.Task.IsCompleted); Assert.Equal(1, upstream.Calls);
        submitted.Dispose(); upstream.Gate.SetResult();
        using var resumed = await Send(f, key, HttpMethod.Get, url, cursor: first.Id);
        var replay = await resumed.Content.ReadAsStringAsync(Ct);
        var replayEvents = Parse(replay);
        Assert.DoesNotContain(replayEvents, frame => frame.Event["type"]!.GetValue<string>() is "RUN_STARTED" or "TEXT_MESSAGE_START");
        Assert.DoesNotContain(replayEvents, frame => frame.Event["delta"]?.GetValue<string>() == "合成");
        Assert.Equal("合成回答", replayEvents.Single(frame => frame.Event["type"]!.GetValue<string>() == "RUN_FINISHED").Event["result"]!["answer"]!.GetValue<string>());
        Assert.DoesNotContain("secret", replay); Assert.Equal(1, upstream.Calls);
        using var repeated = await Submit(f, workflow.Id, key, Input("client-one"));
        Assert.Equal(internalId, repeated.Headers.GetValues("X-Workflow-Run-ID").Single());
        var complete = await repeated.Content.ReadAsStringAsync(Ct);
        var all = Parse(complete);
        foreach (var step in all.Where(frame => frame.Event["type"]!.GetValue<string>() == "STEP_STARTED"))
        {
            var name = step.Event["stepName"]!.GetValue<string>();
            var end = Assert.Single(all, frame => frame.Event["type"]!.GetValue<string>() == "STEP_FINISHED" && frame.Event["stepName"]!.GetValue<string>() == name);
            Assert.True(all.IndexOf(step) < all.IndexOf(end));
        }
        Assert.Equal(3, all.Count(frame => frame.Event["type"]!.GetValue<string>() == "STEP_STARTED"));
        // Official SDK accepts the actual HTTP stream, not just a synthetic formatter sample.
        using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(complete));
        var official = new List<BaseEvent>();
        await foreach (var item in new SseEventStreamFormatter().ReadAsync(bytes, Ct)) official.Add(item);
        Assert.IsType<RunStartedEvent>(official[0]); Assert.IsType<RunFinishedEvent>(official[^1]);
        using var changedInput = await Submit(f, workflow.Id, key, Input("client-one", "different")); Assert.Equal(HttpStatusCode.Conflict, changedInput.StatusCode);
        var thread = Input("client-one"); thread["threadId"] = "another-thread";
        using var changedThread = await Submit(f, workflow.Id, key, thread); Assert.Equal(HttpStatusCode.Conflict, changedThread.StatusCode);
        using var changedKey = await Submit(f, workflow.Id, key, Input("client-one"), "different-idempotency"); Assert.Equal(HttpStatusCode.Conflict, changedKey.StatusCode);
        // Cursor inside the first text record resumes its second frame, not a second message start.
        var textStart = all.Single(frame => frame.Event["type"]!.GetValue<string>() == "TEXT_MESSAGE_START");
        using var split = await Send(f, key, HttpMethod.Get, url, cursor: textStart.Id);
        var partial = Parse(await split.Content.ReadAsStringAsync(Ct));
        Assert.Equal("TEXT_MESSAGE_CONTENT", partial[0].Event["type"]!.GetValue<string>());
        using var invalidPart = await Send(f, key, HttpMethod.Get, url, cursor: textStart.Id![..textStart.Id!.LastIndexOf(':')] + ":999"); Assert.Equal(HttpStatusCode.BadRequest, invalidPart.StatusCode);
        using var ahead = await Send(f, key, HttpMethod.Get, url, cursor: internalId + ":999999:1"); Assert.Equal(HttpStatusCode.BadRequest, ahead.StatusCode);
        using var otherRun = await Send(f, key, HttpMethod.Get, url, cursor: Guid.NewGuid() + ":1:1"); Assert.Equal(HttpStatusCode.BadRequest, otherRun.StatusCode);
        using var terminalCursor = await Send(f, key, HttpMethod.Get, url, cursor: all[^1].Id); Assert.Empty(await terminalCursor.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, upstream.Calls);
        await f.Db.SqlAsync($"DELETE FROM workflow_run WHERE run_id='{internalId}'");
        Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event"));
    }

    /// <summary>主动取消和上游流缺结束帧都不能把预览作为成功结果交付。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)][InlineData(true)]
    public async Task CancelAndBrokenModelNeverDeliverPartialSuccess(bool broken)
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, broken ? "" : frames.Last);
        await using var f = await WorkflowStreamHttpTests.Fixture(upstream);
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var submitted = await Submit(f, workflow.Id, key, Input("terminal"));
        submitted.EnsureSuccessStatusCode(); var id = submitted.Headers.GetValues("X-Workflow-Run-ID").Single();
        using var reader = new StreamReader(await submitted.Content.ReadAsStreamAsync(Ct)); await Next(reader, "TEXT_MESSAGE_CONTENT");
        if (!broken) { using var cancel = await Send(f, key, HttpMethod.Delete, "/api/external/v1/runs/" + id); Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        upstream.Gate.SetResult();
        var terminal = await Next(reader, broken ? "RUN_ERROR" : "RUN_FINISHED");
        Assert.Null(terminal.Event["result"]);
        if (broken) Assert.Equal("workflow_failed", terminal.Event["code"]!.GetValue<string>());
        else Assert.Equal("cancelled", terminal.Event["outcome"]!["type"]!.GetValue<string>());
        Assert.Equal(1, upstream.Calls);
    }

    /// <summary>刻意在日志读取与写响应之间撤销 Key，复核必须挡住已缓存正文。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RevokingKeyAfterReadingBatchBlocksCachedTextAndOtherKeys()
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, frames.Last);
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true,
            configure: (fixture, services) => {
                services.AddScoped<IWorkflowV2Models>(sp => new StreamModels(fixture.Models, sp.GetRequiredService<WorkflowPublicTextStream>(), upstream));
                services.AddScoped<IWorkflowRunJournal>(sp => new AfterReadJournal(new WorkflowRunJournal(sp.GetRequiredService<CreativeFlowConnectionFactory>()),
                    () => fixture.Db.SqlAsync("UPDATE workflow_api_key SET revoked_at=now(),enabled=false")));
            });
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var submitted = await Submit(f, workflow.Id, key, Input("revoke"));
        submitted.EnsureSuccessStatusCode();
        var stream = await submitted.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Parse(stream), frame => frame.Event["type"]!.GetValue<string>() == "TEXT_MESSAGE_CONTENT");
        var error = Parse(stream).Last().Event; Assert.Equal("stream_access_lost", error["code"]!.GetValue<string>());
        Assert.True(error["metadata"]!["clearPreview"]!.GetValue<bool>());
        upstream.Gate.SetResult();
        using var denied = await Send(f, key, HttpMethod.Get, submitted.Headers.GetValues("X-Workflow-Events-URL").Single()); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>知识引用的授权在实时订阅和重连时都重新检查。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KnowledgeGrantIsRecheckedDuringAgUiStream()
    {
        using var upstream = new StreamingTextTransport("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"{\\\"text\\\":\\\"合成答案\"}}]}\n\n");
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true,
            configure: (fixture, services) => services.AddScoped<IWorkflowV2Models>(sp => new WorkflowStreamHttpTests.KnowledgeModels(fixture.Models, sp.GetRequiredService<WorkflowPublicTextStream>(), upstream)));
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f, WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, f.VersionId));
        var path = $"/api/v1/admin/knowledge-bases/{f.LibraryId}/api-key-grants/{key.Id}";
        using (var grant = await f.Client.PutAsJsonAsync(path, new { enabled = true, reason = "synthetic AG-UI" }, Ct)) grant.EnsureSuccessStatusCode();
        using var submitted = await Submit(f, workflow.Id, key, Input("knowledge"));
        submitted.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await submitted.Content.ReadAsStreamAsync(Ct));
        var text = await Next(reader, "TEXT_MESSAGE_CONTENT"); Assert.Equal("合成答案", text.Event["delta"]!.GetValue<string>());
        using (var revoke = await f.Client.PutAsJsonAsync(path, new { enabled = false, reason = "synthetic revoke" }, Ct)) revoke.EnsureSuccessStatusCode();
        var error = await Next(reader, "RUN_ERROR"); Assert.Equal("stream_access_lost", error.Event["code"]!.GetValue<string>());
        upstream.Gate.SetResult();
        using var reconnect = await Send(f, key, HttpMethod.Get, submitted.Headers.GetValues("X-Workflow-Events-URL").Single(), cursor: text.Id);
        Assert.Equal(HttpStatusCode.Forbidden, reconnect.StatusCode);
    }

    /// <summary>订阅限流和不受支持的客户端协议功能在后台受理前拒绝。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task StreamAdmissionAndInvalidProtocolRequestsCannotEnqueueWork()
    {
        using var upstream = new StreamingTextTransport(""); await using var f = await WorkflowStreamHttpTests.Fixture(upstream, worker: false);
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        f.App.Services.GetRequiredService<ExternalWorkflowApiOptions>().MaxStreamsPerApiKey = 1;
        using (var lease = f.App.Services.GetRequiredService<ExternalWorkflowRateLimiter>().AcquireStream(new WorkflowApiKeyId(Guid.Parse(key.Id))))
        { using var denied = await Submit(f, workflow.Id, key, Input("limit")); Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode); Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run")); }
        foreach (var bad in new[] { new JsonObject(), Input("bad whitespace"), Input("state"), Input("context"), Input("images") })
        {
            if (bad["runId"]?.GetValue<string>() == "state") bad["state"] = new JsonObject { ["mutation"] = true };
            if (bad["runId"]?.GetValue<string>() == "context") bad["context"] = new JsonArray(new JsonObject { ["description"] = "client context", ["value"] = "unsafe" });
            if (bad["runId"]?.GetValue<string>() == "images") { bad.Remove("forwardedProps"); bad["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["id"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "binary", ["mimeType"] = "image/png", ["data"] = "bad" }) }); }
            using var invalid = await Submit(f, workflow.Id, key, bad); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run")); Assert.Equal(0, upstream.Calls);
        using var cursorOnNewTask = await Send(f, key, HttpMethod.Post, $"/api/agui/v1/workflows/{workflow.Id}/runs", Input("cursor-new"), cursor: Guid.NewGuid() + ":1:1");
        Assert.Equal(HttpStatusCode.BadRequest, cursorOnNewTask.StatusCode); Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run"));
        using var queued = await Submit(f, workflow.Id, key, Input("accepted")); queued.EnsureSuccessStatusCode(); queued.Dispose();
        Assert.Equal(1, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run"));
    }

    /// <summary>媒体直通图仍使用原引擎和素材签名，其他 Key 不可订阅它的结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MediaRunsReturnSignedPublishedAssetsWithoutChangingTheMediaEngine()
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true);
        var draft = await f.PostAsync<WorkflowResponse>(Admin, new { name = "AG-UI media", graph = new { nodes = new object[0], edges = new object[0] } });
        var image = await f.UploadAsync<GeneratedAsset>($"{Admin}/{draft.Id}/assets");
        using (var save = await f.Client.PutAsJsonAsync($"{Admin}/{draft.Id}", new { graph = AdminWorkflowApisHttpTests.Graph(image.Url) }, Ct)) save.EnsureSuccessStatusCode();
        await f.PostAsync<WorkflowDeploymentResponse>($"{Admin}/{draft.Id}/publish", new { });
        var key = await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{draft.Id}/api-keys", new { name = "AG-UI" });
        var other = await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{draft.Id}/api-keys", new { name = "Other" });
        f.Identity("anonymous"); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", key.Key);
        var uploaded = await f.UploadAsync<ExternalImageUploadResponse>("/api/external/v1/assets/images");
        var input = Input("media"); input["forwardedProps"]!["inputs"] = new JsonObject { ["image"] = new JsonObject { ["assetId"] = uploaded.AssetId } };
        using var submitted = await Submit(f, draft.Id, key, input); submitted.EnsureSuccessStatusCode();
        var all = Parse(await submitted.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(all, frame => frame.Event["type"]!.GetValue<string>() == "TEXT_MESSAGE_CONTENT");
        var terminal = all.Last().Event; Assert.Equal("RUN_FINISHED", terminal["type"]!.GetValue<string>());
        var url = terminal["result"]!["result"]![0]!["url"]!.GetValue<string>();
        using var download = await f.Client.GetAsync(url, Ct); download.EnsureSuccessStatusCode(); Assert.NotEmpty(await download.Content.ReadAsByteArrayAsync(Ct));
        using var denied = await Send(f, other, HttpMethod.Get, submitted.Headers.GetValues("X-Workflow-Events-URL").Single()); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    /// <summary>服务器恢复中断运行后，持久化日志跨作用域重放成对步骤和失败终态。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RestartRecoveryPersistsStepTerminationAndReplaysAcrossScopes()
    {
        using var upstream = new StreamingTextTransport(""); await using var f = await WorkflowStreamHttpTests.Fixture(upstream, worker: false);
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var queued = await Submit(f, workflow.Id, key, Input("restart")); queued.EnsureSuccessStatusCode();
        var id = queued.Headers.GetValues("X-Workflow-Run-ID").Single(); var url = queued.Headers.GetValues("X-Workflow-Events-URL").Single();
        using var reader = new StreamReader(await queued.Content.ReadAsStreamAsync(Ct)); var accepted = await Next(reader, "CUSTOM"); queued.Dispose();
        await f.Db.SqlAsync($"UPDATE workflow_run SET status='running',started_at=now() WHERE run_id='{id}'; UPDATE workflow_node_run SET status='running',started_at=now() WHERE run_id='{id}' AND node_id='llm'");
        using (var scope = f.App.Services.CreateScope()) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().FailInterruptedAsync(DateTimeOffset.UtcNow, Ct));
        using var resumed = await Send(f, key, HttpMethod.Get, url, cursor: accepted.Id); resumed.EnsureSuccessStatusCode();
        var all = Parse(await resumed.Content.ReadAsStringAsync(Ct));
        Assert.Single(all, frame => frame.Event["type"]!.GetValue<string>() == "STEP_STARTED"
            && frame.Event["stepName"]!.GetValue<string>() == "LLM"
            && frame.Event["metadata"]!["nodeId"]!.GetValue<string>() == "llm");
        Assert.Single(all, frame => frame.Event["type"]!.GetValue<string>() == "STEP_FINISHED"
            && frame.Event["stepName"]!.GetValue<string>() == "LLM"
            && frame.Event["metadata"]!["nodeId"]!.GetValue<string>() == "llm");
        Assert.Equal("RUN_ERROR", all.Last().Event["type"]!.GetValue<string>()); Assert.Contains("server restarted", all.Last().Event["message"]!.GetValue<string>());
        Assert.Equal(1, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event WHERE kind='run.finished'")); Assert.Equal(0, upstream.Calls);
    }

    /// <summary>模拟主运行已经失败、节点收尾尚未写入时崩溃；恢复仅补审计，不重试模型。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RestartBetweenTerminalRunAndNodeFinalizationCannotLeaveAnEndlessStream()
    {
        using var upstream = new StreamingTextTransport(""); await using var f = await WorkflowStreamHttpTests.Fixture(upstream, worker: false);
        var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var submitted = await Submit(f, workflow.Id, key, Input("interrupted-finalizer")); submitted.EnsureSuccessStatusCode();
        var id = submitted.Headers.GetValues("X-Workflow-Run-ID").Single(); var url = submitted.Headers.GetValues("X-Workflow-Events-URL").Single(); submitted.Dispose();
        await f.Db.SqlAsync($"UPDATE workflow_run SET status='failed',error='synthetic interrupted finalizer',finished_at=now() WHERE run_id='{id}'");
        Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event WHERE kind='run.finished'"));
        using (var scope = f.App.Services.CreateScope()) Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().FailInterruptedAsync(DateTimeOffset.UtcNow, Ct));
        using var resumed = await Send(f, key, HttpMethod.Get, url); resumed.EnsureSuccessStatusCode();
        Assert.Equal("RUN_ERROR", Parse(await resumed.Content.ReadAsStringAsync(Ct)).Last().Event["type"]!.GetValue<string>());
        Assert.Equal(1, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event WHERE kind='run.finished'"));
        Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event event WHERE sequence > (SELECT MAX(sequence) FROM workflow_run_event WHERE kind='run.finished')"));
        var count = await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event");
        await f.Db.SqlAsync($"UPDATE workflow_node_run SET progress=1 WHERE run_id='{id}'");
        Assert.Equal(count, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_event")); Assert.Equal(0, upstream.Calls);
    }

    /// <summary>响应写入超过期限会释放额度，已经完成的付费任务不被取消或重新生成。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task SlowSubscriberExpiresItsLeaseWithoutCancellingOrRegenerating()
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat); using var upstream = new StreamingTextTransport(frames.First, frames.Last); upstream.Gate.SetResult();
        await using var f = await WorkflowStreamHttpTests.Fixture(upstream); var (workflow, key) = await WorkflowStreamHttpTests.Publish(f);
        using var submitted = await Submit(f, workflow.Id, key, Input("slow")); submitted.EnsureSuccessStatusCode();
        await submitted.Content.ReadAsStringAsync(Ct); var id = submitted.Headers.GetValues("X-Workflow-Run-ID").Single();
        f.App.Services.GetRequiredService<ExternalWorkflowApiOptions>().MaxStreamsPerApiKey = 1;
        using var scope = f.App.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Request.Headers.Authorization = "Bearer " + key.Key;
        context.Response.Body = new BlockedBody();
        var controller = ActivatorUtilities.CreateInstance<AgUiWorkflowsController>(scope.ServiceProvider);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var watch = System.Diagnostics.Stopwatch.StartNew(); await controller.Events(id, Ct);
        Assert.InRange(watch.Elapsed.TotalSeconds, 14, 25);
        using var lease = f.App.Services.GetRequiredService<ExternalWorkflowRateLimiter>().AcquireStream(new WorkflowApiKeyId(Guid.Parse(key.Id))); Assert.NotNull(lease);
        using var state = await Send(f, key, HttpMethod.Get, "/api/external/v1/runs/" + id);
        Assert.Equal("succeeded", (await state.Content.ReadFromJsonAsync<JsonObject>(Ct))!["status"]!.GetValue<string>()); Assert.Equal(1, upstream.Calls);
    }

    /// <summary>仅异步写入可用、持续阻塞直到取消的响应替身。</summary>
    private sealed class BlockedBody : Stream
    {
        /// <summary>响应替身不提供读取。</summary>
        public override bool CanRead => false;
        /// <summary>响应流不支持定位。</summary>
        public override bool CanSeek => false;
        /// <summary>支持阻塞异步写入以验证服务端期限。</summary>
        public override bool CanWrite => true;
        /// <summary>没有任何字节被实际接收。</summary>
        public override long Length => 0;
        /// <summary>当前位置恒为零，禁止客户端定位。</summary>
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        /// <summary>只依赖写入取消结束，不能让测试替身自行提前解除阻塞。</summary>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => await Task.Delay(Timeout.Infinite, cancellationToken);
        /// <summary>空响应没有可刷新的缓冲。</summary>
        public override void Flush() { }
        /// <summary>显式禁止读取响应替身。</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <summary>显式禁止流定位。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>响应替身不能更改长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>同步写入不能绕过异步写入期限测试。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>一次性在日志读出后撤权，制造真实读取／发送之间的权限竞争。</summary>
    private sealed class AfterReadJournal(IWorkflowRunJournal inner, Func<Task> revoke) : IWorkflowRunJournal
    {
        /// <summary>保证撤权动作只执行一次。</summary>
        private bool _revoked;
        /// <summary>读取到首个文本批次后先撤权，再把批次交给真实订阅器。</summary>
        public async Task<IReadOnlyList<WorkflowRunJournalEntry>> ReadAsync(WorkflowRunId id, long after, CancellationToken ct)
        { var result = await inner.ReadAsync(id, after, ct); if (!_revoked && result.Any(entry => entry.Kind == "text.delta")) { _revoked = true; await revoke(); } return result; }
    }
    /// <summary>使用真实上游流解析器和公开文本回调，不访问模型网络。</summary>
    private sealed class StreamModels(WorkflowV2Fixtures.Models inner, WorkflowPublicTextStream stream, StreamingTextTransport upstream) : IWorkflowV2Models
    {
        /// <summary>复用合成模型快照，不改变真实发布绑定校验。</summary>
        public Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct) => inner.CaptureAsync(plan, ct);
        /// <summary>把受控上游流交给真实解析器，错误仍按工作流异常处理。</summary>
        public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct)
        { var result = await TextStreamProtocolTests.Client(ProviderType.OpenAiChat, upstream).GenerateAsync(StreamingTextTransport.Request(ProviderType.OpenAiChat, stream.Receiver(node)!), ct); if (!result.Ok) throw new WorkflowV2Exception(result.Error!); return result.Text!; }
    }
    /// <summary>构造仅包含发布参数的标准 AG-UI 请求。</summary>
    private static JsonObject Input(string id, string question = "test") => new() { ["threadId"] = "thread-one", ["runId"] = id,
        ["messages"] = new JsonArray(), ["tools"] = new JsonArray(), ["context"] = new JsonArray(), ["state"] = new JsonObject(),
        ["forwardedProps"] = new JsonObject { ["inputs"] = new JsonObject { ["question"] = question } } };
    /// <summary>提交后只等响应头，允许在模型还未结束时读首个增量。</summary>
    private static Task<HttpResponseMessage> Submit(AdminWorkflowApisHttpTests.Fixture f, string workflow, WorkflowApiKeyResponse key, JsonObject input, string? idempotency = null) =>
        Send(f, key, HttpMethod.Post, $"/api/agui/v1/workflows/{workflow}/runs", input, idempotency);
    /// <summary>外部请求只携带测试 Key，明确剥离管理员身份。</summary>
    private static async Task<HttpResponseMessage> Send(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, HttpMethod method, string url, object? body = null, string? idempotency = null, string? cursor = null)
    {
        using var request = new HttpRequestMessage(method, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        request.Headers.Add("Test-Identity", "anonymous"); if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        if (cursor is not null) request.Headers.Add("Last-Event-ID", cursor); if (body is not null) request.Content = JsonContent.Create(body);
        return await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }
    /// <summary>已完整解析的一帧及其独立 SSE 游标。</summary>
    /// <param name="Id">当前帧的持久化序号和子帧号，错误帧可能没有游标。</param>
    /// <param name="Event">官方事件 JSON，用于断言发布结果和生命周期。</param>
    private sealed record Frame(string? Id, JsonObject Event);
    /// <summary>有十五秒上限地读取到目标事件，非预期失败不能被静默忽略。</summary>
    private static async Task<Frame> Next(StreamReader reader, string type)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string? id = null; JsonObject? data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = JsonNode.Parse(line[6..])!.AsObject();
            if (line.Length != 0) continue;
            if (data?["type"]?.GetValue<string>() == type) return new(id, data);
            Assert.NotEqual("RUN_ERROR", data?["type"]?.GetValue<string>()); id = null; data = null;
        }
        throw new InvalidOperationException("Stream ended before " + type);
    }
    /// <summary>解析已结束的官方单行 data 帧，保留事件顺序和精确游标。</summary>
    private static List<Frame> Parse(string stream)
    {
        var result = new List<Frame>(); string? id = null;
        foreach (var line in stream.Split('\n'))
        { if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..]; if (line.StartsWith("data: ", StringComparison.Ordinal)) result.Add(new(id, JsonNode.Parse(line[6..])!.AsObject())); if (line.Length == 0) id = null; }
        return result;
    }
}
