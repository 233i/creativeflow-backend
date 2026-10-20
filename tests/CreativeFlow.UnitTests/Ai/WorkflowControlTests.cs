using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>循环、批处理和重试配置的离线契约测试，不调用模型、数据库或外部接口。</summary>
public sealed class WorkflowControlTests
{
    [Fact]
    public void LoopGraphValidatesAndExposesStableOutputs()
    {
        var graph = Graph("loop", "object");
        var result = WorkflowV2GraphValidator.Validate(graph);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var loop = result.Plan!.Nodes.Single(node => node.Type == "loop");
        Assert.Equal(["results", "errors", "count"], WorkflowV2GraphValidator.OutputFields(loop).OfType<JsonObject>().Select(field => field["key"]!.GetValue<string>()));
    }

    [Fact]
    public void BatchRejectsInvalidBatchSizeAndLoopBodySideEffects()
    {
        var graph = Graph("batch", "array");
        graph["nodes"]![1]!["data"]!["batchSize"] = 0;
        Assert.False(WorkflowV2GraphValidator.Validate(graph).IsValid);
        graph = Graph("batch", "array");
        graph["nodes"]![1]! ["data"]!["body"]!["nodes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "http", ["type"] = "httpRequest", ["position"] = new JsonObject { ["x"] = 1, ["y"] = 1 },
            ["data"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = "input", ["type"] = "string", ["required"] = true }), ["http"] = new JsonObject() },
        });
        Assert.False(WorkflowV2GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void RetryPolicyDefaultsToSafeSingleAttemptAndRejectsUnboundedSettings()
    {
        var settings = WorkflowRetryPolicy.Read(new JsonObject());
        Assert.Equal(1, settings.MaxAttempts);
        Assert.NotNull(WorkflowRetryPolicy.Validate(new JsonObject
        {
            ["maxAttempts"] = 6, ["backoffMs"] = 0, ["maxBackoffMs"] = 0, ["retryOn"] = new JsonArray(),
        }));
        Assert.False(WorkflowRetryPolicy.ShouldRetry(new WorkflowHttpException("unknown", "unknown", true), settings));
        Assert.False(WorkflowRetryPolicy.ShouldRetry(new WorkflowV2Exception("状态不明确"), settings));
    }

    private static JsonObject Graph(string type, string bodyType)
    {
        var key = type == "batch" ? "items" : "item";
        var body = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "body-start", ["type"] = "start", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                    ["data"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = key, ["type"] = bodyType, ["required"] = true }) },
                },
                new JsonObject
                {
                    ["id"] = "body-end", ["type"] = "end", ["position"] = new JsonObject { ["x"] = 1, ["y"] = 0 },
                    ["data"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = "result", ["type"] = bodyType, ["required"] = true }) },
                }),
            ["edges"] = new JsonArray(new JsonObject { ["id"] = "body-edge", ["source"] = "body-start", ["sourceHandle"] = key, ["target"] = "body-end", ["targetHandle"] = "result" }),
        };
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "start", ["type"] = "start", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                    ["data"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = "items", ["type"] = "array", ["required"] = true }) },
                },
                new JsonObject
                {
                    ["id"] = "loop", ["type"] = type, ["position"] = new JsonObject { ["x"] = 1, ["y"] = 0 },
                    ["data"] = new JsonObject
                    {
                        ["fields"] = new JsonArray(new JsonObject { ["key"] = "items", ["type"] = "array", ["required"] = true }),
                        ["body"] = body, ["maxItems"] = 10, ["parallelism"] = 1, ["batchSize"] = 2, ["failurePolicy"] = "continue",
                        ["outputFields"] = new JsonArray(
                            new JsonObject { ["key"] = "results", ["type"] = "array", ["required"] = true, ["items"] = new JsonObject { ["key"] = "item", ["type"] = "object", ["required"] = true } },
                            new JsonObject { ["key"] = "errors", ["type"] = "array", ["required"] = true, ["items"] = new JsonObject { ["key"] = "error", ["type"] = "string", ["required"] = true } },
                            new JsonObject { ["key"] = "count", ["type"] = "number", ["required"] = true }),
                    },
                },
                new JsonObject
                {
                    ["id"] = "end", ["type"] = "end", ["position"] = new JsonObject { ["x"] = 2, ["y"] = 0 },
                    ["data"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = "results", ["type"] = "array", ["required"] = true, ["items"] = new JsonObject { ["key"] = "item", ["type"] = "object", ["required"] = true } }) },
                }),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "edge-1", ["source"] = "start", ["sourceHandle"] = "items", ["target"] = "loop", ["targetHandle"] = "items" },
                new JsonObject { ["id"] = "edge-2", ["source"] = "loop", ["sourceHandle"] = "results", ["target"] = "end", ["targetHandle"] = "results" }),
        };
    }
}
