using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Rusty.Engine;

namespace DarkMessiah.Shared;

/// <summary>A named point on a model's skeleton: the joint and the transform local to it (glTF space).</summary>
public readonly record struct ModelAttachment(string Joint, Transform Local);

/// <summary>
/// A hit box: a box on a joint (glTF space, in the joint's frame) and the hit
/// group a trace reports for it. A model's hit boxes are numbered in order, as
/// a trace's hitbox index counts them.
/// </summary>
public readonly record struct ModelHitbox(string Joint, int Group, Vector3 Center, Vector3 HalfExtents);

/// <summary>One convex piece of a model's collision: points in a joint's frame (glTF, metres) and triangles over them.</summary>
public sealed record ConvexHull(string Joint, Vector3[] Points, Triangle[] Triangles);

/// <summary>
/// One of a model's clips as dm-import wrote it: its Source sequence's frame rate and frame count, whether it loops,
/// and Arkane's animation events as (cycle, id) pairs: 2186 opens a hit window and 2190 closes it, 2102 and 2139 are
/// the struck parts. <see cref="Movement"/> is its root motion, which Source keeps out of the root bone (#10150).
/// </summary>
/// <param name="Movement">Where the clip has moved the model by each cycle, cumulative, in the model's own frame (Source
/// units: X forward, Y left), piece by piece as Source's mstudiomovement_t has it; empty for a clip that stays put.</param>
public sealed record ModelClip(string Name, float Fps, int Frames, bool Looping, IReadOnlyList<(float Cycle, int Event)> Events,
    IReadOnlyList<(float Cycle, Vector2 Position)>? Movement = null)
{
    /// <summary>
    /// How far the clip has moved the model by <paramref name="cycle"/> (Source units, X forward, Y left): linear within
    /// each movement piece, from nothing at cycle 0.
    /// </summary>
    public Vector2 MovedAt(float cycle)
    {
        if (Movement is not { Count: > 0 } pieces) return Vector2.Zero;
        cycle = Math.Clamp(cycle, 0, 1);
        (float Cycle, Vector2 Position) from = (0, Vector2.Zero);
        foreach (var piece in pieces)
        {
            if (cycle <= piece.Cycle)
                return piece.Cycle > from.Cycle ? Vector2.Lerp(from.Position, piece.Position, (cycle - from.Cycle) / (piece.Cycle - from.Cycle)) : piece.Position;
            from = piece;
        }
        return from.Position;
    }

    /// <summary>Seconds the clip runs at playback rate 1: its frames over its rate, as Source plays a sequence.</summary>
    public float Seconds => Fps > 0 && Frames > 1 ? (Frames - 1) / Fps : 0;

    /// <summary>The cycles of every event with this id, in order.</summary>
    public IEnumerable<float> At(int id) => Events.Where(e => e.Event == id).Select(e => e.Cycle);
}

/// <summary>
/// What dm-import records about a model in its GLB's asset extras and clip extras: attachments in the model's own
/// order, the default hitbox set, a one-solid collision model, and every clip's timing and events. Only the JSON
/// chunk is read; the Engine has no reader for glTF extras.
/// </summary>
public sealed class ModelInfo
{
    public static readonly ModelInfo Empty = new([], [], [], []);

    public ModelInfo(IReadOnlyList<(string Name, ModelAttachment Attachment)> attachments, IReadOnlyList<ModelHitbox> hitboxes,
        IReadOnlyList<ConvexHull> collision, IReadOnlyList<ModelClip> clips)
    {
        AttachmentList = attachments;
        Attachments = attachments.Where(a => a.Attachment.Joint.Length > 0)
            .GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Attachment, StringComparer.Ordinal);
        Hitboxes = hitboxes;
        Collision = collision;
        Clips = clips.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, ModelAttachment> Attachments { get; }
    /// <summary>The model's attachments in its own order, as the Wire numbers them from 1 (m_iParentAttachment).</summary>
    public IReadOnlyList<(string Name, ModelAttachment Attachment)> AttachmentList { get; }
    public ModelAttachment? AttachmentAt(int number) =>
        number >= 1 && number <= AttachmentList.Count && AttachmentList[number - 1].Attachment.Joint.Length > 0 ? AttachmentList[number - 1].Attachment : null;
    /// <summary>The model's default hitbox set, in order; empty for an import made before hit boxes were.</summary>
    public IReadOnlyList<ModelHitbox> Hitboxes { get; }
    /// <summary>A weapon's, shield's or prop's collision model (its one .phy solid); empty for a character or an older import.</summary>
    public IReadOnlyList<ConvexHull> Collision { get; }
    /// <summary>The model's clips by name.</summary>
    public IReadOnlyDictionary<string, ModelClip> Clips { get; }

    /// <summary>The length of a GLB's header and JSON chunk, from its first 20 bytes; 0 when it isn't a GLB.</summary>
    public static int HeaderLength(ReadOnlySpan<byte> first20) =>
        first20.Length >= 20 && first20[..4].SequenceEqual("glTF"u8) ? 20 + (int)BitConverter.ToUInt32(first20[12..16]) : 0;

    /// <summary>The model described by a GLB's header and JSON chunk (at least <see cref="HeaderLength"/> bytes).</summary>
    public static ModelInfo FromGlb(ReadOnlySpan<byte> glb)
    {
        int length = HeaderLength(glb);
        if (length == 0 || glb.Length < length) return Empty;
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb[20..length]));
        var extras = json?["asset"]?["extras"];
        return new ModelInfo(ReadAttachments(extras), ReadHitboxes(extras), ReadCollision(extras), ReadClips(json));
    }

    static List<(string Name, ModelAttachment Attachment)> ReadAttachments(JsonNode? extras)
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

    static Vector3 V(JsonNode? n) => n is JsonArray a ? new(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : Vector3.Zero;

    static List<ModelHitbox> ReadHitboxes(JsonNode? extras) =>
        (extras?["hitboxes"]?.AsArray() ?? []).Select(h => new ModelHitbox(h?["joint"]?.GetValue<string>() ?? "", h?["group"]?.GetValue<int>() ?? 0,
            V(h?["center"]), V(h?["halfExtents"]))).ToList();

    static List<ConvexHull> ReadCollision(JsonNode? extras) =>
        (extras?["collision"]?["pieces"]?.AsArray() ?? []).Select(piece =>
        {
            var p = piece!["points"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            var t = piece["triangles"]!.AsArray().Select(v => v!.GetValue<uint>()).ToArray();
            return new ConvexHull(extras!["collision"]!["joint"]!.GetValue<string>(),
                Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[3 * i], p[3 * i + 1], p[3 * i + 2])).ToArray(),
                Enumerable.Range(0, t.Length / 3).Select(i => new Triangle(t[3 * i], t[3 * i + 1], t[3 * i + 2])).ToArray());
        }).ToList();

    static List<ModelClip> ReadClips(JsonNode? json) =>
        (json?["animations"]?.AsArray() ?? []).Select(a =>
        {
            var e = a?["extras"];
            var events = (e?["events"]?.AsArray() ?? []).Select(p => (p![0]!.GetValue<float>(), (int)p[1]!.GetValue<float>())).ToList();
            var movement = (e?["movement"]?.AsArray() ?? []).Select(p => (p![0]!.GetValue<float>(), new Vector2(p[1]!.GetValue<float>(), p[2]!.GetValue<float>()))).ToList();
            return new ModelClip(a?["name"]?.GetValue<string>() ?? "", e?["fps"]?.GetValue<float>() ?? 0, e?["frames"]?.GetValue<int>() ?? 0,
                e?["looping"]?.GetValue<bool>() ?? false, events, movement);
        }).ToList();
}
