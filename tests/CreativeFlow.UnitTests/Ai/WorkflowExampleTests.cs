using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>交付的示例文件必须通过实际稳定图及发布契约校验，不能只作为示意图。</summary>
public sealed class WorkflowExampleTests
{
    /// <summary>验证知识命中/HTTP 默认路径、名义类型和两类可选结束输出。</summary>
    [Fact]
    public async Task KnowledgeRoutingExampleHasAValidPublishableContract()
    {
        await using var stream = typeof(WorkflowExampleTests).Assembly.GetManifestResourceStream("CreativeFlow.TestExamples.knowledge-routing.workflow.json")!;
        using var reader = new StreamReader(stream); var json = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var graph = JsonNode.Parse(json);
        var result = WorkflowV2GraphValidator.Validate(graph);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var (_, inputs, outputs) = WorkflowV2PublicationService.Contract(json);
        Assert.Equal("question", Assert.Single(inputs)!["key"]!.GetValue<string>());
        Assert.Equal(new[] { "answer", "fallback" }, outputs.Select(field => field!["key"]!.GetValue<string>()).ToArray());
    }
}
