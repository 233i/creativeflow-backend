using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowPartialRerunPlannerTests
{
    /// <summary>回归场景：Includes Selected Node And Only Its Transitive Downstream Nodes。</summary>
    [Fact]
    public void IncludesSelectedNodeAndOnlyItsTransitiveDownstreamNodes()
    {
        var aToB = Edge("a-b", "a", "b");
        var bToC = Edge("b-c", "b", "c");
        var cToD = Edge("c-d", "c", "d");
        var bToE = Edge("b-e", "b", "e");
        var eToF = Edge("e-f", "e", "f");
        var plan = new WorkflowPlan(
        [
            Node("a"),
            Node("b", aToB),
            Node("c", bToC),
            Node("e", bToE),
            Node("d", cToD),
            Node("f", eToF),
        ]);

        var rerunNodeIds = WorkflowPartialRerunPlanner.FindRerunNodeIds(plan, "c");

        Assert.NotNull(rerunNodeIds);
        Assert.Equal(2, rerunNodeIds.Count);
        Assert.Contains("c", rerunNodeIds);
        Assert.Contains("d", rerunNodeIds);
        Assert.DoesNotContain("e", rerunNodeIds);
        Assert.DoesNotContain("f", rerunNodeIds);
    }

    /// <summary>回归场景：Returns Null When Start Node Does Not Exist。</summary>
    [Fact]
    public void ReturnsNullWhenStartNodeDoesNotExist()
    {
        var plan = new WorkflowPlan([Node("only")]);

        Assert.Null(WorkflowPartialRerunPlanner.FindRerunNodeIds(plan, "missing"));
    }

    /// <summary>回归场景：Node。</summary>
    private static WorkflowPlanNode Node(
        string id,
        params WorkflowPlanEdge[] inputs) =>
        new(id, "text", new JsonObject(), inputs);

    /// <summary>构造局部重跑测试需要的带端口图连线。</summary>
    private static WorkflowPlanEdge Edge(string id, string source, string target) =>
        new(id, source, "text", target, "prompt", WorkflowValueType.Text);
}
