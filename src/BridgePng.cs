using System.Buffers.Binary;
using System.IO.Compression;

namespace SpireLink;

internal static class BridgePng
{
    private static ReadOnlySpan<byte> Signature =>
        [137, 80, 78, 71, 13, 10, 26, 10];
    private static ReadOnlySpan<byte> Ihdr => "IHDR"u8;
    private static ReadOnlySpan<byte> Idat => "IDAT"u8;
    private static ReadOnlySpan<byte> Iend => "IEND"u8;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] EncodeRgba8(
        byte[] rgbaBytes,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rgbaBytes);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var rowBytes = checked(width * 4);
        var expectedLength = checked((long)rowBytes * height);
        if (rgbaBytes.LongLength != expectedLength)
        {
            throw new ArgumentException(
                $"RGBA buffer length {rgbaBytes.LongLength} does not match {width}x{height} RGBA8 ({expectedLength}).",
                nameof(rgbaBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(
                compressedStream,
                CompressionLevel.Fastest,
                leaveOpen: true))
            {
                for (var row = 0; row < height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zlib.WriteByte(0); // PNG filter type 0: None.
                    zlib.Write(rgbaBytes, row * rowBytes, rowBytes);
                }
            }

            compressed = compressedStream.ToArray();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream(checked(57 + compressed.Length));
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), (uint)height);
        header[8] = 8; // Bit depth.
        header[9] = 6; // Truecolour with alpha.
        header[10] = 0; // Compression method.
        header[11] = 0; // Filter method.
        header[12] = 0; // No interlace.
        WriteChunk(output, Ihdr, header, cancellationToken);
        WriteChunk(output, Idat, compressed, cancellationToken);
        WriteChunk(output, Iend, ReadOnlySpan<byte>.Empty, cancellationToken);
        return output.ToArray();
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)data.Length));
        output.Write(value);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(value, ComputeCrc(type, data, cancellationToken));
        output.Write(value);
    }

    private static uint ComputeCrc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        var crc = UpdateCrc(uint.MaxValue, type);
        const int blockSize = 64 * 1024;
        for (var offset = 0; offset < data.Length; offset += blockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            crc = UpdateCrc(crc, data.Slice(offset, Math.Min(blockSize, data.Length - offset)));
        }

        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xedb88320U ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
