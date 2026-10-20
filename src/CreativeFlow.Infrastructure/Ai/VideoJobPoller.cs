using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>
/// 推进正在执行的视频任务。
/// </summary>
/// <remarks>
/// 轮询在服务器端而非浏览器中进行，使任务能在启动它的标签页关闭后继续存在：
/// 用户可关闭页面，稍后返回时视频已在等待。客户端只轮询成本较低的本地记录，
/// 而无需保持连接数分钟。
/// <para>
/// 单实例假设：若有多个 API 实例，每个实例都会轮询同一任务。要安全横向扩展，需要数据行锁
/// （<c>FOR UPDATE SKIP LOCKED</c>）或领导者选举；实际部署副本前无需承担这些复杂度。
/// </para>
/// </remarks>
internal sealed class VideoJobPoller(
    IServiceScopeFactory scopeFactory,
    ILogger<VideoJobPoller> logger) : BackgroundService
{
    /// <summary>供应商渲染需要数分钟，因此紧密循环只会浪费配额。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 超过此时长未更新的任务会被标记为失败。用于限制供应商接受任务后再也不报告状态的情况，
    /// 否则该记录会永久轮询。
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>每次循环推进的任务数量，防止一个繁忙设备饿死其他设备。</summary>
    private const int BatchSize = 10;

    /// <summary>按原轮询周期推进活动视频任务，记录单轮异常后继续运行，宿主取消时停止。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // 绝不能让一次失败循环终止整个轮询；否则正在推进的任务会挂起到进程重启。
                logger.LogError(exception, "Video job poll failed; continuing.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>扫描并推进一批视频任务，为每个任务恢复其当前账号范围。</summary>
    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IVideoJobRepository>();

        var failed = await jobs.FailStaleAsync(DateTimeOffset.UtcNow - StaleAfter, ct);
        if (failed > 0)
        {
            logger.LogWarning("Failed {Count} stale video jobs.", failed);
        }

        var active = await jobs.ListActiveAsync(BatchSize, ct);
        if (active.Count == 0)
        {
            return;
        }

        var providers = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var accountScope = scope.ServiceProvider.GetRequiredService<BackgroundAccountScope>();
        var protector = scope.ServiceProvider.GetRequiredService<IApiKeyProtector>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IVideoGenerationDispatcher>();
        var assetStore = scope.ServiceProvider.GetRequiredService<IAssetStore>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        foreach (var job in active)
        {
            // 轮询在提交视频的认证请求之外运行。恢复当前拥有该任务设备的账户，
            // 使关联设备的供应商仍然可见。下一任务属于访客时，辅助方法也会清除上一账户。
            await accountScope.RestoreForDeviceAsync(job.DeviceId, ct);

            await AdvanceAsync(
                job, jobs, providers, protector, dispatcher, assetStore,
                httpClientFactory, ct);
        }
    }

    /// <summary>推进单个视频任务并在成功后按原顺序保存视频和元数据。</summary>
    private async Task AdvanceAsync(
        VideoJob job,
        IVideoJobRepository jobs,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IVideoGenerationDispatcher dispatcher,
        IAssetStore assetStore,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var operationKey = $"video-job:{job.JobId:N}";
        if (await assetStore.FindSavedAsync(operationKey, ct) is { } recovered)
        {
            await jobs.CompleteFileAsync(job, recovered, ct);
            return;
        }
        if (job.UpstreamJobId is null || job.ProviderId is null)
        {
            await FailAsync(jobs, job, "The job lost its provider association.", ct);
            return;
        }

        var provider = await providers.FindAsync(new ProviderId(job.ProviderId.Value), ct);

        if (provider is null)
        {
            await FailAsync(jobs, job, "The provider was deleted while the job was running.", ct);
            return;
        }

        string apiKey;
        try
        {
            apiKey = provider.ApiKey is null ? string.Empty : protector.Unprotect(provider.ApiKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await FailAsync(jobs, job, "The stored API key could not be decrypted.", ct);
            return;
        }

        var model = provider.Models.FirstOrDefault(m => m.ModelKey == job.ModelKey);
        var protocol = provider.ResolveProtocol(model);

        var result = await dispatcher.PollAsync(
            protocol, provider, apiKey, job.UpstreamJobId, ct);

        switch (result.State)
        {
            case VideoJobState.Succeeded when result.VideoUrl is { } videoUrl:
            {
                // 供应商 URL 有效期较短，因此立即下载字节，而不是交给客户端稍后获取。
                var stored = await DownloadAsync(
                    httpClientFactory, videoUrl, provider.BaseUrl, apiKey, assetStore, operationKey, ct);

                if (stored is null)
                {
                    await FailAsync(jobs, job, "The finished video could not be downloaded.", ct);
                    return;
                }

                await jobs.CompleteFileAsync(job, stored, ct);

                logger.LogInformation("Video job {JobId} completed.", job.JobId);
                break;
            }

            case VideoJobState.Failed:
                await FailAsync(jobs, job, result.Error ?? "Generation failed.", ct);
                break;

            default:
                // 仍在工作。刷新时间戳，防止过期扫描清除正在推进的任务。
                await jobs.UpdateAsync(
                    job with
                    {
                        Status = VideoJobStatus.Running,
                        Progress = result.Progress ?? job.Progress,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    ct);
                break;
        }
    }

    /// <summary>持久化视频任务失败状态并通知所属设备。</summary>
    private static async Task FailAsync(
        IVideoJobRepository jobs,
        VideoJob job,
        string error,
        CancellationToken ct)
    {
        await jobs.UpdateAsync(
            job with
            {
                Status = VideoJobStatus.Failed,
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            ct);
    }

    /// <summary>下载上游生成的视频，保留来源校验及凭据附加规则。</summary>
    private async Task<StoredAsset?> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        string url,
        string providerBaseUrl,
        string apiKey,
        IAssetStore assetStore,
        string operationKey,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("upstream");

            using var request = BuildDownloadRequest(url, providerBaseUrl, apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Video download returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "video/mp4";
            const long maxBytes = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Video output exceeds the file limit.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            return await assetStore.SaveAsync(input, mediaType, ".mp4", maxBytes, ct, operationKey);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(exception, "Video download failed.");
            return null;
        }
    }

    /// <summary>
    /// 为成品视频创建下载请求。只在下载地址与供应商 Base URL 同源时携带密钥。
    /// </summary>
    /// <remarks>
    /// 一些 OpenAI 兼容供应商通过自身的 <c>/videos/{id}/content</c> 路由返回视频，
    /// 该路由仍需 Bearer Token；另一些供应商返回无需鉴权的 CDN 预签名地址。
    /// 比较 scheme、host 和有效端口，而不根据域名文本特征猜测，可以同时兼容两者并避免向第三方泄露密钥。
    /// </remarks>
    internal static HttpRequestMessage BuildDownloadRequest(
        string url,
        string providerBaseUrl,
        string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (IsSameOrigin(url, providerBaseUrl))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    /// <summary>比较协议、主机和端口，判断两个地址是否同源。</summary>
    private static bool IsSameOrigin(string resourceUrl, string providerBaseUrl) =>
        Uri.TryCreate(resourceUrl, UriKind.Absolute, out var resource)
        && Uri.TryCreate(providerBaseUrl, UriKind.Absolute, out var provider)
        && IsHttpScheme(resource.Scheme)
        && IsHttpScheme(provider.Scheme)
        && string.Equals(resource.Scheme, provider.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(resource.IdnHost, provider.IdnHost, StringComparison.OrdinalIgnoreCase)
        && resource.Port == provider.Port;

    /// <summary>判断地址是否采用 HTTP 或 HTTPS 协议。</summary>
    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
