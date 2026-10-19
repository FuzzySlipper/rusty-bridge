using System.IO.Compression;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>
/// The donor's file system: the VPKs, with a map's embedded pak file (its
/// cube maps and patched materials) searched first while that map imports.
/// </summary>
public sealed class DonorFiles
{
    readonly VpkIndex _vpk;
    Dictionary<string, byte[]>? _pak;

    public DonorFiles(VpkIndex vpk) => _vpk = vpk;

    public byte[]? Read(string path)
    {
        if (_pak is not null && _pak.TryGetValue(VpkIndex.Normalize(path), out var bytes)) return bytes;
        return _vpk.Read(path);
    }

    /// <summary>The VPKs' files under <paramref name="folder"/> (a donor path prefix, any case), in path order.</summary>
    public IEnumerable<string> Under(string folder)
    {
        string prefix = VpkIndex.Normalize(folder).TrimEnd('/') + "/";
        return _vpk.Paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    }

    /// <summary>Searches a map's PAKFILE lump (a zip) first until the returned scope ends.</summary>
    public IDisposable WithPak(byte[] zip)
    {
        var files = new Dictionary<string, byte[]>();
        if (zip.Length > 0)
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0) continue;
                using var s = entry.Open();
                var data = new byte[entry.Length];
                s.ReadExactly(data);
                files[VpkIndex.Normalize(entry.FullName)] = data;
            }
        }
        _pak = files;
        return new Scope(this);
    }

    sealed class Scope(DonorFiles owner) : IDisposable
    {
        public void Dispose() => owner._pak = null;
    }
}
