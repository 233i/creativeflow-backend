using System.Buffers.Binary;

namespace CreativeFlow.Application.Features.Workflow;

public static partial class WorkflowExternalImageValidator
{
    /// <summary>检查上传缓冲流的容器结构，跳过像素正文；固定内存，保留动画、截断和尺寸校验。</summary>
    public static bool TryInspect(Stream stream, string mediaType, int maxDimension, long maxPixels,
        out WorkflowExternalImageInfo? info, out string error, CancellationToken ct)
    {
        info = null; error = string.Empty;
        if (!stream.CanSeek) { error = "The upload must be buffered before validation."; return false; }
        var start = stream.Position;
        try
        {
            // ASP.NET Core 的表单文件已经采用有界内存/磁盘缓冲，这里不再复制整份上传。
            using var buffered = new BufferedStream(new LeaveOpenStream(stream), 8192);
            info = mediaType switch
            {
                "image/png" => InspectPngStream(buffered, ct),
                "image/jpeg" => InspectJpegStream(buffered, ct),
                "image/webp" => InspectWebPStream(buffered, ct),
                _ => null,
            };
            if (info is null || info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("The image file is malformed or unsupported.");
            if (info.Width > maxDimension || info.Height > maxDimension || (long)info.Width * info.Height > maxPixels)
                throw new InvalidDataException("The image dimensions or total pixel count exceed the upload limit.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            info = null; error = ex is InvalidDataException ? ex.Message : "The image file is malformed or truncated.";
            return false;
        }
        finally { stream.Position = start; }
    }

    private static WorkflowExternalImageInfo? InspectPngStream(Stream s, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[13];
        s.ReadExactly(header[..8]); if (!header[..8].SequenceEqual(PngSignature)) return null;
        WorkflowExternalImageInfo? info = null;
        while (s.Position <= s.Length - 12)
        {
            ct.ThrowIfCancellationRequested();
            var offset = s.Position;
            s.ReadExactly(header[..8]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var end = s.Position + size + 4;
            if (end > s.Length || size > int.MaxValue) return null;
            if (header.Slice(4, 4).SequenceEqual("IHDR"u8))
            {
                if (info is not null || size != 13 || offset != 8) return null;
                s.ReadExactly(header);
                info = new(checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[..4])), checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4))));
            }
            else if (header.Slice(4, 4).SequenceEqual("acTL"u8)) throw new InvalidDataException("Animated images are not supported.");
            else if (header.Slice(4, 4).SequenceEqual("IEND"u8)) return size == 0 ? info : null;
            s.Position = end;
        }
        return null;
    }

    private static WorkflowExternalImageInfo? InspectJpegStream(Stream s, CancellationToken ct)
    {
        if (s.ReadByte() != 0xff || s.ReadByte() != 0xd8) return null;
        WorkflowExternalImageInfo? info = null;
        Span<byte> header = stackalloc byte[7];
        while (s.Position < s.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (s.ReadByte() != 0xff) return null;
            int marker; do { marker = s.ReadByte(); } while (marker == 0xff);
            if (marker < 0) return null;
            if (marker == 0xd9) return info;
            if (marker == 0xda)
            {
                Span<byte> block = stackalloc byte[8192];
                var previous = -1;
                int count;
                while ((count = s.Read(block)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (var i = 0; i < count; i++)
                    {
                        if (previous == 0xff && block[i] == 0xd9) return info;
                        previous = block[i];
                    }
                }
                return null;
            }
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            var offset = s.Position;
            s.ReadExactly(header[..2]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
            if (size < 2 || offset + size > s.Length) return null;
            if (IsStartOfFrame((byte)marker))
            {
                if (size < 7) return null;
                s.ReadExactly(header[..5]);
                info = new(BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3, 2)), BinaryPrimitives.ReadUInt16BigEndian(header.Slice(1, 2)));
            }
            s.Position = offset + size;
        }
        return null;
    }

    private static WorkflowExternalImageInfo? InspectWebPStream(Stream s, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[12];
        s.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8)
            || (long)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) + 8 != s.Length) return null;
        WorkflowExternalImageInfo? info = null;
        while (s.Position <= s.Length - 8)
        {
            ct.ThrowIfCancellationRequested();
            s.ReadExactly(header[..8]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            var end = s.Position + size + (size & 1);
            if (end > s.Length || size > int.MaxValue) return null;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            if (header[..4].SequenceEqual("ANIM"u8) || header[..4].SequenceEqual("ANMF"u8)) throw new InvalidDataException("Animated images are not supported.");
            if (type == BinaryPrimitives.ReadUInt32LittleEndian("VP8X"u8))
            {
                if (size < 10) return null;
                s.ReadExactly(header[..10]);
                if ((header[0] & 2) != 0) throw new InvalidDataException("Animated images are not supported.");
                info = new(1 + ReadUInt24LittleEndian(header.Slice(4, 3)), 1 + ReadUInt24LittleEndian(header.Slice(7, 3)));
            }
            else if (info is null && type == BinaryPrimitives.ReadUInt32LittleEndian("VP8 "u8))
            {
                if (size < 10) return null;
                s.ReadExactly(header[..10]);
                if (!header.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a })) return null;
                info = new(BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2)) & 0x3fff);
            }
            else if (info is null && type == BinaryPrimitives.ReadUInt32LittleEndian("VP8L"u8))
            {
                if (size < 5) return null;
                s.ReadExactly(header[..5]); if (header[0] != 0x2f) return null;
                info = new(1 + header[1] + ((header[2] & 0x3f) << 8), 1 + (header[2] >> 6) + (header[3] << 2) + ((header[4] & 0x0f) << 10));
            }
            s.Position = end;
        }
        return info;
    }

    /// <summary>校验缓冲器的释放不关闭调用方仍要用于保存的表单流。</summary>
    private sealed class LeaveOpenStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
