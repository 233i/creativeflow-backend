using System.Text;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>执行 LLM、参数提取与意图分类节点，共用原提示词、图片和结构化输出策略。</summary>
public sealed class ModelNodeExecutor(IWorkflowV2Models models, IWorkflowV2AssetResolver? assets = null)
    : GeneralWorkflowNodeExecutor
{
    /// <summary>从固定模型快照发起一次调用，保留输入限制及模型结果校验，不自动重试。</summary>
    protected override async Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct)
    {
        // 图片字段只进入多模态内容段，不拼进文本提示词；一次调用后仍按节点声明校验结构化输出。
        var node = context.Node;
        var snapshot = context.Snapshot;
        var prompt = new StringBuilder();
        var imageFields = ((JsonArray)node.Data["fields"]!).OfType<JsonObject>()
            .Where(field => WorkflowV2Values.Text(field["type"]) == "image")
            .Select(field => WorkflowV2Values.Text(field["key"]))
            .ToArray();
        var images = new List<ReferenceImage>();
        foreach (var key in imageFields)
        {
            if (!bound.TryGetPropertyValue(key, out var value) || value is null) continue;
            if (assets is null) throw new WorkflowV2Exception("图片输入读取服务尚未配置。");
            images.Add(await assets.ReadAsync(context.Run.DeviceId, value, ct));
        }
        foreach (var segment in ((JsonArray)node.Data["prompt"]!).OfType<JsonObject>())
        {
            if (segment.ContainsKey("text")) prompt.Append(WorkflowV2Values.Text(segment["text"]));
            else if (bound.TryGetPropertyValue(WorkflowV2Values.Text(segment["input"]), out var value))
            {
                var key = WorkflowV2Values.Text(segment["input"]);
                if (!imageFields.Contains(key, StringComparer.Ordinal))
                    prompt.Append(value is JsonValue item && item.TryGetValue<string>(out var text) ? text : value?.ToJsonString());
            }
            else throw new WorkflowV2Exception("提示词引用的输入没有值。请填写输入或设置显式默认值。");
        }
        if (string.IsNullOrWhiteSpace(prompt.ToString()) && images.Count == 0) throw new WorkflowV2Exception("展开后的提示词不能为空。");
        if (prompt.Length > 16000 || Encoding.UTF8.GetByteCount(prompt.ToString()) > WorkflowV2Values.MaxBytes)
            throw new WorkflowV2Exception("展开后的提示词超过大小限制。");
        if (snapshot["models"]?[node.Id] is not JsonObject model) throw new WorkflowV2Exception("模型配置快照缺失。");
        var constrainedPrompt = WorkflowModelTasks.ConstrainPrompt(node, prompt.ToString());
        var generated = images.Count > 0
            ? models is IWorkflowV2MultimodalModels multimodal
                ? await multimodal.GenerateAsync(node, model, constrainedPrompt, images, ct)
                : throw new WorkflowV2Exception("当前模型执行器不支持图片输入。")
            : await models.GenerateAsync(node, model, constrainedPrompt, ct);
        return WorkflowModelTasks.Parse(node, generated);
    }
}
