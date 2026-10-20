using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>凭据只授予明确方法和路径；迁移的查询权限还锁定参数映射和输出契约。</summary>
public static class HttpCredentialPolicy
{
    /// <summary>规范化固定端点，查询参数属于节点配置，不扩大到同站点其他路径。</summary>
    public static string Endpoint(string url)
    {
        url = url.Split('?', 2)[0];
        if (url.Contains('{') || url.Contains('}') || url.Contains('\\') || url.Any(char.IsControl)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw Denied();
        return uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>权限清单只接受固定端点，不接受域名通配或动态路径；空清单为拒绝全部。</summary>
    public static bool ValidRequests(JsonNode? value, string origin) => value is JsonArray { Count: > 0 and <= 32 } requests
        && requests.All(item => item is JsonObject { Count: 2 } request
            && Text(request["method"]) is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            && ValidEndpoint(Text(request["url"]), origin));

    /// <summary>配置摘要不包含名称或凭据 ID，密钥轮换不会解锁原请求契约。</summary>
    public static string RequestDigest(JsonObject data)
    {
        var contract = data.DeepClone().AsObject();
        foreach (var key in new[] { "title", "credentialId", "credentialRevisionId" }) contract.Remove(key);
        return HttpRequestDefinition.Digest(contract);
    }

    /// <summary>捕获和发送前共用校验，拒绝改方法、改路径及绕过迁移后的固定查询契约。</summary>
    public static void Require(HttpCredentialRevision credential, JsonObject data, Uri? expanded = null)
    {
        var definition = JsonNode.Parse(credential.DefinitionJson)!.AsObject();
        var http = data["http"]!.AsObject();
        var origin = Text(definition["origin"]);
        if (origin != HttpRequestDefinition.Origin(Text(http["url"]))) throw Denied();
        var url = expanded?.GetLeftPart(UriPartial.Path) ?? Endpoint(Text(http["url"]));
        if (!ValidRequests(definition["requests"], origin) || !definition["requests"]!.AsArray().Any(request =>
                Text(request!["method"]) == Text(http["method"]) && Text(request["url"]) == url)) throw Denied();
        if (definition["requestDigest"] is not null && Text(definition["requestDigest"]) != RequestDigest(data)) throw Denied();
    }

    /// <summary>固定权限端点不得携带查询串或非规范路径，避免审批值和发送值不一致。</summary>
    private static bool ValidEndpoint(string url, string origin)
    {
        try { return url.Length <= 4096 && Endpoint(url) == url && HttpRequestDefinition.Origin(url) == origin; }
        catch (WorkflowHttpException) { return false; }
    }

    /// <summary>授权失败不回显凭据或内部接口配置。</summary>
    private static WorkflowHttpException Denied() => new("HTTP_CREDENTIAL_SCOPE", "凭据未授权此方法、接口路径或请求契约。请检查接口级权限，不能仅凭同站点凭据调用其他接口。");
}
