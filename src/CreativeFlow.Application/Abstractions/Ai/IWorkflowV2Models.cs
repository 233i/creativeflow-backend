using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>新版 LLM 节点的受限模型访问端口，与旧画布文本调用的配置和网络策略隔离。</summary>
public interface IWorkflowV2Models
{
    /// <summary>入队前校验可用模型，并捕获不含密钥的模型和渠道配置。</summary>
    Task<JsonObject> CaptureAsync(WorkflowPlan plan, CancellationToken ct);

    /// <summary>复验当前授权后，按运行保存的配置调用一次模型，不自动重试。</summary>
    Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt, CancellationToken ct);
}

/// <summary>可选的新版模型多模态扩展；旧的测试模型和第三方实现仍可只实现文本端口。</summary>
public interface IWorkflowV2MultimodalModels
{
    Task<string> GenerateAsync(WorkflowPlanNode node, JsonObject snapshot, string prompt,
        IReadOnlyList<ReferenceImage> images, CancellationToken ct);
}

/// <summary>按设备授权读取工作流图片字段；只接受自有图片素材，不接受任意 URL 或路径。</summary>
public interface IWorkflowV2AssetResolver
{
    Task ValidateAsync(DeviceId deviceId, JsonNode value, CancellationToken ct);
    Task<ReferenceImage> ReadAsync(DeviceId deviceId, JsonNode value, CancellationToken ct);
}

/// <summary>允许向用户展示的新版工作流错误，不携带上游原始响应、密钥或内部异常。</summary>
public sealed class WorkflowV2Exception(string message) : Exception(message);
