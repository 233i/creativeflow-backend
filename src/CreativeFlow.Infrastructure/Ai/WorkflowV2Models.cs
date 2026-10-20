using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>复用现有文本协议分派器，通过独立端点许可、连接固定和响应大小限制发送模型任务。</summary>
internal sealed class WorkflowV2Models(IProviderRepository providers, IApiKeyProtector protector,
    WorkflowV2Network network, IHttpClientFactory clients, WorkflowModelCallTrace? trace = null,
    ITextGenerationDispatcher? text = null, WorkflowPublicTextStream? stream = null) : IWorkflowV2Models, IWorkflowV2MultimodalModels
{
    /// <summary>在创建任务前捕获每个 LLM 节点的非敏感配置，不调用模型。</summary>
    public async Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct)
    {
        if (!network.Enabled) throw new WorkflowV2Exception("AI 工作流尚未启用。");
        var snapshots = new JsonObject();
        foreach (var node in Flatten(plan).Where(node => WorkflowModelTasks.UsesModel(node.Type)))
        {
            var (provider, model) = await ResolveAsync(node, ct);
            CheckConfiguredEndpoint(provider, model);
            if (RequiresVision(node) && !model.SupportsVision) throw new WorkflowV2Exception("该模型未声明支持图片输入，请更换支持视觉的聊天模型。");
            snapshots[node.Id] = Snapshot(provider, model, RequiresVision(node));
        }
        return snapshots;
    }

    /// <summary>只发送一次文本请求；模型被禁用或配置改变时停止，密钥轮换不影响相同端点的调用。</summary>
    public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct)
        => await GenerateAsync(node, snapshot, prompt, [], ct);

    /// <summary>发送一次文本或多模态请求；图片内容只在内存中使用，不写入运行快照。</summary>
    public async Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt,
        IReadOnlyList<ReferenceImage> images, CancellationToken ct)
    {
        if (!network.Enabled) throw new WorkflowV2Exception("AI 工作流尚未启用。");
        var (provider, model) = await ResolveAsync(node, ct);
        if (!JsonNode.DeepEquals(snapshot, Snapshot(provider, model, RequiresVision(node))))
            throw new WorkflowV2Exception("模型配置已改变，请检查配置后创建新运行。");
        if (images.Count > 0 && !model.SupportsVision)
            throw new WorkflowV2Exception("该模型未声明支持图片输入，请更换支持视觉的聊天模型。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(WorkflowV2Values.Number(node.Data["timeoutSeconds"])!.Value));
        try
        {
            var receiver = stream?.Receiver(node);
            await CheckEndpointAsync(provider, model, deadline.Token, receiver is not null);
            var key = provider.ApiKey is null ? "" : protector.Unprotect(provider.ApiKey);
            if (trace is not null) await trace.BeginAsync(node, provider, model, deadline.Token);
            var request = new TextGenerationRequest(provider, key, model.ModelKey, prompt,
                WorkflowV2Values.Text(node.Data["systemPrompt"]), images,
                (int)WorkflowV2Values.Number(node.Data["maxOutputTokens"])!.Value,
                WorkflowV2Values.Number(node.Data["temperature"]))
            {
                ResponseFormat = WorkflowModelTasks.ResponseFormat(node),
                HttpClientName = "workflow-model",
                OnTextDelta = receiver,
            };
            var protocol = provider.ResolveProtocol(model);
            var result = text is not null
                ? await text.GenerateAsync(protocol, request, deadline.Token)
                : await new OpenAiChatClient(new RestrictedFactory(clients), NullLogger<OpenAiChatClient>.Instance)
                    .GenerateAsync(request, deadline.Token);
            var httpStatus = result.ResponseStatusCode ?? result.StatusCode;
            // 收到成功 HTTP 响应与取得可用正文是两个状态，空正文也不能误记为网络结果未知。
            if (trace is not null) await trace.FinishAsync(result.Ok || httpStatus is >= 200 and < 300 ? "completed" : httpStatus.HasValue ? "failed" : "unknown", result, ct);
            if (!result.Ok || result.Text is null)
                throw new WorkflowV2Exception(result.FinishReason == "length"
                    ? $"模型输出被截断，未返回有效回答正文（HTTP {httpStatus?.ToString() ?? "未知"}，结束原因 length）。请提高节点的最大输出 token，或缩短输入；思考模型的推理也可能占用输出预算。未自动重试。"
                    : $"模型未返回有效文本（HTTP {httpStatus?.ToString() ?? "未知"}）。" + (receiver is not null ? result.Error : "未自动重试；重新运行将再次调用模型。"));
            if (node.Type == "knowledgeAnswer"
                ? System.Text.Encoding.UTF8.GetByteCount(result.Text) > KnowledgeWorkflowValues.MaxAnswerResponseBytes
                : WorkflowModelTasks.IsStructured(node)
                ? System.Text.Encoding.UTF8.GetByteCount(result.Text) > 32768
                : !WorkflowV2Values.IsModelOutputBounded(JsonValue.Create(result.Text)))
                throw new WorkflowV2Exception("模型输出超过文本或 JSON 大小限制。未自动重试。");
            if ((WorkflowModelTasks.IsStructured(node) || node.Type == "knowledgeAnswer") && result.FinishReason is "length" or "content_filter" or "incomplete")
                throw new WorkflowV2Exception("模型输出被截断或过滤，不能当作完整结构化结果。未自动重试。");
            return result.Text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { await UnknownAsync(); throw new WorkflowV2Exception("模型调用超时，结果状态不明确。未自动重试；重新运行将再次调用模型。"); }
        catch (WorkflowV2Exception) { throw; }
        catch (OperationCanceledException) { await UnknownAsync(); throw; }
        catch (Exception)
        { await UnknownAsync(); throw new WorkflowV2Exception("模型调用未完成，结果状态不明确。请检查模型服务；未自动重试。"); }

        /// <summary>调用中断时尽力补充待核对状态，写入失败也保留先前请求收据。</summary>
        async Task UnknownAsync()
        {
            if (trace?.Current?["status"]?.GetValue<string>() != "requesting") return;
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await trace.FinishAsync("unknown", null, flush.Token); } catch { /* 已持久化的 requesting 收据保留为待核对。 */ }
        }
    }

    /// <summary>仅允许显式启用的全局聊天模型；不根据模型名称猜测协议，也不把向量模型当作 LLM。</summary>
    private async Task<(AiProvider Provider, AiModel Model)> ResolveAsync(WorkflowPlanNode node, CancellationToken ct)
    {
        if (!ModelId.TryParse(WorkflowV2Values.Text(node.Data["modelId"]), out var id))
            throw new WorkflowV2Exception("请选择有效的聊天模型。");
        var model = await providers.FindModelAsync(id, ct);
        var provider = model is null ? null : await providers.FindAsync(model.ProviderId, ct);
        if (model is null || provider is null || !model.Enabled || !provider.Enabled
            || model.Category != ModelCategory.Chat || provider.ResolveProtocol(model) is not (ProviderType.OpenAiChat or ProviderType.OpenAiResponses or ProviderType.Anthropic or ProviderType.Gemini)
            || !provider.Models.Any(candidate => candidate.Id == id))
            throw new WorkflowV2Exception("模型不可用。工作流模型节点需要已启用且协议受支持的聊天模型。");
        if (model.MaxOutputTokens is { } max && WorkflowV2Values.Number(node.Data["maxOutputTokens"]) > max)
            throw new WorkflowV2Exception($"节点的最大输出 token 超过模型配置上限 {max}，请调整节点参数或在模型管理中核对上限。");
        return (provider, model);
    }

    /// <summary>提交阶段只校验 URL 许可，不等待 DNS 或建立连接，确保可及时返回入队回执。</summary>
    private Uri CheckConfiguredEndpoint(AiProvider provider, AiModel model)
    {
        try
        {
            var protocol = provider.ResolveProtocol(model);
            var baseUrl = protocol is ProviderType.Anthropic or ProviderType.Gemini
                ? AiProvider.NormalizeBaseUrl(provider.BaseUrl, protocol)
                : provider.BaseUrl.TrimEnd('/');
            var endpoint = protocol switch
            {
                ProviderType.OpenAiChat => new Uri(baseUrl + "/chat/completions"),
                ProviderType.OpenAiResponses => new Uri(baseUrl + "/responses"),
                ProviderType.Anthropic => new Uri(baseUrl + "/v1/messages"),
                ProviderType.Gemini => new Uri(baseUrl + "/v1beta/models/" + Uri.EscapeDataString(model.ModelKey) + ":generateContent"),
                _ => throw new InvalidOperationException("Unsupported workflow model protocol."),
            };
            network.Policy.ValidateEndpoint(endpoint);
            return endpoint;
        }
        catch { throw new WorkflowV2Exception("模型端点未获许可。请配置 Workflow:AllowedEndpoints（精确 URL 或公网 HTTPS 通配 https://*）。"); }
    }

    /// <summary>后台执行时复验 DNS 和 IP 安全，解析等待计入节点期限，不在提交请求内探测远程服务。</summary>
    private async Task CheckEndpointAsync(AiProvider provider, AiModel model, CancellationToken ct, bool streaming = false)
    {
        try
        {
            var endpoint = CheckConfiguredEndpoint(provider, model);
            if (streaming && provider.ResolveProtocol(model) == ProviderType.Gemini)
                endpoint = new Uri(endpoint.AbsoluteUri.Replace(":generateContent", ":streamGenerateContent?alt=sse", StringComparison.Ordinal));
            await network.Policy.ResolveAsync(endpoint, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new WorkflowV2Exception("模型端点未获许可或无法安全解析。请检查 Workflow:AllowedEndpoints 和 DNS；正式环境仅允许公网 HTTPS。"); }
    }

    /// <summary>持久化模型标识、协议、地址和能力限制，不保存渠道密钥或其密文。</summary>
    private static JsonObject Snapshot(AiProvider provider, AiModel model, bool includeVision)
    {
        var snapshot = new JsonObject
        {
            ["modelId"] = model.Id.ToString(), ["providerId"] = provider.Id.ToString(),
            ["modelKey"] = model.ModelKey, ["protocol"] = provider.ResolveProtocol(model).ToWire(), ["baseUrl"] = provider.BaseUrl,
            ["maxOutputTokens"] = model.MaxOutputTokens,
        };
        if (includeVision) snapshot["supportsVision"] = model.SupportsVision;
        return snapshot;
    }

    private static bool RequiresVision(WorkflowPlanNode node) =>
        (node.Data["fields"] as JsonArray)?.OfType<JsonObject>().Any(field => WorkflowV2Values.Text(field["type"]) == "image") == true;

    /// <summary>展开循环子图中的模型节点；子图节点仍使用自身稳定 ID 保存模型快照。</summary>
    private static IEnumerable<WorkflowPlanNode> Flatten(WorkflowPlan plan)
    {
        foreach (var node in plan.Nodes)
        {
            yield return node;
            if (node.Type is not ("loop" or "batch") || node.Data["body"] is not JsonObject body) continue;
            var validation = WorkflowV2GraphValidator.Validate(body);
            if (!validation.IsValid || validation.Plan is null) continue;
            foreach (var nested in Flatten(validation.Plan)) yield return nested;
        }
    }

    /// <summary>将旧协议适配器的客户端请求定向到新版专用连接，不改变全局 upstream 客户端。</summary>
    private sealed class RestrictedFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        /// <summary>无论协议适配器传入何种名称，都使用具有响应大小限制的专用客户端。</summary>
        public HttpClient CreateClient(string name) => inner.CreateClient("workflow-model");
    }
}

/// <summary>新版模型开关和独立网络许可，不能继承其他模块的白名单。</summary>
/// <param name="Enabled">是否允许创建和执行新版工作流。</param>
/// <param name="Policy">针对新版模型端点配置的独立连接策略。</param>
internal sealed record WorkflowV2Network(bool Enabled, EmbeddingEndpointPolicy Policy);

/// <summary>每次发送均校验完整 URL，防止连接池复用绕过路径检查，并允许无密钥的本地模型。</summary>
internal sealed class WorkflowV2RequestGuard(WorkflowV2Network network) : DelegatingHandler
{
    /// <summary>检查请求许可并移除空 Bearer 头；禁止协议层重定向到其他端点。</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        network.Policy.ValidateEndpoint(request.RequestUri!);
        if (string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter)) request.Headers.Authorization = null;
        return base.SendAsync(request, cancellationToken);
    }
}
