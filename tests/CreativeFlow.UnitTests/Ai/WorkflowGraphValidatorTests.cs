using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowGraphValidatorTests
{
    /// <summary>回归场景：Compiles A Valid Typed Workflow In Dependency Order。</summary>
    [Fact]
    public void CompilesAValidTypedWorkflowInDependencyOrder()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "out", "type": "imageOutput", "data": {} },
                { "id": "generate", "type": "imageGenerate", "data": { "count": 1 } },
                { "id": "prompt", "type": "text", "data": { "text": "a lighthouse" } }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
                { "id": "e2", "source": "generate", "sourceHandle": "image", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.True(result.IsValid);
        Assert.Equal(["prompt", "generate", "out"], result.Plan!.Nodes.Select(node => node.Id));
    }

    /// <summary>回归场景：Rejects Incompatible Ports。</summary>
    [Fact]
    public void RejectsIncompatiblePorts()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "hello" } },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": [
                { "id": "e", "source": "prompt", "sourceHandle": "text", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("incompatible", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Cycles Even When Every Port Is Valid。</summary>
    [Fact]
    public void RejectsCyclesEvenWhenEveryPortIsValid()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "a", "type": "imageGenerate", "data": {} },
                { "id": "b", "type": "imageGenerate", "data": {} },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "a", "sourceHandle": "image", "target": "b", "targetHandle": "image" },
                { "id": "e2", "source": "b", "sourceHandle": "image", "target": "a", "targetHandle": "image" },
                { "id": "e3", "source": "b", "sourceHandle": "image", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains("workflow cannot contain cycles", result.Errors);
    }

    /// <summary>回归场景：Rejects Unknown Custom Nodes。</summary>
    [Fact]
    public void RejectsUnknownCustomNodes()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "script", "type": "python", "data": { "code": "open('/etc/passwd')" } },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": []
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unsupported type", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Compiles A Video Result Node With Its Typed Input。</summary>
    [Fact]
    public void CompilesAVideoResultNodeWithItsTypedInput()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "slow camera move" } },
                { "id": "generate", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
                { "id": "e2", "source": "generate", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ]
            }
            """));

        Assert.True(result.IsValid);
        Assert.Equal(["prompt", "generate", "out"], result.Plan!.Nodes.Select(node => node.Id));
    }

    /// <summary>回归场景：Compiles Video Continuation Through Its Last Frame。</summary>
    [Fact]
    public void CompilesVideoContinuationThroughItsLastFrame()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "continue the motion" } },
                { "id": "first", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "checkpoint", "type": "videoOutput", "data": {} },
                { "id": "frame", "type": "videoFrame", "data": { "frame": "last" } },
                { "id": "second", "type": "videoGenerate", "data": { "duration": 5, "inputMode": "first-last" } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "first", "targetHandle": "prompt" },
                { "id": "e2", "source": "first", "sourceHandle": "video", "target": "checkpoint", "targetHandle": "video" },
                { "id": "e3", "source": "checkpoint", "sourceHandle": "video", "target": "frame", "targetHandle": "video" },
                { "id": "e4", "source": "frame", "sourceHandle": "image", "target": "second", "targetHandle": "image" },
                { "id": "e5", "source": "second", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ]
            }
            """));

        Assert.True(result.IsValid);
        Assert.Equal(
            ["prompt", "first", "checkpoint", "frame", "second", "out"],
            result.Plan!.Nodes.Select(node => node.Id));
        Assert.Equal(
            WorkflowValueType.Image,
            result.Plan.Nodes.Single(node => node.Id == "second").Inputs.Single().ValueType);
    }

    /// <summary>回归场景：Compiles Video Extension With Prompt And One Video。</summary>
    [Fact]
    public void CompilesVideoExtensionWithPromptAndOneVideo()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "continue walking" } },
                { "id": "first", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "extend", "type": "videoExtend", "data": { "duration": 6 } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "first", "targetHandle": "prompt" },
                { "id": "e2", "source": "prompt", "sourceHandle": "text", "target": "extend", "targetHandle": "prompt" },
                { "id": "e3", "source": "first", "sourceHandle": "video", "target": "extend", "targetHandle": "video" },
                { "id": "e4", "source": "extend", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ]
            }
            """));

        Assert.True(result.IsValid);
        var extension = result.Plan!.Nodes.Single(node => node.Id == "extend");
        Assert.Contains(extension.Inputs, input => input.ValueType == WorkflowValueType.Text);
        Assert.Contains(extension.Inputs, input => input.ValueType == WorkflowValueType.Video);
    }

    /// <summary>回归场景：Rejects Video Extension Without Prompt Or With Invalid Duration。</summary>
    [Fact]
    public void RejectsVideoExtensionWithoutPromptOrWithInvalidDuration()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "first scene" } },
                { "id": "first", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "extend", "type": "videoExtend", "data": { "duration": 11 } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "first", "targetHandle": "prompt" },
                { "id": "e2", "source": "first", "sourceHandle": "video", "target": "extend", "targetHandle": "video" },
                { "id": "e3", "source": "extend", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("text prompt", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("between 2 and 10", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Video Frame Without A Video Input。</summary>
    [Fact]
    public void RejectsVideoFrameWithoutAVideoInput()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "frame", "type": "videoFrame", "data": { "frame": "last" } },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": [
                { "id": "e", "source": "frame", "sourceHandle": "image", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("needs a video input", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Unsupported Video Frame Settings。</summary>
    [Fact]
    public void RejectsUnsupportedVideoFrameSettings()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "motion" } },
                { "id": "video", "type": "videoGenerate", "data": { "inputMode": "video-edit" } },
                { "id": "frame", "type": "videoFrame", "data": { "frame": "middle" } },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "video", "targetHandle": "prompt" },
                { "id": "e2", "source": "video", "sourceHandle": "video", "target": "frame", "targetHandle": "video" },
                { "id": "e3", "source": "frame", "sourceHandle": "image", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("input mode", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("frame position", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Video Duration Above Fifteen Seconds。</summary>
    [Fact]
    public void RejectsVideoDurationAboveFifteenSeconds()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "slow camera move" } },
                { "id": "generate", "type": "videoGenerate", "data": { "duration": 16 } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
                { "id": "e2", "source": "generate", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("between 1 and 15", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Rejects Video Connected To An Image Result Node。</summary>
    [Fact]
    public void RejectsVideoConnectedToAnImageResultNode()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "slow camera move" } },
                { "id": "generate", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "out", "type": "imageOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
                { "id": "e2", "source": "generate", "sourceHandle": "video", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("incompatible", StringComparison.Ordinal));
    }

    /// <summary>回归场景：Compiles An Image Result As A Pass Through Checkpoint。</summary>
    [Fact]
    public void CompilesAnImageResultAsAPassThroughCheckpoint()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "a lighthouse at dusk" } },
                { "id": "first", "type": "imageGenerate", "data": { "count": 1 } },
                { "id": "checkpoint", "type": "imageOutput", "data": {} },
                { "id": "variation", "type": "imageGenerate", "data": { "count": 1 } },
                { "id": "video", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "variationOut", "type": "imageOutput", "data": {} },
                { "id": "videoOut", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "first", "targetHandle": "prompt" },
                { "id": "e2", "source": "first", "sourceHandle": "image", "target": "checkpoint", "targetHandle": "image" },
                { "id": "e3", "source": "checkpoint", "sourceHandle": "image", "target": "variation", "targetHandle": "image" },
                { "id": "e4", "source": "checkpoint", "sourceHandle": "image", "target": "video", "targetHandle": "image" },
                { "id": "e5", "source": "variation", "sourceHandle": "image", "target": "variationOut", "targetHandle": "image" },
                { "id": "e6", "source": "video", "sourceHandle": "video", "target": "videoOut", "targetHandle": "video" }
              ]
            }
            """));

        Assert.True(result.IsValid);
        Assert.Equal(
            ["prompt", "first", "checkpoint", "variation", "video", "variationOut", "videoOut"],
            result.Plan!.Nodes.Select(node => node.Id));
    }

    /// <summary>回归场景：Keeps The Legacy Mixed Output Node Valid。</summary>
    [Fact]
    public void KeepsTheLegacyMixedOutputNodeValid()
    {
        var result = WorkflowGraphValidator.Validate(Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "legacy" } },
                { "id": "generate", "type": "imageGenerate", "data": {} },
                { "id": "out", "type": "output", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "generate", "targetHandle": "prompt" },
                { "id": "e2", "source": "generate", "sourceHandle": "image", "target": "out", "targetHandle": "image" }
              ]
            }
            """));

        Assert.True(result.IsValid);
    }

    /// <summary>回归场景：Template Validation Allows Empty Text And Image Placeholders。</summary>
    [Fact]
    public void TemplateValidationAllowsEmptyTextAndImagePlaceholders()
    {
        var graph = Parse(
            """
            {
              "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "" } },
                { "id": "image", "type": "image", "data": {} },
                { "id": "imageGenerate", "type": "imageGenerate", "data": { "count": 1 } },
                { "id": "videoGenerate", "type": "videoGenerate", "data": { "duration": 5 } },
                { "id": "imageOut", "type": "imageOutput", "data": {} },
                { "id": "videoOut", "type": "videoOutput", "data": {} }
              ],
              "edges": [
                { "id": "e1", "source": "prompt", "sourceHandle": "text", "target": "imageGenerate", "targetHandle": "prompt" },
                { "id": "e2", "source": "imageGenerate", "sourceHandle": "image", "target": "imageOut", "targetHandle": "image" },
                { "id": "e3", "source": "image", "sourceHandle": "image", "target": "videoGenerate", "targetHandle": "image" },
                { "id": "e4", "source": "videoGenerate", "sourceHandle": "video", "target": "videoOut", "targetHandle": "video" }
              ]
            }
            """);

        var ordinaryWorkflow = WorkflowGraphValidator.Validate(graph);
        var template = WorkflowGraphValidator.ValidateTemplate(graph);

        Assert.False(ordinaryWorkflow.IsValid);
        Assert.Contains(ordinaryWorkflow.Errors, error => error.Contains("text node", StringComparison.Ordinal));
        Assert.Contains(ordinaryWorkflow.Errors, error => error.Contains("stored image asset", StringComparison.Ordinal));
        Assert.True(template.IsValid);
    }

    /// <summary>保留旧节点的空交换设置，并拒绝无法按布尔值解释的新配置。</summary>
    [Theory]
    [InlineData("null", true)]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    [InlineData("[]", false)]
    public void ValidatesAndPreservesFrameSwapSetting(string value, bool valid)
    {
        var graph = Parse("""
            { "nodes": [
                { "id": "prompt", "type": "text", "data": { "text": "move" } },
                { "id": "video", "type": "videoGenerate", "data": { "inputMode": "first-last" } },
                { "id": "out", "type": "videoOutput", "data": {} }
              ], "edges": [
                { "id": "a", "source": "prompt", "sourceHandle": "text", "target": "video", "targetHandle": "prompt" },
                { "id": "b", "source": "video", "sourceHandle": "video", "target": "out", "targetHandle": "video" }
              ] }
            """);
        graph["nodes"]![1]!["data"]!["reverseFrames"] = JsonNode.Parse(value);
        var result = WorkflowGraphValidator.Validate(graph);
        Assert.Equal(valid, result.IsValid);
        if (valid)
            Assert.Equal(value, result.Plan!.Nodes.Single(node => node.Id == "video").Data["reverseFrames"]?.ToJsonString() ?? "null");
        else
            Assert.Contains(result.Errors, error => error.Contains("reverseFrames", StringComparison.Ordinal));
    }

    /// <summary>按当前协议解析 JSON 响应并应用既有回退规则。</summary>
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
