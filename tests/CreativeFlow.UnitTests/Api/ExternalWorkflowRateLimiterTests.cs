using CreativeFlow.Api.Http;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.UnitTests.Api;

public sealed class ExternalWorkflowRateLimiterTests
{
    /// <summary>回归场景：Enforces Independent Limits For Each Verified Key And Operation。</summary>
    [Fact]
    public void EnforcesIndependentLimitsForEachVerifiedKeyAndOperation()
    {
        using var limiter = new ExternalWorkflowRateLimiter(new ExternalWorkflowApiOptions
        {
            CreateRunPermitLimitPerMinute = 2,
            ImageUploadPermitLimitPerMinute = 1,
            StatusPermitLimitPerMinute = 3,
        });
        var firstKey = WorkflowApiKeyId.New();
        var secondKey = WorkflowApiKeyId.New();

        Assert.True(limiter.TryAcquire(firstKey, ExternalWorkflowRateLimitKind.CreateRun, out _));
        Assert.True(limiter.TryAcquire(firstKey, ExternalWorkflowRateLimitKind.CreateRun, out _));
        Assert.False(limiter.TryAcquire(
            firstKey, ExternalWorkflowRateLimitKind.CreateRun, out var retryAfter));
        Assert.NotNull(retryAfter);

        Assert.True(limiter.TryAcquire(secondKey, ExternalWorkflowRateLimitKind.CreateRun, out _));
        Assert.True(limiter.TryAcquire(firstKey, ExternalWorkflowRateLimitKind.ReadStatus, out _));
        Assert.True(limiter.TryAcquire(firstKey, ExternalWorkflowRateLimitKind.UploadImage, out _));
        Assert.False(limiter.TryAcquire(firstKey, ExternalWorkflowRateLimitKind.UploadImage, out _));
    }
}
