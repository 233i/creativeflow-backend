using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Support;

/// <summary>HTTP 节点的合成图与传输端口，不访问业务接口。</summary>
internal static class HttpRequestFixtures
{
    /// <summary>只用于测试的接口级凭据申请。</summary>
    public static object CredentialForm() => new { name = "Synthetic credential", description = "Test only", origin = "https://example.com",
        headers = Array.Empty<object>(), requests = new[] { new { method = "GET", url = "https://example.com/query" } } };

    /// <summary>提取字段模式保留分支测试的 text 端口。</summary>
    public static JsonObject ExtractedData(Guid credential, Guid revision)
    {
        var data = Data(); data["responseMode"] = "fields";
        data["credentialId"] = credential.ToString(); data["credentialRevisionId"] = revision.ToString();
        data["outputFields"] = JsonNode.Parse("""[{"key":"text","type":"string","required":true}]""");
        data["extract"] = JsonNode.Parse("""{"text":"/data/result"}"""); return data;
    }

    /// <summary>分支、汇合和发布使用同一个 HTTP 节点，不保留旧操作引擎。</summary>
    public static JsonObject BranchGraph(Guid credential, Guid revision)
    {
        var graph = WorkflowBranchFixtures.Graph(); graph["nodes"]![1]!["type"] = "httpRequest";
        graph["nodes"]![1]!["data"] = ExtractedData(credential, revision); return graph;
    }
    /// <summary>基础 GET 节点，输出同时覆盖文本、状态和响应头。</summary>
    public static JsonObject Data(string url = "https://example.com/query") => JsonNode.Parse("""
        {"fields":[{"key":"question","type":"string","required":true}],
         "http":{"method":"GET","url":"","query":[{"name":"q","value":"{{question}}"}],"headers":[],
           "bodyType":"none","body":"","form":[],"timeoutSeconds":3,"responseLimitBytes":4096,"failOnHttpError":true},
         "outputFields":[],"extract":{},"credentialId":"","credentialRevisionId":""}
        """)!.AsObject().WithUrl(url);

    /// <summary>将不带模型的 HTTP 节点接入开始和结束，保持真实边与输出契约。</summary>
    public static JsonObject Graph(Guid? credential = null, Guid? revision = null)
    {
        var graph = WorkflowV2Fixtures.Graph(); var node = graph["nodes"]![1]!;
        node["id"] = "http"; node["type"] = "httpRequest"; node["data"] = Data();
        if (credential.HasValue) { node["data"]!["credentialId"] = credential.ToString(); node["data"]!["credentialRevisionId"] = revision.ToString(); }
        graph["edges"]![0]!["source"] = "http"; graph["edges"]![0]!["sourceHandle"] = "body";
        graph["edges"]![1]!["target"] = "http";
        return graph;
    }

    /// <summary>补入每条测试独立的目标地址。</summary>
    private static JsonObject WithUrl(this JsonObject data, string url) { data["http"]!["url"] = url; return data; }

    /// <summary>权限和持久化测试保留真实引擎，只替换外部 HTTP 副作用。</summary>
    internal sealed class Transport : IWorkflowHttpTransport
    {
        /// <summary>发送计数。</summary>
        public int Calls { get; private set; }
        /// <summary>最近一次固定凭据版本。</summary>
        public Guid? Revision { get; private set; }
        /// <summary>模拟发送后未知结果。</summary>
        public bool Fail { get; set; }
        /// <summary>边界测试提供明确大小的正文，不使用任何真实上游。</summary>
        public string? Body { get; set; }
        /// <summary>请求返回前撤权等并发行为。</summary>
        public Func<Task>? BeforeReturn { get; set; }
        /// <summary>快照预检不产生业务请求。</summary>
        public Task ValidateEndpointAsync(string endpoint, CancellationToken ct) => Task.CompletedTask;
        /// <summary>返回合成正文并记录实际传输次数，供唯一 MAF 路径验证。</summary>
        public async Task<JsonObject> SendAsync(JsonObject data, JsonObject inputs, HttpCredentialRevision? credential, CancellationToken ct)
        {
            Calls++; Revision = credential?.Id;
            if (Fail) throw new WorkflowHttpException("HTTP_TIMEOUT", "合成 HTTP 请求超时，未自动重试。", true);
            if (BeforeReturn is not null) await BeforeReturn();
            if (WorkflowV2Values.Text(data["responseMode"]) == "fields") return new JsonObject { ["text"] = "Synthetic HTTP: " + WorkflowV2Values.Text(inputs["question"]) };
            return new JsonObject { ["statusCode"] = 200, ["body"] = Body ?? "HTTP: " + WorkflowV2Values.Text(inputs["question"]), ["headers"] = new JsonObject { ["content-type"] = "text/plain" } };
        }
    }
}
