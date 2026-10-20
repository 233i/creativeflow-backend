using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证脚本节点的图契约、沙箱端口和严格 JSON 输出，不启动真实 OpenSandbox。</summary>
public sealed class WorkflowScriptTests
{
    [Fact]
    public void ScriptGraphRequiresSupportedLanguageAndBoundedCode()
    {
        var graph = Graph();
        Assert.True(WorkflowV2GraphValidator.Validate(graph, forExecution: false).IsValid);

        graph["nodes"]![1]! ["data"]!["language"] = "ruby";
        Assert.False(WorkflowV2GraphValidator.Validate(graph, forExecution: false).IsValid);

        graph = Graph();
        graph["nodes"]![1]! ["data"]!["code"] = new string('a', WorkflowScriptValues.MaxCodeBytes + 1);
        Assert.False(WorkflowV2GraphValidator.Validate(graph, forExecution: false).IsValid);
    }

    [Fact]
    public async Task ScriptExecutorPassesJsonStdinAndBindsDeclaredOutput()
    {
        var plan = WorkflowV2GraphValidator.Validate(Graph()).Plan!;
        var node = plan.Nodes.Single(item => item.Type == "script");
        var runner = new FakeRunner
        {
            Result = new WorkflowScriptResult("succeeded", 0, "{\"result\":{\"sum\":3}}", "", false, false),
        };
        var run = WorkflowNodeTestHost.Run();
        var context = new WorkflowNodeExecutionContext(
            WorkflowNodeFamily.General,
            run,
            node,
            new Dictionary<string, WorkflowNodeValue>(),
            new JsonObject(),
            new HashSet<string>(),
            new JsonObject { ["input"] = new JsonObject { ["a"] = 1, ["b"] = 2 } },
            WorkflowNodeTestHost.NodeRun(run, node));

        var output = await new ScriptNodeExecutor(runner).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal("python", runner.LastRequest!.Language);
        Assert.Equal(1, runner.LastRequest.Input["input"]!["a"]!.GetValue<int>());
        Assert.Equal(3, output.Fields!["result"]!["sum"]!.GetValue<int>());
    }

    [Fact]
    public async Task ScriptExecutorRejectsUnknownOutputFields()
    {
        var plan = WorkflowV2GraphValidator.Validate(Graph()).Plan!;
        var node = plan.Nodes.Single(item => item.Type == "script");
        var runner = new FakeRunner
        {
            Result = new WorkflowScriptResult("succeeded", 0, "{\"unexpected\":true}", "", false, false),
        };
        var run = WorkflowNodeTestHost.Run();
        var context = new WorkflowNodeExecutionContext(
            WorkflowNodeFamily.General,
            run,
            node,
            new Dictionary<string, WorkflowNodeValue>(),
            new JsonObject(),
            new HashSet<string>(),
            new JsonObject { ["input"] = new JsonObject { ["a"] = 1 } },
            WorkflowNodeTestHost.NodeRun(run, node));

        var error = await Assert.ThrowsAsync<WorkflowV2Exception>(() =>
            new ScriptNodeExecutor(runner).ExecuteAsync(context, CancellationToken.None));
        Assert.Contains("输出校验失败", error.Message);
    }

    [Fact]
    public async Task ScriptExecutorRejectsTruncatedOutput()
    {
        var plan = WorkflowV2GraphValidator.Validate(Graph()).Plan!;
        var node = plan.Nodes.Single(item => item.Type == "script");
        var runner = new FakeRunner
        {
            Result = new WorkflowScriptResult("succeeded", 0, "{}", "", true, false),
        };
        var run = WorkflowNodeTestHost.Run();
        var context = new WorkflowNodeExecutionContext(
            WorkflowNodeFamily.General,
            run,
            node,
            new Dictionary<string, WorkflowNodeValue>(),
            new JsonObject(),
            new HashSet<string>(),
            new JsonObject { ["input"] = new JsonObject { ["a"] = 1 } },
            WorkflowNodeTestHost.NodeRun(run, node));

        var error = await Assert.ThrowsAsync<WorkflowV2Exception>(() =>
            new ScriptNodeExecutor(runner).ExecuteAsync(context, CancellationToken.None));
        Assert.Contains("标准输出超过限制", error.Message);
    }

    private static JsonObject Graph() => JsonNode.Parse("""
        {"schemaVersion":1,"nodes":[
          {"id":"start","type":"start","position":{"x":0,"y":0},"data":{"fields":[{"key":"input","type":"object","required":true}]}},
          {"id":"script","type":"script","position":{"x":300,"y":0},"data":{"fields":[{"key":"input","type":"object","required":true}],"outputFields":[{"key":"result","type":"object","required":true}],"language":"python","code":"import json,sys\nprint(json.dumps({\"result\":json.load(sys.stdin)}))","timeoutSeconds":10}},
          {"id":"end","type":"end","position":{"x":600,"y":0},"data":{"fields":[{"key":"result","type":"object","required":true}]}}
        ],"edges":[
          {"id":"a","source":"start","sourceHandle":"input","target":"script","targetHandle":"input"},
          {"id":"b","source":"script","sourceHandle":"result","target":"end","targetHandle":"result"}
        ]}
        """)!.AsObject();

    private sealed class FakeRunner : IWorkflowScriptRunner
    {
        public bool Enabled => true;
        public int MaximumTimeoutSeconds => 30;
        public int MaximumCodeBytes => WorkflowScriptValues.MaxCodeBytes;
        public int MaximumInputBytes => WorkflowV2Values.MaxBytes;
        public int MaximumOutputBytes => 256 * 1024;
        public int Calls { get; private set; }
        public WorkflowScriptRequest? LastRequest { get; private set; }
        public WorkflowScriptResult Result { get; init; } = new("succeeded", 0, "{}", "", false, false);

        public Task<WorkflowScriptResult> RunAsync(WorkflowScriptRequest request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
