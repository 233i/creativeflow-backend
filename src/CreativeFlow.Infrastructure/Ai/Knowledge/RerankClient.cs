using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

/// <summary>单次 Cohere/Jina 兼容调用，受全局和渠道并发约束；不自动重试、不采用上游返回的正文。</summary>
internal sealed class RerankClient(IHttpClientFactory clients, RerankNetwork network) : IRerankClient
{
    /// <summary>保护全局和服务商在途计数；忙时立即拒绝，不隐藏排队。</summary>
    private readonly object gate = new();
    /// <summary>按服务商身份隔离的在途重排请求数。</summary>
    private readonly Dictionary<ProviderId, List<int>> active = [];
    /// <summary>当前进程全部在途重排请求数。</summary>
    private int total;

    /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
    public async Task<RerankResult> RankAsync(RerankRequest request, CancellationToken ct)
    {
        var error = RerankValidation.Settings(request.Settings) ?? RerankValidation.Inputs(request.Settings, request.Query, request.Documents);
        if (error is not null) return RerankResult.Failure("invalid_request", error);
        if (!request.Provider.Enabled || !request.Model.Enabled || request.Model.ProviderId != request.Provider.Id
            || request.Model.Category != ModelCategory.Rerank || request.Provider.ResolveProtocol(request.Model) != ProviderType.Rerank)
            return RerankResult.Failure("model_unavailable", "重排序模型或渠道不可用。");
        lock (gate)
        {
            var limits = active.GetValueOrDefault(request.Provider.Id);
            var limit = limits is { Count: > 0 } ? Math.Min(limits.Min(), request.Settings.Concurrency) : request.Settings.Concurrency;
            if (total >= 4 || (limits?.Count ?? 0) >= limit) return RerankResult.Failure("busy", "重排序并发已达上限，尚未发送请求。");
            limits ??= active[request.Provider.Id] = [];
            limits.Add(request.Settings.Concurrency); total++;
        }
        try { return await SendAsync(request, ct); }
        finally
        {
            lock (gate)
            {
                var limits = active[request.Provider.Id]; limits.Remove(request.Settings.Concurrency);
                if (limits.Count == 0) active.Remove(request.Provider.Id);
                total--;
            }
        }
    }

    /// <summary>按超时读取至多 1 MiB 响应，不跟随重定向或暴露上游错误正文。</summary>
    private async Task<RerankResult> SendAsync(RerankRequest input, CancellationToken ct)
    {
        if (!AiProvider.TryNormalizeBaseUrl(input.Provider.BaseUrl, ProviderType.Rerank, out var baseUrl, out _))
            return RerankResult.Failure("invalid_endpoint", "重排序 Base URL 无效。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(input.Settings.TimeoutSeconds));
        try
        {
            var endpoint = new Uri(baseUrl.TrimEnd('/') + "/" + input.Settings.EndpointPath);
            await network.Policy.ResolveAsync(endpoint, deadline.Token);
            using var client = clients.CreateClient("knowledge-rerank");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (input.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", input.ApiKey);
            request.Content = new StringContent(BuildPayload(input).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return RerankResult.Failure("upstream_error", $"重排序服务返回 HTTP {(int)response.StatusCode}；未自动重试。", (int)response.StatusCode);
            await response.Content.LoadIntoBufferAsync(1048576, deadline.Token);
            return Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token), input.Documents.Count, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return RerankResult.Failure("timeout_unknown", "重排序请求超时，受理结果未知；未自动重试。再次提交可能重复计费。"); }
        catch (InvalidOperationException) { return RerankResult.Failure("endpoint_not_allowed", "重排序端点未获许可，请配置 Rerank:AllowedEndpoints 完整地址。"); }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        { return RerankResult.Failure("response_too_large", "重排序响应超过 1 MiB；未采用返回内容。"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or UriFormatException)
        { return RerankResult.Failure("connection_unknown", "重排序服务连接或响应无效，受理状态待核对；未自动重试。"); }
    }

    /// <summary>构造 Cohere/Jina 兼容请求，保留问题及完整候选顺序和编号。</summary>
    internal static JsonObject BuildPayload(RerankRequest input) => new()
    {
        ["model"] = input.Model.ModelKey, ["query"] = input.Query,
        ["documents"] = new JsonArray(input.Documents.Select(text => (JsonNode)JsonValue.Create(text)!).ToArray()),
        ["top_n"] = input.Documents.Count,
    };

    /// <summary>只接受完整、唯一、范围正确的候选编号及有限相关性分数。</summary>
    internal static RerankResult Parse(byte[] body, int count, int status)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != count)
                return Invalid();
            var scores = new List<RerankScore>(); var seen = new HashSet<int>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("index", out var index) || !index.TryGetInt32(out var i)
                    || i < 0 || i >= count || !seen.Add(i) || !result.TryGetProperty("relevance_score", out var score)
                    || !score.TryGetDouble(out var s) || !double.IsFinite(s) || s is < 0 or > 1) return Invalid();
                scores.Add(new(i, s));
            }
            long? tokens = null;
            if (json.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var totalTokens))
            {
                if (!totalTokens.TryGetInt64(out var value) || value < 0) return Invalid();
                tokens = value;
            }
            return new(true, scores.OrderByDescending(item => item.Score).ThenBy(item => item.Index).ToArray(), null, null, status, tokens);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return Invalid(); }
        // 返回格式或协议无效的安全错误，不交付部分排名。
        RerankResult Invalid() => RerankResult.Failure("invalid_response", "重排序返回的候选编号、分数或数量无效；未采用上游正文。", status);
    }
}
