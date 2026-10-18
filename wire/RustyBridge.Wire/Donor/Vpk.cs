using System.Text;

namespace RustyBridge.Wire.Donor;

/// <summary>
/// Reads files out of the donor's VPK v1/v2 archives (a directory file per
/// depot, data in numbered archives beside it). Paths are matched without
/// case, with forward slashes, and without the leading "mm/" some depots use.
/// </summary>
public sealed class VpkIndex
{
    readonly record struct Entry(string DirFile, ushort Archive, uint Offset, uint Length, long PreloadAt, ushort PreloadBytes, long DataBase);
    readonly Dictionary<string, Entry> _entries = new();

    public int Count => _entries.Count;

    public VpkIndex(string vpksDirectory)
    {
        foreach (var dir in Directory.GetFiles(vpksDirectory, "*_dir.vpk").OrderBy(p => p, StringComparer.Ordinal))
            Load(dir);
    }

    public static string Normalize(string path)
    {
        string p = path.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
        return p.StartsWith("mm/", StringComparison.Ordinal) ? p[3..] : p;
    }

    void Load(string dirFile)
    {
        var b = File.ReadAllBytes(dirFile);
        uint sig = BitConverter.ToUInt32(b, 0), version = BitConverter.ToUInt32(b, 4), tree = BitConverter.ToUInt32(b, 8);
        if (sig != 0x55AA1234 || version is not (1 or 2)) throw new InvalidDataException($"{dirFile}: not a VPK v1/v2 directory");
        int header = version == 1 ? 12 : 28;  // v2 adds four section sizes after the tree size
        int i = header;
        long dataBase = header + tree;
        string Str() { int j = Array.IndexOf(b, (byte)0, i); var s = Encoding.ASCII.GetString(b, i, j - i); i = j + 1; return s; }
        while (true)
        {
            string ext = Str(); if (ext.Length == 0) break;
            while (true)
            {
                string folder = Str(); if (folder.Length == 0) break;
                while (true)
                {
                    string name = Str(); if (name.Length == 0) break;
                    ushort preload = BitConverter.ToUInt16(b, i + 4), archive = BitConverter.ToUInt16(b, i + 6);
                    uint offset = BitConverter.ToUInt32(b, i + 8), length = BitConverter.ToUInt32(b, i + 12);
                    long preloadAt = i + 18;
                    i += 18 + preload;
                    string path = folder == " " ? $"{name}.{ext}" : $"{folder}/{name}.{ext}";
                    _entries.TryAdd(Normalize(path), new Entry(dirFile, archive, offset, length, preloadAt, preload, dataBase));
                }
            }
        }
    }

    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out var e)) return null;
        var data = new byte[e.PreloadBytes + e.Length];
        using (var dir = File.OpenRead(e.DirFile))
        {
            dir.Position = e.PreloadAt;
            dir.ReadExactly(data, 0, e.PreloadBytes);
            if (e.Archive == 0x7FFF && e.Length > 0)
            {
                dir.Position = e.DataBase + e.Offset;
                dir.ReadExactly(data, e.PreloadBytes, (int)e.Length);
            }
        }
        if (e.Archive != 0x7FFF && e.Length > 0)
        {
            string archive = e.DirFile[..^"_dir.vpk".Length] + $"_{e.Archive:D3}.vpk";
            using var a = File.OpenRead(archive);
            a.Position = e.Offset;
            a.ReadExactly(data, e.PreloadBytes, (int)e.Length);
        }
        return data;
    }
}
