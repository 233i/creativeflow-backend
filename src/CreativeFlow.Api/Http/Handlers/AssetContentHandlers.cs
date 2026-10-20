using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>资产内容、缩略图和批量下载复用同一权限边界；存储路径不接受客户端输入。</summary>
internal static class AssetContentHandlers
{
    internal static async Task<IActionResult> ContentAsync(Guid id, Guid? scope, bool publicOnly, bool download, HttpContext http,
        IAssetCenterRepository assets, IAssetStore store, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff";
        var content = await assets.GetContentAsync(id, scope, publicOnly, ct); if (content is null) return ApiResults.NotFound();
        var stream = await store.OpenReadAsync(content.StoragePath, ct); if (stream is null) return ApiResults.NotFound();
        if (await assets.GetContentAsync(id, scope, publicOnly, ct) is null) { await stream.DisposeAsync(); return ApiResults.NotFound(); }
        if (scope is null && await CheckOriginsAsync(id, http, assets, ct) is { } denied) { await stream.DisposeAsync(); return denied; }
        return new FileStreamResult(stream, content.MediaType) { EnableRangeProcessing = true, FileDownloadName = download ? DownloadName(content) : null };
    }

    internal static async Task<IActionResult> ThumbnailAsync(Guid id, Guid? scope, bool publicOnly, HttpContext http,
        IAssetCenterRepository assets, IAssetPreviewService previews, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff";
        var content = await assets.GetContentAsync(id, scope, publicOnly, ct); if (content is null) return ApiResults.NotFound();
        var stream = await previews.OpenThumbnailAsync(content, ct); if (stream is null) return ApiResults.NotFound();
        if (await assets.GetContentAsync(id, scope, publicOnly, ct) is null) { await stream.DisposeAsync(); return ApiResults.NotFound(); }
        if (scope is null && await CheckOriginsAsync(id, http, assets, ct) is { } denied) { await stream.DisposeAsync(); return denied; }
        return ApiResults.Stream(stream, "image/jpeg");
    }

    /// <summary>管理资产入口不能绕过来源通用工作流的资源撤权；普通素材原件仍只按原归属访问。</summary>
    internal static async Task<IActionResult?> CheckOriginsAsync(Guid id, HttpContext http, IAssetCenterRepository assets, CancellationToken ct)
    {
        var detail = await assets.GetAsync(id, null, ct); if (detail is null) return ApiResults.NotFound();
        var content = await assets.GetContentAsync(id, null, false, ct);
        if (content is null) return detail.Asset.State is "purged" or "cleaning" ? null : ApiResults.NotFound();
        foreach (var origin in detail.Origins.Where(x => x.Kind == "workflow" && x.WorkflowId is not null).DistinctBy(x => x.Id))
        {
            var runs = http.RequestServices.GetService<IWorkflowRunRepository>(); if (runs is null) return new StatusCodeResult(403);
            var workflowId = new WorkflowId(Guid.Parse(origin.WorkflowId!)); var runId = new WorkflowRunId(Guid.Parse(origin.Id));
            var run = await runs.FindAsync(new DeviceId(content.DeviceId), workflowId, runId, ct) ?? await runs.FindExternalAsync(workflowId, runId, ct);
            if (run is null) return new StatusCodeResult(403);
            if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } denied) return denied;
        }
        return null;
    }

    /// <summary>完整检查所选资产后才创建 ZIP；大小有界、逐项流式复制，任何缺失都明确失败。</summary>
    internal static async Task<IActionResult> DownloadAsync(DownloadAssetsRequest request, Guid? scope, HttpContext http,
        IAssetCenterRepository assets, IAssetStore store, CancellationToken ct)
    {
        if (request.Ids is not { Length: > 0 and <= 50 } || request.Ids.Any(x => !Guid.TryParse(x, out _)))
            return ApiResults.BadRequest(new ErrorResponse("invalid_request", ["一次最多下载 50 个资产。"]));
        var contents = new List<AssetContent>();
        foreach (var id in request.Ids.Select(Guid.Parse).Distinct())
        {
            if (scope is null && await CheckOriginsAsync(id, http, assets, ct) is { } denied) return denied;
            var content = await assets.GetContentAsync(id, scope, false, ct); if (content is null) return ApiResults.NotFound(); contents.Add(content);
        }
        if (contents.Sum(x => x.ByteSize) > 512L * 1024 * 1024)
            return ApiResults.BadRequest(new ErrorResponse("download_too_large", ["单次打包不能超过 512 MiB，请分批下载。"]));
        var temporary = Path.Combine(Path.GetTempPath(), "creativeflow-assets-" + Guid.NewGuid().ToString("N") + ".zip");
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            long copied = 0;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var content in contents)
                {
                    await using var stream = await store.OpenReadAsync(content.StoragePath, ct);
                    if (stream is null) throw new FileNotFoundException("Asset original is unavailable.");
                    await using var entry = zip.CreateEntry(content.Id.ToString()[..8] + "-" + DownloadName(content), CompressionLevel.NoCompression).Open();
                    var buffer = new byte[131072]; int count;
                    while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        copied += count; if (copied > 512L * 1024 * 1024) throw new InvalidDataException("Asset download limit exceeded.");
                        await entry.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                }
            foreach (var content in contents)
            {
                if (await assets.GetContentAsync(content.Id, scope, false, ct) is null) { await output.DisposeAsync(); return ApiResults.NotFound(); }
                if (scope is null && await CheckOriginsAsync(content.Id, http, assets, ct) is { } denied) { await output.DisposeAsync(); return denied; }
            }
            output.Position = 0; http.Response.Headers.CacheControl = "private, no-store";
            return new FileStreamResult(output, "application/zip") { FileDownloadName = "CreativeFlow-assets.zip" };
        }
        catch (FileNotFoundException) { await output.DisposeAsync(); return ApiResults.NotFound(); }
        catch (InvalidDataException) { await output.DisposeAsync(); return ApiResults.BadRequest(new ErrorResponse("download_too_large", ["原件超过打包限制，请分批下载。"])); }
        catch { await output.DisposeAsync(); throw; }
    }

    private static string DownloadName(AssetContent content)
    {
        var name = new string(content.Name.Where(c => !char.IsControl(c) && c is not '/' and not '\\' and not '"').Take(180).ToArray());
        var extension = content.MediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", "video/mp4" => ".mp4", _ => ".bin" };
        return string.IsNullOrWhiteSpace(name) ? content.Id + extension : Path.HasExtension(name) ? name : name + extension;
    }
}
