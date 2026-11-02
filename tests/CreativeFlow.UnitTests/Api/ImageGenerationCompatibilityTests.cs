using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>在移动图片业务之前锁定真实 HTTP 行为以及上游/存储副作用。</summary>
public sealed class ImageGenerationCompatibilityTests
{
    /// <summary>未认证请求必须在查找渠道、生成或写入素材之前被拒绝。</summary>
    [Fact]
    public async Task UnauthenticatedRequestsHaveNoBusinessSideEffects()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(authenticated: false);
        using var response = await fixture.PostAsync(fixture.Request());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Providers.Finds);
        Assert.Equal(0, fixture.Images.Calls);
        Assert.Empty(fixture.Assets.Inserted);
    }

    /// <summary>保持数量的默认/夹取规则、管理员图片格式、原始参数及生成素材来源信息。</summary>
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(10, 4)]
    public async Task SuccessPreservesRequestPolicyAndAssetMetadata(int? count, int expected)
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with { Count = count });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var output = Assert.Single(body.GetProperty("assets").EnumerateArray());
        var stored = Assert.Single(fixture.Assets.Inserted);

        // 返回 URL、元数据归属与保存的字节必须指向同一个素材；格式策略来自服务端。
        Assert.Equal(stored.AssetId.ToString(), output.GetProperty("assetId").GetString());
        Assert.Equal($"/api/v1/canvas/assets/{stored.AssetId}", output.GetProperty("url").GetString());
        Assert.Equal("image/webp", output.GetProperty("mediaType").GetString());
        Assert.Equal(fixture.DeviceId, stored.DeviceId);
        Assert.Equal(fixture.Images.Result.Images[0].SourceUrl, stored.SourceUrl);
        Assert.Equal(fixture.Images.Result.Images[0].SourceUrlExpiresAt, stored.SourceUrlExpiresAt);
        Assert.Equal([4, 5, 6], fixture.Store.Available[stored.StoragePath]);
        Assert.Equal([".webp"], fixture.Store.Extensions);
        Assert.Equal(1, fixture.Images.Calls);
        Assert.Equal(ProviderType.OpenAiImages, fixture.Images.Protocol);
        Assert.Equal(expected, fixture.Images.Request!.Count);
        Assert.Equal("base64", fixture.Images.Request.ResponseFormat);
        Assert.Equal("webp", fixture.Images.Request.OutputFormat);
        Assert.Equal("1024x1024", fixture.Images.Request.Size);
        Assert.Equal("high", fixture.Images.Request.Quality);
        Assert.Equal(42, fixture.Images.Request.Seed);
    }

    /// <summary>不合法的 provider id 保持原错误体，且不会访问仓储。</summary>
    [Fact]
    public async Task InvalidProviderIdIsRejectedBeforeLookup()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with { ProviderId = "invalid" });
        await AssertInvalidAsync(response, "malformed provider id");
        Assert.Equal(0, fixture.Providers.Finds);
    }

    /// <summary>渠道不存在仍返回空的 404，不泄漏内部错误格式。</summary>
    [Fact]
    public async Task MissingProviderReturnsNotFound()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var request = fixture.Request();
        fixture.Providers.Provider = null;
        using var response = await fixture.PostAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fixture.Images.Calls);
    }

    /// <summary>缺少提示词与参考图的校验继续发生在渠道查询之前。</summary>
    [Fact]
    public async Task MissingPromptAndReferencesIsRejectedBeforeLookup()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with { Prompt = " " });
        await AssertInvalidAsync(response, "a prompt or at least one reference image is required");
        Assert.Equal(0, fixture.Providers.Finds);
    }

    /// <summary>保持禁用渠道、未知/禁用模型和非图片模型的错误顺序及文本。</summary>
    [Theory]
    [InlineData("provider-disabled", "provider 'Test provider' is disabled")]
    [InlineData("model-missing", "model 'image-model' is not configured on this provider")]
    [InlineData("model-disabled", "model 'image-model' is disabled")]
    [InlineData("model-category", "model 'image-model' is not an image model")]
    public async Task UnavailableModelsNeverCallUpstream(string scenario, string error)
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = scenario switch
        {
            "provider-disabled" => provider with { Enabled = false },
            "model-missing" => provider with { Models = [] },
            "model-disabled" => provider with { Models = [provider.Models[0] with { Enabled = false }] },
            _ => provider with { Models = [provider.Models[0] with { Category = ModelCategory.Chat }] },
        };
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertInvalidAsync(response, error);
        Assert.Equal(0, fixture.Images.Calls);
    }

    /// <summary>不可用的普通参考图继续被跳过，带查询字符串的自有素材仍能读取。</summary>
    [Fact]
    public async Task OrdinaryReferencesRetainTheirPermissiveFailurePolicy()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var reference = fixture.AddReference();
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Images = ["https://untrusted.invalid/image.png", "/api/v1/canvas/assets/missing", reference + "?v=1"],
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3], Assert.Single(fixture.Images.Request!.ReferenceImages).Bytes);
    }

    /// <summary>输入只有不可解析的普通参考图时，仍保持旧版允许上游处理空提示词的行为。</summary>
    [Fact]
    public async Task SkippingAllOrdinaryReferencesDoesNotIntroduceNewValidation()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Prompt = "", Images = ["https://untrusted.invalid/image.png"],
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(fixture.Images.Request!.ReferenceImages);
    }

    /// <summary>蒙版错误不能套用普通参考图的跳过策略，否则局部重绘会变成整图生成。</summary>
    [Theory]
    [InlineData("missing", "the inpaint mask is not an available canvas asset")]
    [InlineData("jpeg", "the inpaint mask must be a PNG image")]
    [InlineData("no-reference", "an inpaint mask requires at least one reference image")]
    [InlineData("protocol", "the selected image protocol does not support inpaint masks")]
    public async Task InvalidMasksNeverGenerateUnmaskedImages(string scenario, string error)
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var reference = fixture.AddReference();
        var mask = scenario == "missing" ? "/api/v1/canvas/assets/missing"
            : fixture.AddReference(scenario == "jpeg" ? "image/jpeg" : "image/png");
        if (scenario == "protocol")
        {
            fixture.Providers.Provider = fixture.Providers.Provider! with { Type = ProviderType.Gemini };
        }
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Images = scenario == "no-reference" ? null : [reference], MaskUrl = mask,
        });
        await AssertInvalidAsync(response, error);
        Assert.Equal(0, fixture.Images.Calls);
    }

    /// <summary>有效 PNG 蒙版与参考图分别传给上游，模型协议覆盖保持最高优先级。</summary>
    [Fact]
    public async Task ValidMasksUseTheModelsExplicitProtocol()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = provider with
        {
            Type = ProviderType.Gemini,
            Models = [provider.Models[0] with { Type = ProviderType.OpenAiImages }],
        };
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Images = [fixture.AddReference()], MaskUrl = fixture.AddReference(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ProviderType.OpenAiImages, fixture.Images.Protocol);
        Assert.Equal("image/png", fixture.Images.Request!.Mask!.MediaType);
        Assert.Single(fixture.Images.Request.ReferenceImages);
    }

    /// <summary>超过参考图数量限制时不调用上游。</summary>
    [Fact]
    public async Task TooManyReferencesKeepTheExistingLimit()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.PostAsync(fixture.Request() with
        {
            Images = Enumerable.Repeat("unavailable", 9).ToArray(),
        });
        await AssertInvalidAsync(response, "at most 8 reference images are allowed");
        Assert.Equal(0, fixture.Images.Calls);
    }

    /// <summary>密钥损坏继续提示重新输入，不自动重试生成。</summary>
    [Fact]
    public async Task CorruptKeysKeepTheirUserFacingError()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        fixture.Providers.Provider = fixture.Providers.Provider! with { ApiKey = new([], [], [], "test") };
        fixture.Protector.Fail = true;
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertInvalidAsync(response, "The stored API key could not be decrypted. Re-enter it in settings.");
        Assert.Equal(0, fixture.Images.Calls);
    }

    /// <summary>上游失败继续映射到旧版 400 错误体，不透传供应商状态码或保存素材。</summary>
    [Fact]
    public async Task UpstreamFailuresKeepTheHttpMapping()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        fixture.Images.Result = ImageGenerationResult.Failure(502, "upstream unavailable");
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertInvalidAsync(response, "upstream unavailable");
        Assert.Equal(1, fixture.Images.Calls);
        Assert.Empty(fixture.Store.Extensions);
        Assert.Empty(fixture.Assets.Inserted);
    }

    /// <summary>Seedream 参考素材仍按当前账号读取，丢失或越权图片不能静默变为文生图。</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task SeedreamPreservesReferencesAndRejectsUnavailableInputs(bool available)
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        var provider = fixture.Providers.Provider!;
        fixture.Providers.Provider = provider with { Models = [provider.Models[0] with { Type = ProviderType.SeedreamImages }] };
        var reference = available ? fixture.AddReference() : $"/api/v1/canvas/assets/{Guid.NewGuid()}";
        using var response = await fixture.PostAsync(fixture.Request() with { Images = [reference], OutputFormat = "png" });
        Assert.Equal(available ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(available ? 1 : 0, fixture.Images.Calls);
        if (available)
        {
            Assert.Equal(ProviderType.SeedreamImages, fixture.Images.Protocol);
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(fixture.Images.Request!.ReferenceImages).Bytes);
        }
    }

    /// <summary>空上游错误正文仍在公开错误文本中保留状态码，不改变原 HTTP 映射或写素材。</summary>
    [Fact]
    public async Task EmptyUpstreamErrorStillExplainsThe404()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        fixture.Images.Result = ImageGenerationResult.Failure(404, "");
        using var response = await fixture.PostAsync(fixture.Request());
        await AssertInvalidAsync(response, "Image API returned HTTP 404 with an empty error body.");
        Assert.Equal(1, fixture.Images.Calls); Assert.Empty(fixture.Assets.Inserted);
    }

    /// <summary>无效 JSON 仍由 MVC 在业务入口之前返回绑定错误。</summary>
    [Fact]
    public async Task MalformedJsonStillUsesMvcBinding()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        using var response = await fixture.Client.PostAsync("/api/v1/ai/images",
            new StringContent("{bad", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, fixture.Providers.Finds);
    }

    /// <summary>保存顺序仍为先字节后元数据；本次结构迁移不夹带事务或补偿语义变化。</summary>
    [Fact]
    public async Task MetadataFailureDoesNotResubmitOrChangeStorageOrder()
    {
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync();
        fixture.Assets.FailInsert = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PostAsync(fixture.Request()));
        Assert.Equal(1, fixture.Images.Calls);
        Assert.Single(fixture.Store.Extensions);
        Assert.Empty(fixture.Assets.Inserted);
    }

    /// <summary>精确核对现有业务错误结构，防止改为另一种全局错误格式。</summary>
    private static async Task AssertInvalidAsync(HttpResponseMessage response, string detail)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(detail, Assert.Single(body.GetProperty("details").EnumerateArray()).GetString());
    }
}
