namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>只读取已经生成的文件，绝不能调用图片生成端点。</summary>
public interface IImageOutputDownloader
{
    /// <summary>读取产物，每次重定向均按同源规则决定是否携带密钥。</summary>
    Task<StoredAsset> DownloadAsync(string url, string providerBaseUrl, string apiKey,
        string expectedMediaType, CancellationToken ct, string? operationKey = null);
}
