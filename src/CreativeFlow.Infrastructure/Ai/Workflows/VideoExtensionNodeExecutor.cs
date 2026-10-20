using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>执行 xAI 视频续接，保留源视频约束、模型选择与已有任务复用。</summary>
internal sealed class VideoExtensionNodeExecutor(
    IVideoJobRepository videoJobs,
    WorkflowMediaModelResolver models,
    WorkflowMediaAssetReader media,
    IVideoGenerationDispatcher dispatcher,
    WorkflowVideoJobAwaiter awaiter) : IWorkflowNodeExecutor
{
    /// <summary>按既有模型与来源限制执行工作流视频续接节点。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var run = context.Run;
        var node = context.Node;
        var values = context.Values;
        // 续接沿用源视频的可读取内容和当前模型约束，并通过本地任务记录等待结果，避免重复提交。
        var input = GatherInputs(node, values);
        var inheritedJob = run.ResumedFromRunId is { } sourceRunId
            ? await videoJobs.FindLatestForWorkflowNodeAsync(
                run.DeviceId, sourceRunId, node.Id, ct)
            : null;

        Guid jobId;
        if (inheritedJob is { Status: VideoJobStatus.Succeeded, AssetId: { } inheritedAssetId })
        {
            return WorkflowNodeValue.FromVideo($"/api/v1/canvas/assets/{inheritedAssetId}");
        }
        if (inheritedJob is { Status: VideoJobStatus.Succeeded, AssetId: null })
        {
            throw new WorkflowExecutionException(
                "The completed video extension job has no stored asset. " +
                "Run the workflow again from the beginning.");
        }
        if (inheritedJob is { Status: VideoJobStatus.Pending or VideoJobStatus.Running })
        {
            jobId = inheritedJob.JobId;
        }
        else
        {
            if (input.Videos.Count != 1)
            {
                throw new WorkflowExecutionException(
                    "The video extension node requires exactly one video input.");
            }

            var prompt = string.Join("\n", input.Texts).Trim();
            if (prompt.Length == 0)
            {
                throw new WorkflowExecutionException(
                    "The video extension node requires a continuation prompt.");
            }

            var sourceVideo = await media.LoadVideoAsync(run, input.Videos[0], ct);
            var managed = await models.ResolveVideoExtensionAsync(ct);
            var duration = Math.Clamp(
                ReadInt(node.Data, "duration") ?? VideoExtensionLimits.DefaultDurationSeconds,
                VideoExtensionLimits.MinimumDurationSeconds,
                VideoExtensionLimits.MaximumDurationSeconds);
            var submitted = await dispatcher.ExtendAsync(
            ProviderType.XaiVideo,
            new VideoExtensionRequest(
                managed.Provider,
                managed.ApiKey,
                managed.Model.ModelKey,
                prompt,
                sourceVideo,
                duration),
            ct);

            if (!submitted.Ok || submitted.UpstreamJobId is null)
            {
                throw new WorkflowExecutionException(
                    submitted.Error ?? "The video provider rejected the extension node.");
            }

            jobId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await videoJobs.InsertAsync(
                new VideoJob(
                    jobId,
                    run.DeviceId,
                    managed.Provider.Id.Value,
                    managed.Model.ModelKey,
                    VideoJobStatus.Running,
                    submitted.UpstreamJobId,
                    null,
                    null,
                    null,
                    new JsonObject
                    {
                        ["workflowRunId"] = run.Id.ToString(),
                        ["nodeId"] = node.Id,
                        ["operation"] = "extension",
                        ["sourceVideo"] = input.Videos[0],
                        ["prompt"] = prompt,
                        ["duration"] = duration,
                    }.ToJsonString(),
                    now,
                    now),
                ct);
        }

        return await awaiter.WaitAsync(context, jobId, ct);
    }
}
