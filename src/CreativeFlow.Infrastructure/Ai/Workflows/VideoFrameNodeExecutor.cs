using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>提取每个输入视频的尾帧，按原连线顺序输出可复用图片资产。</summary>
internal sealed class VideoFrameNodeExecutor(IVideoFrameService frames) : IWorkflowNodeExecutor
{
    /// <summary>执行视频抽帧节点并返回可复用的图片素材引用。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        // 每个输入按连线顺序提取已有素材的尾帧；任一输入失效就沿用原失败语义，不交付部分结果。
        var run = context.Run;
        var node = context.Node;
        var values = context.Values;
        var input = GatherInputs(node, values);
        if (input.Videos.Count == 0)
        {
            throw new WorkflowExecutionException("The video frame node received no video.");
        }

        var urls = new List<string>(input.Videos.Count);
        foreach (var url in input.Videos)
        {
            if (!TryAssetId(url, out var assetId))
            {
                throw new WorkflowExecutionException("The input video is no longer available.");
            }

            var result = await frames.ExtractLastFrameAsync(run.DeviceId, assetId, ct);
            if (result.Status != VideoFrameAssetStatus.Succeeded || result.AssetId is not { } frameId)
            {
                throw new WorkflowExecutionException(
                    result.Error ?? (result.Status == VideoFrameAssetStatus.NotFound
                        ? "The input video is no longer available."
                        : "The final video frame could not be extracted."));
            }

            urls.Add($"/api/v1/canvas/assets/{frameId}");
        }

        return new WorkflowNodeValue([], urls, []);
    }
}
