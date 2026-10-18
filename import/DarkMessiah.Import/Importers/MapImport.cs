using System.Numerics;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Source;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>
/// What a map import produced: one GLB per drawable BSP model, keyed "*N" as
/// the Wire names brush models ("*0" is the world), and the brush models with
/// nothing to draw (triggers, clips, tool brushes), which get no GLB.
/// </summary>
public sealed record MapImportResult(string DonorPath, IReadOnlyDictionary<string, string> Models, IReadOnlyList<string> Invisible, int Triangles, long Bytes);

/// <summary>
/// A map's world and brush models as static GLBs, one per BSP model, with
/// one primitive per material. Displacements are tessellated from their
/// base face. Lightmaps are not imported; each material keeps its donor path
/// and base texture.
/// </summary>
public sealed class MapImport
{
    readonly DonorFiles _files;
    readonly Materials _materials;
    readonly ImportLog _log;

    public int MaxTextureSize { get; init; } = 512;

    public MapImport(DonorFiles files, Materials materials, ImportLog log) { _files = files; _materials = materials; _log = log; }

    /// <param name="fileFor">Output file for BSP model N.</param>
    public MapImportResult Import(string donorPath, Func<int, string> fileFor)
    {
        var bytes = _files.Read(donorPath) ?? throw new FileNotFoundException($"{donorPath}: not in the donor's files");
        var bsp = new BspFile(donorPath, bytes);
        var models = new Dictionary<string, string>();
        var invisible = new List<string>();
        int triangles = 0;
        long total = 0;
        using (_files.WithPak(bsp.Pak()))
        {
            _materials.Clear();
            for (int m = 0; m < bsp.ModelList.Length; m++)
            {
                var gltf = new GltfBuilder();
                int count = Model(bsp, m, gltf);
                triangles += count;
                if (count == 0) { invisible.Add($"*{m}"); continue; }
                gltf.Extras["donorPath"] = donorPath;
                gltf.Extras["brushModel"] = $"*{m}";
                gltf.Extras["convention"] = SourceSpace.Convention;
                var origin = SourceSpace.Position(bsp.ModelList[m].Origin);
                gltf.Extras["origin"] = GltfBuilder.FloatArray(origin.X, origin.Y, origin.Z);
                string file = fileFor(m);
                var glb = gltf.ToGlb();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, glb);
                total += glb.Length;
                models[$"*{m}"] = file;
            }
            _materials.Clear();
        }
        return new MapImportResult(donorPath, models, invisible, triangles, total);
    }

    sealed class Batch
    {
        public readonly List<float> Positions = [], Normals = [], Uvs = [];
        public readonly List<uint> Indices = [];
        public uint Add(Vector3 p, Vector3 n, Vector2 uv)
        {
            var gp = SourceSpace.Position(p);
            var gn = SourceSpace.Direction(n);
            Positions.AddRange([gp.X, gp.Y, gp.Z]);
            Normals.AddRange([gn.X, gn.Y, gn.Z]);
            Uvs.AddRange([uv.X, uv.Y]);
            return (uint)(Positions.Count / 3 - 1);
        }
    }

    int Model(BspFile bsp, int index, GltfBuilder gltf)
    {
        var model = bsp.ModelList[index];
        var batches = new Dictionary<string, Batch>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        for (int f = model.FirstFace; f < model.FirstFace + model.FaceCount; f++)
        {
            var face = bsp.FaceList[f];
            if (face.TexInfo < 0 || face.TexInfo >= bsp.TexInfos.Length) { skipped++; continue; }
            var info = bsp.TexInfos[face.TexInfo];
            if ((info.Flags & (BspTexInfo.Sky | BspTexInfo.Sky2D | BspTexInfo.NoDraw | BspTexInfo.Hint | BspTexInfo.Skip | BspTexInfo.Trigger)) != 0) { skipped++; continue; }
            if (info.TexData < 0 || info.TexData >= bsp.TexDatas.Length) { skipped++; continue; }
            var tex = bsp.TexDatas[info.TexData];
            string material = Materials.Normalize(tex.Material);
            if (material.StartsWith("tools/", StringComparison.Ordinal)) { skipped++; continue; }
            if (!batches.TryGetValue(material, out var batch)) batches[material] = batch = new Batch();

            var (normal, _) = bsp.PlaneList[face.Plane];
            if (face.Side) normal = -normal;
            var polygon = bsp.Polygon(face);
            Vector2 Uv(Vector3 p) => new(
                (Vector3.Dot(p, new(info.S.X, info.S.Y, info.S.Z)) + info.S.W) / Math.Max(1, tex.Width),
                (Vector3.Dot(p, new(info.T.X, info.T.Y, info.T.Z)) + info.T.W) / Math.Max(1, tex.Height));

            if (face.DispInfo >= 0)
            {
                if (polygon.Length != 4 || face.DispInfo >= bsp.DispInfos.Length) { _log.Warn($"displacement on a {polygon.Length}-sided face skipped"); continue; }
                Displacement(bsp, bsp.DispInfos[face.DispInfo], polygon, normal, batch, Uv);
                continue;
            }
            if (polygon.Length < 3) continue;
            var first = new uint[polygon.Length];
            for (int k = 0; k < polygon.Length; k++) first[k] = batch.Add(polygon[k], normal, Uv(polygon[k]));
            bool flip = Vector3.Dot(Vector3.Cross(polygon[1] - polygon[0], polygon[2] - polygon[0]), normal) < 0;
            for (int k = 1; k + 1 < polygon.Length; k++)
                if (flip) batch.Indices.AddRange([first[0], first[k + 1], first[k]]);
                else batch.Indices.AddRange([first[0], first[k], first[k + 1]]);
        }

        var primitives = new JsonArray();
        int triangles = 0, dropped = 0;
        foreach (var (material, batch) in batches.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            dropped += Triangles.RemoveDegenerate(batch.Indices, batch.Positions);
            if (batch.Indices.Count == 0) continue;
            triangles += batch.Indices.Count / 3;
            primitives.Add(new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["POSITION"] = gltf.Floats(batch.Positions.ToArray(), "VEC3", bounds: true, vertexData: true),
                    ["NORMAL"] = gltf.Floats(batch.Normals.ToArray(), "VEC3", vertexData: true),
                    ["TEXCOORD_0"] = gltf.Floats(batch.Uvs.ToArray(), "VEC2", vertexData: true),
                },
                ["indices"] = gltf.Indices(batch.Indices.ToArray()),
                ["material"] = _materials.AddTo(gltf, material, MaxTextureSize),
            });
        }
        if (dropped > 0) _log.Warn($"*{index}: {dropped} degenerate triangles dropped");
        string name = index == 0 ? "world" : $"*{index}";
        if (primitives.Count > 0)
            gltf.Node(new JsonObject { ["name"] = name, ["mesh"] = gltf.Mesh(new JsonObject { ["name"] = name, ["primitives"] = primitives }) });
        return triangles;
    }

    /// <summary>
    /// A displacement: a (2^power+1)² grid over the base face, starting at the
    /// corner nearest the stored start position, each vertex pushed along its
    /// offset vector by its distance. Texture coordinates come from the base face.
    /// </summary>
    static void Displacement(BspFile bsp, BspDispInfo disp, Vector3[] corners, Vector3 normal, Batch batch, Func<Vector3, Vector2> uv)
    {
        int start = 0;
        float best = float.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            float d = Vector3.DistanceSquared(corners[k], disp.Start);
            if (d < best) { best = d; start = k; }
        }
        var c = new Vector3[4];
        for (int k = 0; k < 4; k++) c[k] = corners[(start + k) % 4];

        int n = (1 << disp.Power) + 1;
        var positions = new Vector3[n * n];
        var bases = new Vector3[n * n];
        for (int i = 0; i < n; i++)
        {
            float ti = i / (float)(n - 1);
            var left = Vector3.Lerp(c[0], c[1], ti);
            var right = Vector3.Lerp(c[3], c[2], ti);
            for (int j = 0; j < n; j++)
            {
                int v = i * n + j;
                bases[v] = Vector3.Lerp(left, right, j / (float)(n - 1));
                var (offset, distance) = disp.FirstVert + v < bsp.DispVertList.Length ? bsp.DispVertList[disp.FirstVert + v] : (Vector3.Zero, 0f);
                positions[v] = bases[v] + offset * distance;
            }
        }

        var tris = new List<(int, int, int)>();
        for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                int a = i * n + j, b = a + 1, d = a + n, e = d + 1;
                if (((i + j) & 1) == 0) { tris.Add((a, d, e)); tris.Add((a, e, b)); }
                else { tris.Add((a, d, b)); tris.Add((b, d, e)); }
            }
        var (t0, t1, t2) = tris[0];
        bool flip = Vector3.Dot(Vector3.Cross(bases[t1] - bases[t0], bases[t2] - bases[t0]), normal) < 0;

        var normals = new Vector3[n * n];
        foreach (var (a, b, d) in tris)
        {
            var fn = Vector3.Cross(positions[b] - positions[a], positions[d] - positions[a]);
            if (flip) fn = -fn;
            normals[a] += fn; normals[b] += fn; normals[d] += fn;
        }
        var ids = new uint[n * n];
        for (int v = 0; v < ids.Length; v++)
            ids[v] = batch.Add(positions[v], normals[v].LengthSquared() > 0 ? Vector3.Normalize(normals[v]) : normal, uv(bases[v]));
        foreach (var (a, b, d) in tris)
            if (flip) batch.Indices.AddRange([ids[a], ids[d], ids[b]]);
            else batch.Indices.AddRange([ids[a], ids[b], ids[d]]);
    }
}
