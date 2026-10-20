using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>先在原 HTTP Handler 上锁定文本行为，再用同一组断言验证应用用例提取。</summary>
public sealed class TextGenerationCompatibilityTests
{
    /// <summary>缺少已验证账号或设备时，在解析非法输入和访问端口之前返回原 MVC 401。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BothAccountAndDeviceAreRequired(bool hasAccount, bool hasDevice)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync(hasAccount, hasDevice);
        using var response = await fixture.PostAsync(fixture.Request() with { ProviderId = "invalid", Prompt = " " });
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Empty(fixture.Log.Entries);
    }

    /// <summary>渠道标识先于提示词校验；即使带有参考图，空白提示词也不能进入渠道查询。</summary>
    [Theory]
    [InlineData(true, "malformed provider id")]
    [InlineData(false, "a prompt is required")]
    public async Task InputValidationPrecedesProviderLookup(bool malformedId, string error)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var request = fixture.Request() with { Prompt = " \t", Images = [fixture.AddReference()] };
        if (malformedId) request = request with { ProviderId = "invalid" };
        using var response = await fixture.PostAsync(request);
        await AssertInvalidAsync(response, error);
        Assert.Empty(fixture.Log.Entries);
    }

    /// <summary>不存在的渠道保持 MVC 404，不继续访问密钥、素材或上游。</summary>
    [Fact]
    public async Task MissingProviderPreservesNotFoundResponse()
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var request = fixture.Request();
        fixture.Providers.Provider = null;
        using var response = await fixture.PostAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found");
        Assert.Equal(["provider"], fixture.Log.Entries);
    }

    /// <summary>保留渠道启用、模型精确匹配、启用及聊天分类的失败顺序，先于解密和参考图上限。</summary>
    [Theory]
    [InlineData("provider", "provider 'Text provider' is disabled")]
    [InlineData("missing", "model 'chat-model' is not configured on this provider")]
    [InlineData("case", "model 'chat-model' is not configured on this provider")]
    [InlineData("disabled", "model 'chat-model' is disabled")]
    [InlineData("category", "model 'chat-model' is not a chat model")]
    public async Task ModelValidationOrderIsStable(string scenario, string error)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider! with { ApiKey = new ApiKeySecret([], [], [], "test") };
        fixture.Providers.Provider = scenario switch
        {
            "provider" => provider with { Enabled = false, Models = [] },
            "missing" => provider with { Models = [] },
            "case" => provider with { Models = [provider.Models[0] with { ModelKey = "CHAT-MODEL" }] },
            "disabled" => provider with { Models = [provider.Models[0] with { Enabled = false, Category = ModelCategory.Image }] },
            _ => provider with { Models = [provider.Models[0] with { Category = ModelCategory.Image }] },
        };
        fixture.Protector.Error = new CryptographicException("must not be reached");
        using var response = await fixture.PostAsync(fixture.Request() with { Images = new string[9] });
        await AssertInvalidAsync(response, error);
        Assert.Equal(["provider"], fixture.Log.Entries);
    }

    /// <summary>无密钥的渠道向上游传空串，已保存的密钥只解密一次且不写入响应。</summary>
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "text-test-key")]
    public async Task ApiKeysAreOptionalAndRemainServerSide(bool hasKey, string expectedKey)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        if (hasKey) fixture.Providers.Provider = fixture.Providers.Provider! with { ApiKey = new ApiKeySecret([], [], [], "test") };
        else fixture.Protector.Error = new CryptographicException("must not decrypt a missing key");
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertTextAsync(response, "  generated text\n");
        Assert.Equal(expectedKey, fixture.Text.Request!.ApiKey);
        Assert.Equal(hasKey ? ["provider", "key", "dispatch"] : ["provider", "dispatch"], fixture.Log.Entries);
    }

    /// <summary>只有密码学异常映射为既有重填密钥提示，而且发生在参考图数量检查之前。</summary>
    [Fact]
    public async Task CorruptKeyPrecedesReferenceLimit()
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        fixture.Providers.Provider = fixture.Providers.Provider! with { ApiKey = new ApiKeySecret([], [], [], "test") };
        fixture.Protector.Error = new CryptographicException("sensitive diagnostic");
        using var response = await fixture.PostAsync(fixture.Request() with { Images = new string[9] });
        await AssertInvalidAsync(response, "The stored API key could not be decrypted. Re-enter it in settings.");
        Assert.Equal(["provider", "key"], fixture.Log.Entries);
    }

    /// <summary>非密码学异常继续传播，不伪装成密钥损坏或重试生成。</summary>
    [Fact]
    public async Task UnexpectedKeyErrorIsNotTranslated()
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        fixture.Providers.Provider = fixture.Providers.Provider! with { ApiKey = new ApiKeySecret([], [], [], "test") };
        fixture.Protector.Error = new InvalidOperationException("Simulated key service failure.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PostAsync(fixture.Request()));
        Assert.Same(fixture.Protector.Error, error);
        Assert.Equal(["provider", "key"], fixture.Log.Entries);
    }

    /// <summary>参考图上限按原始 URL 数量计算；八个不可解析 URL 仍可降为纯文本生成。</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public async Task ReferenceLimitCountsInputsBeforeFiltering(int count)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var urls = Enumerable.Repeat("https://untrusted.invalid/reference.png", count).ToArray();
        using var response = await fixture.PostAsync(fixture.Request() with { Images = urls });
        if (count == 9)
        {
            await AssertInvalidAsync(response, "at most 8 reference images are allowed");
            Assert.Equal(["provider"], fixture.Log.Entries);
        }
        else
        {
            await AssertTextAsync(response, "  generated text\n");
            Assert.Empty(fixture.Text.Request!.ReferenceImages);
            Assert.Equal(["provider", "dispatch"], fixture.Log.Entries);
        }
    }

    /// <summary>仅原样站内前缀及可解析 ID 会进入素材读取；不下载外部 URL 或扩展 URL 规范化。</summary>
    [Theory]
    [InlineData("https://untrusted.invalid/reference.png")]
    [InlineData("/API/v1/canvas/assets/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/v1/canvas/assets/invalid")]
    [InlineData("/api/v1/canvas/assets/00000000-0000-0000-0000-000000000000#fragment")]
    [InlineData("")]
    public async Task UnresolvableReferencesAreSkipped(string url)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with { Images = [url] });
        await AssertTextAsync(response, "  generated text\n");
        Assert.Empty(fixture.Text.Request!.ReferenceImages);
        Assert.Equal(["provider", "dispatch"], fixture.Log.Entries);
    }

    /// <summary>保留素材读取顺序、重复项和查询串处理；缺失或越界素材跳过，不新增媒体或视觉能力校验。</summary>
    [Fact]
    public async Task ReferencesPreserveOwnershipOrderBytesAndLegacyMetadataPolicy()
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var first = fixture.AddReference([4, 5], mediaType: "image/jpeg");
        var second = fixture.AddReference([8, 9, 10], mediaType: "video/mp4", kind: "video");
        var foreign = fixture.AddReference(owner: DeviceId.New());
        var missingBytes = fixture.AddReference();
        fixture.Store.Available.Remove(fixture.Assets.Available.Values.Last().StoragePath);
        var missingRecord = $"/api/v1/canvas/assets/{Guid.NewGuid()}";
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Images = ["https://untrusted.invalid/a", foreign, missingRecord, missingBytes, second + "?v=1", first, second],
        });
        await AssertTextAsync(response, "  generated text\n");

        // 现有入口信任可访问素材的字节与媒体类型，不能在分层时偷偷增加图片类型过滤或去重。
        var references = fixture.Text.Request!.ReferenceImages;
        Assert.Equal(3, references.Count);
        Assert.Equal([8, 9, 10], references[0].Bytes);
        Assert.Equal("video/mp4", references[0].MediaType);
        Assert.Equal([4, 5], references[1].Bytes);
        Assert.Equal("image/jpeg", references[1].MediaType);
        Assert.Equal(references[0].Bytes, references[2].Bytes);
        Assert.Equal(fixture.Store.OpenedPaths[1], fixture.Store.OpenedPaths[3]);
        Assert.All(fixture.Assets.RequestedDevices, device => Assert.Equal(fixture.DeviceId, device));
        Assert.All(fixture.Store.Streams, stream => Assert.True(stream.WasDisposed));
        Assert.Equal(3, fixture.Store.Streams.Count);
        Assert.Equal(1, fixture.Log.Entries.Count(stage => stage == "dispatch"));
        Assert.Single(fixture.Log.Tokens.Distinct());
    }

    /// <summary>请求输出上限仅在 null 时回退到模型；提示词、系统指令及温度均不裁剪或夹取。</summary>
    [Theory]
    [InlineData(null, 4096, 4096, null)]
    [InlineData(2048, 4096, 2048, 0.7)]
    [InlineData(0, 4096, 0, 0.0)]
    [InlineData(-1, 4096, -1, -0.5)]
    [InlineData(null, null, null, 2.5)]
    public async Task RequestOptionsAndModelFallbackAreUnchanged(
        int? requested, int? configured, int? expected, double? temperature)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = provider with { Models = [provider.Models[0] with { MaxOutputTokens = configured }] };
        var request = fixture.Request() with { MaxOutputTokens = requested, Temperature = temperature };
        using var response = await fixture.PostAsync(request);
        await AssertTextAsync(response, "  generated text\n");
        var upstream = fixture.Text.Request!;
        Assert.Same(fixture.Providers.Provider, upstream.Provider);
        Assert.Equal(fixture.Providers.Provider.Id, fixture.Providers.RequestedId);
        Assert.Equal(request.ModelKey, upstream.ModelKey);
        Assert.Equal(request.Prompt, upstream.Prompt);
        Assert.Equal(request.SystemPrompt, upstream.SystemPrompt);
        Assert.Equal(expected, upstream.MaxOutputTokens);
        Assert.Equal(temperature, upstream.Temperature);
        Assert.Empty(upstream.ReferenceImages);
        Assert.Equal(["provider", "dispatch"], fixture.Log.Entries);
    }

    /// <summary>聊天模型继承渠道协议，显式覆盖优先；没有为模型增加新的文本协议白名单。</summary>
    [Theory]
    [InlineData(ProviderType.OpenAiChat, null, ProviderType.OpenAiChat)]
    [InlineData(ProviderType.Anthropic, null, ProviderType.Anthropic)]
    [InlineData(ProviderType.OpenAiChat, ProviderType.Gemini, ProviderType.Gemini)]
    [InlineData(ProviderType.Gemini, ProviderType.OpenAiResponses, ProviderType.OpenAiResponses)]
    public async Task ModelProtocolOverrideIsForwarded(ProviderType providerType, ProviderType? modelType, ProviderType expected)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = provider with { Type = providerType, Models = [provider.Models[0] with { Type = modelType }] };
        using var response = await fixture.PostAsync(fixture.Request() with { SystemPrompt = null });
        await AssertTextAsync(response, "  generated text\n");
        Assert.Equal(expected, fixture.Text.Protocol);
        Assert.Null(fixture.Text.Request!.SystemPrompt);
    }

    /// <summary>不受文本分派器支持的显式协议仍交由原分派器决定错误，不在 HTTP 或用例中提前改写。</summary>
    [Fact]
    public async Task UnsupportedProtocolFailureStillComesFromDispatcher()
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = provider with { Models = [provider.Models[0] with { Type = ProviderType.OpenAiImages }] };
        fixture.Text.Result = TextGenerationResult.Failure(null, "No text generation client is registered for protocol 'openai-images'.");
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertInvalidAsync(response, fixture.Text.Result.Error!);
        Assert.Equal(ProviderType.OpenAiImages, fixture.Text.Protocol);
        Assert.Equal(["provider", "dispatch"], fixture.Log.Entries);
    }

    /// <summary>成功要求 Ok 且文本非 null；空串合法，上游状态码不直接成为 HTTP 状态。</summary>
    [Theory]
    [InlineData(true, "", 200, null, null)]
    [InlineData(true, " \n", 500, "ignored error", null)]
    [InlineData(true, null, 200, null, "text generation failed")]
    [InlineData(false, "ignored text", 429, "upstream limit", "upstream limit")]
    [InlineData(false, null, 503, null, "text generation failed")]
    [InlineData(false, null, null, "", "")]
    public async Task UpstreamResultMappingIsStable(bool ok, string? text, int? status, string? error, string? expectedError)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        fixture.Text.Result = new TextGenerationResult(ok, text, status, error);
        using var response = await fixture.PostAsync(fixture.Request());
        if (expectedError is null) await AssertTextAsync(response, text!);
        else await AssertInvalidAsync(response, expectedError);
        Assert.Equal(["provider", "dispatch"], fixture.Log.Entries);
    }

    /// <summary>读取或分派异常原样传播；不会把异常当成缺失参考图、重试调用或保存部分结果。</summary>
    [Theory]
    [InlineData("provider")]
    [InlineData("asset")]
    [InlineData("open")]
    [InlineData("copy")]
    [InlineData("dispatch")]
    public async Task UnexpectedIoFailuresStopAtTheOriginalBoundary(string stage)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        var request = fixture.Request() with { Images = [fixture.AddReference()] };
        fixture.Log.FailureStage = stage;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.PostAsync(request));
        Assert.Same(fixture.Log.Failure, error);
        AssertStoppedAt(fixture, stage);
        Assert.All(fixture.Store.Streams, stream => Assert.True(stream.WasDisposed));
    }

    /// <summary>客户端取消传到正在执行的仓储、读流或上游端口，并在管线退出时释放已打开的流。</summary>
    [Theory]
    [InlineData("provider")]
    [InlineData("asset")]
    [InlineData("open")]
    [InlineData("copy")]
    [InlineData("dispatch")]
    public async Task RequestCancellationReachesTheActivePort(string stage)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Log.BlockedStage = stage;
        var pending = fixture.PostAsync(fixture.Request() with { Images = [fixture.AddReference()] }, cancellation.Token);

        // 等 I/O 确实开始后取消，随后等待服务端退出，避免只证明 HttpClient 提前返回。
        await fixture.Log.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await fixture.Log.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.RequestFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertStoppedAt(fixture, stage);
        Assert.All(fixture.Log.Tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.Single(fixture.Log.Tokens.Distinct());
        Assert.All(fixture.Store.Streams, stream => Assert.True(stream.WasDisposed));
    }

    /// <summary>MVC 继续处理缺少必填字段、无效 JSON 和字段类型错误，不进入业务端口。</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{\"providerId\":null,\"modelKey\":\"chat-model\",\"prompt\":\"hello\"}")]
    [InlineData("{\"providerId\":\"00000000-0000-0000-0000-000000000000\",\"prompt\":\"hello\"}")]
    [InlineData("{\"providerId\":\"00000000-0000-0000-0000-000000000000\",\"modelKey\":\"chat-model\",\"prompt\":null}")]
    [InlineData("{\"maxOutputTokens\":\"invalid\"}")]
    public async Task InvalidJsonNeverCallsBusinessPorts(string json)
    {
        await using var fixture = await TextGenerationHttpFixture.CreateAsync();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/api/v1/ai/text", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.NotEmpty(body.GetProperty("errors").EnumerateObject());
        Assert.Empty(fixture.Log.Entries);
    }

    /// <summary>失败或取消发生后，所有后续 I/O 都应停止，既有端口不允许重试。</summary>
    private static void AssertStoppedAt(TextGenerationHttpFixture fixture, string stage)
    {
        string[] stages = ["provider", "asset", "open", "copy", "dispatch"];
        Assert.Equal(stages.Take(Array.IndexOf(stages, stage) + 1), fixture.Log.Entries);
    }

    /// <summary>成功响应只包含既有 text 字段，且保留文本空白与媒体类型。</summary>
    private static async Task AssertTextAsync(HttpResponseMessage response, string text)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(["text"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal(text, body.GetProperty("text").GetString());
    }

    /// <summary>业务错误保持 400、invalid_request 及原有单项 details 文本。</summary>
    private static async Task AssertInvalidAsync(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(["details", "error"], body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal([error], body.GetProperty("details").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>锁定当前 MVC 标准错误体的稳定字段，追踪 ID 只要求存在而不固定值。</summary>
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(title, body.GetProperty("title").GetString());
        Assert.StartsWith("https://", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
