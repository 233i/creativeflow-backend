using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 签发短期公开 URL，供上游模型获取已存储媒体。
/// </summary>
/// <remarks>
/// 画布素材路由需要浏览器 Cookie，因此不能直接传给模型供应商。签名路由不携带会话状态：
/// 在令牌过期前，持有其限定范围的 HMAC 令牌是读取该素材的唯一凭证。
/// </remarks>
public interface ISignedMediaUrlService
{
    /// <summary>
    /// 若存在仍有效的供应商 URL，则优先选择它，并保留 CreativeFlow 签名 URL 作为回退。
    /// 对公开源为回环地址的纯本地部署，不返回 URL。
    /// </summary>
    MediaInputUrls CreateInputUrls(CanvasAsset asset);

    /// <summary>无论公开源是否可达，都创建 CreativeFlow 签名 URL。</summary>
    string CreateSignedUrl(CanvasAsset asset);

    /// <summary>验证完整的签名 URL 元组。</summary>
    bool Validate(Guid assetId, DeviceId ownerDeviceId, long expiresAtUnixSeconds, string signature);
}

/// <param name="PrimaryUrl">优先交付给上游读取的素材地址。</param>
/// <param name="FallbackUrl">首选来源不可用时按既有规则尝试的备用地址。</param>
public sealed record MediaInputUrls(string? PrimaryUrl, string? FallbackUrl);
