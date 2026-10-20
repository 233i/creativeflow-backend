using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>执行媒体视频生成；恢复时优先复用已有视频任务，不引入新的重试策略。</summary>
internal sealed class VideoGenerationNodeExecutor(
    IVideoJobRepository videoJobs,
    WorkflowMediaModelResolver models,
    WorkflowMediaAssetReader media,
    IVideoGenerationDispatcher dispatcher,
    WorkflowVideoJobAwaiter awaiter) : IWorkflowNodeExecutor
{
    /// <summary>提交工作流视频生成节点并等待既有轮询器推进任务。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var run = context.Run;
        var node = context.Node;
        var values = context.Values;
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
                "The completed video job has no stored asset. Run the workflow again from the beginning.");
        }
        if (inheritedJob is { Status: VideoJobStatus.Pending or VideoJobStatus.Running })
        {
            // 进程重启可能使工作流失败，但供应商任务仍处于活动状态。
            // 续接过程会监视同一持久化任务，而不是重复提交并计费两次。
            jobId = inheritedJob.JobId;
        }
        else
        {
            var managed = await models.ResolveAsync("video", ModelCategory.Video, ct);
            var inputMode = NullIfEmpty(ReadString(node.Data, "inputMode")) ?? "reference";
            var reverseFrames = ReadBool(node.Data, "reverseFrames") ?? false;
            var prepared = WorkflowVideoInputPolicy.Prepare(inputMode, reverseFrames, input.Images);
            if (prepared.Error is { } inputError) throw new WorkflowExecutionException(inputError);
            var references = await media.LoadReferencesAsync(run, prepared.Images, 4, ct);
            // 首尾帧带有明确角色，缺失素材时不能把尾帧悄悄提升为首帧；普通参考图仍沿用跳过规则。
            if (inputMode == "first-last" && references.Count != prepared.Images.Count)
                throw new WorkflowExecutionException("A first/last frame image is no longer available. Reconnect the image before running again.");
            var prompt = string.Join("\n", input.Texts);
            var duration = Math.Clamp(
                ReadInt(node.Data, "duration") ?? VideoGenerationLimits.DefaultDurationSeconds,
                VideoGenerationLimits.MinimumDurationSeconds,
                VideoGenerationLimits.MaximumDurationSeconds);

            var submitted = await dispatcher.SubmitAsync(
                managed.Provider.ResolveProtocol(managed.Model),
                new VideoGenerationRequest(
                    managed.Provider,
                    managed.ApiKey,
                    managed.Model.ModelKey,
                    prompt,
                    references,
                    inputMode,
                    NullIfEmpty(ReadString(node.Data, "aspect")),
                    NullIfEmpty(ReadString(node.Data, "resolution")),
                    duration,
                    null,
                    ReadInt(node.Data, "seed"),
                    ReadBool(node.Data, "watermark"),
                    ReadBool(node.Data, "generateAudio")),
                ct);

            if (!submitted.Ok || submitted.UpstreamJobId is null)
            {
                throw new WorkflowExecutionException(
                    submitted.Error ?? "The video provider rejected the workflow node.");
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
                        ["prompt"] = prompt,
                        ["duration"] = duration,
                        ["inputMode"] = inputMode,
                        ["reverseFrames"] = reverseFrames,
                    }.ToJsonString(),
                    now,
                    now),
                ct);
        }

        return await awaiter.WaitAsync(context, jobId, ct);
    }
}
