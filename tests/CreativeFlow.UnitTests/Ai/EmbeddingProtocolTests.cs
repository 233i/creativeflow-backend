using System.Net;
using System.Text;
using System.Text.Json;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证独立向量协议的载荷、严格解析、出站和失败语义，全部使用合成响应。</summary>
public sealed class EmbeddingProtocolTests
{
    /// <summary>显式向量协议可往返，旧聊天或媒体协议不会因模型名称被重新绑定。</summary>
    [Fact]
    public void ProtocolIsExplicitAndRoundTrips()
    {
        Assert.True(AiEnumNames.TryParseProviderType("openai-embeddings", out var protocol));
        Assert.Equal("openai-embeddings", protocol.ToWire());
        var request = EmbeddingFixtures.Request();
        Assert.Equal(protocol, request.Provider.ResolveProtocol(request.Model));
        Assert.Equal(ProviderType.OpenAiChat,
            (request.Provider with { Type = ProviderType.OpenAiChat }).ResolveProtocol(request.Model with { Type = null }));
    }

    /// <summary>载荷只包含向量字段，按用途加前缀，默认不发送 dimensions 或伪造密钥。</summary>
    [Fact]
    public async Task PayloadAndOrderArePreserved()
    {
        var request = EmbeddingFixtures.Request();
        using var transport = new EmbeddingFixtures.Transport();
        var client = new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy());
        var result = await client.EmbedAsync(request with { Purpose = "query", Settings = new() { QueryPrefix = "Q: " } }, TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.Equal(["a", "b"], result.Vectors.Select(vector => vector.Id));
        Assert.Equal(new float[] { 1, 0, 0 }, result.Vectors[0].Values);
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(4, result.TotalTokens);
        Assert.Null(transport.Authorization);
        using var payload = JsonDocument.Parse(transport.Payload!);
        Assert.Equal("Q: first", payload.RootElement.GetProperty("input")[0].GetString());
        Assert.Equal("test-embedding", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("float", payload.RootElement.GetProperty("encoding_format").GetString());
        Assert.False(payload.RootElement.TryGetProperty("dimensions", out _));
        Assert.False(payload.RootElement.TryGetProperty("messages", out _));
    }

    /// <summary>仅显式设置维度时发送相应参数；需要鉴权的服务收到原凭据。</summary>
    [Fact]
    public async Task ExplicitDimensionsAndOptionalAuthAreSent()
    {
        using var transport = new EmbeddingFixtures.Transport();
        var request = EmbeddingFixtures.Request() with { ApiKey = "synthetic-key", Settings = new() { RequestedDimensions = 3 } };
        var result = await new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy()).EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.Equal("Bearer synthetic-key", transport.Authorization);
        using var payload = JsonDocument.Parse(transport.Payload!);
        Assert.Equal(3, payload.RootElement.GetProperty("dimensions").GetInt32());
    }

    /// <summary>缺条、重复、越界、空向量、非数值、无穷大、零向量和用量错误均不能部分入库。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":0,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":2,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[]},{\"index\":1,\"embedding\":[]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":1,\"embedding\":[1,2]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[\"NaN\"]},{\"index\":1,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1e100]},{\"index\":1,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0]},{\"index\":1,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":1,\"embedding\":[1]}],\"usage\":{\"total_tokens\":-1}}")]
    public void MalformedResponseIsRejected(string body)
    {
        var result = OpenAiEmbeddingResponse.Parse(Encoding.UTF8.GetBytes(body), EmbeddingFixtures.Request(), 200);
        Assert.False(result.Ok);
        Assert.Empty(result.Vectors);
        Assert.Equal("invalid_response", result.Code);
    }

    /// <summary>维度声明不匹配时拒绝，不裁剪或填零伪造符合要求的向量。</summary>
    [Fact]
    public async Task WrongExpectedDimensionsAreRejected()
    {
        using var transport = new EmbeddingFixtures.Transport();
        var result = await new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy()).EmbedAsync(
            EmbeddingFixtures.Request() with { Settings = new() { ExpectedDimensions = 2 } }, TestContext.Current.CancellationToken);
        Assert.Equal("invalid_response", result.Code);
        Assert.Empty(result.Vectors);
    }

    /// <summary>危险路径、资源限制和空参数不能保存为可执行配置。</summary>
    [Theory]
    [InlineData("https://example.com/embeddings")]
    [InlineData("../embeddings")]
    [InlineData("/embeddings")]
    [InlineData("embeddings?token=x")]
    [InlineData("%2e%2e/embeddings")]
    public void InvalidPathIsRejected(string path) =>
        Assert.NotNull(EmbeddingValidation.ValidateSettings(new() { EndpointPath = path }));

    /// <summary>输入标识重复、用途错误、超长前缀和并发越界会在发送前失败。</summary>
    [Fact]
    public void InputsAndLimitsAreValidated()
    {
        Assert.NotNull(EmbeddingValidation.ValidateSettings(new() { Concurrency = 5 }));
        Assert.NotNull(EmbeddingValidation.ValidateSettings(new() { MaxBatchSize = 33 }));
        Assert.NotNull(EmbeddingValidation.ValidateSettings(new() { RequestedDimensions = 3, ExpectedDimensions = 4 }));
        Assert.NotNull(EmbeddingValidation.ValidateSettings(new() { QueryPrefix = null! }));
        Assert.NotNull(EmbeddingValidation.ValidateInputs(new(), [new("same", "a"), new("same", "b")], "document"));
        Assert.NotNull(EmbeddingValidation.ValidateInputs(new(), [new("id", "a")], "other"));
        Assert.NotNull(EmbeddingValidation.ValidateInputs(new() { MaxInputCharacters = 2, DocumentPrefix = "xx" }, [new("id", "a")], "document"));
    }

    /// <summary>HTTP 错误不自动重试，且不会回显含密钥/输入的上游错误正文。</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task HttpErrorsAreRedactedAndNeverRetried(int status)
    {
        using var transport = new EmbeddingFixtures.Transport { Status = (HttpStatusCode)status, Body = "secret-key private input" };
        var result = await new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy()).EmbedAsync(EmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(status, result.StatusCode);
        Assert.DoesNotContain("secret-key", result.Error!);
        Assert.DoesNotContain("private input", result.Error!);
    }

    /// <summary>只保留上游机器字段帮助定位参数错误，不回显错误消息中的提示词或凭据。</summary>
    [Fact]
    public async Task StructuredHttpErrorExposesSafeParameterHintOnly()
    {
        using var transport = new EmbeddingFixtures.Transport
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":{"code":"InvalidParameter","param":"dimensions","message":"secret-key private input"}}"""
        };
        var result = await new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy()).EmbedAsync(
            EmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("upstream_error", result.Code);
        Assert.Contains("code: InvalidParameter", result.Error!);
        Assert.Contains("parameter: dimensions", result.Error!);
        Assert.DoesNotContain("secret-key", result.Error!);
        Assert.DoesNotContain("private input", result.Error!);
    }

    /// <summary>豆包批量上限错误只返回数字摘要，避免用户只能看到笼统的 input 失败。</summary>
    [Fact]
    public async Task BatchLimitErrorExposesNumericLimitWithoutRawMessage()
    {
        using var transport = new EmbeddingFixtures.Transport
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":{"code":"InvalidParameter","param":"input","message":"Embeddings API input limit exceeded: max 10, got 16. secret-key"}}"""
        };
        var result = await new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy()).EmbedAsync(
            EmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Contains("max inputs: 10; received: 16", result.Error!);
        Assert.DoesNotContain("secret-key", result.Error!);
    }

    /// <summary>超时只返回未知结果提示；调用方取消继续传播，二者都不重发。</summary>
    [Fact]
    public async Task TimeoutAndCancellationAreDistinct()
    {
        using var transport = new EmbeddingFixtures.Transport { OnSend = _ => throw new TaskCanceledException() };
        var client = new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy());
        Assert.Equal("timeout_unknown", (await client.EmbedAsync(EmbeddingFixtures.Request(), TestContext.Current.CancellationToken)).Code);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(EmbeddingFixtures.Request(), cancelled.Token));
        Assert.Equal(1, transport.Calls);
    }

    /// <summary>精确许可不能扩展到其他端口、路径、云元数据或生产回环地址。</summary>
    [Fact]
    public async Task EndpointPolicyIsIndependentAndFailClosed()
    {
        var policy = EmbeddingFixtures.Policy();
        Assert.Throws<InvalidOperationException>(() => policy.ValidateEndpoint(new("http://127.0.0.1:1235/v1/embeddings")));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateEndpoint(new("http://127.0.0.1:1234/admin")));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(new("https://example.com"), [IPAddress.Parse("169.254.169.254")]));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(new("https://example.com"), [IPAddress.Parse("::ffff:10.0.0.1")]));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(new("https://example.com"), [IPAddress.Parse("fc00::1")]));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses(new("https://example.com"), [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmbeddingFixtures.Policy(false).ResolveAsync(new("http://127.0.0.1:1234/v1/embeddings"), TestContext.Current.CancellationToken));
    }
}
