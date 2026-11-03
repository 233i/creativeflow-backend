using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>执行媒体图片生成，保留原模型角色、参考图顺序、取消检查与素材保存规则。</summary>
internal sealed class ImageGenerationNodeExecutor(
    WorkflowMediaModelResolver models,
    WorkflowMediaAssetReader media,
    IImageGenerationSettingsRepository generationSettings,
    IImageGenerationDispatcher dispatcher,
    IAssetStore assetStore,
    ICanvasAssetRepository assets,
    IWorkflowRunRepository runs,
    IImageOutputDownloader downloader,
    IWorkflowNodeProgress progress) : IWorkflowNodeExecutor
{
    /// <summary>执行工作流图片生成节点并保存生成素材。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var run = context.Run;
        var node = context.Node;
        var values = context.Values;
        // 当前工作流图片节点不传蒙版；生成成功后仍需检查运行是否已取消，再保存输出素材。
        var input = GatherInputs(node, values);
        var managed = await models.ResolveAsync(
            "image",
            ModelCategory.Image,
            ct,
            ReadString(node.Data, "providerId"),
            ReadString(node.Data, "modelId"));
        var references = await media.LoadReferencesAsync(run, input.Images, 8, ct);
        var settings = await generationSettings
            .GetAsync(ct);
        var count = Math.Clamp(ReadInt(node.Data, "count") ?? 1, 1, 4);
        var aspect = ReadString(node.Data, "aspect");

        var protocol = managed.Provider.ResolveProtocol(managed.Model);
        if (protocol == ProviderType.SeedreamImages && references.Count != input.Images.Count)
            throw new WorkflowExecutionException("Seedream 参考图无法完整读取或超过 8 张，未发起图片生成。");
        var result = await dispatcher.GenerateAsync(
            protocol,
            new ImageGenerationRequest(
                managed.Provider,
                managed.ApiKey,
                managed.Model.ModelKey,
                string.Join("\n", input.Texts),
                references,
                null,
                protocol == ProviderType.SeedreamImages ? SeedreamSize(node.Data, managed.Model.ModelKey, aspect) : SizeFor(aspect),
                NullIfEmpty(ReadString(node.Data, "quality")),
                NullIfEmpty(ReadString(node.Data, "outputFormat")),
                settings.ResponseFormat,
                count,
                ReadInt(node.Data, "seed"), DeferDownloads: true)
            { PromptOptimizeMode = protocol == ProviderType.SeedreamImages ? NullIfEmpty(ReadString(node.Data, "promptOptimizeMode")) : null },
            ct);

        if (!result.Ok)
        {
            throw new WorkflowExecutionException(result.Error ?? "Image generation failed.");
        }

        if (await runs
            .IsCancellationRequestedAsync(run.Id, ct))
        {
            throw new WorkflowCancelledException();
        }

        if (run.TriggerType == WorkflowRunTriggerType.PublicCreative)
            await progress.ReportAsync(run, context.NodeRun, 0.95, ct);
        var urls = new List<string>(result.Images.Count);
        foreach (var image in result.Images)
        {
            var saved = image.DownloadUrl is { } downloadUrl && image.Bytes.Length == 0
                ? await downloader.DownloadAsync(downloadUrl, managed.Provider.BaseUrl, managed.ApiKey, image.MediaType, ct)
                : await assetStore.SaveAsync(image.Bytes, image.MediaType, ExtensionFor(image.MediaType), ct);
            var assetId = Guid.CreateVersion7();
            await assets.InsertAsync(
                new CanvasAsset(
                    assetId,
                    run.DeviceId,
                    "image",
                    saved.MediaType,
                    saved.StoragePath,
                    saved.ByteSize,
                    run.TriggerType == WorkflowRunTriggerType.PublicCreative ? null : string.Join("\n", input.Texts),
                    DateTimeOffset.UtcNow,
                    SourceUrl: image.SourceUrl,
                    SourceUrlExpiresAt: image.SourceUrlExpiresAt),
                ct);
            urls.Add($"/api/v1/canvas/assets/{assetId}");
        }

        return new WorkflowNodeValue([], urls, []);
    }

    /// <summary>外部 API 和已保存图同样校验型号支持的档位，避免把无效配置静默变成 2K。</summary>
    private static string SeedreamSize(System.Text.Json.Nodes.JsonObject data, string model, string aspect)
    {
        var resolution = NullIfEmpty(ReadString(data, "resolution"));
        if (resolution is not null && !SeedreamImageOptions.Resolutions(model).Contains(resolution))
            throw new WorkflowExecutionException("当前 Seedream 模型不支持所选分辨率。");
        var size = SeedreamImageOptions.SizeForAspect(aspect, resolution);
        if (SeedreamImageOptions.Validate(model, size, NullIfEmpty(ReadString(data, "promptOptimizeMode"))) is { } error)
            throw new WorkflowExecutionException(error);
        return size;
    }
}
