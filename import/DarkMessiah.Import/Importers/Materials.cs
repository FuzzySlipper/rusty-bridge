using RustyBridge.Wire.Donor;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Images;
using DarkMessiah.Import.Source;

namespace DarkMessiah.Import;

/// <summary>What a GLB keeps of a donor material: its base texture and how it cuts or blends alpha.</summary>
public sealed record SourceMaterial(string Path, string Shader, string? BaseTexture, bool AlphaTest, float AlphaCutoff, bool Translucent, bool TwoSided);

/// <summary>
/// Resolves donor materials (.vmt, patches followed) and their base textures
/// (.vtf), and turns them into glTF materials with embedded PNGs. Decoded
/// textures are cached by path and size, since many GLBs share them.
/// </summary>
public sealed class Materials
{
    readonly DonorFiles _files;
    readonly ImportLog _log;
    readonly Dictionary<string, SourceMaterial?> _materials = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, int), (byte[] Png, bool Alpha)?> _textures = new();

    public Materials(DonorFiles files, ImportLog log) { _files = files; _log = log; }

    /// <summary>Forget everything read through a map's pak file.</summary>
    public void Clear() { _materials.Clear(); _textures.Clear(); }

    /// <summary>A material by its path under materials/, without extension.</summary>
    public SourceMaterial? Find(string path)
    {
        string key = Normalize(path);
        if (_materials.TryGetValue(key, out var cached)) return cached;
        var kv = Load($"materials/{key}.vmt", 0);
        SourceMaterial? material = null;
        if (kv is not null)
        {
            string shader = kv.Name;
            var hdr = kv.Blocks.FirstOrDefault(b => b.Name.EndsWith("_dx9", StringComparison.OrdinalIgnoreCase));
            string? Value(string k) => hdr?[k] ?? kv[k];
            bool Flag(string k) => Value(k) is string v && v.Trim() is not ("0" or "");
            material = new SourceMaterial(key, shader, Value("$basetexture"), Flag("$alphatest"),
                float.TryParse(Value("$alphatestreference"), System.Globalization.CultureInfo.InvariantCulture, out float cutoff) && cutoff > 0 ? cutoff : 0.5f,
                Flag("$translucent"), Flag("$nocull"));
        }
        return _materials[key] = material;
    }

    /// <summary>Exists, so a caller can choose between search directories.</summary>
    public bool Exists(string path) => _files.Read($"materials/{Normalize(path)}.vmt") is not null;

    KeyValues? Load(string file, int depth)
    {
        var bytes = _files.Read(file);
        if (bytes is null) return null;
        var kv = KeyValues.Parse(bytes);
        if (kv is null) { _log.Warn($"{file}: unreadable material"); return null; }
        if (!kv.Name.Equals("patch", StringComparison.OrdinalIgnoreCase) || depth > 4) return kv;
        // A patch names a base material and inserts or replaces some of its values.
        string? include = kv["include"];
        var baseKv = include is null ? null : Load(include, depth + 1);
        if (baseKv is null) { _log.Warn($"{file}: patch of missing {include}"); return null; }
        foreach (var name in new[] { "insert", "replace" })
            if (kv.Block(name) is KeyValues changes)
                foreach (var (k, v) in changes.Values) baseKv.Values.Add((k, v));
        return baseKv;
    }

    /// <summary>A texture under materials/, decoded at the largest mip no bigger than <paramref name="maxSize"/>.</summary>
    public (byte[] Png, bool Alpha)? Texture(string path, int maxSize)
    {
        var key = (Normalize(path), maxSize);
        if (_textures.TryGetValue(key, out var cached)) return cached;
        (byte[], bool)? result = null;
        var bytes = _files.Read($"materials/{key.Item1}.vtf");
        if (bytes is null) _log.Warn($"materials/{key.Item1}.vtf: missing");
        else if (!Vtf.TryDecode(bytes, maxSize, out var image, out var problem)) _log.Warn($"materials/{key.Item1}.vtf: {problem}");
        else
        {
            bool alpha = image.HasAlpha();
            result = (Png.Encode(image, alpha), alpha);
        }
        return _textures[key] = result;
    }

    /// <summary>The glTF material for a donor material path, added once per GLB. Missing materials get a grey stand-in under the same name.</summary>
    public int AddTo(GltfBuilder gltf, string path, int maxTextureSize)
    {
        string name = Normalize(path);
        if (gltf.TryGetMaterial(name, out int existing)) return existing;
        var material = Find(name);
        var pbr = new JsonObject { ["metallicFactor"] = 0f, ["roughnessFactor"] = 1f };
        var json = new JsonObject { ["pbrMetallicRoughness"] = pbr };
        if (material is null)
        {
            _log.Warn($"materials/{name}.vmt: missing");
            pbr["baseColorFactor"] = GltfBuilder.FloatArray(0.5f, 0.5f, 0.5f, 1f);
            json["extras"] = new JsonObject { ["sourceMaterial"] = name, ["missing"] = true };
            return gltf.Material(name, json);
        }
        json["extras"] = new JsonObject { ["sourceMaterial"] = name, ["shader"] = material.Shader };
        if (material.BaseTexture is string texture && Texture(texture, maxTextureSize) is { } decoded)
        {
            var (png, alpha) = decoded;
            pbr["baseColorTexture"] = new JsonObject { ["index"] = gltf.PngTexture(png, Normalize(texture)) };
            // The donor's VertexLitGeneric and LightmappedGeneric keep alpha as an
            // env-map or spec mask unless the material asks for alpha test or blending.
            if (alpha && material.AlphaTest) { json["alphaMode"] = "MASK"; json["alphaCutoff"] = material.AlphaCutoff; }
            else if (alpha && material.Translucent) json["alphaMode"] = "BLEND";
        }
        if (material.TwoSided || material.AlphaTest) json["doubleSided"] = true;
        return gltf.Material(name, json);
    }

    public static string Normalize(string path)
    {
        string p = path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        p = string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != "."));
        if (p.StartsWith("materials/", StringComparison.Ordinal)) p = p["materials/".Length..];
        if (p.EndsWith(".vmt", StringComparison.Ordinal) || p.EndsWith(".vtf", StringComparison.Ordinal)) p = p[..^4];
        return p;
    }
}

/// <summary>Problems met while importing, each reported once.</summary>
public sealed class ImportLog
{
    readonly HashSet<string> _seen = [];
    public List<string> Warnings { get; } = [];
    public void Warn(string message) { if (_seen.Add(message)) Warnings.Add(message); }
}
