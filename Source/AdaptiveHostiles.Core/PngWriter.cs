using System.IO.Compression;

namespace AdaptiveHostiles.Core;

/// <summary>Minimal 8-bit palette PNG encoder: enough to send a small cell map as an image.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// <paramref name="cells"/> holds one palette index per cell, row by row from the top; every cell becomes a
    /// <paramref name="scale"/> x <paramref name="scale"/> block of pixels.
    /// </summary>
    public static byte[] EncodeIndexed(byte[] cells, int width, int height, int scale, IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        if (cells.Length != width * height) throw new ArgumentException("cells must be width * height", nameof(cells));
        if (scale < 1 || palette.Count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(scale));

        var pixelWidth = width * scale;
        var raw = new byte[(pixelWidth + 1) * height * scale];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            var rowStart = at;
            raw[at++] = 0; // filter: none
            for (var x = 0; x < width; x++)
                for (var s = 0; s < scale; s++) raw[at++] = cells[y * width + x];
            for (var s = 1; s < scale; s++)
            {
                Buffer.BlockCopy(raw, rowStart, raw, at, pixelWidth + 1);
                at += pixelWidth + 1;
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)pixelWidth);
        WriteBigEndian(header, 4, (uint)(height * scale));
        header[8] = 8; // bit depth
        header[9] = 3; // colour type: palette
        WriteChunk(output, "IHDR", header);

        var plte = new byte[palette.Count * 3];
        for (var i = 0; i < palette.Count; i++)
        {
            plte[i * 3] = palette[i].R;
            plte[i * 3 + 1] = palette[i].G;
            plte[i * 3 + 2] = palette[i].B;
        }
        WriteChunk(output, "PLTE", plte);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typed, 4, data.Length);
        output.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc(typed));
        output.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
