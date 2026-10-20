using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证新向量通道的渠道/全局背压和失败后计数释放，不涉及原媒体队列。</summary>
public sealed class EmbeddingDispatcherTests
{
    /// <summary>渠道并发达到上限时不发送，首个请求完成后额度恢复。</summary>
    [Fact]
    public async Task ProviderLimitRejectsWithoutSubmittingAndReleases()
    {
        var client = new BlockingClient();
        var dispatcher = new EmbeddingDispatcher([client]);
        var request = EmbeddingFixtures.Request();
        var first = dispatcher.EmbedAsync(request, TestContext.Current.CancellationToken);
        var busy = await dispatcher.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("busy", busy.Code);
        Assert.Equal(1, client.Calls);
        client.Completion.SetResult(EmbeddingResult.Failure("synthetic", "test failure"));
        await first;
        Assert.Equal("synthetic", (await dispatcher.EmbedAsync(request, TestContext.Current.CancellationToken)).Code);
        Assert.Equal(2, client.Calls);
    }

    /// <summary>不同渠道共享四个活跃请求上限，不能通过增加渠道绕过限制。</summary>
    [Fact]
    public async Task GlobalLimitIsSharedAcrossProviders()
    {
        var client = new BlockingClient();
        var dispatcher = new EmbeddingDispatcher([client]);
        var tasks = Enumerable.Range(0, 4).Select(_ => dispatcher.EmbedAsync(EmbeddingFixtures.Request(), TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal("busy", (await dispatcher.EmbedAsync(EmbeddingFixtures.Request(), TestContext.Current.CancellationToken)).Code);
        Assert.Equal(4, client.Calls);
        client.Completion.SetResult(EmbeddingResult.Failure("synthetic", "test failure"));
        await Task.WhenAll(tasks);
    }

    /// <summary>禁用、错配、错误分类和聊天协议均在向量客户端之前被拒绝。</summary>
    [Fact]
    public async Task InvalidModelNeverReachesTheClient()
    {
        var client = new BlockingClient();
        var dispatcher = new EmbeddingDispatcher([client]);
        var request = EmbeddingFixtures.Request();
        foreach (var invalid in new[]
                 {
                     request with { Provider = request.Provider with { Enabled = false } },
                     request with { Model = request.Model with { Enabled = false } },
                     request with { Model = request.Model with { ProviderId = ProviderId.New() } },
                     request with { Model = request.Model with { Category = ModelCategory.Chat } },
                     request with { Model = request.Model with { Type = ProviderType.OpenAiChat } },
                 }) Assert.False((await dispatcher.EmbedAsync(invalid, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(0, client.Calls);
    }

    /// <summary>保持待完成的合成客户端，只记录进入次数，不发送网络请求。</summary>
    private sealed class BlockingClient : IEmbeddingClient
    {
        /// <summary>本替身声明的唯一协议。</summary>
        public ProviderType Protocol => ProviderType.OpenAiEmbeddings;
        /// <summary>已进入客户端的调用数。</summary>
        public int Calls { get; private set; }
        /// <summary>由测试显式完成的应答，用于保持并发额度占用。</summary>
        public TaskCompletionSource<EmbeddingResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>记录并等待测试应答，等待支持取消。</summary>
        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
        {
            Calls++;
            return Completion.Task.WaitAsync(ct);
        }
    }
}
