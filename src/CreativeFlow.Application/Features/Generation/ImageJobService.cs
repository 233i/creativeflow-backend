using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Generation;

/// <summary>入队不执行上游请求；查询和下载恢复都不允许进入生成分支。</summary>
public sealed class ImageJobService(IImageJobRepository jobs, IProviderRepository providers, ImageQueueLimits limits)
{
    /// <summary>校验轻量配置后提交；参考图字节和蒙版沿用 worker 中的现有业务校验。</summary>
    public async Task<ImageJobSubmission> SubmitAsync(Guid accountId, Guid deviceId, SubmitImageJobRequest request,
        CancellationToken ct, Guid? regenerateFrom = null)
    {
        var input = request.Input;
        if (request.Key == Guid.Empty || input is null || !ProviderId.TryParse(input.ProviderId, out var providerId))
            throw new ArgumentException("需要有效的提交 Key 和服务商 ID。");
        if (string.IsNullOrWhiteSpace(input.Prompt) && (input.Images?.Length ?? 0) == 0)
            throw new ArgumentException("请输入提示词或连接参考图。");
        if ((input.Prompt?.Length ?? 0) > 100_000 || (input.Images?.Length ?? 0) > 8
            || input.Images?.Any(url => url is null || url.Length > 2048) == true
            || input.NodeId?.Length > 200 || input.ModelKey?.Length > 200 || input.MaskUrl?.Length > 2048)
            throw new ArgumentException("图片生成输入超过允许长度或数量。");
        // 固定默认值，等价提交不会因为省略 Count 而绕过去重。
        input = input with { Count = Math.Clamp(input.Count ?? 1, 1, 4), Images = input.Images ?? [] };
        var json = JsonSerializer.Serialize(input, ImageJobJsonContext.Default.GenerateImageRequest);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{regenerateFrom?.ToString() ?? "submit"}:{json}")));
        // 已接受的重试不能因管理员随后禁用/移除模型而变成新的错误或生成。
        if (await jobs.FindSubmissionAsync(accountId, request.Key, ct) is { } existing)
            return existing.RequestHash == hash ? new(existing) : new(null, Conflict: true);
        var provider = await providers.FindAsync(providerId, ct);
        var model = provider?.Models.FirstOrDefault(candidate => candidate.ModelKey == input.ModelKey);
        if (provider?.Enabled != true || model?.Enabled != true || model.Category != ModelCategory.Image)
            throw new ArgumentException("所选图片服务商或模型不可用，请检查配置。");
        return await jobs.SubmitAsync(new(accountId, deviceId, request.Key, providerId.Value,
            Guid.TryParse(input.CanvasId, out var canvas) ? canvas : null, input.NodeId, json, hash,
            ForceNew: regenerateFrom.HasValue), limits, ct);
    }

    /// <summary>只返回本站已存储的素材地址；原始下载链接只在后台使用。</summary>
    public async Task<ImageJobResponse> DescribeAsync(ImageJob job, CancellationToken ct)
    {
        var outputs = await jobs.OutputsAsync(job.JobId, ct);
        return new(job.JobId.ToString(), job.Status, job.FailureStage, job.Error, job.CreatedAt, job.StartedAt,
            job.FinishedAt, job.HasCheckpoint && job.Status == "failed", job.CanvasId?.ToString(), job.NodeId,
            Deserialize(job.RequestJson), outputs.Where(output => output.Saved).Select(output =>
                new ImageJobAsset(output.Index, $"/api/v1/canvas/assets/{output.AssetId}", output.MediaType, output.AssetId.ToString())).ToArray());
    }

    /// <summary>服务器保存的快照是重新生成的唯一输入，不读取当前画布参数。</summary>
    public static GenerateImageRequest Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ImageJobJsonContext.Default.GenerateImageRequest)
        ?? throw new InvalidOperationException("图片任务参数快照损坏。");
}

/// <summary>持久化快照采用与 HTTP 相同的字段名，兼容 Native AOT。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateImageRequest))]
internal partial class ImageJobJsonContext : JsonSerializerContext;
