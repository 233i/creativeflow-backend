using CreativeFlow.Application.Features.Agent;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>审批策略的输入矩阵，不依赖模型、数据库或浏览器。</summary>
public sealed class AgentToolApprovalPolicyTests
{
    /// <summary>保留 Always、Never 和具体破坏性 action 的判断，损坏 JSON 不扩大审批范围。</summary>
    [Theory]
    [InlineData(AgentApprovalPolicy.Always, "{}", true)]
    [InlineData(AgentApprovalPolicy.Never, "{\"action\":\"clear\"}", false)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":\"clear\"}", true)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":\"import\"}", true)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":\"replace\"}", true)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":\"delete_canvas\"}", true)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":\"export\"}", false)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{\"action\":1}", false)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{broken", false)]
    [InlineData(AgentApprovalPolicy.DestructiveAction, "{}", false)]
    public void CanvasPolicyRetainsExistingApprovalRules(AgentApprovalPolicy policy, string json, bool required)
    {
        var descriptor = new AgentToolDescriptor("test-canvas", AgentToolSite.Canvas, policy, "test", "{}");
        var summary = new AgentToolApprovalPolicy().CanvasSummary(descriptor, json);
        Assert.Equal(required, summary is not null);
        if (required) Assert.Equal("Allow the Agent to run the destructive canvas tool 'test-canvas'?", summary);
    }
}
