using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DarkMessiah.Import.Gltf;

/// <summary>
/// Builds one self-contained glTF 2.0 binary (GLB): every buffer view lives in
/// the single embedded buffer, images included, as the Engine's GLB admission
/// requires.
/// </summary>
public sealed class GltfBuilder
{
    public const int Float = 5126, UnsignedByte = 5121, UnsignedShort = 5123, UnsignedInt = 5125;
    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    readonly MemoryStream _bin = new();
    readonly JsonArray _views = [], _accessors = [], _meshes = [], _materials = [], _textures = [], _images = [], _skins = [], _animations = [];
    readonly List<JsonObject> _nodes = [];
    readonly List<List<int>> _children = [];
    readonly List<int> _roots = [];
    readonly Dictionary<string, int> _materialByName = new(StringComparer.Ordinal);

    public JsonObject Extras { get; } = [];
    public int NodeCount => _nodes.Count;

    int View(ReadOnlySpan<byte> bytes, int? target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = bytes.Length };
        if (target is int t) view["target"] = t;
        _bin.Write(bytes);
        _views.Add(view);
        return _views.Count - 1;
    }

    static int Components(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };

    /// <summary>A float accessor; <paramref name="bounds"/> writes min/max (required for positions and animation inputs).</summary>
    public int Floats(ReadOnlySpan<float> values, string type, bool bounds = false, bool vertexData = false)
    {
        int n = Components(type);
        if (values.Length % n != 0) throw new ArgumentException($"{values.Length} floats is not a whole number of {type}");
        var accessor = new JsonObject
        {
            ["bufferView"] = View(MemoryMarshal.AsBytes(values), vertexData ? ArrayBuffer : null),
            ["componentType"] = Float,
            ["count"] = values.Length / n,
            ["type"] = type,
        };
        if (bounds)
        {
            var min = new float[n]; var max = new float[n];
            Array.Fill(min, float.PositiveInfinity); Array.Fill(max, float.NegativeInfinity);
            for (int i = 0; i < values.Length; i++) { min[i % n] = Math.Min(min[i % n], values[i]); max[i % n] = Math.Max(max[i % n], values[i]); }
            accessor["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
            accessor["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
        }
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    /// <summary>Triangle indices, as 16-bit when they fit.</summary>
    public int Indices(ReadOnlySpan<uint> indices)
    {
        uint top = 0;
        foreach (uint i in indices) top = Math.Max(top, i);
        int view, component;
        if (top < ushort.MaxValue)
        {
            var narrow = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++) narrow[i] = (ushort)indices[i];
            view = View(MemoryMarshal.AsBytes<ushort>(narrow), ElementArrayBuffer);
            component = UnsignedShort;
        }
        else
        {
            view = View(MemoryMarshal.AsBytes(indices), ElementArrayBuffer);
            component = UnsignedInt;
        }
        _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = component, ["count"] = indices.Length, ["type"] = "SCALAR" });
        return _accessors.Count - 1;
    }

    /// <summary>JOINTS_0: four joint indices per vertex, as bytes when the skin is small enough.</summary>
    public int Joints(ReadOnlySpan<ushort> joints, int jointCount)
    {
        int view, component;
        if (jointCount <= 256)
        {
            var narrow = new byte[joints.Length];
            for (int i = 0; i < joints.Length; i++) narrow[i] = (byte)joints[i];
            view = View(narrow, ArrayBuffer);
            component = UnsignedByte;
        }
        else
        {
            view = View(MemoryMarshal.AsBytes(joints), ArrayBuffer);
            component = UnsignedShort;
        }
        _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = component, ["count"] = joints.Length / 4, ["type"] = "VEC4" });
        return _accessors.Count - 1;
    }

    /// <summary>Adds a node under <paramref name="parent"/>, or at the scene root.</summary>
    public int Node(JsonObject node, int? parent = null)
    {
        _nodes.Add(node);
        _children.Add([]);
        int index = _nodes.Count - 1;
        if (parent is int p) _children[p].Add(index); else _roots.Add(index);
        return index;
    }

    public JsonObject NodeAt(int index) => _nodes[index];

    public int Mesh(JsonObject mesh) { _meshes.Add(mesh); return _meshes.Count - 1; }

    public int Skin(JsonObject skin) { _skins.Add(skin); return _skins.Count - 1; }

    public void Animation(JsonObject animation) => _animations.Add(animation);

    public bool TryGetMaterial(string name, out int index) => _materialByName.TryGetValue(name, out index);

    public int Material(string name, JsonObject material)
    {
        material["name"] = name;
        _materials.Add(material);
        return _materialByName[name] = _materials.Count - 1;
    }

    /// <summary>An embedded PNG as a texture with a repeating, mipmapped sampler.</summary>
    public int PngTexture(byte[] png, string name)
    {
        _images.Add(new JsonObject { ["bufferView"] = View(png, null), ["mimeType"] = "image/png", ["name"] = name });
        _textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = _images.Count - 1 });
        return _textures.Count - 1;
    }

    public byte[] ToGlb()
    {
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "rusty-bridge DarkMessiah.Import" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = Ints(_roots) }),
        };
        if (Extras.Count > 0) root["asset"]!["extras"] = Extras.DeepClone();
        for (int i = 0; i < _nodes.Count; i++)
            if (_children[i].Count > 0) _nodes[i]["children"] = Ints(_children[i]);
        root["nodes"] = new JsonArray(_nodes.Select(n => (JsonNode)n.DeepClone()).ToArray());
        void Put(string key, JsonArray array) { if (array.Count > 0) root[key] = array.DeepClone(); }
        Put("meshes", _meshes);
        Put("materials", _materials);
        Put("skins", _skins);
        Put("animations", _animations);
        if (_images.Count > 0)
        {
            Put("images", _images);
            Put("textures", _textures);
            // Linear filtering with mipmaps, repeating, as the donor samples its materials.
            root["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        if (_bin.Length > 0)
        {
            Put("bufferViews", _views);
            Put("accessors", _accessors);
            root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length });
        }

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonPadded = (json.Length + 3) & ~3;
        long total = 12 + 8 + jsonPadded + (_bin.Length > 0 ? 8 + _bin.Length : 0);
        if (total > uint.MaxValue) throw new InvalidOperationException($"GLB of {total} bytes exceeds the format's 4 GiB limit");
        var output = new MemoryStream((int)total);
        var w = new BinaryWriter(output);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonPadded);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(json);
        for (int i = json.Length; i < jsonPadded; i++) w.Write((byte)' ');
        if (_bin.Length > 0)
        {
            w.Write((uint)_bin.Length);
            w.Write(0x004E4942u); // "BIN\0"
            w.Write(_bin.GetBuffer(), 0, (int)_bin.Length);
        }
        w.Flush();
        return output.ToArray();
    }

    static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode)v).ToArray());

    public static JsonArray FloatArray(params float[] values) => new(values.Select(v => (JsonNode)v).ToArray());
}
