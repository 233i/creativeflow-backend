using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>
/// 通过 HTTP 获取供应商的模型目录。
/// </summary>
/// <remarks>
/// 使用 <see cref="JsonDocument"/> 读取响应，而不反序列化为 DTO。每个供应商返回的信封结构略有不同，
/// 也会自由新增字段；每种协议一个 DTO 必须注册到 <c>JsonSerializerContext</c>，
/// 且供应商一旦改变结构便会失效。只读取实际需要的两个字段既兼容 AOT，也更具韧性。
/// </remarks>
internal sealed partial class UpstreamModelCatalog(
    IHttpClientFactory httpClientFactory,
    ILogger<UpstreamModelCatalog> logger,
    EmbeddingEndpointPolicy? embeddingPolicy = null) : IUpstreamModelCatalog
{
    /// <summary>按渠道协议选择模型目录端点和鉴权方式，拉取并转换上游模型列表或返回原错误信息。</summary>
    public async Task<UpstreamCatalogResult> ListModelsAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken ct)
    {
        if (provider.Type == ProviderType.OpenAiEmbeddings)
            return await ListEmbeddingModelsAsync(provider, apiKey, ct);
        var protocol = provider.Type;
        var baseUrl = provider.BaseUrl.TrimEnd('/');

        var (url, request) = protocol switch
        {
            ProviderType.Anthropic => BuildAnthropic(baseUrl, apiKey),
            ProviderType.Gemini => BuildGemini(baseUrl, apiKey),
            _ => BuildOpenAiCompatible(baseUrl, apiKey),
        };

        var client = httpClientFactory.CreateClient("upstream");

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Model catalog request to {Url} failed with {StatusCode}", url, (int)response.StatusCode);

                return UpstreamCatalogResult.Failure((int)response.StatusCode, Summarize(body));
            }

            return UpstreamCatalogResult.Success(Parse(body, protocol));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpstreamCatalogResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            // 基础 URL 由用户提供，因此主机不可达是预期结果而非系统故障。
            // 应将其作为结果报告，而不是返回 500。
            return UpstreamCatalogResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return UpstreamCatalogResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }

    /// <summary>新向量渠道使用独立的精确端点策略读取目录，不改变原聊天/媒体目录行为。</summary>
    private async Task<UpstreamCatalogResult> ListEmbeddingModelsAsync(AiProvider provider, string apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (embeddingPolicy is null || !AiProvider.TryNormalizeBaseUrl(provider.BaseUrl, provider.Type, out var baseUrl, out _))
                return UpstreamCatalogResult.Failure(null, "Embedding endpoint policy or Base URL is not configured.");
            var (url, request) = BuildOpenAiCompatible(baseUrl, apiKey);
            using var message = request;
            await embeddingPolicy.ResolveAsync(new Uri(url), timeout.Token);
            using var client = httpClientFactory.CreateClient("embedding");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return UpstreamCatalogResult.Failure((int)response.StatusCode, "Embedding model catalog request failed; check the configured local service.");
            // 目录检查不触发推理；先限制缓冲再读取，避免无界模型列表响应。
            await response.Content.LoadIntoBufferAsync(524288, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return UpstreamCatalogResult.Success(Parse(body, provider.Type)
                .Where(model => model.Category == ModelCategory.Embedding).ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpstreamCatalogResult.Failure(null, "Embedding model catalog timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or System.Net.Sockets.SocketException)
        {
            return UpstreamCatalogResult.Failure(null, "Cannot read embedding models. Check Base URL, server allowlist and local service availability.");
        }
    }

    /// <summary>构造 OpenAI 兼容渠道的模型目录请求。</summary>
    private static (string Url, HttpRequestMessage Request) BuildOpenAiCompatible(
        string baseUrl,
        string apiKey)
    {
        var url = $"{baseUrl}/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return (url, request);
    }

    /// <summary>构造 Anthropic 模型目录请求及必要协议头。</summary>
    private static (string Url, HttpRequestMessage Request) BuildAnthropic(
        string baseUrl,
        string apiKey)
    {
        // BaseUrl 规范化时会移除末尾 /v1，因此在此重新添加。
        var url = $"{baseUrl}/v1/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        return (url, request);
    }

    /// <summary>构造 Gemini 模型目录请求。</summary>
    private static (string Url, HttpRequestMessage Request) BuildGemini(string baseUrl, string apiKey)
    {
        var url = $"{baseUrl}/v1beta/models";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

        return (url, request);
    }

    /// <summary>按当前协议解析 JSON 响应并应用既有回退规则。</summary>
    private static IReadOnlyList<UpstreamModel> Parse(string body, ProviderType protocol)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        // 兼容 OpenAI 的接口和 Anthropic 都嵌套在 "data" 下；Gemini 使用 "models"。
        var arrayProperty = protocol == ProviderType.Gemini ? "models" : "data";

        if (!root.TryGetProperty(arrayProperty, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<UpstreamModel>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(element, "id") ?? ReadString(element, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // Gemini 返回完全限定的资源名称（"models/gemini-2.5-flash"）。
            var modelKey = id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id;

            var displayName = ReadString(element, "display_name")
                ?? ReadString(element, "displayName")
                ?? modelKey;

            models.Add(new UpstreamModel(modelKey, displayName, InferCategory(modelKey)));
        }

        return models;
    }

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 根据模型标识符推测其用途。
    /// </summary>
    /// <remarks>
    /// 大多数目录端点完全不报告分类，而画布需要分类来决定哪些节点可以选择模型。
    /// 错误推测可以在设置中修正；拒绝导入所有未识别模型更糟，因此回退为 <c>chat</c>。
    /// </remarks>
    private static ModelCategory InferCategory(string modelKey)
    {
        if (RerankPattern().IsMatch(modelKey)) return ModelCategory.Rerank;
        if (VideoPattern().IsMatch(modelKey)) return ModelCategory.Video;
        if (ImagePattern().IsMatch(modelKey)) return ModelCategory.Image;
        if (EmbeddingPattern().IsMatch(modelKey)) return ModelCategory.Embedding;
        if (SpeechPattern().IsMatch(modelKey)) return ModelCategory.Speech;

        return ModelCategory.Chat;
    }

    /// <summary>截断上游错误正文，防止冗长 HTML 页面淹没界面或日志。</summary>
    private static string Summarize(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) return "Upstream returned an empty error body.";

        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }

    // 使用源生成：已编译正则可避开解释器，而解释器存在裁剪风险。
    /// <summary>生成用于识别视频模型名称的正则表达式。</summary>
    [GeneratedRegex(@"(sora|veo|seedance|kling|runway|video|wan2)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPattern();

    /// <summary>生成用于识别图片模型名称的正则表达式。</summary>
    [GeneratedRegex(@"(dall-e|gpt-image|imagen|seedream|flux|stable-diffusion|sdxl|kolors|image)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    /// <summary>生成用于识别向量模型名称的正则表达式。</summary>
    [GeneratedRegex(@"(embedding|embed|bge-|gte-|text-embedding)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddingPattern();

    /// <summary>先于 bge 等向量名称规则识别显式重排序模型。</summary>
    [GeneratedRegex(@"(rerank|reranker)", RegexOptions.IgnoreCase)]
    private static partial Regex RerankPattern();

    /// <summary>生成用于识别语音模型名称的正则表达式。</summary>
    [GeneratedRegex(@"(whisper|tts|speech|audio|voice)", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechPattern();
}
