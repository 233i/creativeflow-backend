using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>本机 FFmpeg 生成小图并缓存；有限并发、有限时长、不允许解码器访问网络。</summary>
internal sealed class AssetPreviewService(NpgsqlDataSource dataSource, IAssetStore store, ILogger<AssetPreviewService> logger) : IAssetPreviewService
{
    private readonly SemaphoreSlim slots = new(2, 2);

    public async Task<Stream?> OpenThumbnailAsync(AssetContent asset, CancellationToken ct)
    {
        if (await OpenCachedAsync(asset.Id, ct) is { } cached) return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var acquired = false;
        var directory = Path.Combine(Path.GetTempPath(), "creativeflow-asset-preview-" + Guid.NewGuid().ToString("N"));
        StoredAsset? preview = null;
        try
        {
            await slots.WaitAsync(deadline.Token); acquired = true;
            if (await OpenCachedAsync(asset.Id, deadline.Token) is { } ready) return ready;
            if (asset.ByteSize > 512L * 1024 * 1024) return null;
            Directory.CreateDirectory(directory); var input = Path.Combine(directory, "media"); var output = Path.Combine(directory, "preview.jpg");
            await using (var original = await store.OpenReadAsync(asset.StoragePath, deadline.Token))
            {
                if (original is null) return null;
                await using var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                var buffer = new byte[131072]; long copied = 0; int count;
                while ((count = await original.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    copied += count; if (copied > 512L * 1024 * 1024) return null;
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
            }
            var probe = await RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "stream=width,height:format=duration", "-of", "json", input], deadline.Token);
            var parsed = JsonNode.Parse(probe); var stream = (parsed?["streams"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["width"] is not null);
            int? width = stream?["width"]?.GetValue<int>(); int? height = stream?["height"]?.GetValue<int>();
            double? duration = double.TryParse(parsed?["format"]?["duration"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) ? seconds : null;
            if (width is null || height is null || width > 32768 || height > 32768 || (long)width * height > 100_000_000) return null;
            await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-max_alloc", "268435456", "-protocol_whitelist", "file,pipe", "-i", input, "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1", "-vf", "scale=480:480:force_original_aspect_ratio=decrease", "-q:v", "4", output], deadline.Token);
            var info = new FileInfo(output); if (!info.Exists || info.Length is <= 0 or > 2097152) return null;
            await using (var thumbnail = File.OpenRead(output))
                preview = await store.SaveAsync(thumbnail, "image/jpeg", ".jpg", 2097152, deadline.Token);
            await using var inputFile = File.OpenRead(input); var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(inputFile, deadline.Token));
            await using var connection = await dataSource.OpenConnectionAsync(deadline.Token); await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT pg_advisory_xact_lock_shared(721909190);
                INSERT INTO asset_preview(asset_id,storage_path,byte_size)
                SELECT asset_id,@Path,@Bytes FROM canvas_asset WHERE asset_id=@Id AND purged_at IS NULL AND NOT purge_pending
                ON CONFLICT DO NOTHING;
                UPDATE canvas_asset SET width=@Width,height=@Height,duration=@Duration,sha256=@Hash
                WHERE asset_id=@Id AND purged_at IS NULL AND NOT purge_pending;
                """;
            command.Parameters.AddWithValue("Id", asset.Id); command.Parameters.AddWithValue("Path", preview.StoragePath); command.Parameters.AddWithValue("Bytes", preview.ByteSize);
            command.Parameters.AddWithValue("Width", NpgsqlDbType.Integer, (object?)width ?? DBNull.Value); command.Parameters.AddWithValue("Height", NpgsqlDbType.Integer, (object?)height ?? DBNull.Value);
            command.Parameters.AddWithValue("Duration", NpgsqlDbType.Double, (object?)duration ?? DBNull.Value); command.Parameters.AddWithValue("Hash", hash);
            await command.ExecuteNonQueryAsync(deadline.Token); await transaction.CommitAsync(deadline.Token);
            var final = await CachedAsync(asset.Id, ct);
            if (final != preview.StoragePath) await store.DeleteAsync(preview.StoragePath, CancellationToken.None);
            preview = null; return final is null ? null : await store.OpenReadAsync(final, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning(exception, "Could not create thumbnail for asset {Id}", asset.Id); return null; }
        finally
        {
            if (preview is not null)
                try { await store.DeleteAsync(preview.StoragePath, CancellationToken.None); } catch (Exception exception) { logger.LogWarning(exception, "Could not release unfinished thumbnail."); }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException exception) { logger.LogWarning(exception, "Could not clean temporary thumbnail files."); }
            if (acquired) slots.Release();
        }
    }

    /// <summary>缩略图字节丢失时只失效对应缓存，下一次受限生成可恢复预览而不重跑模型。</summary>
    private async Task<Stream?> OpenCachedAsync(Guid id, CancellationToken ct)
    {
        var path = await CachedAsync(id, ct);
        if (path is null) return null;
        var stream = await store.OpenReadAsync(path, ct);
        if (stream is not null) return stream;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM asset_preview WHERE asset_id=@Id AND storage_path=@Path";
        command.Parameters.AddWithValue("Id", id); command.Parameters.AddWithValue("Path", path);
        await command.ExecuteNonQueryAsync(ct);
        return null;
    }

    private async Task<string?> CachedAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.storage_path FROM asset_preview p JOIN canvas_asset a USING(asset_id) WHERE p.asset_id=@Id AND a.purged_at IS NULL AND NOT a.purge_pending"; command.Parameters.AddWithValue("Id", id);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>参数通过 ArgumentList 传递；进程超时后整树终止，输出读取也限制大小。</summary>
    private static async Task<string> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("Media preview process unavailable.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, ct); var error = ReadBoundedAsync(process.StandardError, ct);
            await Task.WhenAll(process.WaitForExitAsync(ct), output, error);
            if (process.ExitCode != 0) throw new InvalidDataException("Media preview could not be decoded.");
            return await output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0) { if (result.Length + count > 1048576) throw new InvalidDataException("Media probe output exceeded its limit."); result.Append(buffer, 0, count); }
        return result.ToString();
    }
}
