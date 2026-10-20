using System.Net.Http.Headers;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>只下载已有产物；每一跳单独校验来源，不把服务商密钥转发给 CDN。</summary>
public sealed class ImageOutputDownloader(IHttpClientFactory clients, IAssetStore store) : IImageOutputDownloader
{
    /// <summary>单张下载图片的最大字节数，避免异常响应耗尽内存。</summary>
    private const int MaxBytes = 25 * 1024 * 1024;

    /// <summary>仅协议、主机及有效端口全部相同才视为可携带密钥的同源。</summary>
    internal static bool SameOrigin(Uri target, string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var provider)
        && target.Scheme == provider.Scheme && target.Port == provider.Port
        && string.Equals(target.Host, provider.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>下载上游图片并逐跳检查 HTTP(S) 重定向，仅在目标与服务商同源时携带 API Key。</summary>
    public async Task<StoredAsset> DownloadAsync(string url, string providerBaseUrl, string apiKey,
        string expectedMediaType, CancellationToken ct, string? operationKey = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
            throw new InvalidOperationException("上游返回了无效的图片地址，无法重新下载。");
        using var client = clients.CreateClient("image-output");
        // 该客户端必须禁用自动重定向；否则无法逐跳控制 Authorization。
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0)
                throw new InvalidOperationException("上游图片地址必须为 HTTP(S) 地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (SameOrigin(target, providerBaseUrl) && !string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } next)
                    throw new InvalidOperationException("上游图片下载重定向缺少地址。");
                target = new Uri(target, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"图片已生成，但下载失败（HTTP {(int)response.StatusCode}）。可以重新下载，无需重新生成。");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException("上游图片超过 25 MiB 下载限制。");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is "text/html" or "application/json")
                throw new InvalidOperationException("上游返回的不是图片文件，可以检查服务商后重新下载。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await store.SaveAsync(stream, type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                ? type : expectedMediaType, ".img", MaxBytes, ct, operationKey);
        }
        throw new InvalidOperationException("上游图片下载重定向次数过多。");
    }
}
