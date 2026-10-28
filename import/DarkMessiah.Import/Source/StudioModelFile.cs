using RustyBridge.Wire.Donor;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DarkMessiah.Import.Source;

public sealed record StudioBone(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Euler,
    Vector3 PositionScale, Vector3 EulerScale, float[] PoseToBone, int Flags, int ProceduralType);

/// <summary>
/// One piece of an animation's root motion (mstudiomovement_t, 44 bytes): from the last piece's end to
/// <paramref name="EndFrame"/> the model moves to <paramref name="Position"/> (cumulative, in the model's own frame,
/// Source units: X forward, Y left) and turns to <paramref name="Angle"/> degrees. Source strips this from the root
/// bone and moves the entity by it.
/// </summary>
public sealed record StudioMovement(int EndFrame, int Flags, float Angle, Vector3 Position);

public sealed record StudioAnimation(string Name, float Fps, int Flags, int Frames, int DataOffset, int Block, int SectionFrames,
    StudioMovement[]? Movements = null)
{
    public const int Looping = 0x1, Delta = 0x4, AllZeros = 0x20;
    public bool IsDelta => (Flags & Delta) != 0;
}

/// <summary>
/// A sequence: a blend grid of animations (row-major, <see cref="Columns"/> wide) and an optional per-bone weight list.
/// <see cref="Parameters"/> are what its columns and rows blend over: an index into its file's
/// <see cref="StudioModelFile.PoseParameters"/> (-1 for none) and the values at its first and last column or row.
/// </summary>
public sealed record StudioSequence(string Label, string Activity, int Flags, int Columns, int Rows, short[] Animations, float[]? BoneWeights,
    float FadeIn, float FadeOut, int EventCount, (int Index, float Start, float End)[]? Parameters = null)
{
    public const int Looping = 0x1, Delta = 0x4, Autoplay = 0x8, Post = 0x10;
}

/// <summary>A pose parameter: what a blend grid's column or row runs over, from <paramref name="Start"/> to <paramref name="End"/>, wrapping every <paramref name="Loop"/> when it loops (0 when not).</summary>
public sealed record StudioPoseParameter(string Name, int Flags, float Start, float End, float Loop);

/// <summary>A named point on a bone (weapon grips, eyes, effects): a Source 3×4 transform local to the bone.</summary>
public sealed record StudioAttachment(string Name, int Bone, float[] Local);

/// <summary>
/// A hit box of the model's first hitbox set ("default", which traces test):
/// a box on a bone, from <paramref name="Min"/> to <paramref name="Max"/> in
/// the bone's frame (Source units), and the hit group a trace reports for it.
/// </summary>
public sealed record StudioHitbox(int Bone, int Group, Vector3 Min, Vector3 Max);

public sealed record StudioMesh(int Material, int VertexOffset, int VertexCount, int MaterialType);

public sealed record StudioSubModel(string Name, int FirstVertex, int VertexCount, StudioMesh[] Meshes);

public sealed record StudioBodyPart(string Name, StudioSubModel[] Models);

/// <summary>
/// A Source studio model header (.mdl version 44, the Episode One layout
/// Dark Messiah ships): skeleton, animations, sequences, materials and body
/// parts. Geometry lives in the .vvd and .vtx beside it.
/// </summary>
public sealed class StudioModelFile
{
    const int MovementBytes = 44;
    const int BoneBytes = 216, AnimDescBytes = 100, SeqDescBytes = 212, TextureBytes = 64, BodyPartBytes = 16, ModelBytes = 148, MeshBytes = 116, VertexBytes = 48, AttachmentBytes = 92,
        HitboxBytes = 68;

    readonly byte[] _b;
    public string Path { get; }
    public int Version { get; }
    public string InternalName { get; }
    public Vector3 EyePosition { get; }
    public StudioBone[] Bones { get; }
    public StudioAnimation[] Animations { get; }
    public StudioSequence[] Sequences { get; }
    public string[] Textures { get; }
    public string[] TextureDirectories { get; }
    public short[][] SkinFamilies { get; }
    public StudioBodyPart[] BodyParts { get; }
    public string[] Includes { get; }
    public StudioAttachment[] Attachments { get; }
    /// <summary>The first hitbox set's boxes, in their order: a trace's hitbox index counts them.</summary>
    public StudioHitbox[] Hitboxes { get; }
    public int FlexCount { get; }
    /// <summary>The model's own pose parameters, in its order: what its sequences' blend grids name.</summary>
    public StudioPoseParameter[] PoseParameters { get; }

    public StudioModelFile(string path, byte[] b)
    {
        Path = path;
        _b = b;
        if (b.Length < 408 || Encoding.ASCII.GetString(b, 0, 4) != "IDST") throw new InvalidDataException($"{path}: not a studio model");
        Version = I(4);
        if (Version != 44) throw new InvalidDataException($"{path}: studio model version {Version}, this reader knows 44");
        InternalName = Str(12);
        EyePosition = V3(80);

        Bones = new StudioBone[I(156)];
        for (int i = 0; i < Bones.Length; i++)
        {
            int o = I(160) + i * BoneBytes;
            var pose = new float[12];
            for (int k = 0; k < 12; k++) pose[k] = F(o + 96 + 4 * k);
            Bones[i] = new StudioBone(Str(o + I(o)), I(o + 4), V3(o + 32), new Quaternion(F(o + 44), F(o + 48), F(o + 52), F(o + 56)),
                V3(o + 60), V3(o + 72), V3(o + 84), pose, I(o + 160), I(o + 164));
        }

        Animations = new StudioAnimation[I(180)];
        for (int i = 0; i < Animations.Length; i++)
        {
            int o = I(184) + i * AnimDescBytes;
            // nummovements at +20, movementindex at +24 (relative to the animdesc).
            var movements = new StudioMovement[Math.Max(0, I(o + 20))];
            for (int m = 0; m < movements.Length; m++)
            {
                int p = o + I(o + 24) + m * MovementBytes;
                movements[m] = new StudioMovement(I(p), I(p + 4), F(p + 16), V3(p + 32));
            }
            Animations[i] = new StudioAnimation(Str(o + I(o + 4)), F(o + 8), I(o + 12), I(o + 16), o + I(o + 56), I(o + 52), I(o + 84), movements);
        }

        Sequences = new StudioSequence[I(188)];
        for (int i = 0; i < Sequences.Length; i++)
        {
            int o = I(192) + i * SeqDescBytes;
            int columns = Math.Max(1, I(o + 68)), rows = Math.Max(1, I(o + 72));
            var anims = new short[columns * rows];
            for (int k = 0; k < anims.Length; k++) anims[k] = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(o + I(o + 60) + 2 * k));
            float[]? weights = null;
            int weightList = I(o + 156);
            if (weightList != 0)
            {
                weights = new float[Bones.Length];
                for (int k = 0; k < weights.Length; k++) weights[k] = F(o + weightList + 4 * k);
            }
            // paramindex[2] at +76, paramstart[2] at +84, paramend[2] at +92.
            var parameters = new[] { (I(o + 76), F(o + 84), F(o + 92)), (I(o + 80), F(o + 88), F(o + 96)) };
            Sequences[i] = new StudioSequence(Str(o + I(o + 4)), Str(o + I(o + 8)), I(o + 12), columns, rows, anims, weights,
                F(o + 104), F(o + 108), I(o + 24), parameters);
        }

        // numlocalposeparameters at +300, localposeparamindex at +304: 20 bytes each (name, flags, start, end, loop).
        PoseParameters = new StudioPoseParameter[Math.Max(0, I(300))];
        for (int i = 0; i < PoseParameters.Length; i++)
        {
            int o = I(304) + i * 20;
            PoseParameters[i] = new StudioPoseParameter(Str(o + I(o)), I(o + 4), F(o + 8), F(o + 12), F(o + 16));
        }

        Textures = new string[I(204)];
        for (int i = 0; i < Textures.Length; i++) { int o = I(208) + i * TextureBytes; Textures[i] = Str(o + I(o)); }
        TextureDirectories = new string[I(212)];
        for (int i = 0; i < TextureDirectories.Length; i++) TextureDirectories[i] = Str(I(I(216) + 4 * i));
        int skinRefs = I(220);
        SkinFamilies = new short[I(224)][];
        for (int f = 0; f < SkinFamilies.Length; f++)
        {
            SkinFamilies[f] = new short[skinRefs];
            for (int k = 0; k < skinRefs; k++) SkinFamilies[f][k] = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(I(228) + 2 * (f * skinRefs + k)));
        }

        BodyParts = new StudioBodyPart[I(232)];
        for (int p = 0; p < BodyParts.Length; p++)
        {
            int o = I(236) + p * BodyPartBytes;
            var models = new StudioSubModel[I(o + 4)];
            for (int m = 0; m < models.Length; m++)
            {
                int mo = o + I(o + 12) + m * ModelBytes;
                var meshes = new StudioMesh[I(mo + 72)];
                for (int k = 0; k < meshes.Length; k++)
                {
                    int me = mo + I(mo + 76) + k * MeshBytes;
                    meshes[k] = new StudioMesh(I(me), I(me + 12), I(me + 8), I(me + 24));
                }
                models[m] = new StudioSubModel(Str(mo), I(mo + 84) / VertexBytes, I(mo + 80), meshes);
            }
            BodyParts[p] = new StudioBodyPart(Str(o + I(o)), models);
        }

        Attachments = new StudioAttachment[I(240)];
        for (int i = 0; i < Attachments.Length; i++)
        {
            int o = I(244) + i * AttachmentBytes;
            var local = new float[12];
            for (int k = 0; k < 12; k++) local[k] = F(o + 12 + 4 * k);
            Attachments[i] = new StudioAttachment(Str(o + I(o)), I(o + 8), local);
        }

        // mstudiohitboxset_t (name, count, offset) at numhitboxsets +172, hitboxsetindex +176; each
        // mstudiobbox_t is bone, group, bbmin, bbmax, name offset and 8 unused ints.
        Hitboxes = [];
        if (I(172) > 0)
        {
            int set = I(176), at = set + I(set + 8);
            Hitboxes = new StudioHitbox[I(set + 4)];
            for (int i = 0; i < Hitboxes.Length; i++)
            {
                int o = at + i * HitboxBytes;
                Hitboxes[i] = new StudioHitbox(I(o), I(o + 4), new Vector3(F(o + 8), F(o + 12), F(o + 16)), new Vector3(F(o + 20), F(o + 24), F(o + 28)));
            }
        }

        FlexCount = I(260);
        Includes = new string[I(336)];
        for (int k = 0; k < Includes.Length; k++) { int o = I(340) + k * 8; Includes[k] = Str(o + I(o + 4)); }
    }

    /// <summary>
    /// One frame of an animation as parent-relative Source bone transforms.
    /// A delta animation gives deltas (identity where it has no data); any
    /// other gives full local transforms (the bone's default where it has
    /// none). Follows CalcBoneQuaternion/CalcBonePosition.
    /// </summary>
    public void Pose(StudioAnimation anim, int frame, Span<Vector3> positions, Span<Quaternion> rotations)
    {
        if (anim.Block != 0) throw new NotSupportedException($"{Path}: {anim.Name} lives in an external animation block");
        if (anim.SectionFrames != 0) throw new NotSupportedException($"{Path}: {anim.Name} is split into sections");
        bool delta = anim.IsDelta;
        for (int i = 0; i < Bones.Length; i++)
        {
            positions[i] = delta ? Vector3.Zero : Bones[i].Position;
            rotations[i] = delta ? Quaternion.Identity : Bones[i].Rotation;
        }
        if ((anim.Flags & StudioAnimation.AllZeros) != 0) return;
        frame = Math.Clamp(frame, 0, Math.Max(0, anim.Frames - 1));

        const int RawPos = 0x1, RawRot = 0x2, AnimPos = 0x4, AnimRot = 0x8, RawRot2 = 0x20;
        int p = anim.DataOffset;
        while (true)
        {
            int bone = _b[p], flags = _b[p + 1];
            int next = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(p + 2));
            if (bone < Bones.Length)
            {
                var b = Bones[bone];
                int data = p + 4;
                if ((flags & RawRot) != 0) { rotations[bone] = Quaternion48(data); data += 6; }
                else if ((flags & RawRot2) != 0) { rotations[bone] = Quaternion64(data); data += 8; }
                else if ((flags & AnimRot) != 0)
                {
                    var angle = new Vector3(
                        AnimValue(data, 0, frame) * b.EulerScale.X,
                        AnimValue(data, 1, frame) * b.EulerScale.Y,
                        AnimValue(data, 2, frame) * b.EulerScale.Z);
                    if (!delta) angle += b.Euler;
                    rotations[bone] = SourceSpace.FromRadianEuler(angle);
                    data += 6;
                }
                if ((flags & RawPos) != 0) positions[bone] = Vector48(data);
                else if ((flags & AnimPos) != 0)
                {
                    var pos = new Vector3(
                        AnimValue(data, 0, frame) * b.PositionScale.X,
                        AnimValue(data, 1, frame) * b.PositionScale.Y,
                        AnimValue(data, 2, frame) * b.PositionScale.Z);
                    positions[bone] = delta ? pos : pos + b.Position;
                }
            }
            if (next == 0) break;
            p += next;
        }
    }

    /// <summary>ExtractAnimValue: run-length encoded shorts behind an mstudioanim_valueptr_t.</summary>
    float AnimValue(int valuePtr, int axis, int frame)
    {
        int offset = BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(valuePtr + 2 * axis));
        if (offset == 0) return 0;
        int v = valuePtr + offset, k = frame;
        while (_b[v + 1] <= k)
        {
            k -= _b[v + 1];
            v += (_b[v] + 1) * 2;
            if (_b[v + 1] == 0) return 0;
        }
        int valid = _b[v];
        int at = valid > k ? v + (k + 1) * 2 : v + valid * 2;
        return BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(at));
    }

    Quaternion Quaternion48(int o)
    {
        int x = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(o)), y = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(o + 2));
        int zw = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(o + 4));
        float qx = (x - 32768) / 32768f, qy = (y - 32768) / 32768f, qz = ((zw & 0x7FFF) - 16384) / 16384f;
        float w = MathF.Sqrt(Math.Max(0, 1 - qx * qx - qy * qy - qz * qz));
        return new Quaternion(qx, qy, qz, (zw & 0x8000) != 0 ? -w : w);
    }

    Quaternion Quaternion64(int o)
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_b.AsSpan(o));
        float qx = ((long)(v & 0x1FFFFF) - 1048576) / 1048576.5f;
        float qy = ((long)((v >> 21) & 0x1FFFFF) - 1048576) / 1048576.5f;
        float qz = ((long)((v >> 42) & 0x1FFFFF) - 1048576) / 1048576.5f;
        float w = MathF.Sqrt(Math.Max(0, 1 - qx * qx - qy * qy - qz * qz));
        return new Quaternion(qx, qy, qz, (v >> 63) != 0 ? -w : w);
    }

    Vector3 Vector48(int o) => new(
        (float)BinaryPrimitives.ReadHalfLittleEndian(_b.AsSpan(o)),
        (float)BinaryPrimitives.ReadHalfLittleEndian(_b.AsSpan(o + 2)),
        (float)BinaryPrimitives.ReadHalfLittleEndian(_b.AsSpan(o + 4)));

    int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(o));
    float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(o));
    Vector3 V3(int o) => new(F(o), F(o + 4), F(o + 8));
    string Str(int o)
    {
        if (o <= 0 || o >= _b.Length) return "";
        int end = Array.IndexOf(_b, (byte)0, o);
        return Encoding.Latin1.GetString(_b, o, (end < 0 ? _b.Length : end) - o);
    }
}
