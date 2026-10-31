using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SpireLink;

var cases = new[]
{
    new TestImage(2, 2,
    [
        255, 0, 0, 255, 0, 255, 0, 128,
        0, 0, 255, 0, 255, 255, 255, 64
    ]),
    new TestImage(1, 3,
    [
        1, 2, 3, 4,
        10, 20, 30, 40,
        250, 240, 230, 220
    ])
};

var assertions = 0;
foreach (var testImage in cases)
{
    var png = BridgePng.EncodeRgba8(testImage.Rgba, testImage.Width, testImage.Height);
    Check(png.AsSpan(0, 8).SequenceEqual((byte[])[137, 80, 78, 71, 13, 10, 26, 10]), "Invalid PNG signature.");

    var chunks = ReadChunks(png);
    Check(chunks.Select(chunk => chunk.Type).SequenceEqual(["IHDR", "IDAT", "IEND"]), "Unexpected PNG chunks.");
    foreach (var chunk in chunks)
    {
        Check(chunk.Crc == ComputeCrc(chunk.Type, chunk.Data), $"Invalid CRC for {chunk.Type}.");
    }

    var header = chunks[0].Data;
    Check(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) == testImage.Width, "Incorrect width.");
    Check(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) == testImage.Height, "Incorrect height.");
    Check(header.AsSpan(8).SequenceEqual((byte[])[8, 6, 0, 0, 0]), "Incorrect RGBA8 IHDR fields.");

    using var compressed = new MemoryStream(chunks.Single(chunk => chunk.Type == "IDAT").Data);
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    using var scanlines = new MemoryStream();
    zlib.CopyTo(scanlines);
    var decoded = scanlines.ToArray();
    var rowBytes = testImage.Width * 4;
    Check(decoded.Length == (rowBytes + 1) * testImage.Height, "Incorrect decompressed scanline length.");
    for (var row = 0; row < testImage.Height; row++)
    {
        Check(decoded[row * (rowBytes + 1)] == 0, "Expected PNG filter type 0.");
        Check(decoded.AsSpan(row * (rowBytes + 1) + 1, rowBytes)
            .SequenceEqual(testImage.Rgba.AsSpan(row * rowBytes, rowBytes)), "RGBA pixels changed during encoding.");
    }
}

try
{
    BridgePng.EncodeRgba8(new byte[15], 2, 2);
    throw new Exception("Wrong RGBA length was accepted.");
}
catch (ArgumentException)
{
    assertions++;
}

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        BridgePng.EncodeRgba8(new byte[4], 1, 1, cancelled.Token);
        throw new Exception("Cancelled PNG encoding completed.");
    }
    catch (OperationCanceledException)
    {
        assertions++;
    }
}

Console.WriteLine($"PNG tests passed: {assertions} assertions");

void Check(bool condition, string message)
{
    assertions++;
    if (!condition)
    {
        throw new Exception(message);
    }
}

static List<PngChunk> ReadChunks(byte[] png)
{
    var chunks = new List<PngChunk>();
    var offset = 8;
    while (offset < png.Length)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
        offset += 4;
        var type = Encoding.ASCII.GetString(png, offset, 4);
        offset += 4;
        var data = png.AsSpan(offset, length).ToArray();
        offset += length;
        var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        offset += 4;
        chunks.Add(new PngChunk(type, data, crc));
    }

    if (offset != png.Length)
    {
        throw new Exception("PNG chunk lengths exceed the file.");
    }

    return chunks;
}

static uint ComputeCrc(string type, byte[] data)
{
    var crc = uint.MaxValue;
    foreach (var value in Encoding.ASCII.GetBytes(type).Concat(data))
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }
    }

    return ~crc;
}

sealed record TestImage(int Width, int Height, byte[] Rgba);
sealed record PngChunk(string Type, byte[] Data, uint Crc);
