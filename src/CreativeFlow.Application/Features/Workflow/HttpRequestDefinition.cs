using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CreativeFlow.Application.Abstractions.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>HTTP 节点的有界请求模板与响应契约；模板只读取真实输入，不执行脚本。</summary>
public static partial class HttpRequestDefinition
{
    /// <summary>响应正文以实际传输字节计量，最大 1 MiB；配置、输出和恢复链路使用相同量级。</summary>
    public const int MaxResponseBytes = 1024 * 1024;
    /// <summary>固定响应端口与用户声明的 JSON 提取端口组成完整输出契约。</summary>
    public static JsonArray Outputs(JsonObject data)
    {
        var fields = Text(data["responseMode"]) == "fields" ? new JsonArray() : JsonNode.Parse("""[{"key":"statusCode","label":"HTTP 状态码","type":"number","required":true},{"key":"body","label":"响应正文","type":"string","required":true,"maxLength":1048576},{"key":"headers","label":"响应头","type":"object","required":true}]""")!.AsArray();
        foreach (var field in data["outputFields"] as JsonArray ?? []) fields.Add(field?.DeepClone());
        return fields;
    }

    /// <summary>保存和执行共用结构验证，草稿可暂不填写 URL；发送前必须完整有效。</summary>
    public static string? Validate(JsonObject data, bool forExecution = true)
    {
        // 模板、字段和提取路径在保存与执行之间共用契约；秘密头始终只能来自凭据资源。
        if (!IsBounded(data) || data.Any(pair => pair.Key is not ("title" or "fields" or "http" or "outputFields" or "extract" or "credentialId" or "credentialRevisionId" or "responseMode"))) return "HTTP 节点包含未知配置或超过 1 MiB。";
        if (data["responseMode"] is not null && Text(data["responseMode"]) is not ("full" or "fields")) return "响应模式无效。";
        if (data["fields"] is not JsonArray fields || ValidateFields(fields) is not null) return "HTTP 输入字段契约无效。";
        if (data["http"] is not JsonObject http || http.Any(pair => pair.Key is not ("method" or "url" or "query" or "headers" or "bodyType" or "body" or "form" or "timeoutSeconds" or "responseLimitBytes" or "failOnHttpError"))) return "HTTP 请求配置无效。";
        if (Text(http["method"]) is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS")) return "HTTP 请求方法未支持。";
        if (Text(http["bodyType"]) is not ("none" or "json" or "text" or "form")) return "请求体类型无效。";
        if (Text(http["method"]) is "GET" or "HEAD" && Text(http["bodyType"]) != "none") return "GET 和 HEAD 请求不能携带请求体。";
        if (Number(http["timeoutSeconds"]) is not { } timeout || timeout is < 1 or > 120 || timeout != Math.Truncate(timeout)) return "HTTP 超时必须为 1–120 秒。";
        if (Number(http["responseLimitBytes"]) is not { } limit || limit is < 1024 or > MaxResponseBytes || limit != Math.Truncate(limit)) return "HTTP 响应上限必须为 1024–1048576 字节（1 MiB）。";
        if (http["failOnHttpError"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)) return "请指定 HTTP 错误状态处理方式。";
        if (http["url"]?.GetValueKind() != JsonValueKind.String || Text(http["url"]).Length > 4096) return "请填写有效的 HTTP URL。";
        var inputs = (data["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(field => Text(field["key"])).ToHashSet(StringComparer.Ordinal);
        if (forExecution || Text(http["url"]).Length != 0)
        {
            try { _ = Origin(Text(http["url"])); }
            catch (WorkflowHttpException) { return "URL 必须为完整 HTTP(S) 地址，变量只能出现在路径或查询参数中。"; }
        }
        if (!ValidTemplate(Text(http["url"]), inputs)) return "URL 引用了不存在的输入变量或包含不完整的 {{变量}}。";
        foreach (var name in new[] { "query", "headers", "form" })
        {
            if (http[name] is not JsonArray rows || rows.Count > 24) return "查询参数、请求头和表单各最多 24 项。";
            var names = new HashSet<string>(name == "headers" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row is not JsonObject pair || pair.Count != 2 || pair["name"]?.GetValueKind() != JsonValueKind.String || pair["value"]?.GetValueKind() != JsonValueKind.String
                    || Text(pair["name"]).Length is < 1 or > 128 || Text(pair["name"]).Any(char.IsControl) || !names.Add(Text(pair["name"]))) return "参数名称不能为空、重复或包含控制字符。";
                if (Text(pair["value"]).Length > MaxResponseBytes || !ValidTemplate(Text(pair["value"]), inputs)) return "参数模板过长或引用了不存在的输入变量。";
                if (name == "headers" && (!ValidHeader(Text(pair["name"])) || SensitiveHeader(Text(pair["name"])))) return "该请求头不可直接配置；鉴权信息请使用凭据引用。";
            }
        }
        if (http["body"]?.GetValueKind() != JsonValueKind.String || Text(http["body"]).Length > MaxResponseBytes || !ValidTemplate(Text(http["body"]), inputs)) return "请求正文过长或引用了不存在的输入变量。";
        if (Text(http["bodyType"]) == "json" && (forExecution || Text(http["body"]).Length != 0))
        {
            try { _ = JsonNode.Parse(Text(http["body"]), documentOptions: new() { MaxDepth = 12 }); }
            catch (JsonException) { return "JSON 请求体无效。变量写在 JSON 字符串中，例如 {\"query\":\"{{question}}\"}。"; }
        }
        if (data["outputFields"] is not JsonArray output || output.Count > (Text(data["responseMode"]) == "fields" ? 16 : 13) || ValidateFields(Outputs(data)) is { }) return "HTTP 输出字段无效或与 statusCode、body、headers 重名。";
        if (output.OfType<JsonObject>().Any(field => Text(field["type"]) is "contextChunks" or "answer" or "image")) return "HTTP 提取字段只支持普通 JSON 类型。";
        if (data["extract"] is not JsonObject extract || extract.Count != output.Count || extract.Any(pair => !output.OfType<JsonObject>().Any(field => Text(field["key"]) == pair.Key)
            || pair.Value?.GetValueKind() != JsonValueKind.String || !ValidPointer(Text(pair.Value)))) return "每个提取字段必须配置一个 JSON Pointer 路径。";
        if (new[] { "credentialId", "credentialRevisionId" }.Any(name => data[name] is not null && data[name]!.GetValueKind() != JsonValueKind.String)) return "凭据引用必须为字符串。";
        var credential = Text(data["credentialId"]); var revision = Text(data["credentialRevisionId"]);
        if (credential.Length != 0 || revision.Length != 0)
            if (!Guid.TryParse(credential, out _) || !Guid.TryParse(revision, out _)) return "请选择凭据及其固定版本。";
        return null;
    }

    /// <summary>返回不受输入值影响的目标源，禁止模板改变协议、主机、端口或 URL 用户信息。</summary>
    public static string Origin(string template)
    {
        var separator = template.IndexOf("://", StringComparison.Ordinal);
        var end = separator < 0 ? 0 : template.IndexOfAny(['/', '?', '#'], separator + 3);
        if (end < 0) end = template.Length;
        if (separator < 0 || template[..end].Contains('{') || template[..end].Contains('}') || template.Any(char.IsControl)
            || !Uri.TryCreate(Variable().Replace(template, "value"), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || template.Contains('\\')) throw Invalid("HTTP URL 无效。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>展开路径和查询值时分别编码；输入不能增加主机、请求头或额外查询参数。</summary>
    public static Uri Url(JsonObject http, JsonObject inputs)
    {
        var original = Text(http["url"]);
        var origin = Origin(original);
        var expanded = ExpandText(original, inputs, encode: true);
        var uri = new UriBuilder(expanded);
        var query = ((JsonArray)http["query"]!).Select(row => Uri.EscapeDataString(Text(row!["name"])) + "=" + Uri.EscapeDataString(ExpandText(Text(row["value"]), inputs)));
        uri.Query = string.Join('&', new[] { uri.Query.TrimStart('?') }.Concat(query).Where(value => value.Length != 0));
        if (uri.Uri.GetLeftPart(UriPartial.Authority) != origin || uri.Uri.AbsoluteUri.Length > 16000) throw Invalid("展开后的 HTTP URL 无效或超过长度限制。");
        return uri.Uri;
    }

    /// <summary>预检只替换路径变量，不发送业务请求。</summary>
    public static string ProbeUrl(JsonObject http) => Variable().Replace(Text(http["url"]), "value");

    /// <summary>字符串模板使用有界输入；对象和数组按 JSON 表示。</summary>
    public static string ExpandText(string template, JsonObject inputs, bool encode = false) => Variable().Replace(template, match =>
    {
        if (match.Value == "{{{{") return "{{";
        if (match.Value == "}}}}") return "}}";
        if (!inputs.TryGetPropertyValue(match.Groups[1].Value, out var value)) throw Invalid("请求模板引用的输入没有值，请填写输入或默认值。");
        var text = Scalar(value);
        return encode ? Uri.EscapeDataString(text) : text;
    });

    /// <summary>整值变量保留 JSON 类型，字符串内部插值由 JSON 序列化负责转义。</summary>
    public static JsonNode? ExpandJson(JsonNode? template, JsonObject inputs) => template switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, ExpandJson(pair.Value, inputs)))),
        JsonArray array => new JsonArray(array.Select(value => ExpandJson(value, inputs)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => WholeVariable(text, inputs),
        _ => template?.DeepClone(),
    };

    /// <summary>读取固定 JSON Pointer；缺失与 JSON null 分开处理。</summary>
    public static bool TryExtract(JsonNode? root, string pointer, out JsonNode? value)
    {
        value = root;
        if (!ValidPointer(pointer)) return false;
        if (pointer.Length == 0) return true;
        foreach (var part in pointer.Split('/').Skip(1).Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
        {
            if (value is JsonObject obj && obj.TryGetPropertyValue(part, out var found)) value = found;
            else if (value is JsonArray array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count) value = array[index];
            else return false;
        }
        return true;
    }

    /// <summary>规范化对象键顺序后摘要，JSONB 重排不会改变发布版本身份。</summary>
    public static string Digest(JsonNode node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node)!.ToJsonString())));

    /// <summary>允许普通端到端头；传输头由 HTTP 客户端维护。</summary>
    public static bool ValidHeader(string name) => name.Length is >= 1 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
        && name.ToLowerInvariant() is not ("host" or "content-length" or "transfer-encoding" or "connection" or "upgrade" or "trailer" or "te" or "proxy-authorization" or "proxy-connection" or "cookie" or "set-cookie" or "accept-encoding");

    /// <summary>常见认证头通过密文凭据保存，避免进入图导出和运行记录。</summary>
    public static bool SensitiveHeader(string name) => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api-key", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    /// <summary>仅接受已声明变量和完整模板标记。</summary>
    private static bool ValidTemplate(string template, HashSet<string> inputs) => Variable().Matches(template).All(match => !match.Groups[1].Success || inputs.Contains(match.Groups[1].Value))
        && !Variable().Replace(template, "").Contains("{{", StringComparison.Ordinal) && !Variable().Replace(template, "").Contains("}}", StringComparison.Ordinal);
    /// <summary>限制 JSON Pointer 深度、长度和转义序列。</summary>
    private static bool ValidPointer(string pointer) => pointer.Length <= 512 && (pointer.Length == 0 || pointer.StartsWith('/')) && pointer.Split('/').Length <= 13
        && !Regex.IsMatch(pointer, "~(?![01])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    /// <summary>文本模板保留字符串原文，其他值使用 JSON 表示。</summary>
    private static string Scalar(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null";
    /// <summary>完整变量返回原类型副本，嵌入字符串的变量转为文本。</summary>
    private static JsonNode? WholeVariable(string text, JsonObject inputs)
    {
        var match = Variable().Match(text);
        if (match.Success && match.Groups[1].Success && match.Length == text.Length)
            return inputs.TryGetPropertyValue(match.Groups[1].Value, out var value) ? value?.DeepClone() : throw Invalid("JSON 请求模板引用的输入没有值。");
        return JsonValue.Create(ExpandText(text, inputs));
    }
    /// <summary>递归按序输出对象键，保留数组顺序。</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()), _ => node?.DeepClone(),
    };
    /// <summary>构造发送之前的输入错误。</summary>
    private static WorkflowHttpException Invalid(string message) => new("HTTP_INPUT_INVALID", message);
    /// <summary>有界字段名模板，不提供代码执行或表达式求值能力。</summary>
    [GeneratedRegex(@"\{\{\{\{|\}\}\}\}|\{\{\s*([A-Za-z][A-Za-z0-9_]{0,47})\s*\}\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Variable();
}
