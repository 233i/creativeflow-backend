using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.UnitTests.Support;
using static CreativeFlow.UnitTests.Support.WorkflowBranchFixtures;

namespace CreativeFlow.UnitTests.Api;

/// <summary>提取、转换、判断、外部调用和发布共用生产执行链的集成回归。</summary>
[Collection("Knowledge index database lock")]
public sealed class WorkflowDataPipelineHttpTests
{
    /// <summary>仅在明确提供隔离库时执行集成测试。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>模型分类沿用 MAF 分支与汇合语义，默认路径、非法结果和复用均不隐式追加调用。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("match", "succeeded", 2, "fallback")]
    [InlineData("else", "succeeded", 1, "hit")]
    [InlineData("unknown", "failed", 1, "hit")]
    public async Task IntentRoutesAreValidatedAndOnlySelectedBranchesExecute(string route, string status, int calls, string skipped)
    {
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true);
        f.Models.StructuredResponse = new JsonObject { ["route"] = route }.ToJsonString();
        var graph = WorkflowBranchFixtures.Graph();
        var classifier = graph["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "condition")!;
        classifier["type"] = "intentClassify";
        classifier["data"] = WorkflowV2Fixtures.Graph()["nodes"]![1]!["data"]!.DeepClone();
        classifier["data"]!["intents"] = JsonNode.Parse("""[{"key":"match","label":"咨询","description":"请求产品信息"}]""");
        var document = await f.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "Intent verification", schemaVersion = 1, graph });
        var path = "/api/v1/admin/workflow-apis/" + document.Id;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "synthetic" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id, status);
        Assert.Equal(calls, f.Models.Calls);
        Assert.NotEqual("succeeded", result["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == skipped)!["status"]!.GetValue<string>());
        if (status != "succeeded") return;
        // 只重跑汇合时复用已校验的分类，不能再次询问模型或切换原分支。
        var partial = await f.PostAsync<WorkflowRunResponse>(path + $"/runs/{run.Id}/partial-rerun", new { fromNodeId = "merge" });
        var reused = await f.WaitAsync(path + "/runs/" + partial.Id);
        Assert.Equal(calls, f.Models.Calls);
        Assert.True(reused["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "condition")!["isReused"]!.GetValue<bool>());
    }

    /// <summary>完整提取链交付类型化结果；发布后外部调用仍只用冻结配置与同一字段校验。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ExtractionTransformConditionHttpAndPublishedApiRunTheSamePipeline()
    {
        // 先验证完整交互链，再切换为独立 Key；固定操作授权不能借管理员身份自动继承。
        await using var f = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(worker: true, v2: true, http: true);
        f.Models.StructuredResponse = """{"value":" HIT "}""";
        var operation = await f.PostAsync<JsonObject>("/api/v1/admin/http-credentials", HttpRequestFixtures.CredentialForm());
        var operationId = operation["id"]!.GetValue<string>(); var revisionId = operation["revisionId"]!.GetValue<string>();
        var graph = HttpRequestFixtures.BranchGraph(Guid.Parse(operationId), Guid.Parse(revisionId));
        var model = (JsonObject)WorkflowV2Fixtures.Graph()["nodes"]![1]!["data"]!.DeepClone();
        model["outputFields"] = JsonNode.Parse("""[{"key":"value","type":"string","required":true}]""");
        graph["nodes"]!.AsArray().Add(Node("extract", "parameterExtract", model));
        graph["nodes"]!.AsArray().Add(Node("transform", "transform", JsonNode.Parse("""
          {"fields":[{"key":"value","type":"string","required":true}],"outputFields":[{"key":"question","type":"string","required":true}],
           "assignments":[{"key":"question","expression":{"op":"lower","args":[{"op":"trim","args":[{"op":"input","input":"value"}]}]}}]}
          """)!.AsObject()));
        var edges = graph["edges"]!.AsArray();
        foreach (var edge in edges.OfType<JsonObject>().Where(edge => edge["source"]!.GetValue<string>() == "start")) edge["source"] = "transform";
        edges.Add(Edge("extract-input", "start", "question", "extract", "question"));
        edges.Add(Edge("transform-input", "extract", "value", "transform", "value"));
        var workflow = await f.PostAsync<WorkflowResponse>("/api/v1/admin/workflow-apis", new { name = "Data pipeline", schemaVersion = 1, graph });
        var path = "/api/v1/admin/workflow-apis/" + workflow.Id;
        var run = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "input" } });
        var result = await f.WaitAsync(path + "/runs/" + run.Id);
        Assert.Equal(1, f.Models.Calls); Assert.Equal(1, f.Http.Calls);
        Assert.Equal("Synthetic HTTP: hit", result["nodes"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "end")!["output"]!["fields"]!["answer"]!.GetValue<string>());
        f.Models.StructuredResponse = """{"value":12}""";
        var invalid = await f.PostAsync<WorkflowRunResponse>(path + "/runs", new { inputs = new { question = "input" } });
        await f.WaitAsync(path + "/runs/" + invalid.Id, "failed"); Assert.Equal(2, f.Models.Calls); Assert.Equal(1, f.Http.Calls);
        f.Models.StructuredResponse = """{"value":" HIT "}""";
        await f.PostAsync<WorkflowDeploymentResponse>(path + "/publish", new { description = "Synthetic data pipeline", maxConcurrency = 1 });
        var key = await f.PostAsync<WorkflowApiKeyResponse>(path + "/api-keys", new { name = "Synthetic external caller" });
        using var grant = await f.Client.PutAsJsonAsync($"/api/v1/admin/http-credentials/{operationId}/grants/apiKey/{key.Id}", new { enabled = true, reason = "isolated synthetic test" }, Ct);
        grant.EnsureSuccessStatusCode();
        f.Identity("anonymous"); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", key.Key); f.Client.DefaultRequestHeaders.Add("Idempotency-Key", "pipeline-1");
        var accepted = await f.PostAsync<ExternalWorkflowRunCreatedResponse>($"/api/external/v1/workflows/{workflow.Id}/runs", new { inputs = new { question = "published input" } });
        var published = await f.WaitAsync(accepted.StatusUrl);
        Assert.Equal("Synthetic HTTP: hit", published["outputs"]!["answer"]!.GetValue<string>());
        Assert.Equal(3, f.Models.Calls); Assert.Equal(2, f.Http.Calls);
        Assert.DoesNotContain("modelCall", published.ToJsonString());
    }
}
