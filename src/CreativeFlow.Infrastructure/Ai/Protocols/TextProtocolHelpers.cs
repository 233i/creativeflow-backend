using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 文本客户端共享的请求和响应基础逻辑。
/// </summary>
/// <remarks>
/// 各协议只在 URL、认证请求头和响应结构上不同。集中处理其余部分可让失败处理保持一致——
/// 无论来自哪个供应商，主机不可达、超时和正文格式错误都应向用户呈现相同含义。
/// </remarks>
internal static class TextProtocolHelpers
{
    /// <summary>发送文本生成请求并按既有错误规则返回 JSON 响应。</summary>
    public static async Task<TextGenerationResult> SendAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string url,
        JsonObject body,
        Action<HttpRequestMessage> authorize,
        Func<JsonElement, string?> extractText,
        CancellationToken ct,
        Func<JsonElement, TextGenerationResult, TextGenerationResult>? captureMetadata = null,
        string clientName = "upstream")
    {
        var client = httpClientFactory.CreateClient(clientName);

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        authorize(message);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Text generation failed with {StatusCode} at {Url}", (int)response.StatusCode, url);

                return TextGenerationResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            using var document = JsonDocument.Parse(text);
            var generated = extractText(document.RootElement);

            // 状态码为 200 却没有可用内容，通常表示内容过滤或供应商未以错误状态表达的拒绝。
            var result = string.IsNullOrWhiteSpace(generated)
                ? TextGenerationResult.Failure(null, "Upstream returned no text.")
                : TextGenerationResult.Success(generated);
            return captureMetadata is null ? result : captureMetadata(document.RootElement, result with { ResponseStatusCode = (int)response.StatusCode });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TextGenerationResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return TextGenerationResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return TextGenerationResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }
}
