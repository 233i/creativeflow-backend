using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Support;

/// <summary>不访问真实模型的新版图与受控文本端口。</summary>
internal static class WorkflowV2Fixtures
{
    /// <summary>生成包含开始、显式 LLM、结束的有类型图，节点故意乱序以验证拓扑编译。</summary>
    public static JsonObject Graph() => JsonNode.Parse("""
        {"schemaVersion":1,"nodes":[
          {"id":"end","type":"end","position":{"x":700,"y":0},"data":{"fields":[{"key":"answer","type":"string","required":true}]}},
          {"id":"llm","type":"llm","position":{"x":350,"y":0},"data":{"fields":[{"key":"question","type":"string","required":true}],
            "modelId":"10000000-0000-0000-0000-000000000001","prompt":[{"text":"Answer: "},{"input":"question"}],"systemPrompt":"Be concise",
            "maxOutputTokens":512,"temperature":0.7,"timeoutSeconds":120}},
          {"id":"start","type":"start","position":{"x":0,"y":0},"data":{"fields":[{"key":"question","type":"string","required":true}]}}
        ],"edges":[
          {"id":"b","source":"llm","sourceHandle":"text","target":"end","targetHandle":"answer"},
          {"id":"a","source":"start","sourceHandle":"question","target":"llm","targetHandle":"question"}
        ]}
        """)!.AsObject();

    /// <summary>合成 RAG 图保留真实问题与证据连线，不访问已有业务资源。</summary>
    public static JsonObject KnowledgeGraph(Guid library, Guid version)
    {
        var graph = Graph();
        var answer = graph["nodes"]![1]!.AsObject(); answer["id"] = "answer"; answer["type"] = "knowledgeAnswer";
        answer["data"]!["fields"] = KnowledgeWorkflowValues.Inputs("knowledgeAnswer"); answer["data"]!.AsObject().Remove("prompt");
        graph["nodes"]![0]!["data"]!["fields"]![0]!["type"] = "answer";
        graph["nodes"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "retrieve", ["type"] = "knowledgeRetrieve", ["position"] = new JsonObject { ["x"] = 200, ["y"] = 0 },
            ["data"] = new JsonObject { ["fields"] = KnowledgeWorkflowValues.Inputs("knowledgeRetrieve"), ["libraryId"] = library.ToString(), ["versionId"] = version.ToString(), ["topK"] = 4, ["minScore"] = 0 } });
        graph["edges"] = JsonNode.Parse("""
            [{"id":"a","source":"start","sourceHandle":"question","target":"retrieve","targetHandle":"question"},
             {"id":"b","source":"start","sourceHandle":"question","target":"answer","targetHandle":"question"},
             {"id":"c","source":"retrieve","sourceHandle":"context","target":"answer","targetHandle":"context"},
             {"id":"d","source":"answer","sourceHandle":"answer","target":"end","targetHandle":"answer"}]
            """);
        return graph;
    }

    /// <summary>在现有知识图插入参数提取：检索使用 query 输出，问答仍连接开始的原始 question。</summary>
    public static void AddKnowledgeQueryExtraction(JsonObject graph)
    {
        var data = Graph()["nodes"]![1]!["data"]!.DeepClone().AsObject();
        data["outputFields"] = JsonNode.Parse("""[{"key":"query","type":"string","required":true}]""");
        graph["nodes"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "extract", ["type"] = "parameterExtract",
            ["position"] = new JsonObject { ["x"] = 100, ["y"] = 200 }, ["data"] = data });
        var edge = graph["edges"]!.AsArray().Single(edge => edge!["target"]!.GetValue<string>() == "retrieve")!;
        edge["source"] = "extract"; edge["sourceHandle"] = "query";
        graph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"extract-input","source":"start","sourceHandle":"question","target":"extract","targetHandle":"question"}""")!);
    }

    /// <summary>记录确切调用次数与提示词；可暂停或模拟状态不明确，不发送 HTTP。</summary>
    internal sealed class Models : IWorkflowV2Models
    {
        /// <summary>实际生成调用次数。</summary>
        public int Calls { get; private set; }
        /// <summary>记录实际执行的输出预算，验证发布后的草稿改动不会改变已发布请求。</summary>
        public int? LastMaxOutputTokens { get; private set; }
        /// <summary>最近一次渲染后的提示词。</summary>
        public string? Prompt { get; private set; }
        /// <summary>模拟模型请求失败。</summary>
        public bool Fail { get; set; }
        /// <summary>控制生成返回时机，用于验证并发提交和取消。</summary>
        public TaskCompletionSource<bool>? Gate { get; set; }
        /// <summary>知识问答可注入不合法引用或超限结果。</summary>
        public string? AnswerResponse { get; set; }
        /// <summary>为结构化、提取和分类测试指定一次上游 JSON，不访问真实模型。</summary>
        public string? StructuredResponse { get; set; }
        /// <summary>在模型返回前变更权限，验证中途撤权不交付正文。</summary>
        public Func<Task>? BeforeAnswer { get; set; }
        /// <summary>模拟管理员改动渠道配置，发布快照不得静默跟随。</summary>
        public bool ConfigurationChanged { get; set; }
        /// <summary>为每个 LLM 捕获只含合成标识的快照。</summary>
        public Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct)
        {
            var models = new JsonObject();
            foreach (var node in plan.Nodes.Where(node => WorkflowModelTasks.UsesModel(node.Type))) models[node.Id] = new JsonObject { ["test"] = true };
            if (ConfigurationChanged) foreach (var model in models) model.Value!["changed"] = true;
            return Task.FromResult(models);
        }
        /// <summary>返回包含原始提示词的合成文本，允许断言变量没有被当代码执行。</summary>
        public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct)
        {
            Calls++; Prompt = prompt; LastMaxOutputTokens = node.Data["maxOutputTokens"]?.GetValue<int>();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Fail) throw new WorkflowV2Exception("测试模型结果不明确，未自动重试。");
            if (node.Type == "knowledgeAnswer")
            {
                if (BeforeAnswer is not null) await BeforeAnswer();
                return AnswerResponse ?? new JsonObject { ["text"] = "合成手册规定保修两年。", ["citations"] = new JsonArray(JsonNode.Parse(prompt)!["untrustedSources"]![0]!["id"]!.DeepClone()) }.ToJsonString();
            }
            return WorkflowModelTasks.IsStructured(node) ? StructuredResponse ?? "{}" : "Synthetic: " + prompt;
        }
    }
}
