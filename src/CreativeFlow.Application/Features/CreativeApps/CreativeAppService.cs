using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using static CreativeFlow.Application.Features.CreativeApps.CreativeAppContract;

namespace CreativeFlow.Application.Features.CreativeApps;

/// <summary>公共媒体应用的发布与受理规则；身份由入口验证，执行仍使用现有持久工作流。</summary>
public sealed class CreativeAppService(ICreativeAppRepository apps, IWorkflowRepository workflows,
    ICanvasAssetRepository assets, IProviderRepository providers, IModelSlotRepository slots, IWorkflowRunRepository runs)
{
    /// <summary>校验媒体图、公开参数与共享素材，固定模型后发布新版本。</summary>
    public async Task<CreativeAppRelease> PublishAsync(Guid workflowId, Guid actor, JsonObject request, CancellationToken ct)
    {
        var workflow = await workflows.FindApiAsync(new WorkflowId(workflowId), ct)
            ?? throw new CreativeAppException("not_found", "工作流不存在。", 404);
        var graph = JsonNode.Parse(workflow.GraphJson)!.AsObject();
        if (WorkflowGraphCompiler.IsTypedGraph(graph)) throw new CreativeAppException("media_only", "公共创作仅支持媒体工作流。");
        var contract = WorkflowExternalContractValidator.Validate(graph);
        if (!contract.IsValid || contract.Contract is null) throw new CreativeAppException("invalid_contract", string.Join("；", contract.Errors));
        if (contract.Contract.Outputs.Count == 0 || contract.Contract.Outputs.Any(o => o.Type is not ("image" or "video")))
            throw new CreativeAppException("media_outputs_required", "公共创作需要声明图片或视频结果，不能发布文本结果。");
        var nodes = graph["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
        if (nodes.Any(n => Text(n["type"]) is not ("text" or "image" or "imageGenerate" or "videoGenerate" or "imageOutput" or "videoOutput" or "output")))
            throw new CreativeAppException("unsupported_node", "公共创作支持文本、图片输入、图片/视频生成及结果节点。");
        if (!nodes.Any(n => Text(n["type"]) is "imageGenerate" or "videoGenerate")) throw new CreativeAppException("missing_generation", "公共创作需要图片或视频生成节点。");
        var name = Text(request["name"]).Trim(); var description = Text(request["description"]).Trim();
        if (name.Length is < 1 or > 100 || description.Length > 1000) throw new CreativeAppException("invalid_metadata", "名称需要 1–100 字，说明最多 1000 字。");
        var category = Text(request["category"]);
        if (category is not ("image" or "video")) throw new CreativeAppException("invalid_category", "请选择图片或视频分类。");
        var max = Number(request["maxConcurrency"], 2); var perUser = Number(request["userConcurrency"], 1); var daily = Number(request["dailyLimit"], 50);
        if (max is < 1 or > 32 || perUser is < 1 or > 8 || daily is < 1 or > 10000) throw new CreativeAppException("invalid_limits", "并发范围为 1–32，用户并发 1–8，每日额度 1–10000。");
        var share = Flag(request["shareFixedAssets"]);
        foreach (var node in nodes.Where(n => Text(n["type"]) == "image"))
        {
            // Required user image fields never inherit a publisher sample, even when the draft contains one.
            var input = contract.Contract.Inputs.FirstOrDefault(i => i.Target.NodeId == Text(node["id"]) && i.Type == "image");
            if (input?.Required == true)
            {
                node["data"]!.AsObject().Remove("assetUrl");
                foreach (var entry in graph["externalApi"]!["inputs"]!.AsArray().OfType<JsonObject>().Where(i => Text(i["key"]) == input.Key)) entry.Remove("default");
                continue;
            }
            if (AssetId(node["data"]?["assetUrl"]) is { } assetId)
            {
                if (!share) throw new CreativeAppException("sharing_required", "此流程包含固定参考图，请明确授权共享，或将图片参数设为必填。");
                await ValidateAssetAsync(workflow.DeviceId, assetId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(Text(node["data"]?["assetUrl"])))
                throw new CreativeAppException("invalid_reference", "固定参考图需要使用当前账户的素材，不能使用外部地址。");
            if (input?.DefaultValue is not null) throw new CreativeAppException("image_default", "公共图片参数请使用必填上传或节点固定参考图，不支持默认 assetId。");
        }
        Guid? cover = null;
        if (!string.IsNullOrWhiteSpace(Text(request["coverAssetId"])))
        {
            cover = AssetId(request["coverAssetId"]) ?? throw new CreativeAppException("invalid_cover", "封面素材无效。");
            await ValidateAssetAsync(workflow.DeviceId, cover.Value, ct);
        }
        await FreezeModelsAsync(graph, ct);
        // Persist effective defaults so clients can render exactly what the server will use.
        foreach (var input in graph["externalApi"]!["inputs"]!.AsArray().OfType<JsonObject>())
        {
            if (Flag(input["required"]) || Text(input["type"]) == "image" || input.ContainsKey("default")) continue;
            var node = nodes.First(n => Text(n["id"]) == Text(input["target"]?["nodeId"]));
            input["default"] = node["data"]?[Text(input["target"]?["field"])]?.DeepClone();
        }
        contract = WorkflowExternalContractValidator.Validate(graph);
        if (!contract.IsValid || contract.Contract is null) throw new CreativeAppException("invalid_contract", string.Join("；", contract.Errors));
        var release = new CreativeAppRelease(Guid.CreateVersion7(), workflowId, 0, new JsonObject
        { ["name"] = name, ["description"] = description, ["category"] = category, ["coverAssetId"] = cover?.ToString(), ["shareFixedAssets"] = share },
            graph.ToJsonString(), contract.Contract.InputSchemaJson, contract.Contract.OutputSchemaJson, max, perUser, daily, DateTimeOffset.UtcNow);
        return await apps.PublishAsync(release, actor, ct);
    }

    /// <summary>发布时固定默认模型，运行时仍检查启用状态，避免后台默认模型切换改变已发布任务。</summary>
    private async Task FreezeModelsAsync(JsonObject graph, CancellationToken ct)
    {
        // 显式模型只做可用性检查；仅未指定模型的草稿解析角色默认值。
        // 生图协议不同，可公开的质量和分辨率字段也不同，发布时固定支持范围。
        var configured = await slots.ListAsync(ct);
        foreach (var node in graph["nodes"]!.AsArray().OfType<JsonObject>().Where(n => Text(n["type"]) is "imageGenerate" or "videoGenerate"))
        {
            var image = Text(node["type"]) == "imageGenerate"; var kind = image ? "image" : "video"; var data = node["data"]!.AsObject();
            var slot = configured.FirstOrDefault(s => s.Kind == kind);
            if ((!string.IsNullOrWhiteSpace(Text(data["providerId"])) && !ProviderId.TryParse(Text(data["providerId"]), out _)) ||
                (!string.IsNullOrWhiteSpace(Text(data["modelId"])) && !ModelId.TryParse(Text(data["modelId"]), out _)))
                throw new CreativeAppException("model_unavailable", "节点模型标识无效，请重新选择模型。");
            var pid = ProviderId.TryParse(Text(data["providerId"]), out var selectedProvider) ? selectedProvider : slot?.ProviderId;
            var mid = ModelId.TryParse(Text(data["modelId"]), out var selectedModel) ? selectedModel : slot?.ModelId;
            var provider = pid is { } p ? await providers.FindAsync(p, ct) : null;
            var model = provider?.Models.FirstOrDefault(m => m.Id == mid);
            if (provider is not { Enabled: true } || model is not { Enabled: true } || model.Category != (image ? ModelCategory.Image : ModelCategory.Video))
                throw new CreativeAppException("model_unavailable", $"请为{(image ? "图片" : "视频")}生成配置已启用的模型。");
            data["providerId"] = provider.Id.ToString(); data["modelId"] = model.Id.ToString();
            if (image)
            {
                var native = provider.ResolveProtocol(model) == ProviderType.SeedreamImages;
                foreach (var field in graph["externalApi"]!["inputs"]!.AsArray().OfType<JsonObject>().Where(i => Text(i["target"]?["nodeId"]) == Text(node["id"])))
                {
                    var target = Text(field["target"]?["field"]);
                    if (native && target == "quality" || !native && target is "resolution" or "promptOptimizeMode")
                        throw new CreativeAppException("unsupported_parameter", $"模型不支持开放字段 {target}，请移除该参数绑定。");
                    IReadOnlyList<string>? allowed = target switch { "resolution" when native => SeedreamImageOptions.Resolutions(model.ModelKey), "promptOptimizeMode" when native => SeedreamImageOptions.PromptModes(model.ModelKey), _ => null };
                    if (allowed is not null)
                    {
                        var constraints = field["constraints"] as JsonObject ?? new JsonObject(); field["constraints"] = constraints;
                        var selected = constraints["values"] is JsonArray values ? values.Select(Text).Where(allowed.Contains).ToArray() : allowed.ToArray();
                        if (selected.Length == 0) throw new CreativeAppException("unsupported_parameter", $"{target} 没有当前模型支持的可选值。");
                        constraints["values"] = new JsonArray(selected.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    }
                }
            }
        }
    }

    /// <summary>切回旧版本前检查固定模型和共享素材仍然可用，不修改原版本。</summary>
    public async Task<bool> ActivateAsync(Guid workflowId, Guid releaseId, Guid actor, CancellationToken ct)
    {
        var release = await apps.FindAsync(workflowId, releaseId, ct);
        if (release is null) return false;
        var workflow = await workflows.FindApiAsync(new WorkflowId(workflowId), ct);
        if (workflow is null) return false;
        var graph = JsonNode.Parse(release.GraphJson)!.AsObject();
        await FreezeModelsAsync(graph, ct);
        foreach (var node in graph["nodes"]!.AsArray().OfType<JsonObject>().Where(n => Text(n["type"]) == "image"))
            if (AssetId(node["data"]?["assetUrl"]) is { } id) await ValidateAssetAsync(workflow.DeviceId, id, ct);
        if (AssetId(release.Metadata["coverAssetId"]) is { } cover) await ValidateAssetAsync(workflow.DeviceId, cover, ct);
        return await apps.ActivateAsync(workflowId, releaseId, actor, ct);
    }

    /// <summary>校验指定版本和客户输入，复用请求键或创建持久任务。</summary>
    public async Task<CreativeAppAdmission> StartAsync(Guid workflowId, AccountId account, DeviceId device, JsonObject request, CancellationToken ct)
    {
        if (!Guid.TryParse(Text(request["releaseId"]), out var releaseId) || releaseId == Guid.Empty) throw new CreativeAppException("invalid_version", "请重新打开应用获取发布版本。");
        var key = Text(request["requestKey"]);
        if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw new CreativeAppException("invalid_request_key", "请提供 1–128 字的唯一 requestKey。");
        if (request["inputs"] is not JsonObject inputs) throw new CreativeAppException("invalid_inputs", "inputs 必须是对象。");
        var hash = Fingerprint(releaseId, inputs);
        // Query old submissions before checking publication/model/asset state. A lost response never
        // causes a second paid run, even if the administrator has since switched or unpublished.
        if (await apps.FindRequestAsync(account, workflowId, key, ct) is { } oldId)
        {
            var old = await apps.FindRunAsync(account, oldId, ct);
            return old is not null && Fingerprint(old.Release.Id, JsonNode.Parse(old.Run.InputJson!)!.AsObject()) == hash
                ? new("replayed", old.Run.Id) : new("idempotency_conflict", new WorkflowRunId(oldId));
        }
        var release = await apps.FindAsync(workflowId, null, ct) ?? throw new CreativeAppException("not_published", "此应用已下架。", 404);
        if (release.Id != releaseId) return new("version_changed", null);
        var validation = WorkflowExternalContractValidator.Validate(JsonNode.Parse(release.GraphJson));
        var contract = validation.Contract ?? throw new CreativeAppException("invalid_contract", "发布配置不可用。", 409);
        foreach (var field in contract.Inputs.Where(i => i.Required))
            if (!inputs.ContainsKey(field.Key) || inputs[field.Key] is null || field.Type == "string" && string.IsNullOrWhiteSpace(Text(inputs[field.Key])))
                throw new CreativeAppException("required_input", $"请填写：{field.Label}。");
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keyName, value) in WorkflowExternalContractValidator.ReadImageAssetIds(contract, inputs))
        {
            if (!Guid.TryParse(value, out var assetId) || assetId == Guid.Empty) throw new CreativeAppException("invalid_asset", "素材标识无效。"); await ValidateAssetAsync(device, assetId, ct); resolved[keyName] = $"/api/v1/canvas/assets/{assetId}";
        }
        var binding = WorkflowExternalContractValidator.Bind(release.GraphJson, inputs, resolved);
        if (!binding.IsValid) throw new CreativeAppException("invalid_inputs", string.Join("；", binding.Errors));
        // Validate the fixed model IDs again without resolving new default roles.
        var graph = JsonNode.Parse(binding.GraphJson!)!.AsObject(); await FreezeModelsAsync(graph, ct);
        return await apps.EnqueueAsync(release, account, device, key, hash, graph.ToJsonString(), inputs, ct);
    }

    /// <summary>确认图片属于当前账户可访问范围且未被回收。</summary>
    private async Task ValidateAssetAsync(DeviceId device, Guid id, CancellationToken ct)
    {
        if (await assets.FindAsync(device, id, ct) is not { Kind: "image", IsRecycled: false })
            throw new CreativeAppException("asset_denied", "图片不存在、已回收或不属于当前账户。", 403);
    }

    /// <summary>返回客户自己的安全状态视图，隐藏图、内部提示词与上游错误正文。</summary>
    public async Task<JsonObject?> ReadRunAsync(AccountId account, Guid runId, CancellationToken ct)
    {
        // 先按客户限定运行，再投影允许输出的成品；内部节点输出和上游错误始终不外泄。
        var item = await apps.FindRunAsync(account, runId, ct); if (item is null) return null;
        var run = item.Run; var stored = await runs.ListNodesAsync(run.Id, ct);
        var graph = JsonNode.Parse(run.GraphJson)!; var names = graph["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n => Text(n["id"]), n => Text(n["data"]?["label"]));
        var nodes = new JsonArray(); foreach (var node in stored) nodes.Add((JsonNode)new JsonObject
        {
            ["id"] = node.NodeId,
            ["name"] = names.GetValueOrDefault(node.NodeId) is { Length: > 0 } name ? name : NodeName(node.NodeType),
            ["type"] = node.NodeType,
            ["status"] = node.Status.ToString().ToLowerInvariant(),
            ["progress"] = node.Progress,
            ["startedAt"] = node.StartedAt?.ToString("O"),
            ["finishedAt"] = node.FinishedAt?.ToString("O"),
        });
        var outputs = new JsonArray();
        foreach (var declared in JsonNode.Parse(item.Release.OutputSchemaJson)!.AsArray().OfType<JsonObject>())
        {
            var node = stored.FirstOrDefault(n => n.NodeId == Text(declared["nodeId"]) && n.Status == WorkflowNodeRunStatus.Succeeded);
            if (node?.OutputJson is null) continue;
            var value = JsonNode.Parse(node.OutputJson); var kind = Text(declared["type"]);
            var urls = value?[kind == "video" ? "videos" : "images"] as JsonArray ?? [];
            foreach (var url in urls)
                if (AssetId(url) is { } assetId && await assets.FindAsync(run.DeviceId, assetId, ct) is { } asset)
                    outputs.Add((JsonNode)new JsonObject { ["key"] = declared["key"]?.DeepClone(), ["label"] = declared["label"]?.DeepClone(), ["kind"] = kind, ["url"] = $"/api/v1/canvas/assets/{asset.AssetId}", ["mediaType"] = asset.MediaType });
        }
        var status = run.Status.ToString().ToLowerInvariant();
        var active = stored.FirstOrDefault(n => n.Status == WorkflowNodeRunStatus.Running);
        var stage = status == "queued" ? "queued" : status == "running" && (active?.NodeType is "output" or "imageOutput" or "videoOutput" || active?.NodeType == "imageGenerate" && active.Progress >= 0.95) ? "saving" : status == "running" ? "generating" : status;
        return new JsonObject
        {
            ["id"] = run.Id.ToString(),
            ["appId"] = run.WorkflowId.ToString(),
            ["name"] = item.Release.Metadata["name"]?.DeepClone(),
            ["releaseId"] = item.Release.Id.ToString(),
            ["version"] = item.Release.Version,
            ["status"] = status,
            ["stage"] = stage,
            ["createdAt"] = run.CreatedAt.ToString("O"),
            ["updatedAt"] = run.UpdatedAt.ToString("O"),
            ["startedAt"] = run.StartedAt?.ToString("O"),
            ["finishedAt"] = run.FinishedAt?.ToString("O"),
            ["progress"] = stored.Count == 0 ? 0 : (double)stored.Count(n => n.Status is WorkflowNodeRunStatus.Succeeded or WorkflowNodeRunStatus.Skipped) / stored.Count,
            ["error"] = run.Error is null ? null : "生成未完成，请联系管理员查看任务详情。查询结果不会重新生成。",
            ["inputs"] = JsonNode.Parse(run.InputJson ?? "{}"),
            ["nodes"] = nodes,
            ["outputs"] = outputs
        };
    }
    /// <summary>为未自定义名称的媒体节点提供可展示的中文名称。</summary>
    private static string NodeName(string type) => type switch { "text" => "提示词", "image" => "参考图", "imageGenerate" => "图片生成", "videoGenerate" => "视频生成", _ => "保存结果" };
}
