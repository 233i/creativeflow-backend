using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class TextStreamProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(ProviderType.OpenAiChat)][InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)][InlineData(ProviderType.Gemini)]
    public async Task DeliversRealDeltasBeforeCompletionPreservesUsageAndIgnoresReasoning(ProviderType protocol)
    {
        var (first, last) = Frames(protocol);
        using var transport = new StreamingTextTransport(first, last);
        var deltas = new List<string>(); var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = StreamingTextTransport.Request(protocol, (text, _) => { deltas.Add(text); received.TrySetResult(); return Task.CompletedTask; });
        var pending = Client(protocol, transport).GenerateAsync(request, Ct);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.False(pending.IsCompleted); Assert.Equal("合成", string.Concat(deltas));
        transport.Gate.SetResult();
        var result = await pending;
        Assert.True(result.Ok, result.Error); Assert.Equal("合成回答", result.Text); Assert.Equal(result.Text, string.Concat(deltas));
        Assert.Equal(200, result.ResponseStatusCode); Assert.NotNull(result.Usage); Assert.NotNull(result.FinishReason);
        Assert.Equal(10, result.Usage["prompt_tokens"]!.GetValue<long>()); Assert.Equal(4, result.Usage["completion_tokens"]!.GetValue<long>());
        if (protocol == ProviderType.Anthropic) Assert.Null(result.Usage["total_tokens"]);
        else Assert.Equal(14, result.Usage["total_tokens"]!.GetValue<long>());
        Assert.DoesNotContain("secret", result.Text); Assert.Equal(1, transport.Calls); Assert.Equal("workflow-model", transport.ClientName);
        var body = JsonNode.Parse(transport.Payload!)!;
        if (protocol == ProviderType.Gemini) Assert.EndsWith(":streamGenerateContent?alt=sse", transport.Url!.AbsoluteUri);
        else Assert.True(body["stream"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("broken")][InlineData("eof")][InlineData("error")][InlineData("content-type")]
    public async Task IncompleteOrInvalidStreamsFailWithoutRetry(string kind)
    {
        var (first, _) = Frames(ProviderType.OpenAiChat);
        using var transport = new StreamingTextTransport(first, kind switch { "broken" => "data: {broken}\n\n", "error" => "data: {\"error\":{\"message\":\"secret\"}}\n\n", _ => "" });
        transport.Gate.SetResult(); if (kind == "content-type") transport.ContentType = "application/json";
        var result = await Client(ProviderType.OpenAiChat, transport).GenerateAsync(StreamingTextTransport.Request(ProviderType.OpenAiChat, (_, _) => Task.CompletedTask), Ct);
        Assert.False(result.Ok); Assert.Null(result.Text); Assert.Equal(200, result.ResponseStatusCode); Assert.Equal(1, transport.Calls); Assert.DoesNotContain("secret", result.Error!);
    }

    [Fact]
    public async Task CancellationStopsReadingWithoutStartingAnotherRequest()
    {
        using var transport = new StreamingTextTransport(Frames(ProviderType.OpenAiChat).First);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Client(ProviderType.OpenAiChat, transport).GenerateAsync(StreamingTextTransport.Request(ProviderType.OpenAiChat, (_, _) => { received.SetResult(); return Task.CompletedTask; }), cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(1, transport.Calls);
    }

    internal static ITextGenerationClient Client(ProviderType protocol, IHttpClientFactory factory) => protocol switch
    {
        ProviderType.OpenAiChat => new OpenAiChatClient(factory, NullLogger<OpenAiChatClient>.Instance),
        ProviderType.OpenAiResponses => new OpenAiResponsesClient(factory, NullLogger<OpenAiResponsesClient>.Instance),
        ProviderType.Anthropic => new AnthropicTextClient(factory, NullLogger<AnthropicTextClient>.Instance),
        ProviderType.Gemini => new GeminiTextClient(factory, NullLogger<GeminiTextClient>.Instance),
        _ => throw new ArgumentException(nameof(protocol)),
    };

    internal static (string First, string Last) Frames(ProviderType protocol) => protocol switch
    {
        ProviderType.OpenAiChat => (
            "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"secret\",\"content\":\"合成\"}}]}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"回答\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4,\"total_tokens\":14},\"choices\":[]}\n\ndata: [DONE]\n\n"),
        ProviderType.OpenAiResponses => (
            "data: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"secret\"}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"合成\"}\n\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"回答\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"usage\":{\"input_tokens\":10,\"output_tokens\":4,\"total_tokens\":14}}}\n\n"),
        ProviderType.Anthropic => (
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"test\",\"usage\":{\"input_tokens\":10}}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"secret\"}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"合成\"}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"回答\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":4}}\n\ndata: {\"type\":\"message_stop\"}\n\n"),
        ProviderType.Gemini => (
            "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"thought\":true,\"text\":\"secret\"},{\"text\":\"合成\"}]}}]}\n\n",
            "data: {\"responseId\":\"test\",\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"回答\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":4,\"totalTokenCount\":14}}\n\n"),
        _ => throw new ArgumentException(nameof(protocol)),
    };
}
