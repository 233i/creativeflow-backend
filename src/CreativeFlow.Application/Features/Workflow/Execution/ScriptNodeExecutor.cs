using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>执行受限 Python/JavaScript 数据转换脚本；脚本只读取 JSON stdin 并返回 JSON stdout。</summary>
public sealed class ScriptNodeExecutor(IWorkflowScriptRunner scripts) : GeneralWorkflowNodeExecutor
{
    /// <summary>在沙箱边界内运行一次脚本，并按声明的输出字段严格绑定结果。</summary>
    protected override async Task<JsonObject> ExecuteBoundAsync(
        WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct)
    {
        if (!scripts.Enabled) throw new WorkflowV2Exception("脚本沙箱未启用，无法执行脚本节点。");
        if (WorkflowScriptValues.Validate(context.Node.Data) is { } configurationError)
            throw new WorkflowV2Exception($"脚本节点配置无效：{configurationError}");

        var language = WorkflowV2Values.Text(context.Node.Data["language"]);
        var code = WorkflowV2Values.Text(context.Node.Data["code"]);
        if (Encoding.UTF8.GetByteCount(bound.ToJsonString()) > scripts.MaximumInputBytes)
            throw new WorkflowV2Exception("脚本输入超过沙箱限制。");
        var seconds = WorkflowV2Values.Number(context.Node.Data["timeoutSeconds"])
            ?? throw new WorkflowV2Exception("脚本节点超时配置无效。");
        var timeout = TimeSpan.FromSeconds(seconds);
        WorkflowScriptResult result;
        try
        {
            result = await scripts.RunAsync(new WorkflowScriptRequest(language, code, bound, timeout), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            throw new WorkflowExecutionException("脚本沙箱不可用或请求超过执行限制。", error);
        }

        if (result.Status is not "succeeded" || result.ExitCode is not 0)
            throw new WorkflowExecutionException(ScriptFailure(result));
        if (result.StdoutTruncated || Encoding.UTF8.GetByteCount(result.Stdout) > scripts.MaximumOutputBytes)
            throw new WorkflowV2Exception("脚本标准输出超过限制，必须只输出一个有界 JSON 对象。");
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new WorkflowV2Exception("脚本没有输出 JSON 对象。");

        JsonObject output;
        try
        {
            output = JsonNode.Parse(result.Stdout,
                documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                ?? throw new WorkflowV2Exception("脚本输出必须是 JSON 对象。");
        }
        catch (JsonException)
        {
            throw new WorkflowV2Exception("脚本输出不是完整、有效的 JSON 对象。");
        }

        var fields = (JsonArray)context.Node.Data["outputFields"]!;
        if (WorkflowV2Values.Bind(fields, output, out var boundOutput, useDefaults: false) is { } bindingError)
            throw new WorkflowV2Exception($"脚本输出校验失败：{bindingError}");
        return boundOutput;
    }

    /// <summary>交付有限的退出信息，不把完整 stderr 或输入内容写入 API 错误。</summary>
    private static string ScriptFailure(WorkflowScriptResult result)
    {
        var exit = result.ExitCode is { } code ? $"（退出码 {code}）" : string.Empty;
        return $"脚本执行失败{exit}。请检查脚本是否将唯一 JSON 对象写入标准输出。";
    }
}
