using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Rusty.Engine;

namespace DarkMessiah.Mirror.Scene;

/// <summary>A named point on a model's skeleton: the joint and the transform local to it (glTF space).</summary>
internal readonly record struct ModelAttachment(string Joint, Transform Local);

/// <summary>
/// A hit box: a box on a joint (glTF space, in the joint's frame) and the hit
/// group a trace reports for it. A model's hit boxes are numbered in order, as
/// a trace's hitbox index counts them.
/// </summary>
internal readonly record struct ModelHitbox(string Joint, int Group, Vector3 Center, Vector3 HalfExtents);

/// <summary>One convex piece of a model's collision: points in a joint's frame (glTF, metres) and triangles over them.</summary>
internal sealed record ConvexHull(string Joint, Vector3[] Points, Triangle[] Triangles);

/// <summary>One imported model, opened once and shared by every entity that draws it.</summary>
internal sealed class LevelModel(string donorPath, RenderResource mesh, IReadOnlyList<(string Name, ModelAttachment Attachment)> attachments,
    IReadOnlyList<ModelHitbox>? hitboxes = null, IReadOnlyList<ConvexHull>? collision = null)
{
    public string DonorPath { get; } = donorPath;
    public RenderResource Mesh { get; } = mesh;
    public IReadOnlyDictionary<string, ModelAttachment> Attachments { get; } = attachments.Where(a => a.Attachment.Joint.Length > 0)
        .GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Attachment, StringComparer.Ordinal);
    /// <summary>The model's attachments in its own order, as the Wire numbers them from 1 (m_iParentAttachment).</summary>
    public IReadOnlyList<(string Name, ModelAttachment Attachment)> AttachmentList { get; } = attachments;
    public ModelAttachment? AttachmentAt(int number) =>
        number >= 1 && number <= AttachmentList.Count && AttachmentList[number - 1].Attachment.Joint.Length > 0 ? AttachmentList[number - 1].Attachment : null;
    /// <summary>The model's default hitbox set, in order; empty for an import made before hit boxes were.</summary>
    public IReadOnlyList<ModelHitbox> Hitboxes { get; } = hitboxes ?? [];
    /// <summary>A weapon's, shield's or prop's collision model (its one .phy solid); empty for a character or an older import.</summary>
    public IReadOnlyList<ConvexHull> Collision { get; } = collision ?? [];
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
        var extras = Extras(file);
        return opened[donorPath] = new LevelModel(donorPath, mesh, Attachments(extras), Hitboxes(extras), Collision(extras));
    }

    /// <summary>The GLB's asset extras, where dm-import records what glTF has no place for. Only the JSON chunk is read; the Engine has no reader for glTF extras.</summary>
    JsonNode? Extras(string file)
    {
        using var reference = engine.Content.OpenReference(new(file));
        var header = engine.Content.ReadBytes(new(reference, 0, 20)).Span;
        if (header.Length < 20 || !header[..4].SequenceEqual("glTF"u8)) return null;
        uint length = BitConverter.ToUInt32(header[12..16]);
        return JsonNode.Parse(Encoding.UTF8.GetString(engine.Content.ReadBytes(new(reference, 20, length)).Span))?["asset"]?["extras"];
    }

    /// <summary>The attachments dm-import recorded, in the model's order.</summary>
    static List<(string Name, ModelAttachment Attachment)> Attachments(JsonNode? extras)
    {
        var result = new List<(string, ModelAttachment)>();
        foreach (var a in extras?["attachments"]?.AsArray() ?? [])
        {
            string name = a?["name"]?.GetValue<string>() ?? "";
            // Keep the place of an attachment without a joint, so the Wire's numbering still lines up.
            if (a?["joint"]?.GetValue<string>() is not string joint) { result.Add((name, new ModelAttachment("", default))); continue; }
            var m = a["matrix"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            // glTF column-major → System.Numerics row-vector matrix: the same 16 numbers in order.
            var matrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
            result.Add((name, new ModelAttachment(joint, new Transform(translation, rotation, scale))));
        }
        return result;
    }

    /// <summary>The collision model dm-import recorded, as convex pieces.</summary>
    static List<ConvexHull> Collision(JsonNode? extras) =>
        (extras?["collision"]?["pieces"]?.AsArray() ?? []).Select(piece =>
        {
            var p = piece!["points"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            var t = piece["triangles"]!.AsArray().Select(v => v!.GetValue<uint>()).ToArray();
            return new ConvexHull(extras!["collision"]!["joint"]!.GetValue<string>(), Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[3 * i], p[3 * i + 1], p[3 * i + 2])).ToArray(),
                Enumerable.Range(0, t.Length / 3).Select(i => new Triangle(t[3 * i], t[3 * i + 1], t[3 * i + 2])).ToArray());
        }).ToList();

    /// <summary>The hit boxes dm-import recorded, in the model's order.</summary>
    static List<ModelHitbox> Hitboxes(JsonNode? extras)
    {
        static Vector3 V(JsonNode? n) => n is JsonArray a ? new(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : Vector3.Zero;
        return (extras?["hitboxes"]?.AsArray() ?? []).Select(h => new ModelHitbox(h?["joint"]?.GetValue<string>() ?? "", h?["group"]?.GetValue<int>() ?? 0,
            V(h?["center"]), V(h?["halfExtents"]))).ToList();
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
