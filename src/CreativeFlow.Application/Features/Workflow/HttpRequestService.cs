using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>HTTP 节点固定运行配置、绑定独立凭据权限，并复用工作流执行与发布快照。</summary>
public sealed class HttpRequestService(IHttpCredentialRepository credentials, IWorkflowHttpTransport transport, WorkflowHttpOptions options)
{
    /// <summary>HTTP 节点的独立开关。</summary>
    public bool Enabled => options.Enabled;

    /// <summary>识别需要权限检查和禁用正文实时广播的 HTTP 工作流。</summary>
    public static bool ContainsRequests(string graph)
    {
        try { return (JsonNode.Parse(graph)?["nodes"] as JsonArray)?.OfType<JsonObject>().Any(node => Text(node["type"]) == "httpRequest") == true; }
        catch (JsonException) { return false; }
    }

    /// <summary>目录只返回已授权凭据的版本、目标源和头名称，不返回任何秘密。</summary>
    public async Task<JsonArray> CatalogAsync(KnowledgeSubject subject, CancellationToken ct)
    {
        var result = new JsonArray();
        if (!Enabled) return result;
        foreach (var item in await credentials.ListAsync(ct))
        {
            var id = Guid.Parse(Text(item!["id"]));
            if (!await credentials.CanUseAsync(id, subject, ct) || await credentials.DetailsAsync(id, ct) is not { } detail) continue;
            detail.Remove("description");
            if (await credentials.CanUseAsync(id, subject, ct)) result.Add((JsonNode)detail);
        }
        return result;
    }

    /// <summary>固定节点定义摘要与凭据版本，发布预检和运行提交均不调用业务接口。</summary>
    public async Task<JsonObject> CaptureAsync(WorkflowPlan plan, KnowledgeSubject subject, CancellationToken ct)
    {
        if (!Enabled || subject.Kind is not ("admin" or "account" or "apiKey")) throw Denied();
        var references = new JsonObject();
        foreach (var node in plan.Nodes.Where(node => node.Type == "httpRequest"))
        {
            if (HttpRequestDefinition.Validate(node.Data) is { } error) throw new WorkflowHttpException("HTTP_CONFIG_INVALID", error);
            var http = node.Data["http"]!.AsObject();
            await transport.ValidateEndpointAsync(HttpRequestDefinition.ProbeUrl(http), ct);
            var reference = new JsonObject { ["digest"] = HttpRequestDefinition.Digest(node.Data) };
            if (Text(node.Data["credentialId"]).Length != 0)
            {
                var credential = await RequireAsync(Guid.Parse(Text(node.Data["credentialId"])), Guid.Parse(Text(node.Data["credentialRevisionId"])), subject, ct);
                HttpCredentialPolicy.Require(credential, node.Data);
                reference["credentialId"] = credential.CredentialId.ToString(); reference["revisionId"] = credential.Id.ToString(); reference["credentialDigest"] = credential.Digest;
            }
            else await RequireAnonymousAsync(HttpRequestDefinition.ProbeUrl(http), ct);
            references[node.Id] = reference;
        }
        return new JsonObject { ["subject"] = new JsonObject { ["kind"] = subject.Kind, ["id"] = subject.Id.ToString(), ["device"] = subject.Device.ToString() }, ["references"] = references };
    }

    /// <summary>执行和读取前后复验资源权限，撤权和停用会阻止已有运行交付结果。</summary>
    public async Task ValidateAsync(JsonObject snapshot, Guid device, CancellationToken ct)
    {
        var subject = Subject(snapshot);
        if (!Enabled || subject.Device != device || snapshot["http"]?["references"] is not JsonObject references || references.Count is < 1 or > 38) throw Denied();
        foreach (var pair in references)
        {
            if (pair.Value is not JsonObject reference || Text(reference["digest"]).Length != 64) throw Denied();
            if (reference["credentialId"] is null) continue;
            if (!Guid.TryParse(Text(reference["credentialId"]), out var id) || !Guid.TryParse(Text(reference["revisionId"]), out var revisionId)) throw Denied();
            var revision = await RequireAsync(id, revisionId, subject, ct);
            if (revision.Digest != Text(reference["credentialDigest"])) throw Denied();
        }
    }

    /// <summary>只执行已捕获的配置；请求返回后再次核对授权，不重发未知结果。</summary>
    public async Task<JsonObject> ExecuteAsync(WorkflowPlanNode node, JsonObject snapshot, JsonObject inputs, CancellationToken ct)
    {
        var subject = Subject(snapshot); await ValidateAsync(snapshot, subject.Device, ct);
        var reference = snapshot["http"]?["references"]?[node.Id] as JsonObject ?? throw Denied();
        if (Text(reference["digest"]) != HttpRequestDefinition.Digest(node.Data)) throw Denied();
        HttpCredentialRevision? credential = null;
        if (reference["credentialId"] is not null)
        {
            if (Text(reference["credentialId"]) != Text(node.Data["credentialId"]) || Text(reference["revisionId"]) != Text(node.Data["credentialRevisionId"])) throw Denied();
            credential = await RequireAsync(Guid.Parse(Text(reference["credentialId"])), Guid.Parse(Text(reference["revisionId"])), subject, ct);
            HttpCredentialPolicy.Require(credential, node.Data);
        }
        else await RequireAnonymousAsync(HttpRequestDefinition.Url(node.Data["http"]!.AsObject(), inputs).AbsoluteUri, ct);
        var result = await transport.SendAsync(node.Data, inputs, credential, ct);
        await ValidateAsync(snapshot, subject.Device, ct);
        if (Bind(HttpRequestDefinition.Outputs(node.Data), result, out var bound, useDefaults: false) is { } error) throw new WorkflowHttpException("HTTP_OUTPUT_INVALID", error, true);
        return bound;
    }

    /// <summary>只有主体权限仍有效时才能读取指定凭据的密文版本。</summary>
    private async Task<HttpCredentialRevision> RequireAsync(Guid id, Guid revision, KnowledgeSubject subject, CancellationToken ct) =>
        Enabled && await credentials.CanUseAsync(id, subject, ct) && await credentials.FindRevisionAsync(id, revision, ct) is { } value ? value : throw Denied();
    /// <summary>保护原本不带密钥但仍受资源授权控制的查询接口。</summary>
    private async Task RequireAnonymousAsync(string url, CancellationToken ct)
    {
        if (await credentials.IsRestrictedEndpointAsync(HttpCredentialPolicy.Endpoint(url), ct)) throw Denied();
    }
    /// <summary>恢复由认证入口写入的运行主体，拒绝缺失或损坏快照。</summary>
    private static KnowledgeSubject Subject(JsonObject snapshot)
    {
        if (snapshot["http"]?["subject"] is not JsonObject value || Text(value["kind"]) is not ("admin" or "account" or "apiKey")
            || !Guid.TryParse(Text(value["id"]), out var id) || !Guid.TryParse(Text(value["device"]), out var device)) throw Denied();
        return new(Text(value["kind"]), id, device);
    }
    /// <summary>资源拒绝统一使用不含连接秘密的错误说明。</summary>
    private static WorkflowHttpException Denied() => new("HTTP_RESOURCE_DENIED", "HTTP 请求或引用的凭据不可用，请检查开关、版本和授权。");
}

/// <summary>HTTP 请求能力配置。</summary>
/// <param name="Enabled">是否允许提交和执行 HTTP 节点。</param>
public sealed record WorkflowHttpOptions(bool Enabled);
