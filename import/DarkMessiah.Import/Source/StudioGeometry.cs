using System.Buffers.Binary;
using System.Numerics;

namespace DarkMessiah.Import.Source;

/// <summary>A studio vertex: up to three bone weights (global bone indices), position, normal, uv.</summary>
public readonly record struct StudioVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, byte BoneCount,
    byte Bone0, byte Bone1, byte Bone2, float Weight0, float Weight1, float Weight2);

/// <summary>.vvd (IDSV version 4): the model's vertices for LOD 0, fixups applied.</summary>
public static class StudioVertexFile
{
    public static StudioVertex[] Read(string path, byte[] b)
    {
        if (b.Length < 64 || b[0] != 'I' || b[1] != 'D' || b[2] != 'S' || b[3] != 'V') throw new InvalidDataException($"{path}: not a vertex file");
        int version = I(b, 4);
        if (version != 4) throw new InvalidDataException($"{path}: vertex file version {version}, this reader knows 4");
        int lod0 = I(b, 16), fixups = I(b, 48), fixupAt = I(b, 52), dataAt = I(b, 56);
        var all = new StudioVertex[lod0];
        if (fixups == 0)
        {
            for (int i = 0; i < lod0; i++) all[i] = Vertex(b, dataAt + i * 48);
            return all;
        }
        // Fixups list the source vertex runs each LOD keeps; LOD 0 keeps the runs whose lod is >= 0.
        int n = 0;
        for (int f = 0; f < fixups; f++)
        {
            int o = fixupAt + f * 12, lod = I(b, o), source = I(b, o + 4), count = I(b, o + 8);
            if (lod < 0) continue;
            for (int k = 0; k < count && n < all.Length; k++) all[n++] = Vertex(b, dataAt + (source + k) * 48);
        }
        return all;
    }

    static StudioVertex Vertex(byte[] b, int o) => new(
        new(F(b, o + 16), F(b, o + 20), F(b, o + 24)),
        new(F(b, o + 28), F(b, o + 32), F(b, o + 36)),
        new(F(b, o + 40), F(b, o + 44)),
        b[o + 15], b[o + 12], b[o + 13], b[o + 14], F(b, o), F(b, o + 4), F(b, o + 8));

    static int I(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
    static float F(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
}

/// <summary>
/// .vtx (version 7) triangle lists for LOD 0, per body part, model and mesh,
/// as indices into the mesh's own vertices (origMeshVertID). Strips and
/// hardware bone palettes are flattened away.
/// </summary>
public static class StudioStripFile
{
    const int StripIsTriList = 1, StripIsTriStrip = 2;

    /// <summary>[bodypart][model][mesh] → triangle corner indices, three per triangle.</summary>
    public static List<int>[][][] Read(string path, byte[] b)
    {
        int version = I(b, 0);
        if (version != 7) throw new InvalidDataException($"{path}: strip file version {version}, this reader knows 7");
        int bodyParts = I(b, 28), bodyPartAt = I(b, 32);
        var result = new List<int>[bodyParts][][];
        for (int p = 0; p < bodyParts; p++)
        {
            int bp = bodyPartAt + p * 8;
            int models = I(b, bp), modelAt = bp + I(b, bp + 4);
            result[p] = new List<int>[models][];
            for (int m = 0; m < models; m++)
            {
                int mo = modelAt + m * 8;
                int lods = I(b, mo), lodAt = mo + I(b, mo + 4);
                if (lods == 0) { result[p][m] = []; continue; }
                int meshes = I(b, lodAt), meshAt = lodAt + I(b, lodAt + 4);
                result[p][m] = new List<int>[meshes];
                for (int k = 0; k < meshes; k++)
                {
                    var corners = new List<int>();
                    int me = meshAt + k * 9;
                    int groups = I(b, me), groupAt = me + I(b, me + 4);
                    for (int g = 0; g < groups; g++)
                    {
                        int sg = groupAt + g * 25;
                        int vertAt = sg + I(b, sg + 4), indexAt = sg + I(b, sg + 12);
                        int strips = I(b, sg + 16), stripAt = sg + I(b, sg + 20);
                        int Corner(int index) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(vertAt + BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(indexAt + index * 2)) * 9 + 4));
                        for (int s = 0; s < strips; s++)
                        {
                            int st = stripAt + s * 27;
                            int count = I(b, st), first = I(b, st + 4);
                            int flags = b[st + 18];
                            if ((flags & StripIsTriStrip) != 0)
                            {
                                for (int t = 0; t + 2 < count; t++)
                                {
                                    int a = Corner(first + t), c1 = Corner(first + t + 1), c2 = Corner(first + t + 2);
                                    if ((t & 1) == 0) corners.AddRange([a, c1, c2]); else corners.AddRange([c1, a, c2]);
                                }
                            }
                            else
                            {
                                for (int t = 0; t + 2 < count; t += 3) corners.AddRange([Corner(first + t), Corner(first + t + 1), Corner(first + t + 2)]);
                            }
                        }
                    }
                    result[p][m][k] = corners;
                }
            }
        }
        return result;
    }

    static int I(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
}
