using System.Security.Cryptography;
using System.Text;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Security;

/// <summary>使用 HMAC 保护且会过期的单个已存储图片或视频访问方式。</summary>
internal sealed class SignedMediaUrlService : ISignedMediaUrlService
{
    /// <summary>签名输入的固定用途上下文，避免不同用途共享摘要语义。</summary>
    private const string SignatureContext = "creativeflow-signed-media-url-v1";
    /// <summary>判定上游素材地址可复用时预留的失效时间余量。</summary>
    private static readonly TimeSpan SourceSafetyMargin = TimeSpan.FromMinutes(1);

    /// <summary>用于构造对外媒体地址的应用公开 origin。</summary>
    private readonly Uri _publicOrigin;
    /// <summary>公开 origin 是否满足既有外部媒体读取条件。</summary>
    private readonly bool _originIsExternallyReachable;
    /// <summary>签名媒体地址的既有有效时长。</summary>
    private readonly TimeSpan _lifetime;
    /// <summary>用于媒体读取 URL 签名的服务端密钥。</summary>
    private readonly byte[] _signingKey;
    /// <summary>用于期限判定的可替换 UTC 时间源。</summary>
    private readonly IClock _clock;

    /// <summary>初始化 SignedMediaUrlService 的依赖与实例状态。</summary>
    public SignedMediaUrlService(
        AiOptions aiOptions,
        AuthenticationOptions authenticationOptions,
        IClock clock)
    {
        // 签名密钥、公开 origin 与有效期在启动时确定，避免每次签名使用不一致的配置。
        _clock = clock;
        _lifetime = TimeSpan.FromMinutes(aiOptions.SignedMediaUrlLifetimeMinutes);

        if (!Uri.TryCreate(
                authenticationOptions.PublicOrigin.TrimEnd('/'), UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new InvalidOperationException(
                $"{AuthenticationOptions.SectionName}:PublicOrigin must be an absolute HTTP(S) origin.");
        }

        _publicOrigin = origin;
        _originIsExternallyReachable = !origin.IsLoopback;

        byte[] encryptionKey;
        try
        {
            encryptionKey = Convert.FromBase64String(aiOptions.EncryptionKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must be base64-encoded.", exception);
        }

        if (encryptionKey.Length != 32)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must decode to 32 bytes for signed media URLs.");
        }

        _signingKey = HMACSHA256.HashData(
            encryptionKey, Encoding.UTF8.GetBytes(SignatureContext));
        CryptographicOperations.ZeroMemory(encryptionKey);
    }

    /// <summary>优先复用合适的上游素材地址，并在公开来源可达时提供签名素材地址作为主地址或回退。</summary>
    public MediaInputUrls CreateInputUrls(CanvasAsset asset)
    {
        var sourceUrl = IsReusableSource(asset) ? asset.SourceUrl : null;
        var signedUrl = _originIsExternallyReachable ? CreateSignedUrl(asset) : null;

        return sourceUrl is not null
            ? new MediaInputUrls(sourceUrl, signedUrl)
            : new MediaInputUrls(signedUrl, null);
    }

    /// <summary>为素材标识、归属设备和到期时间生成签名下载地址，不在 URL 中包含服务商密钥。</summary>
    public string CreateSignedUrl(CanvasAsset asset)
    {
        var expires = _clock.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        var signature = Sign(asset.AssetId, asset.DeviceId, expires);
        return $"{_publicOrigin.AbsoluteUri.TrimEnd('/')}/api/v1/media/assets/{asset.AssetId:N}" +
               $"?owner={asset.DeviceId.Value:N}&exp={expires}&sig={signature}";
    }

    /// <summary>验证签名格式、到期时间及素材归属对应的签名，拒绝过期或不匹配的下载请求。</summary>
    public bool Validate(
        Guid assetId,
        DeviceId ownerDeviceId,
        long expiresAtUnixSeconds,
        string signature)
    {
        if (string.IsNullOrEmpty(signature) || signature.Length != 43)
        {
            return false;
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var maximum = _clock.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        if (expiresAtUnixSeconds <= now || expiresAtUnixSeconds > maximum)
        {
            return false;
        }

        var expected = Sign(assetId, ownerDeviceId, expiresAtUnixSeconds);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>判断保存的上游来源是否仍满足既有复用条件。</summary>
    private bool IsReusableSource(CanvasAsset asset) =>
        asset.SourceUrlExpiresAt > _clock.UtcNow.Add(SourceSafetyMargin)
        && IsExternalHttpUrl(asset.SourceUrl);

    /// <summary>判断来源是否为允许复用的外部 HTTP(S) 地址。</summary>
    private static bool IsExternalHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !uri.IsLoopback;

    /// <summary>按既有上下文、资源和期限计算媒体 URL 签名。</summary>
    private string Sign(Guid assetId, DeviceId ownerDeviceId, long expiresAtUnixSeconds)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{SignatureContext}\n{assetId:N}\n{ownerDeviceId.Value:N}\n{expiresAtUnixSeconds}");
        var digest = HMACSHA256.HashData(_signingKey, payload);
        return Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
