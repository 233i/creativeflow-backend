using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>一次认领只执行一个明确操作：首次生成，或从已保存检查点恢复下载。</summary>
public sealed class ImageJobRunner(GenerateImagesUseCase generate, IImageJobRepository jobs,
    BackgroundAccountScope accountScope, IAccountContext accountContext, IProviderRepository providers,
    IApiKeyProtector protector, IImageOutputDownloader downloader, IAccountRepository accounts, IAssetStore store)
{
    /// <summary>宿主令牌与浏览器请求无关；异常交由 worker 记录，不重发生成请求。</summary>
    public async Task RunAsync(ImageJob job, CancellationToken ct)
    {
        await accountScope.RestoreForDeviceAsync(new DeviceId(job.DeviceId), ct);
        if (accountContext.AccountId?.Value != job.AccountId)
        {
            await jobs.FinishAsync(job, "任务设备已不属于原账号，停止执行。", ct);
            return;
        }
        if (!job.HasCheckpoint)
        {
            if ((await accounts.FindByIdAsync(new AccountId(job.AccountId), ct))?.IsEnabled != true)
            {
                await jobs.FinishAsync(job, "账号已停用，未发起模型调用。", ct);
                return;
            }
            var result = await generate.GenerateAndCheckpointAsync(new DeviceId(job.DeviceId),
                ImageJobService.Deserialize(job.RequestJson), async (baseUrl, outputs, token) =>
                {
                    // 存储瞬断只重试保存同一份响应，绝不重新调用模型。
                    for (var attempt = 0; ; attempt++)
                    {
                        try { await jobs.CheckpointAsync(job, baseUrl, outputs.Select(x => new ImageJobOutputSource(x.MediaType, x.DownloadUrl, x.SourceUrl, x.SourceUrlExpiresAt)).ToArray(), token); break; }
                        catch (Npgsql.NpgsqlException) when (attempt < 2 && !token.IsCancellationRequested)
                        { await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), token); }
                    }
                    var checkpoints = await jobs.OutputsAsync(job.JobId, token);
                    foreach (var output in checkpoints.Where(x => !x.Saved && outputs[x.Index].Bytes.Length > 0))
                    {
                        // 先尽力落盘本次响应的全部内联结果。单张的元数据失败不能使后续图片丢失。
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                await using var input = new MemoryStream(outputs[output.Index].Bytes, writable: false);
                                await store.SaveAsync(input, output.MediaType, ".img", 25L * 1024 * 1024, token, output.StorageOperationKey);
                                break;
                            }
                            catch (Exception error) when (!token.IsCancellationRequested && error is Npgsql.NpgsqlException or IOException)
                            {
                                // 这里只重试同一份响应；下方逐输出恢复/登记并明确报告无法保存的项。
                                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), token);
                            }
                        }
                    }
                }, ct);
            if (result.Status != GenerateImagesStatus.Succeeded)
            {
                await jobs.FinishAsync(job, (string.IsNullOrWhiteSpace(result.Error) ? "图片生成未完成。" : result.Error) + " 系统未自动重新生成；如上游已受理，请先核实结果。", ct);
                return;
            }
        }
        var outputs = await jobs.OutputsAsync(job.JobId, ct);
        string? failure = null;
        foreach (var output in outputs.Where(output => !output.Saved))
        {
            try
            {
                var file = await store.FindSavedAsync(output.StorageOperationKey, ct);
                if (file is null)
                {
                    if (output.DownloadUrl is not { } url) throw new InvalidOperationException("未找到可恢复的图片文件。");
                    var key = string.Empty;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && ImageOutputDownloader.SameOrigin(uri, output.ProviderBaseUrl ?? ""))
                    {
                        var provider = await providers.FindAsync(new ProviderId(job.ProviderId), ct);
                        if (provider is null || !ImageOutputDownloader.SameOrigin(uri, provider.BaseUrl))
                            throw new InvalidOperationException("原服务商地址已变更，无法安全地携带密钥重新下载。");
                        if (provider.ApiKey is not null) key = protector.Unprotect(provider.ApiKey);
                    }
                    file = await downloader.DownloadAsync(url, output.ProviderBaseUrl ?? "", key, output.MediaType, ct, output.StorageOperationKey);
                }
                await jobs.SaveOutputAsync(job, output, file, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidOperationException error) { failure ??= error.Message; }
            catch (IOException) { failure ??= "文件保存未完成，已保留可用检查点；请重新查询或重新下载，不会自动重新生成。"; }
            catch (HttpRequestException) { failure ??= "图片下载连接失败，已有上游产物已保留，可以重新下载。"; }
            catch (OperationCanceledException) { failure ??= "图片下载超时，已有上游产物已保留，可以重新下载。"; }
        }
        await jobs.FinishAsync(job, failure, ct);
    }
}
