using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Infrastructure.Ai.HttpRequests;

/// <summary>通用 HTTP 节点的一次性传输，限制目标、时间和响应大小，密钥不进入记录。</summary>
internal sealed class WorkflowHttpTransport(IHttpClientFactory clients, WorkflowHttpNetwork network, IApiKeyProtector protector) : IWorkflowHttpTransport
{
    /// <summary>正文保持可读 UTF-8 JSON，外层 API 序列化和前端文本渲染负责上下文转义。</summary>
    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>验证目标及全部 DNS 地址，不发送业务请求。</summary>
    public async Task ValidateEndpointAsync(string endpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw Error("HTTP_ENDPOINT_DENIED", "HTTP 地址无效。");
        await network.ResolveAsync(uri, ct);
    }

    /// <summary>展开变量并发送一次请求，支持 JSON、文本与表单，输出前校验和脱敏。</summary>
    public async Task<JsonObject> SendAsync(JsonObject data, JsonObject inputs, HttpCredentialRevision? credential, CancellationToken ct)
    {
        // 先校验与绑定，发送一次后任何超时或解析失败都保留可能已执行标记；结果脱敏后再交付。
        if (HttpRequestDefinition.Validate(data) is { } invalid) throw Error("HTTP_CONFIG_INVALID", invalid);
        if (Bind((JsonArray)data["fields"]!, inputs, out var bound) is { } inputError) throw Error("HTTP_INPUT_INVALID", inputError);
        var http = data["http"]!.AsObject();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Number(http["timeoutSeconds"])!.Value));
        var sent = false;
        try
        {
            var uri = HttpRequestDefinition.Url(http, bound);
            if (credential is not null) HttpCredentialPolicy.Require(credential, data, uri);
            await network.ResolveAsync(uri, timeout.Token);
            var secretHeaders = Reveal(credential, uri);
            var secrets = Secrets(secretHeaders);
            using var request = new HttpRequestMessage(new HttpMethod(Text(http["method"])), uri);
            request.Content = Content(http, bound);
            if (request.Content?.Headers.ContentLength > MaxBytes) throw Error("HTTP_INPUT_INVALID", "展开后的请求体超过 1 MiB。");
            foreach (var row in ((JsonArray)http["headers"]!)) AddHeader(request, Text(row!["name"]), HttpRequestDefinition.ExpandText(Text(row["value"]), bound));
            foreach (var row in secretHeaders)
            {
                if (request.Headers.Contains(row.Key)) throw Error("HTTP_INPUT_INVALID", "普通请求头与凭据请求头重名，请删除普通请求头中的同名项。");
                AddHeader(request, row.Key, row.Value);
            }
            using var client = clients.CreateClient("workflow-http-request");
            sent = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (http["failOnHttpError"]!.GetValue<bool>() && !response.IsSuccessStatusCode)
                throw Error("HTTP_STATUS_ERROR", $"接口返回 HTTP {(int)response.StatusCode}。可在节点中选择将错误状态交给下游处理。", true);
            var bytes = await ReadBoundedAsync(response.Content, (int)Number(http["responseLimitBytes"])!.Value, timeout.Token);
            var body = new UTF8Encoding(false, true).GetString(bytes);
            JsonNode? parsed = null; var isJson = false;
            if (body.Length != 0)
            {
                try { parsed = JsonNode.Parse(body, documentOptions: new() { MaxDepth = 12 }); isJson = true; }
                catch (JsonException) { if (((JsonArray)data["outputFields"]!).Count != 0) throw Error("HTTP_RESPONSE_INVALID", "响应不是有效 JSON，无法按路径提取字段。", true); }
            }
            if (isJson) { parsed = Redact(parsed, secrets); body = parsed?.ToJsonString(BodyJson) ?? "null"; }
            else body = RedactText(body, secrets);
            var headers = new JsonObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) || HttpRequestDefinition.SensitiveHeader(header.Key)) continue;
                var name = RedactText(header.Key.ToLowerInvariant(), secrets);
                if (headers.ContainsKey(name)) throw Error("HTTP_RESPONSE_INVALID", "响应头在脱敏后出现冲突。", true);
                headers[name] = RedactText(string.Join(", ", header.Value), secrets);
            }
            var fieldsOnly = Text(data["responseMode"]) == "fields";
            var result = fieldsOnly ? new JsonObject() : new JsonObject { ["statusCode"] = (int)response.StatusCode, ["body"] = body, ["headers"] = headers };
            foreach (var field in ((JsonArray)data["outputFields"]!).OfType<JsonObject>())
            {
                var key = Text(field["key"]);
                if (isJson && HttpRequestDefinition.TryExtract(parsed, Text(data["extract"]![key]), out var value) && (!fieldsOnly || value is not null)) result[key] = value?.DeepClone();
                else if (field["required"]!.GetValue<bool>()) throw Error("HTTP_OUTPUT_MISSING", $"响应缺少必填提取字段 {key}，请核对路径。", true);
            }
            if (Bind(HttpRequestDefinition.Outputs(data), result, out var output, useDefaults: false) is { })
                throw Error("HTTP_OUTPUT_INVALID", "HTTP 响应不符合字段契约或超过输出预算（每字段 1 MiB，正文与提取副本合计 2 MiB）。请核对字段约束或改为仅输出提取字段。", true);
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Error("HTTP_TIMEOUT", "HTTP 请求超时，未自动重试。请核对远端结果后再运行。", sent); }
        catch (HttpRequestException) { throw Error("HTTP_CONNECTION_FAILED", "HTTP 连接失败，未自动重试。", sent); }
        catch (IOException) { throw Error("HTTP_CONNECTION_FAILED", "HTTP 响应读取中断，未自动重试。", sent); }
        catch (DecoderFallbackException) { throw Error("HTTP_RESPONSE_INVALID", "HTTP 响应不是 UTF-8 文本。", sent); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or JsonException)
        { throw Error("HTTP_INPUT_INVALID", "HTTP 请求配置或展开后的数据无效。", sent); }
    }

    /// <summary>按显式模式展开请求体，并限制序列化后的字节数。</summary>
    private static HttpContent? Content(JsonObject http, JsonObject inputs)
    {
        var mode = Text(http["bodyType"]);
        if (mode == "none") return null;
        if (mode == "form") return new FormUrlEncodedContent(((JsonArray)http["form"]!).Select(row =>
            KeyValuePair.Create(Text(row!["name"]), HttpRequestDefinition.ExpandText(Text(row["value"]), inputs))));
        var body = mode == "json" ? HttpRequestDefinition.ExpandJson(JsonNode.Parse(Text(http["body"])), inputs)?.ToJsonString() ?? "null"
            : HttpRequestDefinition.ExpandText(Text(http["body"]), inputs);
        if (Encoding.UTF8.GetByteCount(body) > MaxBytes) throw Error("HTTP_INPUT_INVALID", "展开后的请求体超过 1 MiB。");
        return new StringContent(body, Encoding.UTF8, mode == "json" ? "application/json" : "text/plain");
    }

    /// <summary>凭据只在目标源匹配后短暂解密，不返回明文到调用方。</summary>
    private Dictionary<string, string> Reveal(HttpCredentialRevision? credential, Uri uri)
    {
        if (credential is null) return new(StringComparer.OrdinalIgnoreCase);
        if (Text(JsonNode.Parse(credential.DefinitionJson)?["origin"]) != uri.GetLeftPart(UriPartial.Authority))
            throw Error("HTTP_CREDENTIAL_ORIGIN", "凭据与请求目标不匹配，尚未发送请求。");
        if (JsonNode.Parse(credential.DefinitionJson)?["headers"] is JsonArray { Count: 0 } && credential.Secret is null)
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var value = JsonNode.Parse(protector.Unprotect(credential.Secret!))!.AsObject();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in value)
            {
                if (!HttpRequestDefinition.ValidHeader(pair.Key) || pair.Value?.GetValueKind() != JsonValueKind.String) throw new CryptographicException();
                result.Add(pair.Key, pair.Value.GetValue<string>());
            }
            return result;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidOperationException or ArgumentException)
        { throw Error("HTTP_CREDENTIAL_INVALID", "凭据无法读取，请管理员重新保存凭据版本。"); }
    }

    /// <summary>阻止展开变量注入控制字符；内容类型写入内容头集合。</summary>
    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (value.Length > 8192 || value.Any(char.IsControl)) throw Error("HTTP_INPUT_INVALID", "请求头包含控制字符或超过长度限制。");
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
        { request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value); return; }
        if (!request.Headers.TryAddWithoutValidation(name, value)) throw Error("HTTP_INPUT_INVALID", "请求头与当前请求体不兼容。");
    }

    /// <summary>带长度与分块响应都受同一字节上限限制。</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw Error("HTTP_RESPONSE_TOO_LARGE", "HTTP 响应超过配置上限。", true);
        await using var stream = await content.ReadAsStreamAsync(ct); using var result = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)result.Length)), ct);
            if (count == 0) return result.ToArray();
            await result.WriteAsync(buffer.AsMemory(0, count), ct);
            if (result.Length > limit) throw Error("HTTP_RESPONSE_TOO_LARGE", "HTTP 响应超过配置上限。", true);
        }
    }

    /// <summary>构造完整值及 Bearer/Basic 常见回显形式的脱敏集合。</summary>
    private static string[] Secrets(Dictionary<string, string> headers)
    {
        var values = headers.Values.Where(value => value.Length != 0).ToList();
        foreach (var value in headers.Values)
        {
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) values.Add(value[7..]);
            if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                try { var plain = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..])); values.Add(plain); var split = plain.IndexOf(':'); if (split >= 0) values.Add(plain[(split + 1)..]); }
                catch (FormatException) { }
        }
        return values.Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
    }
    /// <summary>较长秘密先替换，避免部分值遮盖后遗漏。</summary>
    private static string RedactText(string text, string[] secrets)
    { foreach (var secret in secrets) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal); return text; }
    /// <summary>递归清理字段名和字符串值；冲突不能覆盖原字段。</summary>
    private static JsonNode? Redact(JsonNode? value, string[] secrets)
    {
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                var key = RedactText(pair.Key, secrets);
                if (result.ContainsKey(key)) throw Error("HTTP_RESPONSE_INVALID", "响应字段在脱敏后出现冲突。", true);
                result[key] = Redact(pair.Value, secrets);
            }
            return result;
        }
        if (value is JsonArray array) return new JsonArray(array.Select(item => Redact(item, secrets)).ToArray());
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? JsonValue.Create(RedactText(text, secrets)) : value?.DeepClone();
    }
    /// <summary>输出稳定错误码与请求是否可能已经发送。</summary>
    private static WorkflowHttpException Error(string code, string message, bool sent = false) => new(code, message, sent);
}
