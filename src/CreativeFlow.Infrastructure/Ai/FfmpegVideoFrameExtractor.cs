using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>
/// 使用 FFmpeg 提取最后一个可解码视频帧。当前视频上限为十五秒，因此解码到末尾既精确，
/// 也比维护时长和关键帧探测逻辑成本更低；后者仍可能漏掉末尾的 B 帧。
/// </summary>
internal sealed class FfmpegVideoFrameExtractor(
    ILogger<FfmpegVideoFrameExtractor> logger) : IVideoFrameExtractor
{
    /// <summary>当前读取或脚本输出允许的最大字节数。</summary>
    private const int MaximumOutputBytes = 32 * 1024 * 1024;
    /// <summary>FFmpeg 抽帧允许的最大等待时长。</summary>
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(45);
    /// <summary>限制本进程最多同时执行两个 FFmpeg 抽帧任务的信号量。</summary>
    private readonly SemaphoreSlim _slots = new(2, 2);

    /// <summary>在并发槽位与超时限制内使用 FFmpeg 提取视频尾帧为 PNG，完成或失败后清理临时文件。</summary>
    public async Task<byte[]> ExtractLastFrameAsync(Stream source, CancellationToken ct)
    {
        // 抽帧使用隔离的临时文件与有限执行时间；进程输出、取消和临时文件释放都要走当前清理路径。
        await _slots.WaitAsync(ct);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"creativeflow-video-frame-{Guid.CreateVersion7():N}");
        var inputPath = Path.Combine(directory, "source.video");
        var outputPath = Path.Combine(directory, "last-frame.png");

        try
        {
            Directory.CreateDirectory(directory);
            await using (var input = new FileStream(
                             inputPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(input, ct);
            }

            using var process = new Process
            {
                StartInfo = BuildStartInfo(inputPath, outputPath),
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg could not be started.");
                }
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "FFmpeg is unavailable on the media server.", exception);
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ExtractionTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                _ = await stderrTask;
                throw new InvalidOperationException("Video frame extraction timed out.");
            }
            catch
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                _ = await stderrTask;
                throw;
            }

            var stderr = await stderrTask;
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                logger.LogWarning(
                    "FFmpeg failed to extract a video frame with exit code {ExitCode}: {Error}",
                    process.ExitCode,
                    TrimError(stderr));
                throw new InvalidDataException("The stored video could not be decoded.");
            }

            var info = new FileInfo(outputPath);
            if (info.Length is <= 8 or > MaximumOutputBytes)
            {
                throw new InvalidDataException("The extracted video frame has an invalid size.");
            }

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            if (!IsPng(bytes))
            {
                throw new InvalidDataException("FFmpeg returned an invalid frame image.");
            }

            return bytes;
        }
        finally
        {
            TryDelete(directory);
            _slots.Release();
        }
    }

    /// <summary>构造受限的 FFmpeg 子进程参数，不通过 shell 拼接执行。</summary>
    private static ProcessStartInfo BuildStartInfo(string inputPath, string outputPath)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // image2 的更新模式会为每个解码帧重写同一 PNG。FFmpeg 到达文件末尾后，
        // 该文件即包含精确的最终显示帧，无需依赖不精确的时长元数据或仅关键帧定位。
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-i", inputPath,
                     "-map", "0:v:0", "-an", "-sn", "-dn",
                     "-fps_mode", "passthrough", "-update", "1", outputPath,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>终止尚未退出的子进程，并容忍其已经结束的竞争。</summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程在 HasExited 检查与 Kill 调用之间退出。
        }
    }

    /// <summary>按原有清理策略尝试删除临时文件。</summary>
    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not remove temporary video-frame directory.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not remove temporary video-frame directory.");
        }
    }

    /// <summary>根据签名字节检查 FFmpeg 输出是否为 PNG。</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    /// <summary>限制外部进程错误文本的长度。</summary>
    private static string TrimError(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length <= 1_000 ? trimmed : trimmed[..1_000];
    }
}
