using System.Numerics;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Source;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>What one model import produced: its GLB.</summary>
public sealed record ModelImportResult(string DonorPath, string File, int Bones, int Triangles, int Clips, long Bytes, IReadOnlyList<string> Sequences);

/// <summary>
/// A studio model (.mdl v44 + .vvd + .dx90.vtx) as one skinned GLB: every bone
/// a joint node, body part 0 of each group as primitives per material, and
/// every sequence of the model and its $includemodel files as a clip named by
/// its Source sequence name. Clips are in place: Source strips linear
/// movement out of the root bone and moves the entity instead.
///
/// The Engine bounds what one GLB's clips decode to, not how many there
/// are: a 60-joint, 715-clip guard is about 2.6 M keyframes and 9.5 M values.
/// </summary>
public sealed class ModelImport
{
    /// <summary>The Engine's per-GLB animation decode budgets (voxel-convert animation.rs, rusty-engine #10072).</summary>
    public const int MaxKeyframes = 16_000_000, MaxValues = 32_000_000;

    readonly DonorFiles _files;
    readonly Materials _materials;
    readonly ImportLog _log;

    public int MaxTextureSize { get; init; } = 1024;

    public ModelImport(DonorFiles files, Materials materials, ImportLog log) { _files = files; _materials = materials; _log = log; }

    /// <summary>A clip ready to write: its samplers' data and its extras.</summary>
    sealed record PendingClip(string Name, float[] Times, List<(int Joint, string Path, float[] Values, bool Constant)> Channels, JsonObject Extras)
    {
        public int Keyframes => Channels.Sum(c => c.Constant ? 2 : Times.Length);
        public int Values => Channels.Sum(c => c.Values.Length);
    }

    public ModelImportResult Import(string donorPath, string file)
    {
        var bytes = _files.Read(donorPath) ?? throw new FileNotFoundException($"{donorPath}: not in the donor's files");
        var mdl = new StudioModelFile(donorPath, bytes);
        for (int i = 0; i < mdl.Bones.Length; i++)
            if (mdl.Bones[i].Parent >= i) throw new InvalidDataException($"{donorPath}: bone {mdl.Bones[i].Name} comes before its parent");
        if (mdl.Bones.Length == 0) throw new InvalidDataException($"{donorPath}: no bones");
        string stem = donorPath[..^4];

        var sequences = Sequences(mdl);
        var names = StudioModel.SequenceNames(donorPath, _files.Read);
        if (!names.SequenceEqual(sequences.Select(s => s.Sequence.Label)))
            _log.Warn($"{donorPath}: clip order differs from the Wire's sequence numbering");
        var clips = new List<PendingClip>();
        for (int i = 0; i < sequences.Count; i++)
            if (Clip(mdl, sequences[i].File, sequences[i].Sequence, i) is PendingClip clip) clips.Add(clip);
        clips = WithinBudget(clips, c => (c.Keyframes, c.Values), c => _log.Warn($"{donorPath}: clip {c.Name} is over the Engine's per-GLB animation budget; skipped"));
        // Arkane's animation events, as [cycle, id] pairs: the hit windows (2186 opens, 2190 closes), the struck
        // parts (2102, 2139), sounds and combo windows. The combat lab times its moves by them.
        var events = StudioModel.Events(donorPath, _files.Read);
        foreach (var clip in clips)
            // A few events carry a non-finite cycle, which JSON can't hold; they are left out.
            if (events.TryGetValue(clip.Name, out var list) && list.Where(e => float.IsFinite(e.Cycle)).ToList() is { Count: > 0 } finite)
                clip.Extras["events"] = new JsonArray(finite.Select(e => (JsonNode)GltfBuilder.FloatArray(e.Cycle, e.Event)).ToArray());

        var gltf = new GltfBuilder();
        var joints = Skeleton(gltf, mdl);
        int skin = gltf.Skin(new JsonObject
        {
            ["joints"] = new JsonArray(joints.Select(j => (JsonNode)j).ToArray()),
            ["inverseBindMatrices"] = gltf.Floats(mdl.Bones.SelectMany(b => SourceSpace.ColumnMajor(SourceSpace.Transform(b.PoseToBone))).ToArray(), "MAT4"),
        });
        string name = Path.GetFileName(stem).ToLowerInvariant();
        int triangles = 0;
        var primitives = Primitives(mdl, stem, gltf, ref triangles);
        if (primitives.Count > 0)
            gltf.Node(new JsonObject { ["name"] = name, ["mesh"] = gltf.Mesh(new JsonObject { ["name"] = name, ["primitives"] = primitives }), ["skin"] = skin });
        else
            // Skeleton only: the Engine admits a skin no mesh references when the scene has no meshes.
            _log.Warn($"{donorPath}: no triangles; written as a skeleton with clips");
        foreach (var clip in clips) Write(gltf, joints, clip);

        gltf.Extras["donorPath"] = donorPath;
        gltf.Extras["convention"] = SourceSpace.Convention;
        var eye = SourceSpace.Position(mdl.EyePosition);
        gltf.Extras["eyePosition"] = GltfBuilder.FloatArray(eye.X, eye.Y, eye.Z);
        gltf.Extras["sequences"] = new JsonArray(sequences.Select(s => (JsonNode)s.Sequence.Label).ToArray());
        var attachments = new JsonArray();
        foreach (var a in mdl.Attachments)
            attachments.Add(new JsonObject
            {
                ["name"] = a.Name,
                ["joint"] = a.Bone >= 0 && a.Bone < mdl.Bones.Length ? mdl.Bones[a.Bone].Name : null,
                ["matrix"] = GltfBuilder.FloatArray(SourceSpace.ColumnMajor(SourceSpace.Transform(a.Local))),
            });
        if (attachments.Count > 0) gltf.Extras["attachments"] = attachments;
        // Hit boxes, as boxes in their joint's frame (glTF space, metres): Source's are axis-aligned in
        // the bone's frame, and the axis mapping keeps them so.
        var hitboxes = new JsonArray();
        foreach (var h in mdl.Hitboxes)
        {
            var center = SourceSpace.Position((h.Min + h.Max) / 2);
            var half = Vector3.Abs(SourceSpace.Position((h.Max - h.Min) / 2));
            hitboxes.Add(new JsonObject
            {
                ["joint"] = h.Bone >= 0 && h.Bone < mdl.Bones.Length ? mdl.Bones[h.Bone].Name : null,
                ["group"] = h.Group,
                ["center"] = GltfBuilder.FloatArray(center.X, center.Y, center.Z),
                ["halfExtents"] = GltfBuilder.FloatArray(half.X, half.Y, half.Z),
            });
        }
        if (hitboxes.Count > 0) gltf.Extras["hitboxes"] = hitboxes;
        // A one-solid collision model (a weapon, shield or prop; characters' ragdolls have one per bone)
        // as its convex pieces: traces meet items by it, not by their hit boxes. The solid is in model
        // space, and is written in the root bone's frame (glTF, metres) to hang on that joint as hit boxes do.
        if (_files.Read(Path.ChangeExtension(donorPath, ".phy")) is byte[] phy && PhysicsModel.Read(donorPath, phy) is [var solid])
        {
            var root = mdl.Bones[0];
            var toRoot = Quaternion.Inverse(root.Rotation);
            gltf.Extras["collision"] = new JsonObject
            {
                ["joint"] = root.Name,
                ["pieces"] = new JsonArray(solid.Select(piece => (JsonNode)new JsonObject
                {
                    ["points"] = GltfBuilder.FloatArray(piece.Points
                        .Select(p => SourceSpace.Position(Vector3.Transform(p - root.Position, toRoot))).SelectMany(g => new[] { g.X, g.Y, g.Z }).ToArray()),
                    ["triangles"] = new JsonArray(piece.Triangles.Select(i => (JsonNode)i).ToArray()),
                }).ToArray()),
            };
        }
        if (mdl.FlexCount > 0) gltf.Extras["flexesNotImported"] = mdl.FlexCount;

        var glb = gltf.ToGlb();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, glb);
        return new ModelImportResult(donorPath, file, mdl.Bones.Length, triangles, clips.Count, glb.Length, sequences.Select(s => s.Sequence.Label).ToList());
    }

    /// <summary>
    /// The items, in order, that fit the Engine's per-GLB keyframe and value
    /// budgets together. Once one doesn't fit it and every later one is
    /// reported and left out, so the clips kept are a prefix of the model's.
    /// </summary>
    internal static List<T> WithinBudget<T>(IEnumerable<T> items, Func<T, (int Keyframes, int Values)> cost, Action<T> over)
    {
        var kept = new List<T>();
        long keyframes = 0, values = 0;
        bool full = false;
        foreach (var item in items)
        {
            var c = cost(item);
            if (!full && keyframes + c.Keyframes <= MaxKeyframes && values + c.Values <= MaxValues)
            {
                kept.Add(item);
                keyframes += c.Keyframes; values += c.Values;
            }
            else { full = true; over(item); }
        }
        return kept;
    }

    /// <summary>One node per bone, parent-relative rest transforms; returns node indices by bone.</summary>
    static int[] Skeleton(GltfBuilder gltf, StudioModelFile mdl)
    {
        var joints = new int[mdl.Bones.Length];
        for (int i = 0; i < mdl.Bones.Length; i++)
        {
            var bone = mdl.Bones[i];
            var t = SourceSpace.Position(bone.Position);
            var r = Quaternion.Normalize(SourceSpace.Rotation(bone.Rotation));
            joints[i] = gltf.Node(new JsonObject
            {
                ["name"] = bone.Name,
                ["translation"] = GltfBuilder.FloatArray(t.X, t.Y, t.Z),
                ["rotation"] = GltfBuilder.FloatArray(r.X, r.Y, r.Z, r.W),
            }, bone.Parent >= 0 ? joints[bone.Parent] : null);
        }
        return joints;
    }

    JsonArray Primitives(StudioModelFile mdl, string stem, GltfBuilder gltf, ref int triangles)
    {
        var primitives = new JsonArray();
        var vvdBytes = _files.Read(stem + ".vvd");
        var vtxBytes = _files.Read(stem + ".dx90.vtx") ?? _files.Read(stem + ".dx80.vtx") ?? _files.Read(stem + ".sw.vtx");
        if (mdl.BodyParts.Length == 0) return primitives;
        if (vvdBytes is null || vtxBytes is null) { _log.Warn($"{mdl.Path}: no .vvd/.vtx; skeleton and clips only"); return primitives; }
        var vertices = StudioVertexFile.Read(stem + ".vvd", vvdBytes);
        var strips = StudioStripFile.Read(stem + ".vtx", vtxBytes);
        var skin = mdl.SkinFamilies.Length > 0 ? mdl.SkinFamilies[0] : null;
        int dropped = 0;

        for (int p = 0; p < mdl.BodyParts.Length && p < strips.Length; p++)
        {
            var part = mdl.BodyParts[p];
            if (part.Models.Length > 1) _log.Warn($"{mdl.Path}: body part {part.Name} has {part.Models.Length} choices; the default (0) is imported");
            if (part.Models.Length == 0 || strips[p].Length == 0) continue;
            var model = part.Models[0];
            for (int k = 0; k < model.Meshes.Length && k < strips[p][0].Length; k++)
            {
                var mesh = model.Meshes[k];
                var corners = strips[p][0][k];
                if (corners.Count == 0) continue;
                int texture = skin is not null && mesh.Material < skin.Length ? skin[mesh.Material] : mesh.Material;
                string materialPath = MaterialPath(mdl, texture);
                if (mesh.MaterialType == 1) _log.Warn($"{mdl.Path}: eyeball mesh {materialPath} keeps its authored uvs (the donor projects eye textures at run time)");

                var remap = new Dictionary<int, uint>();
                var positions = new List<float>(); var normals = new List<float>(); var uvs = new List<float>();
                var jointIds = new List<ushort>(); var weights = new List<float>();
                var indices = new List<uint>(corners.Count);
                foreach (int corner in corners)
                {
                    int source = model.FirstVertex + mesh.VertexOffset + corner;
                    if (!remap.TryGetValue(source, out uint index))
                    {
                        if (source < 0 || source >= vertices.Length) throw new InvalidDataException($"{mdl.Path}: vertex {source} past the vertex file's {vertices.Length}");
                        index = remap[source] = (uint)remap.Count;
                        var v = vertices[source];
                        var pos = SourceSpace.Position(v.Position);
                        var n = SourceSpace.Direction(v.Normal);
                        n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                        positions.AddRange([pos.X, pos.Y, pos.Z]);
                        normals.AddRange([n.X, n.Y, n.Z]);
                        uvs.AddRange([v.Uv.X, v.Uv.Y]);
                        Span<float> w = [v.BoneCount > 0 ? v.Weight0 : 1, v.BoneCount > 1 ? v.Weight1 : 0, v.BoneCount > 2 ? v.Weight2 : 0];
                        Span<byte> b = [v.Bone0, v.BoneCount > 1 ? v.Bone1 : (byte)0, v.BoneCount > 2 ? v.Bone2 : (byte)0];
                        float sum = w[0] + w[1] + w[2];
                        if (sum <= 0) { w[0] = 1; w[1] = w[2] = 0; sum = 1; }
                        for (int j = 0; j < 3; j++)
                        {
                            if (b[j] >= mdl.Bones.Length) throw new InvalidDataException($"{mdl.Path}: vertex {source} names bone {b[j]} of {mdl.Bones.Length}");
                            jointIds.Add(w[j] > 0 ? b[j] : (ushort)0);
                            weights.Add(w[j] / sum);
                        }
                        jointIds.Add(0); weights.Add(0);
                    }
                    indices.Add(index);
                }
                // Source draws clockwise triangles as front faces; glTF's are counter-clockwise.
                for (int t = 0; t + 2 < indices.Count; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
                dropped += Triangles.RemoveDegenerate(indices, positions);
                if (indices.Count == 0) continue;
                triangles += indices.Count / 3;
                primitives.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = gltf.Floats(positions.ToArray(), "VEC3", bounds: true, vertexData: true),
                        ["NORMAL"] = gltf.Floats(normals.ToArray(), "VEC3", vertexData: true),
                        ["TEXCOORD_0"] = gltf.Floats(uvs.ToArray(), "VEC2", vertexData: true),
                        ["JOINTS_0"] = gltf.Joints(jointIds.ToArray(), mdl.Bones.Length),
                        ["WEIGHTS_0"] = gltf.Floats(weights.ToArray(), "VEC4", vertexData: true),
                    },
                    ["indices"] = gltf.Indices(indices.ToArray()),
                    ["material"] = _materials.AddTo(gltf, materialPath, MaxTextureSize),
                });
            }
        }
        if (dropped > 0) _log.Warn($"{mdl.Path}: {dropped} degenerate triangles dropped");
        return primitives;
    }

    /// <summary>The material a texture slot names: the first texture directory that has it, as the engine searches.</summary>
    string MaterialPath(StudioModelFile mdl, int texture)
    {
        string name = texture >= 0 && texture < mdl.Textures.Length ? mdl.Textures[texture] : $"missing_texture_{texture}";
        foreach (var dir in mdl.TextureDirectories)
        {
            string path = Materials.Normalize(dir + "/" + name);
            if (_materials.Exists(path)) return path;
        }
        return Materials.Normalize((mdl.TextureDirectories.FirstOrDefault() ?? "") + "/" + name);
    }

    /// <summary>
    /// Sequences in the order the engine numbers them (virtualmodel_t): the
    /// model's own, then each include's depth first, a name already present
    /// skipped. Each comes with the file whose animations it plays.
    /// </summary>
    List<(StudioModelFile File, StudioSequence Sequence)> Sequences(StudioModelFile root)
    {
        var result = new List<(StudioModelFile, StudioSequence)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>();
        void Append(StudioModelFile file)
        {
            if (!visited.Add(VpkIndex.Normalize(file.Path))) return;
            foreach (var s in file.Sequences) if (seen.Add(s.Label)) result.Add((file, s));
            foreach (var include in file.Includes)
            {
                var b = _files.Read(include);
                if (b is null) { _log.Warn($"{root.Path}: $includemodel {include} missing"); continue; }
                Append(new StudioModelFile(include, b));
            }
        }
        Append(root);
        return result;
    }

    /// <summary>One sequence sampled on the root model's skeleton, or null when it has nothing to play.</summary>
    PendingClip? Clip(StudioModelFile root, StudioModelFile file, StudioSequence seq, int number)
    {
        // A blend grid plays one animation here: its centre, the neutral pose of an aim or look matrix.
        int center = (seq.Rows / 2) * seq.Columns + seq.Columns / 2;
        int animIndex = seq.Animations[Math.Min(center, seq.Animations.Length - 1)];
        if (animIndex < 0 || animIndex >= file.Animations.Length) { _log.Warn($"{file.Path}: sequence {seq.Label} plays missing animation {animIndex}"); return null; }
        if (seq.Animations.Length > 1) _log.Warn($"{root.Path}: blended sequences ({seq.Columns}x{seq.Rows}) play their centre animation only");
        var anim = file.Animations[animIndex];

        // The file's bones by name onto the root's skeleton.
        var rootByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < root.Bones.Length; i++) rootByName.TryAdd(root.Bones[i].Name, i);
        var map = file.Bones.Select(b => rootByName.GetValueOrDefault(b.Name, -1)).ToArray();

        int frames = Math.Max(1, anim.Frames);
        float fps = anim.Fps > 0 ? anim.Fps : 30;
        int keys = Math.Max(2, frames);
        var times = new float[keys];
        for (int f = 0; f < keys; f++) times[f] = f / fps;
        var pos = new Vector3[file.Bones.Length];
        var rot = new Quaternion[file.Bones.Length];
        var t = new Vector3[file.Bones.Length, keys];
        var r = new Quaternion[file.Bones.Length, keys];
        bool delta = anim.IsDelta || (seq.Flags & StudioSequence.Delta) != 0;
        bool post = (seq.Flags & StudioSequence.Post) != 0;
        for (int f = 0; f < keys; f++)
        {
            try { file.Pose(anim, Math.Min(f, frames - 1), pos, rot); }
            catch (NotSupportedException e) { _log.Warn(e.Message); return null; }
            for (int b = 0; b < file.Bones.Length; b++)
            {
                var p = pos[b];
                var q = rot[b];
                if (delta)
                {
                    // Applied to the rest pose as the engine layers deltas: after it for "post" layers, before it otherwise.
                    var rest = map[b] >= 0 ? root.Bones[map[b]] : file.Bones[b];
                    p = rest.Position + p;
                    q = post ? rest.Rotation * q : q * rest.Rotation;
                }
                t[b, f] = SourceSpace.Position(p);
                var g = Quaternion.Normalize(SourceSpace.Rotation(q));
                if (f > 0 && Quaternion.Dot(g, r[b, f - 1]) < 0) g = Quaternion.Negate(g);
                r[b, f] = g;
            }
        }

        var channels = new List<(int, string, float[], bool)>();
        var driven = new HashSet<int>();
        for (int b = 0; b < file.Bones.Length; b++)
        {
            if (map[b] < 0 || !driven.Add(map[b])) continue;
            if (seq.BoneWeights is not null && b < seq.BoneWeights.Length && seq.BoneWeights[b] <= 0) continue;
            bool constantT = true, constantR = true;
            for (int f = 1; f < keys; f++)
            {
                if (Vector3.DistanceSquared(t[b, f], t[b, 0]) > 1e-12f) constantT = false;
                if (MathF.Abs(Quaternion.Dot(r[b, f], r[b, 0])) < 1 - 1e-7f) constantR = false;
            }
            // A translation that never leaves the bone's rest adds nothing the
            // node doesn't hold already; Source bones without position data
            // sit at their default in every sequence.
            bool restT = constantT && Vector3.DistanceSquared(t[b, 0], SourceSpace.Position(root.Bones[map[b]].Position)) <= 1e-12f;
            if (!restT)
            {
                var tv = new List<float>();
                for (int f = 0; f < (constantT ? 2 : keys); f++) { var v = t[b, constantT ? 0 : f]; tv.AddRange([v.X, v.Y, v.Z]); }
                channels.Add((map[b], "translation", tv.ToArray(), constantT));
            }
            var rv = new List<float>();
            for (int f = 0; f < (constantR ? 2 : keys); f++) { var v = r[b, constantR ? 0 : f]; rv.AddRange([v.X, v.Y, v.Z, v.W]); }
            channels.Add((map[b], "rotation", rv.ToArray(), constantR));
        }

        // A sequence weighted to no bones (an event or posture placeholder)
        // still gets a clip, so every sequence name resolves: the first mapped
        // bone held at rest, marked empty.
        bool empty = channels.Count == 0;
        if (empty)
        {
            int b = Array.FindIndex(map, m => m >= 0);
            if (b < 0) { _log.Warn($"{root.Path}: sequence {seq.Label} shares no bones with the model"); return null; }
            var ro = Quaternion.Normalize(SourceSpace.Rotation(root.Bones[map[b]].Rotation));
            channels.Add((map[b], "rotation", [ro.X, ro.Y, ro.Z, ro.W, ro.X, ro.Y, ro.Z, ro.W], true));
        }

        var extras = new JsonObject
        {
            ["sequence"] = number,
            ["activity"] = seq.Activity,
            ["fps"] = fps,
            ["frames"] = frames,
            ["looping"] = (seq.Flags & StudioSequence.Looping) != 0 || (anim.Flags & StudioAnimation.Looping) != 0,
            ["delta"] = delta,
            ["animation"] = anim.Name,
            ["file"] = file.Path,
            ["blend"] = GltfBuilder.FloatArray(seq.Columns, seq.Rows),
            ["fadeIn"] = seq.FadeIn,
            ["fadeOut"] = seq.FadeOut,
            ["weighted"] = seq.BoneWeights is not null && seq.BoneWeights.Any(w => w <= 0),
            ["empty"] = empty,
        };
        if (Movement(anim, frames) is { } movement) extras["movement"] = movement;
        return new PendingClip(seq.Label, times, channels, extras);
    }

    /// <summary>
    /// The clip's root motion, when its animation has any: [cycle, x, y, angle] at each piece's end, cumulative, in the
    /// model's frame (Source units, X forward, Y left; degrees). The lab moves an NPC by it: the orc's attack_Jump
    /// carries it 93 units, as the recordings show its origin travel (#10150).
    /// </summary>
    internal static JsonNode? Movement(StudioAnimation anim, int frames) =>
        anim.Movements is { Length: > 0 } pieces && pieces.Any(m => m.Position != Vector3.Zero || m.Angle != 0)
            ? new JsonArray(pieces.Select(m => (JsonNode)GltfBuilder.FloatArray(frames > 1 ? Math.Clamp(m.EndFrame / (float)(frames - 1), 0, 1) : 1,
                m.Position.X, m.Position.Y, m.Angle)).ToArray())
            : null;

    static void Write(GltfBuilder gltf, int[] joints, PendingClip clip)
    {
        int times = gltf.Floats(clip.Times, "SCALAR", bounds: true);
        int? ends = null;
        var samplers = new JsonArray();
        var channels = new JsonArray();
        foreach (var (joint, path, values, constant) in clip.Channels)
        {
            int input = constant ? ends ??= gltf.Floats([0, clip.Times[^1]], "SCALAR", bounds: true) : times;
            samplers.Add(new JsonObject { ["input"] = input, ["output"] = gltf.Floats(values, path == "rotation" ? "VEC4" : "VEC3"), ["interpolation"] = "LINEAR" });
            channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = joints[joint], ["path"] = path } });
        }
        gltf.Animation(new JsonObject { ["name"] = clip.Name, ["samplers"] = samplers, ["channels"] = channels, ["extras"] = clip.Extras.DeepClone() });
    }
}
