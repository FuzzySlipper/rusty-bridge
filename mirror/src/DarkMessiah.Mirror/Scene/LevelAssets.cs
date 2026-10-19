using System.Text.Json.Nodes;
using DarkMessiah.Shared;
using Rusty.Engine;

namespace DarkMessiah.Mirror.Scene;

/// <summary>One imported model, opened once and shared by every entity that draws it, with what dm-import recorded about it.</summary>
internal sealed class LevelModel(string donorPath, RenderResource mesh, ModelInfo info)
{
    public string DonorPath { get; } = donorPath;
    public RenderResource Mesh { get; } = mesh;
    public ModelInfo Info { get; } = info;
    public IReadOnlyDictionary<string, ModelAttachment> Attachments => Info.Attachments;
    /// <summary>The model's attachments in its own order, as the Wire numbers them from 1 (m_iParentAttachment).</summary>
    public IReadOnlyList<(string Name, ModelAttachment Attachment)> AttachmentList => Info.AttachmentList;
    public ModelAttachment? AttachmentAt(int number) => Info.AttachmentAt(number);
    /// <summary>The model's default hitbox set, in order; empty for an import made before hit boxes were.</summary>
    public IReadOnlyList<ModelHitbox> Hitboxes => Info.Hitboxes;
    /// <summary>A weapon's, shield's or prop's collision model (its one .phy solid); empty for a character or an older import.</summary>
    public IReadOnlyList<ConvexHull> Collision => Info.Collision;
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
        return opened[donorPath] = new LevelModel(donorPath, mesh, Info(file));
    }

    /// <summary>What dm-import recorded about a model: only the GLB's header and JSON chunk are read.</summary>
    ModelInfo Info(string file)
    {
        using var reference = engine.Content.OpenReference(new(file));
        int length = ModelInfo.HeaderLength(engine.Content.ReadBytes(new(reference, 0, 20)).Span);
        return length == 0 ? ModelInfo.Empty : ModelInfo.FromGlb(engine.Content.ReadBytes(new(reference, 0, (uint)length)).Span);
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
