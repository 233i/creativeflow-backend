using System.Buffers.Binary;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowExternalImageValidatorTests
{
    [Fact]
    public void StreamInspectionMatchesContainerChecksAndPreservesPosition()
    {
        (byte[] Data, string Type)[] cases = [
            (Png(1024, 768), "image/png"), (Png(100, 100, true), "image/png"),
            (Png(100, 100)[..20], "image/png"), (Png(10000, 10000), "image/png"),
            (Jpeg(640, 480), "image/jpeg"), (Jpeg(640, 480)[..8], "image/jpeg"),
            (WebP(1280, 720), "image/webp"), (WebP(1280, 720, true), "image/webp"),
            (WebP(1280, 720)[..25], "image/webp"), ([1, 2, 3], "image/png")];
        foreach (var (data, type) in cases)
        {
            var expected = WorkflowExternalImageValidator.TryInspect(data, type, 16384, 40000000, out var expectedInfo, out _);
            using var stream = new MemoryStream(data);
            var actual = WorkflowExternalImageValidator.TryInspect(stream, type, 16384, 40000000, out var actualInfo, out _, TestContext.Current.CancellationToken);
            Assert.Equal(expected, actual); Assert.Equal(expectedInfo, actualInfo);
            Assert.True(stream.CanRead); Assert.Equal(0, stream.Position);
        }
    }
    /// <summary>回归场景：Reads Png Dimensions Within The Configured Limit。</summary>
    [Fact]
    public void ReadsPngDimensionsWithinTheConfiguredLimit()
    {
        var bytes = Png(1_024, 768);

        var valid = WorkflowExternalImageValidator.TryInspect(
            bytes, "image/png", 4_096, 10_000_000, out var info, out var error);

        Assert.True(valid, error);
        Assert.Equal(new WorkflowExternalImageInfo(1_024, 768), info);
    }

    /// <summary>回归场景：Rejects Pixel Bombs And Truncated Images。</summary>
    [Fact]
    public void RejectsPixelBombsAndTruncatedImages()
    {
        var oversized = WorkflowExternalImageValidator.TryInspect(
            Png(10_000, 10_000), "image/png", 16_384, 40_000_000, out _, out var error);
        var truncated = WorkflowExternalImageValidator.TryInspect(
            Png(100, 100)[..20], "image/png", 16_384, 40_000_000, out _, out _);

        Assert.False(oversized);
        Assert.Contains("pixel", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(truncated);
    }

    /// <summary>回归场景：Rejects Animated Png And Web P Containers。</summary>
    [Fact]
    public void RejectsAnimatedPngAndWebPContainers()
    {
        var png = Png(100, 100, animated: true);
        var webp = WebP(100, 100, animated: true);

        Assert.False(WorkflowExternalImageValidator.TryInspect(
            png, "image/png", 1_000, 1_000_000, out _, out var pngError));
        Assert.False(WorkflowExternalImageValidator.TryInspect(
            webp, "image/webp", 1_000, 1_000_000, out _, out var webpError));
        Assert.Contains("Animated", pngError, StringComparison.Ordinal);
        Assert.Contains("Animated", webpError, StringComparison.Ordinal);
    }

    /// <summary>回归场景：Reads Jpeg And Web P Dimensions。</summary>
    [Fact]
    public void ReadsJpegAndWebPDimensions()
    {
        Assert.True(WorkflowExternalImageValidator.TryInspect(
            Jpeg(640, 480), "image/jpeg", 2_000, 4_000_000, out var jpeg, out _));
        Assert.True(WorkflowExternalImageValidator.TryInspect(
            WebP(1_280, 720), "image/webp", 2_000, 4_000_000, out var webp, out _));

        Assert.Equal(new WorkflowExternalImageInfo(640, 480), jpeg);
        Assert.Equal(new WorkflowExternalImageInfo(1_280, 720), webp);
    }

    /// <summary>回归场景：Png。</summary>
    private static byte[] Png(int width, int height, bool animated = false)
    {
        var chunksLength = 25 + (animated ? 20 : 0) + 12;
        var bytes = new byte[8 + chunksLength];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
        var offset = 8;
        WritePngChunk(bytes, ref offset, "IHDR"u8, data =>
        {
            BinaryPrimitives.WriteUInt32BigEndian(data[..4], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(data.Slice(4, 4), (uint)height);
            data[8] = 8;
            data[9] = 2;
        }, 13);
        if (animated) WritePngChunk(bytes, ref offset, "acTL"u8, _ => { }, 8);
        WritePngChunk(bytes, ref offset, "IEND"u8, _ => { }, 0);
        return bytes;
    }

    /// <summary>回归场景：Write Png Chunk。</summary>
    private static void WritePngChunk(
        byte[] target,
        ref int offset,
        ReadOnlySpan<byte> type,
        Action<Span<byte>> writeData,
        int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), (uint)length);
        type.CopyTo(target.AsSpan(offset + 4, 4));
        writeData(target.AsSpan(offset + 8, length));
        offset += 12 + length;
    }

    /// <summary>回归场景：Jpeg。</summary>
    private static byte[] Jpeg(int width, int height) =>
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x0b, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xff, 0xd9,
    ];

    /// <summary>回归场景：Web P。</summary>
    private static byte[] WebP(int width, int height, bool animated = false)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 22);
        "WEBPVP8X"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 10);
        bytes[20] = animated ? (byte)0x02 : (byte)0;
        WriteUInt24LittleEndian(bytes.AsSpan(24, 3), width - 1);
        WriteUInt24LittleEndian(bytes.AsSpan(27, 3), height - 1);
        return bytes;
    }

    /// <summary>回归场景：Write U Int24 Little Endian。</summary>
    private static void WriteUInt24LittleEndian(Span<byte> bytes, int value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }
}
