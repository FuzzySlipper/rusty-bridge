using System.Buffers.Binary;
using DarkMessiah.Import.Images;

namespace DarkMessiah.Import.Source;

/// <summary>
/// Valve texture files, versions 7.0 to 7.2 (what Dark Messiah ships). Reads
/// one mip of the first frame, face and slice into RGBA.
/// </summary>
public static class Vtf
{
    const uint EnvMapFlag = 0x4000;

    public static bool TryDecode(byte[] b, int maxSize, out Rgba image, out string? problem)
    {
        image = null!;
        problem = null;
        if (b.Length < 64 || b[0] != 'V' || b[1] != 'T' || b[2] != 'F' || b[3] != 0) { problem = "not a VTF"; return false; }
        uint major = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)), minor = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        if (major != 7 || minor > 2) { problem = $"VTF {major}.{minor}"; return false; }
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16)), height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));
        int frames = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24)));
        int format = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(52));
        int mips = b[56];
        int lowFormat = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(57));
        int lowW = b[61], lowH = b[62];
        int depth = minor >= 2 ? Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(63))) : 1;
        if ((flags & EnvMapFlag) != 0) { problem = "cube map"; return false; }
        if (Bytes(format, 1, 1) < 0) { problem = $"pixel format {format}"; return false; }

        long at = headerSize + (lowFormat >= 0 && lowW > 0 && lowH > 0 ? Bytes(lowFormat, lowW, lowH) : 0);
        int mip = 0;
        while (mip < mips - 1 && Math.Max(width >> mip, height >> mip) > maxSize) mip++;
        for (int m = mips - 1; m > mip; m--)
            at += (long)Bytes(format, Math.Max(1, width >> m), Math.Max(1, height >> m)) * frames * depth;
        int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
        if (at + Bytes(format, w, h) > b.Length) { problem = "truncated"; return false; }
        image = new Rgba(w, h, Decode(format, b.AsSpan((int)at), w, h));
        return true;
    }

    /// <summary>Bytes of one w×h image in a pixel format, or -1 for formats this reader can't decode.</summary>
    public static int Bytes(int format, int w, int h)
    {
        int blocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
        return format switch
        {
            0 or 1 or 11 or 12 or 16 => w * h * 4,
            2 or 3 or 9 or 10 => w * h * 3,
            4 or 17 or 18 or 19 or 21 or 6 => w * h * 2,
            5 or 8 => w * h,
            13 or 20 => blocks * 8,
            14 or 15 => blocks * 16,
            24 => w * h * 8,
            _ => -1,
        };
    }

    static byte[] Decode(int format, ReadOnlySpan<byte> s, int w, int h)
    {
        var p = new byte[w * h * 4];
        switch (format)
        {
            case 13 or 20: Dxt(s, w, h, p, 8); break;
            case 14 or 15: Dxt(s, w, h, p, format == 14 ? 3 : 5); break;
            default:
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    switch (format)
                    {
                        case 0: s.Slice(i * 4, 4).CopyTo(p.AsSpan(o)); break;
                        case 1: Set(p, o, s[i * 4 + 3], s[i * 4 + 2], s[i * 4 + 1], s[i * 4]); break;
                        case 11: Set(p, o, s[i * 4 + 1], s[i * 4 + 2], s[i * 4 + 3], s[i * 4]); break;
                        case 12: Set(p, o, s[i * 4 + 2], s[i * 4 + 1], s[i * 4], s[i * 4 + 3]); break;
                        case 16: Set(p, o, s[i * 4 + 2], s[i * 4 + 1], s[i * 4], 255); break;
                        case 2 or 9: Set(p, o, s[i * 3], s[i * 3 + 1], s[i * 3 + 2], 255); break;
                        case 3 or 10: Set(p, o, s[i * 3 + 2], s[i * 3 + 1], s[i * 3], 255); break;
                        case 4 or 17:
                        {
                            int v = s[i * 2] | s[i * 2 + 1] << 8;
                            byte r = (byte)((v >> 11) * 255 / 31), g = (byte)(((v >> 5) & 63) * 255 / 63), bl = (byte)((v & 31) * 255 / 31);
                            if (format == 4) Set(p, o, r, g, bl, 255); else Set(p, o, bl, g, r, 255);
                            break;
                        }
                        case 18 or 21:
                        {
                            int v = s[i * 2] | s[i * 2 + 1] << 8;
                            Set(p, o, (byte)(((v >> 10) & 31) * 255 / 31), (byte)(((v >> 5) & 31) * 255 / 31), (byte)((v & 31) * 255 / 31), format == 21 && (v & 0x8000) == 0 ? (byte)0 : (byte)255);
                            break;
                        }
                        case 19:
                        {
                            int v = s[i * 2] | s[i * 2 + 1] << 8;
                            Set(p, o, (byte)(((v >> 8) & 15) * 17), (byte)(((v >> 4) & 15) * 17), (byte)((v & 15) * 17), (byte)((v >> 12) * 17));
                            break;
                        }
                        case 5: Set(p, o, s[i], s[i], s[i], 255); break;
                        case 6: Set(p, o, s[i * 2], s[i * 2], s[i * 2], s[i * 2 + 1]); break;
                        case 8: Set(p, o, 255, 255, 255, s[i]); break;
                        case 24:
                        {
                            // HDR: clamp to [0, 1], no tone mapping.
                            var px = s.Slice(i * 8, 8);
                            Set(p, o, Unit(px), Unit(px[2..]), Unit(px[4..]), Unit(px[6..]));
                            break;
                        }
                    }
                }
                break;
        }
        return p;
    }

    static byte Unit(ReadOnlySpan<byte> half) => (byte)Math.Clamp((float)BinaryPrimitives.ReadHalfLittleEndian(half) * 255f + 0.5f, 0, 255);

    static void Set(byte[] p, int o, byte r, byte g, byte b, byte a) { p[o] = r; p[o + 1] = g; p[o + 2] = b; p[o + 3] = a; }

    /// <summary>DXT1 (kind 8), DXT3 (3) and DXT5 (5) blocks.</summary>
    static void Dxt(ReadOnlySpan<byte> s, int w, int h, byte[] p, int kind)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), blockBytes = kind == 8 ? 8 : 16;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var block = s.Slice((by * bw + bx) * blockBytes, blockBytes);
                var color = kind == 8 ? block : block[8..];
                int c0 = color[0] | color[1] << 8, c1 = color[2] | color[3] << 8;
                Expand565(c0, colors[..4]);
                Expand565(c1, colors[4..8]);
                bool fourColor = kind != 8 || c0 > c1;
                for (int k = 0; k < 3; k++)
                {
                    int a = colors[k], b = colors[4 + k];
                    colors[8 + k] = (byte)(fourColor ? (2 * a + b) / 3 : (a + b) / 2);
                    colors[12 + k] = (byte)(fourColor ? (a + 2 * b) / 3 : 0);
                }
                colors[3] = colors[7] = colors[11] = 255;
                colors[15] = (byte)(fourColor ? 255 : 0);
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(color[4..]);

                ulong alphaBits = 0;
                if (kind == 5)
                {
                    alphas[0] = block[0]; alphas[1] = block[1];
                    for (int k = 2; k < 8; k++)
                        alphas[k] = (byte)(block[0] > block[1]
                            ? ((8 - k) * block[0] + (k - 1) * block[1]) / 7
                            : k < 6 ? ((6 - k) * block[0] + (k - 1) * block[1]) / 5 : k == 6 ? 0 : 255);
                    for (int k = 0; k < 6; k++) alphaBits |= (ulong)block[2 + k] << (8 * k);
                }

                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px >= w || py >= h) continue;
                        int i = y * 4 + x, o = (py * w + px) * 4, c = (int)(bits >> (2 * i)) & 3;
                        p[o] = colors[c * 4]; p[o + 1] = colors[c * 4 + 1]; p[o + 2] = colors[c * 4 + 2];
                        p[o + 3] = kind switch
                        {
                            3 => (byte)(((block[i / 2] >> (4 * (i & 1))) & 15) * 17),
                            5 => alphas[(int)(alphaBits >> (3 * i)) & 7],
                            _ => colors[c * 4 + 3],
                        };
                    }
            }
    }

    static void Expand565(int v, Span<byte> rgb)
    {
        rgb[0] = (byte)((v >> 11) * 255 / 31);
        rgb[1] = (byte)(((v >> 5) & 63) * 255 / 63);
        rgb[2] = (byte)((v & 31) * 255 / 31);
    }
}
