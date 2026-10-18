using System.Buffers.Binary;
using System.IO.Compression;

namespace DarkMessiah.Import.Images;

/// <summary>An 8-bit RGBA image, rows top to bottom.</summary>
public sealed record Rgba(int Width, int Height, byte[] Pixels)
{
    public bool HasAlpha()
    {
        for (int i = 3; i < Pixels.Length; i += 4) if (Pixels[i] != 255) return true;
        return false;
    }
}

/// <summary>Writes RGBA images as PNG (one IDAT, "up" filter on every row).</summary>
public static class Png
{
    static readonly uint[] Crc = BuildCrc();

    static uint[] BuildCrc()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static byte[] Encode(Rgba image, bool alpha)
    {
        int channels = alpha ? 4 : 3, stride = image.Width * channels;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + stride];
            var previous = new byte[stride];
            var current = new byte[stride];
            for (int y = 0; y < image.Height; y++)
            {
                int src = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++)
                    for (int c = 0; c < channels; c++)
                        current[x * channels + c] = image.Pixels[src + x * 4 + c];
                row[0] = 2; // up
                for (int i = 0; i < stride; i++) row[1 + i] = (byte)(current[i] - previous[i]);
                z.Write(row);
                (previous, current) = (current, previous);
            }
        }

        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = (byte)(alpha ? 6 : 2);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        s.Write(typed);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in typed) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, c ^ 0xFFFFFFFFu);
        s.Write(word);
    }
}
