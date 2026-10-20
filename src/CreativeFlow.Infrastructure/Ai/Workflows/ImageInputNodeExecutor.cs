using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>验证图片输入节点的资产权限与类型，交付原素材引用。</summary>
internal sealed class ImageInputNodeExecutor(ICanvasAssetRepository assets) : IWorkflowNodeExecutor
{
    /// <summary>验证输入图片仍属于可访问素材并返回节点图片引用。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var run = context.Run;
        var node = context.Node;
        var url = ReadString(node.Data, "assetUrl");
        if (!TryAssetId(url, out var assetId))
        {
            throw new WorkflowExecutionException("The input image is no longer available.");
        }

        var asset = await assets.FindAsync(run.DeviceId, assetId, ct);
        if (asset is not { Kind: "image", IsRecycled: false }
            || !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowExecutionException("The input image is no longer available.");
        }

        return WorkflowNodeValue.FromImage(url);
    }
}
