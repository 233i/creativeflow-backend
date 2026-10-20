using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Ai;
using CreativeFlow.UnitTests.Support;
using CreativeFlow.Api.Http;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.UnitTests.Api;

[Collection("Knowledge index database lock")]
public sealed class WorkflowStreamHttpTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Admin = "/api/v1/admin/workflow-apis";

    [Fact]
    public void StreamLeasesEnforceKeyAndGlobalLimitsAndReleaseExactlyOnce()
    {
        using var limiter = new ExternalWorkflowRateLimiter(new ExternalWorkflowApiOptions { MaxStreamsPerApiKey = 1, MaxConcurrentStreams = 2 });
        var one = WorkflowApiKeyId.New(); var two = WorkflowApiKeyId.New();
        var first = limiter.AcquireStream(one)!; Assert.NotNull(first); Assert.Null(limiter.AcquireStream(one));
        using var second = limiter.AcquireStream(two); Assert.NotNull(second); Assert.Null(limiter.AcquireStream(WorkflowApiKeyId.New()));
        first.Dispose(); first.Dispose();
        using var next = limiter.AcquireStream(one); Assert.NotNull(next); Assert.Null(limiter.AcquireStream(WorkflowApiKeyId.New()));
    }

    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task RealWorkerStreamsBeforeCompletionAndReplaysOnlyMissingTextWithoutCallingAgain()
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, frames.Last);
        await using var f = await Fixture(upstream);
        var (workflow, key) = await Publish(f);
        var run = await Submit(f, workflow.Id, key, "stream-one");
        using var connection = await Open(f, key, run.EventsUrl);
        Assert.Equal("text/event-stream", connection.Content.Headers.ContentType!.MediaType);
        Assert.True(connection.Headers.CacheControl!.NoStore); Assert.Equal("no", connection.Headers.GetValues("X-Accel-Buffering").Single());
        using var reader = new StreamReader(await connection.Content.ReadAsStreamAsync(Ct));
        var first = await Next(reader, "output.delta");
        Assert.Equal("合成", first.Data["delta"]!.GetValue<string>());
        Assert.False(upstream.Gate.Task.IsCompleted); Assert.Equal(1, upstream.Calls);
        connection.Dispose(); // 仅断开订阅，模型仍等待自己的上游结束帧。
        upstream.Gate.SetResult();
        using var resumed = await Open(f, key, run.EventsUrl, first.Id);
        var replay = await resumed.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("\"delta\": \"合成\"", replay);
        Assert.Contains("run.completed", replay);
        Assert.Contains("回答", JsonText(replay));
        Assert.DoesNotContain("secret", replay); Assert.Equal(1, upstream.Calls);
        var again = await Submit(f, workflow.Id, key, "stream-one"); Assert.Equal(run.RunId, again.RunId);
        using var changedMode = await Send(f, key, HttpMethod.Post, $"/api/external/v1/workflows/{workflow.Id}/runs", new { inputs = new { question = "test" }, stream = false }, "stream-one");
        Assert.Equal(HttpStatusCode.Conflict, changedMode.StatusCode);
        using var history = await Open(f, key, run.EventsUrl);
        var all = await history.Content.ReadAsStringAsync(Ct); Assert.Contains("合成回答", JsonText(all)); Assert.Equal(1, upstream.Calls);
        using var cursor = await Open(f, key, run.EventsUrl, run.RunId + ":999999"); Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        using var wrongRun = await Open(f, key, run.EventsUrl, Guid.NewGuid() + ":1"); Assert.Equal(HttpStatusCode.BadRequest, wrongRun.StatusCode);
    }

    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)][InlineData(true)]
    public async Task CancellationAndBrokenUpstreamProduceTerminalFailureInsteadOfPartialSuccess(bool broken)
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, broken ? "" : frames.Last);
        await using var f = await Fixture(upstream);
        var (workflow, key) = await Publish(f); var run = await Submit(f, workflow.Id, key, "terminal");
        using var response = await Open(f, key, run.EventsUrl);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        var first = await Next(reader, "output.delta");
        if (!broken) { using var cancel = await Send(f, key, HttpMethod.Delete, run.StatusUrl); Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); }
        upstream.Gate.SetResult();
        var terminal = await Next(reader, broken ? "run.failed" : "run.cancelled");
        Assert.Null(terminal.Data["outputs"]); Assert.Equal(1, upstream.Calls);
        using var replay = await Open(f, key, run.EventsUrl, first.Id);
        var text = await replay.Content.ReadAsStringAsync(Ct); Assert.DoesNotContain("event: output.delta", text); Assert.DoesNotContain("run.completed", text);
    }

    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task KeyRevocationClosesLiveStreamAndOtherKeysCannotReadIt()
    {
        var frames = TextStreamProtocolTests.Frames(ProviderType.OpenAiChat);
        using var upstream = new StreamingTextTransport(frames.First, frames.Last);
        await using var f = await Fixture(upstream); var (workflow, key) = await Publish(f); var run = await Submit(f, workflow.Id, key, "revoke");
        var other = await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{workflow.Id}/api-keys", new { name = "other" });
        using var denied = await Open(f, other, run.EventsUrl); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var response = await Open(f, key, run.EventsUrl);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct)); await Next(reader, "output.delta");
        using var revoke = await f.Client.DeleteAsync($"{Admin}/{workflow.Id}/api-keys/{key.Id}", Ct); revoke.EnsureSuccessStatusCode();
        var error = await Next(reader, "stream.error"); Assert.False(error.Data["retryable"]!.GetValue<bool>());
        upstream.Gate.SetResult();
        using var reconnect = await Open(f, key, run.EventsUrl); Assert.Equal(HttpStatusCode.Unauthorized, reconnect.StatusCode);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task EventsPersistAcrossScopesAndCannotBeAppendedAfterTerminalState()
    {
        using var upstream = new StreamingTextTransport(""); await using var f = await Fixture(upstream, worker: false);
        var (workflow, key) = await Publish(f); var run = await Submit(f, workflow.Id, key, "persist"); var id = new WorkflowRunId(Guid.Parse(run.RunId));
        await f.Db.SqlAsync($"UPDATE workflow_run SET status='running' WHERE run_id='{run.RunId}'");
        using (var scope = f.App.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IWorkflowRunEventRepository>();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => events.AppendAsync(id, "{\"delta\":\"saved\"}", Ct)));
        }
        using (var scope = f.App.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IWorkflowRunEventRepository>();
            Assert.Equal(Enumerable.Range(1, 16).Select(x => (long)x), (await events.ReadAsync(id, 0, Ct)).Select(x => x.Sequence));
            Assert.Equal(Enumerable.Range(17, 4).Select(x => (long)x), (await events.ReadAsync(id, 16, Ct)).Select(x => x.Sequence));
            await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().FailInterruptedAsync(DateTimeOffset.UtcNow, Ct);
            await events.AppendAsync(id, "{\"delta\":\"late\"}", Ct); Assert.Equal(20, await events.LastSequenceAsync(id, Ct));
        }
        using var restored = await Open(f, key, run.EventsUrl);
        var history = await restored.Content.ReadAsStringAsync(Ct); Assert.Contains("run.failed", history); Assert.Contains("server restarted", history);
        Assert.Equal(20, history.Split("event: output.delta", StringSplitOptions.None).Length - 1);
        await f.Db.SqlAsync($"DELETE FROM workflow_run WHERE run_id='{run.RunId}'"); Assert.Equal(0, await f.Db.CountAsync("SELECT COUNT(*) FROM workflow_run_text_event"));
    }

    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)][InlineData(true)]
    public async Task KnowledgeAnswerStreamsOnlyTextAndRechecksLiveResourceGrant(bool revoke)
    {
        static string Frame(string text, bool finish = false) => "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0,
            ["delta"] = new JsonObject { ["content"] = text }, ["finish_reason"] = finish ? "stop" : null }) }.ToJsonString() + "\n\n";
        using var upstream = new StreamingTextTransport(Frame("{\"text\":\"合成答案"));
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, knowledge: true,
            configure: (fixture, services) => services.AddScoped<IWorkflowV2Models>(sp => new KnowledgeModels(fixture.Models, sp.GetRequiredService<WorkflowPublicTextStream>(), upstream)));
        var (workflow, key) = await Publish(f, WorkflowV2Fixtures.KnowledgeGraph(f.LibraryId, f.VersionId));
        var path = $"/api/v1/admin/knowledge-bases/{f.LibraryId}/api-key-grants/{key.Id}";
        using (var grant = await f.Client.PutAsJsonAsync(path, new { enabled = true, reason = "synthetic stream" }, Ct)) grant.EnsureSuccessStatusCode();
        var run = await Submit(f, workflow.Id, key, "knowledge");
        using var response = await Open(f, key, run.EventsUrl);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct)); var delta = await Next(reader, "output.delta");
        Assert.Equal("/text", delta.Data["path"]!.GetValue<string>()); Assert.Equal("合成答案", delta.Data["delta"]!.GetValue<string>());
        Assert.DoesNotContain("citations", delta.Data.ToJsonString());
        if (revoke)
        {
            using var denied = await f.Client.PutAsJsonAsync(path, new { enabled = false, reason = "synthetic revoke" }, Ct); denied.EnsureSuccessStatusCode();
            var error = await Next(reader, "stream.error"); Assert.False(error.Data["retryable"]!.GetValue<bool>());
            upstream.Gate.SetResult();
            using var reconnect = await Open(f, key, run.EventsUrl, delta.Id); Assert.Equal(HttpStatusCode.Forbidden, reconnect.StatusCode);
        }
        else
        {
            upstream.Gate.SetResult(); var done = await Next(reader, "run.completed");
            Assert.Equal("合成答案", done.Data["outputs"]!["answer"]!["text"]!.GetValue<string>());
            Assert.Single(done.Data["outputs"]!["answer"]!["citations"]!.AsArray());
        }
        Assert.Equal(1, upstream.Calls);
    }

    internal sealed class KnowledgeModels(WorkflowV2Fixtures.Models inner, WorkflowPublicTextStream stream, StreamingTextTransport upstream) : IWorkflowV2Models
    {
        public Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct) => inner.CaptureAsync(plan, ct);
        public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct)
        {
            var receiver = stream.Receiver(node)!;
            var citations = new JsonArray(JsonNode.Parse(prompt)!["untrustedSources"]![0]!["id"]!.DeepClone());
            upstream.Last = "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0,
                ["delta"] = new JsonObject { ["content"] = "\",\"citations\":" + citations.ToJsonString() + "}" }, ["finish_reason"] = "stop" }) }.ToJsonString() + "\n\ndata: [DONE]\n\n";
            var result = await TextStreamProtocolTests.Client(ProviderType.OpenAiChat, upstream).GenerateAsync(StreamingTextTransport.Request(ProviderType.OpenAiChat, receiver), ct);
            if (!result.Ok) throw new WorkflowV2Exception(result.Error!);
            return result.Text!;
        }
    }

    internal static Task<AdminWorkflowApisHttpTests.Fixture> Fixture(StreamingTextTransport upstream, bool worker = true) =>
        AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: worker, v2: true, configure: (f, services) =>
            services.AddScoped<IWorkflowV2Models>(sp => new Models(f.Models, sp.GetRequiredService<WorkflowPublicTextStream>(), upstream)));

    private sealed class Models(WorkflowV2Fixtures.Models inner, WorkflowPublicTextStream stream, StreamingTextTransport upstream) : IWorkflowV2Models
    {
        public Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct) => inner.CaptureAsync(plan, ct);
        public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct)
        {
            if (stream.Receiver(node) is not { } receiver) return await inner.GenerateAsync(node, snapshot, prompt, ct);
            var response = await TextStreamProtocolTests.Client(ProviderType.OpenAiChat, upstream).GenerateAsync(StreamingTextTransport.Request(ProviderType.OpenAiChat, receiver), ct);
            if (!response.Ok) throw new WorkflowV2Exception(response.Error!);
            return response.Text!;
        }
    }
    internal static async Task<(WorkflowResponse Workflow, WorkflowApiKeyResponse Key)> Publish(AdminWorkflowApisHttpTests.Fixture f, JsonObject? graph = null)
    {
        var workflow = await f.PostAsync<WorkflowResponse>(Admin, new { name = "SSE synthetic", schemaVersion = 1, graph = graph ?? WorkflowV2Fixtures.Graph() });
        await f.PostAsync<WorkflowDeploymentResponse>($"{Admin}/{workflow.Id}/publish", new { });
        return (workflow, await f.PostAsync<WorkflowApiKeyResponse>($"{Admin}/{workflow.Id}/api-keys", new { name = "SSE" }));
    }
    private static async Task<ExternalWorkflowRunCreatedResponse> Submit(AdminWorkflowApisHttpTests.Fixture f, string workflow, WorkflowApiKeyResponse key, string idempotency)
    {
        using var response = await Send(f, key, HttpMethod.Post, $"/api/external/v1/workflows/{workflow}/runs", new { inputs = new { question = "test" }, stream = true }, idempotency);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ExternalWorkflowRunCreatedResponse>(Ct))!;
    }
    private static Task<HttpResponseMessage> Open(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, string url, string? cursor = null) => Send(f, key, HttpMethod.Get, url, cursor: cursor);
    private static async Task<HttpResponseMessage> Send(AdminWorkflowApisHttpTests.Fixture f, WorkflowApiKeyResponse key, HttpMethod method, string url, object? body = null, string? idempotency = null, string? cursor = null)
    {
        using var request = new HttpRequestMessage(method, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        request.Headers.Add("Test-Identity", "anonymous"); if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        if (cursor is not null) request.Headers.Add("Last-Event-ID", cursor); if (body is not null) request.Content = JsonContent.Create(body);
        return await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }
    private static async Task<(string? Id, JsonObject Data)> Next(StreamReader reader, string expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string? id = null; var name = ""; var data = "";
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            if (line.Length == 0)
            {
                if (name == expected) return (id, JsonNode.Parse(data)!.AsObject());
                Assert.NotEqual("stream.error", name); name = ""; data = ""; id = null;
            }
        }
        throw new InvalidOperationException("Stream ended before " + expected);
    }
    private static string JsonText(string stream) => string.Join("\n", stream.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..])!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
}
