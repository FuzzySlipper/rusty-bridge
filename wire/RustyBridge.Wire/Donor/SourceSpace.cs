using System.Numerics;

namespace RustyBridge.Wire.Donor;

/// <summary>
/// Source space to glTF space. Source is right-handed with +X forward, +Y
/// left and +Z up, in inches. glTF is right-handed with +Y up and an asset's
/// front facing +Z (so +X is its left), in metres. Forward→+Z, left→+X and
/// up→+Y is a rotation, so handedness and winding meaning are kept. A Source
/// model faces +X, so an imported one faces +Z.
/// </summary>
public static class SourceSpace
{
    /// <summary>How imported assets record this mapping (GLB asset extras, the asset index's provenance).</summary>
    public const string Convention = "gltf-right-handed-y-up-meters; source +X forward -> +Z, +Y left -> +X, +Z up -> +Y; 1 unit = 0.0254 m";

    /// <summary>One Source unit is one inch.</summary>
    public const float MetresPerUnit = 0.0254f;

    public static Vector3 Direction(Vector3 v) => new(v.Y, v.Z, v.X);

    public static Vector3 Position(Vector3 v) => Direction(v) * MetresPerUnit;

    public static Quaternion Rotation(Quaternion q) => new(q.Y, q.Z, q.X, q.W);

    /// <summary>
    /// A Source rigid transform (3×4, column vectors, as matrix3x4_t stores
    /// it: m[row][column]) converted to a glTF one, as System.Numerics row
    /// vectors. Rows and columns of the rotation are permuted the same way,
    /// translation is permuted and scaled.
    /// </summary>
    public static Matrix4x4 Transform(ReadOnlySpan<float> m34)
    {
        // gltf axis i reads source axis Axis[i].
        ReadOnlySpan<int> axis = [1, 2, 0];
        var r = new Matrix4x4();
        // System.Numerics: element [row i, column j] of the row-vector matrix is
        // the column-vector matrix's [j, i].
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Set(ref r, j, i, m34[axis[i] * 4 + axis[j]]);
        r.M41 = m34[axis[0] * 4 + 3] * MetresPerUnit;
        r.M42 = m34[axis[1] * 4 + 3] * MetresPerUnit;
        r.M43 = m34[axis[2] * 4 + 3] * MetresPerUnit;
        r.M44 = 1;
        return r;
    }

    static void Set(ref Matrix4x4 m, int row, int column, float v)
    {
        switch (row * 4 + column)
        {
            case 0: m.M11 = v; break; case 1: m.M12 = v; break; case 2: m.M13 = v; break;
            case 4: m.M21 = v; break; case 5: m.M22 = v; break; case 6: m.M23 = v; break;
            case 8: m.M31 = v; break; case 9: m.M32 = v; break; case 10: m.M33 = v; break;
        }
    }

    /// <summary>mathlib's AngleQuaternion for a RadianEuler (x roll, y pitch, z yaw): yaw · pitch · roll.</summary>
    public static Quaternion FromRadianEuler(Vector3 angles)
    {
        var (sy, cy) = MathF.SinCos(angles.Z * 0.5f);
        var (sp, cp) = MathF.SinCos(angles.Y * 0.5f);
        var (sr, cr) = MathF.SinCos(angles.X * 0.5f);
        float srXcp = sr * cp, crXsp = cr * sp, crXcp = cr * cp, srXsp = sr * sp;
        return new Quaternion(srXcp * cy - crXsp * sy, crXsp * cy + srXcp * sy, crXcp * sy - srXsp * cy, crXcp * cy + srXsp * sy);
    }

    /// <summary>
    /// An entity's QAngle (pitch, yaw, roll in degrees, as the Wire carries
    /// them) as a Source rotation: AngleMatrix's yaw about Z, then pitch about
    /// Y, then roll about X. Pass it to <see cref="Rotation"/> for glTF space.
    /// </summary>
    public static Quaternion FromAngles(float pitch, float yaw, float roll) =>
        FromRadianEuler(new Vector3(roll, pitch, yaw) * (MathF.PI / 180));

    /// <summary>A matrix as glTF's column-major 16 floats.</summary>
    public static float[] ColumnMajor(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
}
