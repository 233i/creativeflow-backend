using System.Buffers.Binary;

namespace CreativeFlow.Application.Features.Workflow;

/// <param name="Width">图片或画面宽度，单位为像素。</param>
/// <param name="Height">图片或画面高度，单位为像素。</param>
public sealed record WorkflowExternalImageInfo(int Width, int Height);

/// <summary>
/// Reads the structural metadata of supported external image containers before storing them.
/// This rejects truncated files, animated WebP/PNG payloads and dimensions that could expand into
/// an unreasonable amount of memory. The caller still keeps the byte-size and MIME allowlists.
/// </summary>
public static partial class WorkflowExternalImageValidator
{
    /// <summary>PNG 文件格式的固定签名字节。</summary>
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>检查图片格式与尺寸边界，返回可接受的实际图像元数据。</summary>
    public static bool TryInspect(
        ReadOnlySpan<byte> bytes,
        string mediaType,
        int maxDimension,
        long maxPixels,
        out WorkflowExternalImageInfo? info,
        out string error)
    {
        // 媒体类型、签名与图片尺寸共同决定是否可接受，不能仅凭上传文件名或声明类型信任内容。
        info = null;
        error = string.Empty;
        var parsed = mediaType switch
        {
            "image/png" => InspectPng(bytes, out info, out error),
            "image/jpeg" => InspectJpeg(bytes, out info, out error),
            "image/webp" => InspectWebP(bytes, out info, out error),
            _ => false,
        };

        if (!parsed)
        {
            if (error.Length == 0) error = "The image file is malformed or unsupported.";
            info = null;
            return false;
        }

        if (info is null || info.Width <= 0 || info.Height <= 0)
        {
            error = "The image dimensions could not be read.";
            info = null;
            return false;
        }

        if (info.Width > maxDimension || info.Height > maxDimension
            || (long)info.Width * info.Height > maxPixels)
        {
            error = "The image dimensions or total pixel count exceed the upload limit.";
            info = null;
            return false;
        }

        return true;
    }

    /// <summary>读取并验证 PNG 结构中的图像尺寸。</summary>
    private static bool InspectPng(
        ReadOnlySpan<byte> bytes,
        out WorkflowExternalImageInfo? info,
        out string error)
    {
        // 读取 PNG 头和分块之前逐项检查长度，避免截断或伪造的块导致越界读取。
        info = null;
        error = string.Empty;
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(PngSignature)) return false;

        var offset = 8;
        var sawHeader = false;
        var sawEnd = false;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || (long)offset + 12 + length > bytes.Length) return false;
            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, (int)length);

            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || length != 13 || offset != 8) return false;
                var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (rawWidth > int.MaxValue || rawHeight > int.MaxValue) return false;
                info = new WorkflowExternalImageInfo((int)rawWidth, (int)rawHeight);
                sawHeader = true;
            }
            else if (type.SequenceEqual("acTL"u8))
            {
                error = "Animated images are not supported.";
                return false;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0) return false;
                sawEnd = true;
                break;
            }

            offset += checked(12 + (int)length);
        }

        return sawHeader && sawEnd;
    }

    /// <summary>遍历 JPEG 标记读取图像尺寸并检查必要结束标记。</summary>
    private static bool InspectJpeg(
        ReadOnlySpan<byte> bytes,
        out WorkflowExternalImageInfo? info,
        out string error)
    {
        // JPEG 尺寸来自有效帧标记；跳过其他分段时仍需验证其长度与剩余字节一致。
        info = null;
        error = string.Empty;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;

        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker == 0xd9) return info is not null;
            if (marker == 0xda)
                return info is not null && ContainsJpegEnd(bytes[offset..]);
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset > bytes.Length - 2) return false;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) return false;
            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7) return false;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                info = new WorkflowExternalImageInfo(width, height);
            }
            offset += segmentLength;
        }

        return false;
    }

    /// <summary>按 WebP 编码变体解析尺寸并检查容器边界。</summary>
    private static bool InspectWebP(
        ReadOnlySpan<byte> bytes,
        out WorkflowExternalImageInfo? info,
        out string error)
    {
        // 分别处理 WebP 编码头中的尺寸表示，并检查容器长度，不能把不同编码布局混用。
        info = null;
        error = string.Empty;
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return false;
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if ((long)declaredLength + 8 != bytes.Length) return false;

        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var paddedLength = (long)length + (length & 1);
            if (length > int.MaxValue || (long)offset + 8 + paddedLength > bytes.Length) return false;
            var data = bytes.Slice(offset + 8, (int)length);

            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                error = "Animated images are not supported.";
                return false;
            }
            if (type.SequenceEqual("VP8X"u8))
            {
                if (data.Length < 10) return false;
                if ((data[0] & 0x02) != 0)
                {
                    error = "Animated images are not supported.";
                    return false;
                }
                info = new WorkflowExternalImageInfo(
                    1 + ReadUInt24LittleEndian(data.Slice(4, 3)),
                    1 + ReadUInt24LittleEndian(data.Slice(7, 3)));
            }
            else if (type.SequenceEqual("VP8 "u8) && info is null)
            {
                if (data.Length < 10 || data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a)
                    return false;
                info = new WorkflowExternalImageInfo(
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)) & 0x3fff);
            }
            else if (type.SequenceEqual("VP8L"u8) && info is null)
            {
                if (data.Length < 5 || data[0] != 0x2f) return false;
                var width = 1 + data[1] + ((data[2] & 0x3f) << 8);
                var height = 1 + (data[2] >> 6) + (data[3] << 2) + ((data[4] & 0x0f) << 10);
                info = new WorkflowExternalImageInfo(width, height);
            }

            offset += checked(8 + (int)length + ((int)length & 1));
        }

        return info is not null;
    }

    /// <summary>识别包含 JPEG 尺寸信息的帧起始标记。</summary>
    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
        or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    /// <summary>检查 JPEG 数据是否具有有效结束标记。</summary>
    private static bool ContainsJpegEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length - 1; index++)
        {
            if (bytes[index] == 0xff && bytes[index + 1] == 0xd9) return true;
        }
        return false;
    }

    /// <summary>按小端顺序读取 WebP 使用的 24 位整数。</summary>
    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
