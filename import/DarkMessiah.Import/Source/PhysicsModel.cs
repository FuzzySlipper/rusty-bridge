using System.Numerics;
using System.Text;

namespace DarkMessiah.Import.Source;

/// <summary>One convex piece of a collision model: its points (Source units, model space) and triangles over them.</summary>
public sealed record ConvexPiece(Vector3[] Points, int[] Triangles);

/// <summary>
/// A studio model's collision (.phy beside the .mdl), as Dark Messiah ships
/// them: a 16-byte header (its size, id, solid count, checksum), then each
/// solid as its byte size and a <c>VPHY</c> compact surface header (28 bytes
/// from the id: version, model type, surface size, drag areas, axis map
/// size) before Havok's (IVP's) compact surface. That surface is 48 bytes
/// (mass centre, inertia, radius, size, ledge tree offset at +32), then the
/// compact ledges: each a convex hull, 16 bytes (its points' offset, then
/// size and flags, then its triangle count at +12) and 16-byte triangles of
/// three edges whose low 16 bits name their start points. Points are 16
/// bytes, IVP's metres with its axes: Source (x, y, z) is IVP (-y, -z, x)
/// (ConvertPositionToIVP), in model space: an item's bone 0 is turned from it.
/// The ledge tree's leaves (28-byte nodes: right child offset, ledge offset)
/// list the ledges.
/// </summary>
public static class PhysicsModel
{
    const int HeaderBytes = 16, VphyBytes = 28, SurfaceBytes = 48, LedgeBytes = 16, TriangleBytes = 16, PointBytes = 16, NodeBytes = 28;

    /// <summary>Every solid's convex pieces; empty when the file isn't one this reads (an older surface without <c>VPHY</c>).</summary>
    public static List<List<ConvexPiece>> Read(string path, byte[] b)
    {
        var solids = new List<List<ConvexPiece>>();
        if (b.Length < HeaderBytes) return solids;
        int count = BitConverter.ToInt32(b, 8), at = BitConverter.ToInt32(b, 0);
        for (int s = 0; s < count && at + 4 <= b.Length; s++)
        {
            int size = BitConverter.ToInt32(b, at), start = at + 4;
            at = start + size;
            if (size <= 0 || at > b.Length) throw new InvalidDataException($"{path}: solid {s} runs past the file");
            if (Encoding.ASCII.GetString(b, start, 4) != "VPHY") return [];
            int surface = start + VphyBytes;
            var pieces = new List<ConvexPiece>();
            Leaves(b, surface + BitConverter.ToInt32(b, surface + 32), start, at, pieces, path);
            solids.Add(pieces);
        }
        return solids;
    }

    /// <summary>
    /// The first solid's mass (kg) and surface property from the text after the solids: studiomdl's
    /// <c>solid { "index" "0" ... "mass" "80.000000" "surfaceprop" "Wood_Crate" ... }</c> (telekinesis sorts props by
    /// mass, #10164). Null where the file has none.
    /// </summary>
    public static (float? Mass, string? SurfaceProp) Properties(byte[] b)
    {
        if (b.Length < HeaderBytes) return (null, null);
        int count = BitConverter.ToInt32(b, 8), at = BitConverter.ToInt32(b, 0);
        for (int s = 0; s < count && at + 4 <= b.Length; s++)
        {
            int size = BitConverter.ToInt32(b, at);
            if (size <= 0) return (null, null);
            at += 4 + size;
        }
        if (at >= b.Length) return (null, null);
        string text = Encoding.ASCII.GetString(b, at, b.Length - at);
        string? Value(string key) =>
            System.Text.RegularExpressions.Regex.Match(text, "\"" + key + "\"\\s*\"([^\"]*)\"") is { Success: true } m ? m.Groups[1].Value : null;
        float? mass = float.TryParse(Value("mass"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float kg) ? kg : null;
        return (mass, Value("surfaceprop"));
    }

    static void Leaves(byte[] b, int node, int start, int end, List<ConvexPiece> pieces, string path, int depth = 0)
    {
        if (node < start || node + NodeBytes > end || depth > 64) throw new InvalidDataException($"{path}: a ledge tree node lies outside its solid");
        int right = BitConverter.ToInt32(b, node);
        if (right == 0)
        {
            pieces.Add(Ledge(b, node + BitConverter.ToInt32(b, node + 4), start, end, path));
            return;
        }
        Leaves(b, node + NodeBytes, start, end, pieces, path, depth + 1);
        Leaves(b, node + right, start, end, pieces, path, depth + 1);
    }

    static ConvexPiece Ledge(byte[] b, int ledge, int start, int end, string path)
    {
        if (ledge < start || ledge + LedgeBytes > end) throw new InvalidDataException($"{path}: a ledge lies outside its solid");
        int points = ledge + BitConverter.ToInt32(b, ledge);
        int triangles = BitConverter.ToInt16(b, ledge + 12);
        var corners = new int[triangles * 3];
        int highest = -1;
        for (int t = 0; t < triangles; t++)
            for (int e = 0; e < 3; e++)
            {
                int edge = ledge + LedgeBytes + t * TriangleBytes + 4 + e * 4;
                if (edge + 4 > end) throw new InvalidDataException($"{path}: a ledge's triangles run past its solid");
                corners[t * 3 + e] = BitConverter.ToUInt16(b, edge);
                highest = Math.Max(highest, corners[t * 3 + e]);
            }
        if (points + (highest + 1) * PointBytes > end) throw new InvalidDataException($"{path}: a ledge's points run past its solid");
        var at = new Vector3[highest + 1];
        for (int p = 0; p <= highest; p++)
        {
            int o = points + p * PointBytes;
            var ivp = new Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));
            at[p] = new Vector3(ivp.Z, -ivp.X, -ivp.Y) / MetresPerUnit;
        }
        return new ConvexPiece(at, corners);
    }

    const float MetresPerUnit = 0.0254f;
}
