using System.Numerics;

namespace DarkMessiah.Import.Gltf;

public static class Triangles
{
    /// <summary>Rust's f64::EPSILON (machine epsilon), not .NET's double.Epsilon (the smallest subnormal).</summary>
    const double RustF64Epsilon = 2.220446049250313e-16;

    /// <summary>
    /// The Engine's admission test (voxel-convert triangle_is_degenerate):
    /// edges normalised by their largest component, then squared area against
    /// the squared longest edge, in double precision. GLBs keep only triangles
    /// that pass, or the Engine refuses the whole file.
    /// </summary>
    public static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        double abx = (double)b.X - a.X, aby = (double)b.Y - a.Y, abz = (double)b.Z - a.Z;
        double acx = (double)c.X - a.X, acy = (double)c.Y - a.Y, acz = (double)c.Z - a.Z;
        double bcx = (double)c.X - b.X, bcy = (double)c.Y - b.Y, bcz = (double)c.Z - b.Z;
        double scale = Math.Max(Math.Max(Math.Max(Math.Abs(abx), Math.Abs(aby)), Math.Max(Math.Abs(abz), Math.Abs(acx))), Math.Max(Math.Abs(acy), Math.Abs(acz)));
        if (scale == 0) return true;
        abx /= scale; aby /= scale; abz /= scale; acx /= scale; acy /= scale; acz /= scale; bcx /= scale; bcy /= scale; bcz /= scale;
        double cx = aby * acz - abz * acy, cy = abz * acx - abx * acz, cz = abx * acy - aby * acx;
        double area = cx * cx + cy * cy + cz * cz;
        double longest = Math.Max(Math.Max(abx * abx + aby * aby + abz * abz, acx * acx + acy * acy + acz * acz), bcx * bcx + bcy * bcy + bcz * bcz);
        return area <= RustF64Epsilon * longest * longest;
    }

    /// <summary>Drops triangles (index triples) that <see cref="IsDegenerate"/> rejects; returns how many it dropped.</summary>
    public static int RemoveDegenerate(List<uint> indices, IReadOnlyList<float> positions)
    {
        Vector3 P(uint i) => new(positions[(int)i * 3], positions[(int)i * 3 + 1], positions[(int)i * 3 + 2]);
        int kept = 0, dropped = 0;
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if (a == b || b == c || a == c || IsDegenerate(P(a), P(b), P(c))) { dropped++; continue; }
            indices[kept++] = a; indices[kept++] = b; indices[kept++] = c;
        }
        indices.RemoveRange(kept, indices.Count - kept);
        return dropped;
    }
}
