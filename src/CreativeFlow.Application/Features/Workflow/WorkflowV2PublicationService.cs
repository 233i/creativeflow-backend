using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>类型化工作流发布只冻结配置和资源引用；每次外部运行重新绑定真实 Key 主体，复用原队列与 MAF。</summary>
public sealed class WorkflowV2PublicationService(IWorkflowExternalApiRepository deployments, IWorkflowV2Models models,
    KnowledgeWorkflowService? knowledge = null,
    IWorkflowV2AssetResolver? assets = null, HttpRequestService? http = null,
    IWorkflowScriptRunner? scripts = null)
{
    /// <summary>从合法开始/结束字段生成对外契约，不暴露内部节点连线、模型配置或资源标识。</summary>
    public static (WorkflowPlan Plan, JsonArray Inputs, JsonArray Outputs) Contract(string graph)
    {
        var validation = WorkflowV2GraphValidator.Validate(Parse(graph));
        if (!validation.IsValid || validation.Plan is not { } plan)
            throw new WorkflowV2Exception("发布图无效：" + string.Join("; ", validation.Errors));
        return (plan, plan.Nodes.Single(node => node.Type == "start").Data["fields"]!.DeepClone().AsArray(),
            plan.Nodes.Single(node => node.Type == "end").Data["fields"]!.DeepClone().AsArray());
    }

    /// <summary>发布前复验管理员可用资源和模型；不调用聊天、向量或外部查询服务，也不保存管理员权限。</summary>
    public async Task<JsonObject> CaptureAsync(WorkflowPlan plan, KnowledgeSubject subject, CancellationToken ct)
    {
        if (WorkflowScriptValues.ContainsScript(plan) && (scripts is null || !scripts.Enabled))
            throw new WorkflowV2Exception("脚本沙箱未启用，不能发布包含脚本节点的工作流。");
        if (assets is not null)
            foreach (var field in plan.Nodes.SelectMany(node => (node.Data["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                .Where(field => WorkflowV2Values.Text(field["type"]) == "image" && field.ContainsKey("default")))
                await assets.ValidateAsync(new CreativeFlow.Domain.Identity.DeviceId(subject.Device), field["default"]!, ct);
        var snapshot = new JsonObject { ["schemaVersion"] = WorkflowGraphValidator.SchemaVersion, ["models"] = await models.CaptureAsync(plan, ct) };
        if (plan.Nodes.Any(node => KnowledgeWorkflowValues.IsKnowledge(node.Type)))
        {
            if (knowledge is null) throw Unavailable();
            var resource = await knowledge.CaptureAsync(plan, subject, ct); resource.Remove("subject");
            snapshot["knowledge"] = resource;
        }
        if (plan.Nodes.Any(node => node.Type == "httpRequest"))
        {
            if (http is null) throw Unavailable();
            var resource = await http.CaptureAsync(plan, subject, ct); resource.Remove("subject");
            snapshot["http"] = resource;
        }
        return snapshot;
    }

    /// <summary>固定版本校验类型化输入，并把 Key 独立资源授权附加到运行快照；绝不重捕获后替换发布配置。</summary>
    public async Task<JsonObject> BindAsync(WorkflowDeployment deployment, KnowledgeSubject subject, JsonObject inputs, CancellationToken ct)
    {
        if (subject.Kind != "apiKey") throw Unavailable();
        var (plan, fields, outputs) = Contract(deployment.GraphJson);
        if (!JsonNode.DeepEquals(fields, Parse(deployment.InputSchemaJson)) || !JsonNode.DeepEquals(outputs, Parse(deployment.OutputSchemaJson))) throw Unavailable();
        var error = WorkflowV2Values.Bind(fields, inputs, out var bound);
        if (error is not null) throw new WorkflowV2Exception(error);
        if (assets is not null)
            foreach (var field in fields.OfType<JsonObject>().Where(field => WorkflowV2Values.Text(field["type"]) == "image").Select(field => WorkflowV2Values.Text(field["key"])).Where(bound.ContainsKey))
                await assets.ValidateAsync(new CreativeFlow.Domain.Identity.DeviceId(subject.Device), bound[field]!, ct);
        var frozen = Parse(deployment.ExecutionSnapshotJson) as JsonObject ?? throw Unavailable();
        var current = await CaptureAsync(plan, subject, ct);
        if (!JsonNode.DeepEquals(frozen, current)) throw new WorkflowV2Exception("发布使用的模型或资源配置已改变，请管理员核对后重新发布。");
        var snapshot = frozen.DeepClone().AsObject(); snapshot["inputs"] = bound;
        foreach (var name in new[] { "knowledge", "http" })
            if (snapshot[name] is JsonObject resource) resource["subject"] = Subject(subject);
        return snapshot;
    }

    /// <summary>执行每步前后和读取结果前复验 Key、发布快照和资源许可；新发布不会替换已受理版本。</summary>
    public async Task ValidateRunAsync(WorkflowRun run, CancellationToken ct)
    {
        // 发布资源不携带管理员身份；每个运行必须绑定原 Key，再逐类核验资源许可。
        if (run.TriggerType != WorkflowRunTriggerType.Api || run.ApiKeyId is not { } keyId || run.DeploymentId is not { } id) throw Unavailable();
        var key = await deployments.FindApiKeyAsync(keyId, ct);
        if (key is null || key.WorkflowId != run.WorkflowId || !key.Enabled || key.RevokedAt is not null
            || key.ExpiresAt <= DateTimeOffset.UtcNow) throw new WorkflowV2Exception("外部调用 Key 已撤销、过期或不可用。");
        var publication = await deployments.FindDeploymentAsync(id, ct);
        if (publication is null || publication.Deployment.SchemaVersion != WorkflowGraphValidator.SchemaVersion || publication.Deployment.WorkflowId != run.WorkflowId
            || publication.OwnerDeviceId != run.DeviceId || !JsonNode.DeepEquals(Parse(publication.Deployment.GraphJson), Parse(run.GraphJson))) throw Unavailable();
        var frozen = Parse(publication.Deployment.ExecutionSnapshotJson) as JsonObject ?? throw Unavailable();
        var snapshot = Parse(run.InputJson) as JsonObject ?? throw Unavailable();
        var captured = snapshot.DeepClone().AsObject(); captured.Remove("inputs");
        // 传输选项由受理接口写入，既不替换发布配置，也不能携带资源或调用方身份。
        if (captured.Remove("stream", out var stream) && (stream is not JsonValue streamValue || !streamValue.TryGetValue<bool>(out _))) throw Unavailable();
        if (captured.Remove("agui", out var transport))
        {
            // 协议关联 ID 不参与资源快照比较；只允许纯字符串标识，不能夹带授权或模型配置。
            if (transport is not JsonObject identity || identity.Count != 3
                || !TransportId(identity["threadId"]) || !TransportId(identity["runId"])
                || !identity.ContainsKey("parentRunId") || identity["parentRunId"] is not null && !TransportId(identity["parentRunId"])) throw Unavailable();
        }
        var expected = Subject(new("apiKey", keyId.Value, run.DeviceId.Value));
        foreach (var name in new[] { "knowledge", "http" })
            if (captured[name] is JsonObject resource)
            {
                if (!JsonNode.DeepEquals(resource["subject"], expected)) throw Unavailable();
                resource.Remove("subject");
            }
        if (!JsonNode.DeepEquals(captured, frozen)) throw Unavailable();
        if (frozen["knowledge"] is not null)
        {
            if (knowledge is null) throw Unavailable();
            await knowledge.ValidateAsync(snapshot, run.DeviceId.Value, ct);
        }
        if (frozen["http"] is not null)
        {
            if (http is null) throw Unavailable();
            await http.ValidateAsync(snapshot, run.DeviceId.Value, ct);
        }
        if (assets is not null && snapshot["inputs"] is JsonObject inputs)
            foreach (var field in Contract(run.GraphJson).Inputs.OfType<JsonObject>().Where(field => WorkflowV2Values.Text(field["type"]) == "image").Select(field => WorkflowV2Values.Text(field["key"])).Where(inputs.ContainsKey))
                await assets.ValidateAsync(run.DeviceId, inputs[field]!, ct);
    }

    /// <summary>仅投影固定结束节点声明的实际输出，保留数字、布尔、对象、数组及可追溯答案类型。</summary>
    public static JsonObject Outputs(WorkflowDeployment deployment, IReadOnlyList<WorkflowNodeRun> nodes)
    {
        var (plan, _, schema) = Contract(deployment.GraphJson);
        if (!JsonNode.DeepEquals(schema, Parse(deployment.OutputSchemaJson))) throw Unavailable();
        var end = plan.Nodes.Single(node => node.Type == "end");
        var value = nodes.SingleOrDefault(node => node.NodeId == end.Id);
        if (value?.Status != WorkflowNodeRunStatus.Succeeded || !WorkflowV2Values.TryRestore(end, value.OutputJson, out var fields)) throw Unavailable();
        return fields;
    }

    /// <summary>只从可信身份构造资源主体，没有调用方覆盖入口。</summary>
    private static JsonObject Subject(KnowledgeSubject subject) => new()
    { ["kind"] = subject.Kind, ["id"] = subject.Id.ToString(), ["device"] = subject.Device.ToString() };

    /// <summary>不将数据库内的无效 JSON 原文暴露给调用方。</summary>
    private static JsonNode? Parse(string? json)
    {
        try { return json is null ? null : JsonNode.Parse(json); }
        catch (JsonException) { throw Unavailable(); }
    }

    /// <summary>资源、快照缺失统一返回非敏感说明。</summary>
    private static WorkflowV2Exception Unavailable() => new("发布版本或运行配置不可用，请联系管理员。");

    /// <summary>允许纯关联标识作为传输元数据，不接受对象、资源配置或超长控制字符。</summary>
    private static bool TransportId(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        && text is { Length: > 0 and <= 128 } && !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
