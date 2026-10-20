using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Ai;

public sealed class SignedMediaUrlServiceTests
{
    /// <summary>当前测试独立使用的合成密钥，不用于真实凭据。</summary>
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";
    /// <summary>本次操作使用的时间基准。</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);

    /// <summary>回归场景：Fresh Vendor Url Is Preferred With Signed Fallback。</summary>
    [Fact]
    public void FreshVendorUrlIsPreferredWithSignedFallback()
    {
        var service = CreateService(out _);
        var asset = Asset(
            sourceUrl: "https://vendor.example/generated.png?token=short-lived",
            sourceExpiresAt: Now.AddMinutes(5));

        var urls = service.CreateInputUrls(asset);

        Assert.Equal(asset.SourceUrl, urls.PrimaryUrl);
        Assert.StartsWith(
            "https://creative.example/api/v1/media/assets/", urls.FallbackUrl);
    }

    /// <summary>回归场景：Stored Asset Uses Signed Url When Vendor Url Expired。</summary>
    [Fact]
    public void StoredAssetUsesSignedUrlWhenVendorUrlExpired()
    {
        var service = CreateService(out _);
        var asset = Asset(
            sourceUrl: "https://vendor.example/generated.png",
            sourceExpiresAt: Now.AddSeconds(30));

        var urls = service.CreateInputUrls(asset);

        Assert.StartsWith(
            "https://creative.example/api/v1/media/assets/", urls.PrimaryUrl);
        Assert.Null(urls.FallbackUrl);
    }

    /// <summary>回归场景：Signed Tuple Validates Until Expiry And Cannot Be Retargeted。</summary>
    [Fact]
    public void SignedTupleValidatesUntilExpiryAndCannotBeRetargeted()
    {
        var service = CreateService(out var clock);
        var asset = Asset();
        var uri = new Uri(service.CreateSignedUrl(asset));
        var query = ParseQuery(uri.Query);
        var expires = long.Parse(query["exp"]);
        var signature = query["sig"];

        Assert.True(service.Validate(asset.AssetId, asset.DeviceId, expires, signature));
        Assert.False(service.Validate(Guid.NewGuid(), asset.DeviceId, expires, signature));
        Assert.False(service.Validate(
            asset.AssetId, new DeviceId(Guid.NewGuid()), expires, signature));
        Assert.False(service.Validate(
            asset.AssetId,
            asset.DeviceId,
            expires,
            signature[..^1] + (signature[^1] == 'a' ? "b" : "a")));

        clock.UtcNow = Now.AddMinutes(61);
        Assert.False(service.Validate(asset.AssetId, asset.DeviceId, expires, signature));
    }

    /// <summary>回归场景：Loopback Origin Does Not Get Passed To An Upstream Provider。</summary>
    [Fact]
    public void LoopbackOriginDoesNotGetPassedToAnUpstreamProvider()
    {
        var clock = new ClockStub { UtcNow = Now };
        var service = new SignedMediaUrlService(
            new AiOptions
            {
                EncryptionKey = TestKey,
                SignedMediaUrlLifetimeMinutes = 60,
            },
            new AuthenticationOptions { PublicOrigin = "http://localhost:5298" },
            clock);

        Assert.Equal(new(null, null), service.CreateInputUrls(Asset()));
    }

    /// <summary>用测试配置、时钟与替身依赖组装被测服务。</summary>
    private static SignedMediaUrlService CreateService(out ClockStub clock)
    {
        clock = new ClockStub { UtcNow = Now };
        return new SignedMediaUrlService(
            new AiOptions
            {
                EncryptionKey = TestKey,
                SignedMediaUrlLifetimeMinutes = 60,
            },
            new AuthenticationOptions { PublicOrigin = "https://creative.example" },
            clock);
    }

    /// <summary>构造签名地址测试使用的素材及其设备归属。</summary>
    private static CanvasAsset Asset(
        string? sourceUrl = null,
        DateTimeOffset? sourceExpiresAt = null) => new(
        Guid.Parse("0198d68d-f380-7000-8000-000000000001"),
        new DeviceId(Guid.Parse("0198d68d-f380-7000-8000-000000000002")),
        "image",
        "image/png",
        "cloud/test",
        3,
        null,
        Now,
        SourceUrl: sourceUrl,
        SourceUrlExpiresAt: sourceExpiresAt);

    /// <summary>回归场景：Parse Query。</summary>
    private static Dictionary<string, string> ParseQuery(string query) => query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(
            pair => Uri.UnescapeDataString(pair[0]),
            pair => Uri.UnescapeDataString(pair[1]),
            StringComparer.Ordinal);

    private sealed class ClockStub : IClock
    {
        /// <summary>提供可由测试推进的 UTC 时间，用于验证素材签名的到期边界。</summary>
        public DateTimeOffset UtcNow { get; set; }
    }
}
