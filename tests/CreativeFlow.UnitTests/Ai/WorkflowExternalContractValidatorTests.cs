using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowExternalContractValidatorTests
{
    /// <summary>回归场景：Validates A Whitelisted Public Contract。</summary>
    [Fact]
    public void ValidatesAWhitelistedPublicContract()
    {
        var result = WorkflowExternalContractValidator.Validate(Graph());

        Assert.True(result.IsValid);
        var contract = Assert.IsType<WorkflowExternalContract>(result.Contract);
        var input = Assert.Single(contract.Inputs);
        Assert.Equal("prompt", input.Key);
        Assert.Equal(new WorkflowExternalInputTarget("prompt-node", "text"), input.Target);
        Assert.Equal(1_000, input.Constraints.MaxLength);
        Assert.Equal("image", Assert.Single(contract.Outputs).Type);
    }

    /// <summary>回归场景：Binds Caller Values To A Clone Of The Published Graph。</summary>
    [Fact]
    public void BindsCallerValuesToACloneOfThePublishedGraph()
    {
        var original = Graph().ToJsonString();

        var result = WorkflowExternalContractValidator.Bind(
            original,
            new JsonObject { ["prompt"] = "a quiet mountain village" },
            new Dictionary<string, string>());

        Assert.True(result.IsValid);
        var bound = JsonNode.Parse(result.GraphJson!)!.AsObject();
        var prompt = bound["nodes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("a quiet mountain village", prompt["data"]!["text"]!.GetValue<string>());
        Assert.Equal("", JsonNode.Parse(original)!["nodes"]![0]!["data"]!["text"]!.GetValue<string>());
        Assert.Equal("a quiet mountain village", JsonNode.Parse(result.InputJson!)!["prompt"]!.GetValue<string>());
    }

    /// <summary>回归场景：Rejects Unknown Inputs And Values Outside The Contract。</summary>
    [Fact]
    public void RejectsUnknownInputsAndValuesOutsideTheContract()
    {
        var tooLong = new string('x', 1_001);
        var unknown = WorkflowExternalContractValidator.Bind(
            Graph().ToJsonString(),
            new JsonObject { ["prompt"] = "valid", ["nodeId"] = "generate" },
            new Dictionary<string, string>());
        var invalidValue = WorkflowExternalContractValidator.Bind(
            Graph().ToJsonString(),
            new JsonObject { ["prompt"] = tooLong },
            new Dictionary<string, string>());

        Assert.False(unknown.IsValid);
        Assert.Contains(unknown.Errors, error => error.Contains("unknown external input", StringComparison.Ordinal));
        Assert.False(invalidValue.IsValid);
        Assert.Contains(invalidValue.Errors, error => error.Contains("maximum length", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Arbitrary Node Fields And Video Inputs。</summary>
    [Fact]
    public void RejectsArbitraryNodeFieldsAndVideoInputs()
    {
        var graph = Graph();
        var input = graph["externalApi"]!["inputs"]![0]!.AsObject();
        input["type"] = "video";
        input["target"]!["field"] = "providerId";

        var result = WorkflowExternalContractValidator.Validate(graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("cannot be exposed", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Resolves An Uploaded Image Without Accepting An Arbitrary Url。</summary>
    [Fact]
    public void ResolvesAnUploadedImageWithoutAcceptingAnArbitraryUrl()
    {
        var assetId = Guid.NewGuid();
        var graph = GraphWithImageInput();
        var contract = Assert.IsType<WorkflowExternalContract>(
            WorkflowExternalContractValidator.Validate(graph).Contract);
        var supplied = new JsonObject
        {
            ["prompt"] = "use this character",
            ["reference"] = new JsonObject { ["assetId"] = assetId.ToString() },
        };

        var ids = WorkflowExternalContractValidator.ReadImageAssetIds(contract, supplied);
        var result = WorkflowExternalContractValidator.Bind(
            graph.ToJsonString(),
            supplied,
            new Dictionary<string, string>
            {
                ["reference"] = $"/api/v1/canvas/assets/{assetId}",
            });

        Assert.Equal(assetId.ToString(), ids["reference"]);
        Assert.True(result.IsValid);
        var bound = JsonNode.Parse(result.GraphJson!)!;
        Assert.Equal(
            $"/api/v1/canvas/assets/{assetId}",
            bound["nodes"]![1]!["data"]!["assetUrl"]!.GetValue<string>());
    }

    /// <summary>无效节点标识返回校验错误，不能在建立节点字典时抛异常。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsDuplicateOrMissingNodeIdsWithoutThrowing(bool duplicate)
    {
        var graph = Graph();
        var nodes = graph["nodes"]!.AsArray();
        nodes.Add(duplicate ? nodes[0]!.DeepClone() : new JsonObject());
        nodes.Add(duplicate ? nodes[0]!.DeepClone() : new JsonObject());

        var result = WorkflowExternalContractValidator.Validate(graph);
        var bound = WorkflowExternalContractValidator.Bind(graph.ToJsonString(), new JsonObject(), new Dictionary<string, string>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains(duplicate ? "duplicate node id" : "needs an id", StringComparison.Ordinal));
        Assert.False(bound.IsValid);
    }

    /// <summary>旧版通用输出必须连接其声明类型的素材，同时继续兼容有效的图片和视频输出。</summary>
    [Theory]
    [InlineData("image", "image", true)]
    [InlineData("video", "video", true)]
    [InlineData("image", "video", false)]
    [InlineData("video", "image", false)]
    public void ChecksLegacyOutputAgainstConnectedMedia(string connectedType, string declaredType, bool expected)
    {
        var graph = Graph();
        graph["nodes"]![1]!["type"] = connectedType == "image" ? "imageGenerate" : "videoGenerate";
        graph["nodes"]![2]!["type"] = "output";
        graph["edges"]![1]!["sourceHandle"] = connectedType;
        graph["edges"]![1]!["targetHandle"] = connectedType;
        graph["externalApi"]!["outputs"]![0]!["type"] = declaredType;

        var result = WorkflowExternalContractValidator.Validate(graph);

        Assert.Equal(expected, result.IsValid);
        if (!expected) Assert.Contains(result.Errors, error => error.Contains("does not match its output node", StringComparison.Ordinal));
    }

    /// <summary>发布契约包含实际生效的默认约束，并保持原图不变及默认值可用。</summary>
    [Fact]
    public void PublishesEffectiveConstraintsAndDefaultsWithoutMutatingTheGraph()
    {
        var graph = Graph();
        var inputs = graph["externalApi"]!["inputs"]!.AsArray();
        inputs[0]!.AsObject().Remove("constraints");
        inputs[0]!["default"] = "default prompt";
        inputs.Add(JsonNode.Parse("""
            {"key":"aspect","label":"Aspect","type":"enum","required":true,"target":{"nodeId":"generate","field":"aspect"}}
            """));
        inputs.Add(JsonNode.Parse("""
            {"key":"count","label":"Count","type":"integer","required":true,"target":{"nodeId":"generate","field":"count"}}
            """));
        var original = graph.ToJsonString();

        var contract = Assert.IsType<WorkflowExternalContract>(WorkflowExternalContractValidator.Validate(graph).Contract);
        var schema = JsonNode.Parse(contract.InputSchemaJson)!.AsArray();

        Assert.Equal(original, graph.ToJsonString());
        Assert.Equal(20_000, schema[0]!["constraints"]!["maxLength"]!.GetValue<int>());
        Assert.Equal("default prompt", schema[0]!["default"]!.GetValue<string>());
        Assert.Equal(contract.Inputs[1].Constraints.Values, schema[1]!["constraints"]!["values"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(1, schema[2]!["constraints"]!["minimum"]!.GetValue<int>());
        Assert.Equal(4, schema[2]!["constraints"]!["maximum"]!.GetValue<int>());
        Assert.Equal("generate", schema[2]!["target"]!["nodeId"]!.GetValue<string>());

        graph["externalApi"]!["inputs"] = schema;
        Assert.True(WorkflowExternalContractValidator.Bind(graph.ToJsonString(),
            new JsonObject { ["aspect"] = "1:1", ["count"] = 4 }, new Dictionary<string, string>()).IsValid);
        Assert.False(WorkflowExternalContractValidator.Bind(graph.ToJsonString(),
            new JsonObject { ["aspect"] = "1:1", ["count"] = 5 }, new Dictionary<string, string>()).IsValid);
    }

    /// <summary>回归场景：Graph。</summary>
    private static JsonObject Graph() => JsonNode.Parse(
        """
        {
          "nodes": [
            { "id": "prompt-node", "type": "text", "data": { "text": "" } },
            { "id": "generate", "type": "imageGenerate", "data": { "count": 1 } },
            { "id": "output", "type": "imageOutput", "data": {} }
          ],
          "edges": [
            { "id": "e1", "source": "prompt-node", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
            { "id": "e2", "source": "generate", "sourceHandle": "image", "target": "output", "targetHandle": "image" }
          ],
          "externalApi": {
            "schemaVersion": 1,
            "inputs": [
              {
                "key": "prompt",
                "label": "Prompt",
                "description": "Image prompt",
                "type": "string",
                "required": true,
                "target": { "nodeId": "prompt-node", "field": "text" },
                "constraints": { "maxLength": 1000 }
              }
            ],
            "outputs": [
              { "key": "image", "label": "Generated image", "type": "image", "nodeId": "output" }
            ]
          }
        }
        """)!.AsObject();

    /// <summary>回归场景：Graph With Image Input。</summary>
    private static JsonObject GraphWithImageInput()
    {
        var graph = Graph();
        graph["nodes"]!.AsArray().Insert(1, JsonNode.Parse(
            """{ "id": "reference-node", "type": "image", "data": {} }"""));
        graph["edges"]!.AsArray().Insert(1, JsonNode.Parse(
            """{ "id": "reference-edge", "source": "reference-node", "sourceHandle": "image", "target": "generate", "targetHandle": "image" }"""));
        graph["externalApi"]!["inputs"]!.AsArray().Add(JsonNode.Parse(
            """
            {
              "key": "reference",
              "label": "Reference image",
              "description": "Uploaded reference",
              "type": "image",
              "required": true,
              "target": { "nodeId": "reference-node", "field": "assetUrl" }
            }
            """));
        return graph;
    }
}
