using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Embeddings;

/// <summary>通过 OpenAI 兼容 embeddings 接口向量化，支持 LM Studio；不使用聊天端点或自动重试。</summary>
internal sealed partial class OpenAiEmbeddingClient(
    IHttpClientFactory clients, EmbeddingEndpointPolicy endpointPolicy) : IEmbeddingClient
{
    /// <summary>此客户端仅处理向量请求。</summary>
    public ProviderType Protocol => ProviderType.OpenAiEmbeddings;
    /// <summary>限制整个响应体为 16 MiB，避免畸形向量响应耗尽进程内存。</summary>
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>校验完整端点，发送单次请求，在总超时内有界读取并严格解析全部向量。</summary>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        var error = EmbeddingValidation.ValidateSettings(request.Settings)
            ?? EmbeddingValidation.ValidateInputs(request.Settings, request.Inputs, request.Purpose);
        if (error is not null) return EmbeddingResult.Failure("invalid_request", error);
        if (!AiProvider.TryNormalizeBaseUrl(request.Provider.BaseUrl, Protocol, out var baseUrl, out _)
            || !Uri.TryCreate($"{baseUrl}/{request.Settings.EndpointPath}", UriKind.Absolute, out var uri))
            return EmbeddingResult.Failure("invalid_endpoint", "Configure an absolute Base URL without credentials, query or fragment.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Settings.TimeoutSeconds));
        try
        {
            // 发送前与建立连接时各校验一次；后者直接连接校验所得 IP，不把鉴权交给重定向或系统代理。
            endpointPolicy.ValidateEndpoint(uri);
            await endpointPolicy.ResolveAsync(uri, timeout.Token);
            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            message.Content = new ByteArrayContent(BuildPayload(request));
            message.Content.Headers.ContentType = new("application/json");
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            using var client = clients.CreateClient("embedding");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var hint = await ReadUpstreamErrorHintAsync(response, timeout.Token);
                var suffix = hint is null ? "" : $" ({hint})";
                return EmbeddingResult.Failure("upstream_error",
                    $"Embedding service returned HTTP {(int)response.StatusCode}{suffix}. Check its local logs; no automatic retry was made.",
                    (int)response.StatusCode);
            }
            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            return OpenAiEmbeddingResponse.Parse(body, request, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return EmbeddingResult.Failure("timeout_unknown", "Embedding request timed out; its upstream outcome may be unknown. It was not retried.");
        }
        catch (InvalidOperationException)
        {
            return EmbeddingResult.Failure("endpoint_not_allowed", "Embedding endpoint is not allowed. Check Embedding:AllowedEndpoints and the Development-only loopback policy.");
        }
        catch (InvalidDataException)
        {
            return EmbeddingResult.Failure("response_too_large", "Embedding response exceeded the 16 MiB limit.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            return EmbeddingResult.Failure("connection_failed", "The configured embedding service could not be read. Check the local service; no retry or cloud fallback was made.");
        }
    }

    /// <summary>仅写入已确认的向量参数；前缀逐条添加，dimensions 只在显式配置时发送。</summary>
    internal static byte[] BuildPayload(EmbeddingRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model.ModelKey);
            writer.WriteString("encoding_format", "float");
            if (request.Settings.RequestedDimensions is { } dimensions) writer.WriteNumber("dimensions", dimensions);
            writer.WriteStartArray("input");
            var prefix = request.Purpose == "query" ? request.Settings.QueryPrefix : request.Settings.DocumentPrefix;
            foreach (var input in request.Inputs) writer.WriteStringValue(prefix + input.Text);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>同时校验声明和实际读取长度，兼容没有 Content-Length 的分块响应。</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException();
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > MaxResponseBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>
    /// 从上游错误 JSON 中提取有限的机器字段，帮助定位参数错误，同时不把原文、提示词或密钥带回页面。
    /// </summary>
    private static async Task<string?> ReadUpstreamErrorHintAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await ReadBoundedAsync(response.Content, ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested)
                && nested.ValueKind == JsonValueKind.Object ? nested : root;
            var code = SafeErrorToken(error, "code");
            var parameter = SafeErrorToken(error, "param");
            var limit = SafeBatchLimitHint(error);
            if (code is null && parameter is null && limit is null) return null;
            return string.Join("; ", new[]
            {
                code is null ? null : $"code: {code}",
                parameter is null ? null : $"parameter: {parameter}",
                limit,
            }.Where(value => value is not null));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            // 错误响应只能改善诊断，读取/解析失败不能覆盖原始 HTTP 状态，也不能触发第二次请求。
            return null;
        }
    }

    /// <summary>仅允许短 ASCII 标识，拒绝可能包含用户输入、密钥或 HTML 的任意文本。</summary>
    private static string? SafeErrorToken(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var token = value.GetString();
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;
        foreach (var character in token)
        {
            if (!(character is >= 'a' and <= 'z') && !(character is >= 'A' and <= 'Z')
                && !(character is >= '0' and <= '9') && character is not ('_' or '-' or '.' or '/'))
                return null;
        }
        return token;
    }

    /// <summary>仅从固定英文错误句式提取数字上限，避免把供应商消息中的任意文本回显给用户。</summary>
    private static string? SafeBatchLimitHint(JsonElement element)
    {
        if (!element.TryGetProperty("message", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var message = value.GetString();
        if (message is null) return null;
        var match = BatchLimitPattern().Match(message);
        return match.Success ? $"max inputs: {match.Groups["max"].Value}; received: {match.Groups["got"].Value}" : null;
    }

    /// <summary>匹配豆包/OpenAI 兼容服务的批量上限错误，不捕获提示词或请求 ID。</summary>
    [GeneratedRegex(@"\bmax\s+(?<max>[0-9]{1,4}),\s+got\s+(?<got>[0-9]{1,4})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BatchLimitPattern();
}
