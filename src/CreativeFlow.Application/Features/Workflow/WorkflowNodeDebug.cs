using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>服务器写入的单节点调试元数据；不进入工作流图或已发布的外部执行契约。</summary>
public static class WorkflowNodeDebug
{
    /// <summary>安全读取调试目标；普通运行不产生调试字段。</summary>
    public static string? Target(string? inputJson)
    {
        try { return JsonNode.Parse(inputJson ?? "null")?["nodeDebug"]?["nodeId"]?.GetValue<string>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>设备、工作流与请求随机键共同确定调试运行 ID，重发同一请求不会再次调用模型。</summary>
    public static WorkflowRunId RunId(DeviceId device, WorkflowId workflow, Guid requestId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"workflow-node-debug:{device}:{workflow}:{requestId:D}"));
        _ = WorkflowRunId.TryParse(new Guid(digest.AsSpan(0, 16)).ToString(), out var id);
        return id;
    }
}
