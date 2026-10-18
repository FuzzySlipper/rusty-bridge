using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Rusty.Engine;

namespace DarkMessiah.Mirror.Scene;

/// <summary>A named point on a model's skeleton: the joint and the transform local to it (glTF space).</summary>
internal readonly record struct ModelAttachment(string Joint, Transform Local);

/// <summary>One imported model, opened once and shared by every entity that draws it.</summary>
internal sealed class LevelModel(string donorPath, RenderResource mesh, IReadOnlyDictionary<string, ModelAttachment> attachments)
{
    public string DonorPath { get; } = donorPath;
    public RenderResource Mesh { get; } = mesh;
    public IReadOnlyDictionary<string, ModelAttachment> Attachments { get; } = attachments;
}

/// <summary>
/// The imported level: dm-import's <c>&lt;map&gt;.assets.json</c>, whose asset
/// ids are Wire model paths, and the models it names, opened on first use.
/// Each model's GLB holds all its clips, one per Source sequence.
/// </summary>
internal sealed class LevelAssets : IDisposable
{
    readonly IEngineContext engine;
    readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    readonly HashSet<string> invisible = new(StringComparer.Ordinal);
    readonly HashSet<string> textures = new(StringComparer.Ordinal);
    readonly Dictionary<string, LevelModel?> opened = new(StringComparer.Ordinal);

    public string Map { get; }
    public string RecordingFile { get; }
    public string WorldPath => $"maps/{Map}.bsp";
    public IReadOnlyCollection<string> Missing => missing;
    readonly HashSet<string> missing = new(StringComparer.Ordinal);

    public LevelAssets(IEngineContext engine, string descriptorPath)
    {
        this.engine = engine;
        var root = JsonNode.Parse(Content.ReadAll(engine, descriptorPath))!;
        foreach (var asset in root["assets"]!.AsArray())
        {
            string id = asset!["id"]!.GetValue<string>();
            // Sprite materials import as textures; the mirror draws no sprites yet.
            if (asset["kind"]?.GetValue<string>() == "model") files[id] = asset["path"]!.GetValue<string>();
            else textures.Add(id);
        }
        var provenance = root["provenance"]!;
        Map = provenance["map"]!.GetValue<string>();
        RecordingFile = provenance["recordingFile"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"{descriptorPath} names no recording; import it again with this dm-import");
        foreach (var id in provenance["invisible"]?.AsArray() ?? []) invisible.Add(id!.GetValue<string>());
    }

    /// <summary>A brush model with nothing to draw (a trigger or clip brush): known, deliberately absent.</summary>
    public bool IsInvisible(string donorPath) => invisible.Contains(donorPath);

    /// <summary>The model for a Wire model path, or null when the import has none (reported once in <see cref="Missing"/>).</summary>
    public LevelModel? Model(string donorPath)
    {
        if (opened.TryGetValue(donorPath, out var model)) return model;
        if (!files.TryGetValue(donorPath, out var file))
        {
            if (!invisible.Contains(donorPath) && !textures.Contains(donorPath)) missing.Add(donorPath);
            return opened[donorPath] = null;
        }
        RenderResource mesh;
        using (var reference = engine.Content.OpenReference(new(file)))
            mesh = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        return opened[donorPath] = new LevelModel(donorPath, mesh, Attachments(file));
    }

    /// <summary>
    /// The attachments dm-import recorded in the GLB's asset extras. Only the
    /// JSON chunk is read; the Engine has no reader for glTF extras.
    /// </summary>
    Dictionary<string, ModelAttachment> Attachments(string file)
    {
        var result = new Dictionary<string, ModelAttachment>(StringComparer.Ordinal);
        using var reference = engine.Content.OpenReference(new(file));
        var header = engine.Content.ReadBytes(new(reference, 0, 20)).Span;
        if (header.Length < 20 || !header[..4].SequenceEqual("glTF"u8)) return result;
        uint length = BitConverter.ToUInt32(header[12..16]);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(engine.Content.ReadBytes(new(reference, 20, length)).Span));
        foreach (var a in json?["asset"]?["extras"]?["attachments"]?.AsArray() ?? [])
        {
            if (a?["joint"]?.GetValue<string>() is not string joint) continue;
            var m = a["matrix"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            // glTF column-major → System.Numerics row-vector matrix: the same 16 numbers in order.
            var matrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
            result[a["name"]!.GetValue<string>()] = new ModelAttachment(joint, new Transform(translation, rotation, scale));
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var model in opened.Values) model?.Mesh.Dispose();
    }
}

/// <summary>Whole content files, read through the Engine's content service.</summary>
internal static class Content
{
    public static byte[] ReadAll(IEngineContext engine, string path)
    {
        using var reference = engine.Content.OpenReference(new(path));
        ulong length = engine.Content.ReadReferenceInfo(reference).Span[0].ByteLength;
        var bytes = new byte[length];
        for (ulong at = 0; at < length;)
        {
            var chunk = engine.Content.ReadBytes(new(reference, at, (uint)Math.Min(length - at, 16u << 20))).Span;
            if (chunk.Length == 0) throw new InvalidDataException($"content/{path}: ended at {at} of {length} bytes");
            chunk.CopyTo(bytes.AsSpan((int)at));
            at += (ulong)chunk.Length;
        }
        return bytes;
    }
}
