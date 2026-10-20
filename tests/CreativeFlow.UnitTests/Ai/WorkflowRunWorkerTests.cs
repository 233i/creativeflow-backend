using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowRunWorkerTests
{
    /// <summary>知识库适配器已脱敏的连接与配置错误应交付给用户，不能退化为无信息的节点失败。</summary>
    [Theory]
    [InlineData("Qdrant 连接或响应无效，请检查开发隧道和服务配置。")]
    [InlineData("Qdrant 返回 HTTP 503，未自动重试。")]
    [InlineData("向量模型或配置已改变，不能混用旧索引；请创建新的候选版本。")]
    public void KnowledgeFailuresRetainSanitizedExplanation(string message)
    {
        Assert.Equal(message, WorkflowExecutionErrors.UserFacingError(new KnowledgeException(message)));
    }

    /// <summary>没有经过业务层脱敏的网络和运行时异常仍不能暴露端点、密钥或原始响应。</summary>
    [Fact]
    public void UnknownFailuresDoNotExposeInternalDetails()
    {
        const string sensitive = "https://internal.example/path?api_key=synthetic-secret upstream response";
        Assert.Equal("The workflow node could not be completed.",
            WorkflowExecutionErrors.UserFacingError(new HttpRequestException(sensitive)));
        Assert.Equal("The workflow node could not be completed.",
            WorkflowExecutionErrors.UserFacingError(new InvalidOperationException(sensitive)));
    }

    /// <summary>新增知识库提示不改变原通用工作流的已知错误说明。</summary>
    [Fact]
    public void KnownWorkflowFailuresKeepTheirOriginalMessage()
    {
        const string message = "模型或外部接口调用状态不明确，已阻止自动重复调用。";
        Assert.Equal(message, WorkflowExecutionErrors.UserFacingError(new WorkflowV2Exception(message)));
    }

    /// <summary>回归场景：Completed Output Can Be Restored For Continuation。</summary>
    [Fact]
    public void CompletedOutput_CanBeRestoredForContinuation()
    {
        const string json =
            """{"texts":["prompt"],"images":["/asset/image"],"videos":[]}""";

        var restored = WorkflowNodeValue.TryParse(json, out var value);

        Assert.True(restored);
        Assert.Equal(["prompt"], value.Texts);
        Assert.Equal(["/asset/image"], value.Images);
        Assert.Empty(value.Videos);
    }

    /// <summary>回归场景：Invalid Completed Output Is Rejected。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"texts\":[1],\"images\":[],\"videos\":[]}")]
    [InlineData("{\"texts\":[],\"images\":[]}")]
    public void InvalidCompletedOutput_IsRejected(string? json)
    {
        Assert.False(WorkflowNodeValue.TryParse(json, out _));
    }
}
