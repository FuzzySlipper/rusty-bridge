using System.Numerics;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Source;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>What one model import produced: the primary GLB, then its clip packs in order.</summary>
public sealed record ModelImportResult(string DonorPath, IReadOnlyList<string> Files, int Bones, int Triangles, int Clips, long Bytes, IReadOnlyList<string> Sequences);

/// <summary>
/// A studio model (.mdl v44 + .vvd + .dx90.vtx) as skinned GLBs: every bone
/// a joint node, body part 0 of each group as primitives per material, and
/// every sequence of the model and its $includemodel files as a clip named by
/// its Source sequence name. Clips are in place: Source strips linear
/// movement out of the root bone and moves the entity instead.
///
/// The Engine admits at most 64 clips and 4,096 channels per GLB, and a
/// character has hundreds of sequences. The first GLB holds the mesh and the
/// first clips; the rest go into clip packs (same skeleton, a one-triangle
/// stand-in mesh) that a product associates with it
/// (<c>AnimationClipPackAssociationRequest</c>).
/// </summary>
public sealed class ModelImport
{
    /// <summary>The Engine's per-GLB import limits (voxel-convert animation.rs).</summary>
    public const int MaxClips = 64, MaxChannels = 4_096, MaxKeyframes = 1_000_000, MaxValues = 4_000_000;

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

    /// <param name="fileFor">Output file for GLB n: 0 is the model, 1.. its clip packs.</param>
    public ModelImportResult Import(string donorPath, Func<int, string> fileFor)
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

        // Pack clips in sequence order, each GLB under the Engine's limits.
        var packs = Pack(clips, c => (c.Channels.Count, c.Keyframes, c.Values), c => _log.Warn($"{donorPath}: clip {c.Name} alone exceeds the Engine's per-GLB animation limits; skipped"));

        var files = new List<string>();
        long total = 0;
        int triangles = 0;
        for (int p = 0; p < packs.Count; p++)
        {
            var gltf = new GltfBuilder();
            var joints = Skeleton(gltf, mdl);
            int skin = gltf.Skin(new JsonObject
            {
                ["joints"] = new JsonArray(joints.Select(j => (JsonNode)j).ToArray()),
                ["inverseBindMatrices"] = gltf.Floats(mdl.Bones.SelectMany(b => SourceSpace.ColumnMajor(SourceSpace.Transform(b.PoseToBone))).ToArray(), "MAT4"),
            });
            string name = Path.GetFileName(stem).ToLowerInvariant();
            var primitives = p == 0 ? Primitives(mdl, stem, gltf, ref triangles) : [];
            if (primitives.Count == 0) primitives.Add(StandIn(gltf));
            gltf.Node(new JsonObject { ["name"] = name, ["mesh"] = gltf.Mesh(new JsonObject { ["name"] = name, ["primitives"] = primitives }), ["skin"] = skin });
            foreach (var clip in packs[p]) Write(gltf, joints, clip);

            gltf.Extras["donorPath"] = donorPath;
            gltf.Extras["convention"] = SourceSpace.Convention;
            if (p == 0)
            {
                var eye = SourceSpace.Position(mdl.EyePosition);
                gltf.Extras["eyePosition"] = GltfBuilder.FloatArray(eye.X, eye.Y, eye.Z);
                gltf.Extras["sequences"] = new JsonArray(sequences.Select(s => (JsonNode)s.Sequence.Label).ToArray());
                gltf.Extras["clipPacks"] = packs.Count - 1;
                var attachments = new JsonArray();
                foreach (var a in mdl.Attachments)
                    attachments.Add(new JsonObject
                    {
                        ["name"] = a.Name,
                        ["joint"] = a.Bone >= 0 && a.Bone < mdl.Bones.Length ? mdl.Bones[a.Bone].Name : null,
                        ["matrix"] = GltfBuilder.FloatArray(SourceSpace.ColumnMajor(SourceSpace.Transform(a.Local))),
                    });
                if (attachments.Count > 0) gltf.Extras["attachments"] = attachments;
                if (mdl.FlexCount > 0) gltf.Extras["flexesNotImported"] = mdl.FlexCount;
            }
            else
            {
                gltf.Extras["clipPackOf"] = Path.GetFileName(fileFor(0));
                gltf.Extras["clipPack"] = p;
            }

            var glb = gltf.ToGlb();
            string file = fileFor(p);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, glb);
            files.Add(file);
            total += glb.Length;
        }
        return new ModelImportResult(donorPath, files, mdl.Bones.Length, triangles, clips.Count, total, sequences.Select(s => s.Sequence.Label).ToList());
    }

    /// <summary>
    /// Splits items, in order, into groups under the Engine's per-GLB clip,
    /// channel, keyframe and value limits. An item over a limit on its own is
    /// reported and left out. There is always at least one group.
    /// </summary>
    internal static List<List<T>> Pack<T>(IEnumerable<T> items, Func<T, (int Channels, int Keyframes, int Values)> cost, Action<T> tooLarge)
    {
        var packs = new List<List<T>> { new() };
        int channels = 0, keyframes = 0, values = 0;
        foreach (var item in items)
        {
            var c = cost(item);
            if (c.Channels > MaxChannels || c.Keyframes > MaxKeyframes || c.Values > MaxValues) { tooLarge(item); continue; }
            if (packs[^1].Count + 1 > MaxClips || channels + c.Channels > MaxChannels || keyframes + c.Keyframes > MaxKeyframes || values + c.Values > MaxValues)
            {
                packs.Add([]);
                channels = keyframes = values = 0;
            }
            packs[^1].Add(item);
            channels += c.Channels; keyframes += c.Keyframes; values += c.Values;
        }
        return packs;
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

    /// <summary>A clip pack's mesh: one centimetre triangle on the first joint, which the Engine's animated admission needs to find the rig.</summary>
    static JsonObject StandIn(GltfBuilder gltf) => new()
    {
        ["attributes"] = new JsonObject
        {
            ["POSITION"] = gltf.Floats([0, 0, 0, 0.01f, 0, 0, 0, 0.01f, 0], "VEC3", bounds: true, vertexData: true),
            ["JOINTS_0"] = gltf.Joints(new ushort[12], 1),
            ["WEIGHTS_0"] = gltf.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4", vertexData: true),
        },
        ["indices"] = gltf.Indices([0, 1, 2]),
    };

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

        return new PendingClip(seq.Label, times, channels, new JsonObject
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
        });
    }

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
