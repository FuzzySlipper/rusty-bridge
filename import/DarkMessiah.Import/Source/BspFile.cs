using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DarkMessiah.Import.Source;

public readonly record struct BspFace(int Plane, bool Side, int FirstEdge, int EdgeCount, int TexInfo, int DispInfo, int LightOffset);

/// <summary>texinfo: texture axes in texels per unit (s and t: xyz and offset), surface flags and the texdata it uses.</summary>
public readonly record struct BspTexInfo(Vector4 S, Vector4 T, int Flags, int TexData)
{
    public const int Sky2D = 0x2, Sky = 0x4, Trigger = 0x40, NoDraw = 0x80, Hint = 0x100, Skip = 0x200;
}

public readonly record struct BspTexData(string Material, int Width, int Height);

public readonly record struct BspModel(Vector3 Mins, Vector3 Maxs, Vector3 Origin, int FirstFace, int FaceCount);

public readonly record struct BspDispInfo(Vector3 Start, int FirstVert, int Power);

/// <summary>
/// A Source map (VBSP version 20). Dark Messiah's maps carry 4 in the
/// version's high word and change two lumps: texinfo is 96 bytes (six extra
/// floats between the texture and lightmap axes) and a model is 52 bytes (an
/// extra word before headnode). Either layout is recognised by lump size.
/// </summary>
public sealed class BspFile
{
    const int Faces = 7, Planes = 1, TexDataLump = 2, Vertexes = 3, TexInfoLump = 6, Edges = 12, SurfEdges = 13, Models = 14,
        DispInfoLump = 26, DispVerts = 33, Entities = 0, PakFile = 40, TexDataStrings = 43, TexDataTable = 44;

    readonly byte[] _b;
    public int Version { get; }
    public int ArkaneRevision { get; }
    public Vector3[] Vertices { get; }
    public (Vector3 Normal, float Distance)[] PlaneList { get; }
    public (int A, int B)[] EdgeList { get; }
    public int[] SurfEdgeList { get; }
    public BspFace[] FaceList { get; }
    public BspTexInfo[] TexInfos { get; }
    public BspTexData[] TexDatas { get; }
    public BspModel[] ModelList { get; }
    public BspDispInfo[] DispInfos { get; }
    public (Vector3 Offset, float Distance)[] DispVertList { get; }
    public string EntityText { get; }

    public BspFile(string path, byte[] b)
    {
        _b = b;
        if (b.Length < 1036 || Encoding.ASCII.GetString(b, 0, 4) != "VBSP") throw new InvalidDataException($"{path}: not a VBSP map");
        int version = I(4);
        Version = version & 0xFFFF;
        ArkaneRevision = version >> 16;
        if (Version != 20) throw new InvalidDataException($"{path}: map version {Version}, this reader knows 20");

        Vertices = Read(Vertexes, 12, o => V3(o));
        PlaneList = Read(Planes, 20, o => (V3(o), F(o + 12)));
        EdgeList = Read(Edges, 4, o => ((int)U16(o), (int)U16(o + 2)));
        SurfEdgeList = Read(SurfEdges, 4, I);
        FaceList = Read(Faces, 56, o => new BspFace(U16(o), _b[o + 2] != 0, I(o + 4), I16(o + 8), I16(o + 10), I16(o + 12), I(o + 20)));

        bool arkane = ArkaneRevision != 0;
        int texInfoBytes = arkane ? 96 : 72, modelBytes = arkane ? 52 : 48;
        foreach (var (lump, size, name) in new[] { (TexInfoLump, texInfoBytes, "texinfo"), (Models, modelBytes, "model") })
            if (Lump(lump).Length % size != 0) throw new InvalidDataException($"{path}: {name} lump of {Lump(lump).Length} bytes is not a whole number of {size}-byte records");
        int flagsAt = texInfoBytes == 96 ? 88 : 64;
        TexInfos = Read(TexInfoLump, texInfoBytes, o => new BspTexInfo(V4(o), V4(o + 16), I(o + flagsAt), I(o + flagsAt + 4)));

        var (stringsAt, _) = Lump(TexDataStrings);
        var (tableAt, _) = Lump(TexDataTable);
        TexDatas = Read(TexDataLump, 32, o => new BspTexData(Str(stringsAt + I(tableAt + 4 * I(o + 12))), I(o + 16), I(o + 20)));

        int faceAt = modelBytes == 52 ? 44 : 40;
        ModelList = Read(Models, modelBytes, o => new BspModel(V3(o), V3(o + 12), V3(o + 24), I(o + faceAt), I(o + faceAt + 4)));

        DispInfos = Read(DispInfoLump, 176, o => new BspDispInfo(V3(o), I(o + 12), I(o + 20)));
        DispVertList = Read(DispVerts, 20, o => (V3(o), F(o + 12)));
        var (entAt, entLength) = Lump(Entities);
        EntityText = Encoding.Latin1.GetString(b, entAt, entLength).TrimEnd('\0');
    }

    /// <summary>The PAKFILE lump: a zip of files the map carries (cube maps, patched materials).</summary>
    public byte[] Pak()
    {
        var (at, length) = Lump(PakFile);
        return _b.AsSpan(at, length).ToArray();
    }

    /// <summary>A face's corners in order.</summary>
    public Vector3[] Polygon(in BspFace face)
    {
        var points = new Vector3[face.EdgeCount];
        for (int k = 0; k < face.EdgeCount; k++)
        {
            int e = SurfEdgeList[face.FirstEdge + k];
            points[k] = Vertices[e >= 0 ? EdgeList[e].A : EdgeList[-e].B];
        }
        return points;
    }

    (int At, int Length) Lump(int index) => (I(8 + 16 * index), I(12 + 16 * index));

    T[] Read<T>(int lump, int size, Func<int, T> read)
    {
        var (at, length) = Lump(lump);
        var items = new T[length / size];
        for (int i = 0; i < items.Length; i++) items[i] = read(at + i * size);
        return items;
    }

    int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(o));
    short I16(int o) => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(o));
    ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(o));
    float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(o));
    Vector3 V3(int o) => new(F(o), F(o + 4), F(o + 8));
    Vector4 V4(int o) => new(F(o), F(o + 4), F(o + 8), F(o + 12));
    string Str(int o)
    {
        int end = Array.IndexOf(_b, (byte)0, o);
        return Encoding.Latin1.GetString(_b, o, end - o);
    }
}
