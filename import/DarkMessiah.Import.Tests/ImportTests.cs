using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using DarkMessiah.Import;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Source;
using Xunit;

namespace DarkMessiah.Import.Tests;

public sealed class SourceSpaceTests
{
    [Fact]
    public void ForwardLeftUpBecomeZXY()
    {
        Assert.Equal(new Vector3(0, 0, 0.0254f), SourceSpace.Position(Vector3.UnitX));   // forward
        Assert.Equal(new Vector3(0.0254f, 0, 0), SourceSpace.Position(Vector3.UnitY));   // left
        Assert.Equal(new Vector3(0, 0.0254f, 0), SourceSpace.Position(Vector3.UnitZ));   // up
    }

    [Fact]
    public void YawAboutUpStaysYawAboutUp()
    {
        // A Source yaw of 90 degrees turns forward (+X) to left (+Y); in glTF the same turn about +Y takes +Z to +X.
        var yaw = SourceSpace.FromRadianEuler(new Vector3(0, 0, MathF.PI / 2));
        var turned = Vector3.Transform(SourceSpace.Direction(Vector3.UnitX), SourceSpace.Rotation(yaw));
        Assert.Equal(SourceSpace.Direction(Vector3.UnitY).X, turned.X, 5);
        Assert.Equal(0, turned.Z, 5);
    }

    [Fact]
    public void RadianEulerIsYawPitchRoll()
    {
        var angles = new Vector3(0.3f, -0.7f, 1.1f);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X);
        var actual = SourceSpace.FromRadianEuler(angles);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 0.99999f);
    }

    [Fact]
    public void MatrixConversionAgreesWithPointConversion()
    {
        // A rigid Source transform as matrix3x4_t (column vectors, m[row][column]).
        var q = Quaternion.Normalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.9f));
        var t = new Vector3(12, -30, 55);
        var m = Matrix4x4.CreateFromQuaternion(q);
        float[] m34 = [m.M11, m.M21, m.M31, t.X, m.M12, m.M22, m.M32, t.Y, m.M13, m.M23, m.M33, t.Z];
        var converted = SourceSpace.Transform(m34);
        var p = new Vector3(3, 4, -5);
        var expected = SourceSpace.Position(Vector3.Transform(p, q) + t);
        var actual = Vector3.Transform(SourceSpace.Position(p), converted);
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{expected} vs {actual}");
    }
}

public sealed class GltfTests
{
    [Fact]
    public void GlbHasHeaderJsonAndPaddedBinaryChunks()
    {
        var gltf = new GltfBuilder();
        int positions = gltf.Floats([0, 0, 0, 1, 0, 0, 0, 2, 0], "VEC3", bounds: true, vertexData: true);
        int indices = gltf.Indices([0, 1, 2]);
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = positions }, ["indices"] = indices }) }) });
        var glb = gltf.ToGlb();

        Assert.Equal("glTF", Encoding.ASCII.GetString(glb, 0, 4));
        Assert.Equal(glb.Length, BitConverter.ToInt32(glb, 8));
        int jsonLength = BitConverter.ToInt32(glb, 12);
        Assert.Equal(0, jsonLength % 4);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!;
        Assert.Equal("BIN\0", Encoding.ASCII.GetString(glb, 20 + jsonLength + 4, 4));
        Assert.Equal(json["buffers"]![0]!["byteLength"]!.GetValue<long>(), BitConverter.ToInt32(glb, 20 + jsonLength));
        Assert.Equal(2f, json["accessors"]![positions]!["max"]![1]!.GetValue<float>());
        Assert.Equal(GltfBuilder.UnsignedShort, json["accessors"]![indices]!["componentType"]!.GetValue<int>());
    }

    [Fact]
    public void DegenerateTrianglesFollowTheEnginesTest()
    {
        Assert.True(Triangles.IsDegenerate(Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX));
        Assert.True(Triangles.IsDegenerate(Vector3.Zero, Vector3.Zero, Vector3.UnitY));
        Assert.False(Triangles.IsDegenerate(Vector3.Zero, Vector3.UnitX, Vector3.UnitY));
        // A sliver: 100 m long, a micron off the line.
        Assert.True(Triangles.IsDegenerate(Vector3.Zero, new(100, 0, 0), new(50, 1e-9f, 0)));

        var indices = new List<uint> { 0, 1, 2, 0, 1, 3 };
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0];
        Assert.Equal(1, Triangles.RemoveDegenerate(indices, positions));
        Assert.Equal([0u, 1, 2], indices);
    }

    [Fact]
    public void ClipsPackUnderTheEnginesPerGlbLimits()
    {
        // 150 clips of 60 channels: the channel limit (4,096) holds 68, the clip limit 64.
        var packs = ModelImport.Pack(Enumerable.Range(0, 150), _ => (60, 6_000, 20_000), _ => Assert.Fail("none too large"));
        Assert.Equal([64, 64, 22], packs.Select(p => p.Count));
        Assert.Equal(Enumerable.Range(0, 150), packs.SelectMany(p => p));

        // 1,000 channels each: four fit, the fifth starts a new pack.
        packs = ModelImport.Pack(Enumerable.Range(0, 9), _ => (1_000, 10, 10), _ => { });
        Assert.Equal([4, 4, 1], packs.Select(p => p.Count));

        var skipped = new List<int>();
        packs = ModelImport.Pack([1, 2], i => (i == 2 ? 5_000 : 1, 1, 1), skipped.Add);
        Assert.Equal([2], skipped);
        Assert.Equal([1], packs.Single());
    }
}

public sealed class DonorFormatTests
{
    [Fact]
    public void Dxt1BlockDecodesItsEndpointsAndBlends()
    {
        // c0 = pure red (0xF800), c1 = pure blue (0x001F); c0 > c1 so four colours.
        // Row 0 picks indices 0,1,2,3 (bits 00 01 10 11 = 0xE4).
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0xE4, 0x00, 0x00, 0x00];
        var vtf = new byte[80 + block.Length];
        "VTF\0"u8.CopyTo(vtf);
        BitConverter.GetBytes(7).CopyTo(vtf, 4);
        BitConverter.GetBytes(2).CopyTo(vtf, 8);
        BitConverter.GetBytes(80).CopyTo(vtf, 12);
        BitConverter.GetBytes((ushort)4).CopyTo(vtf, 16);
        BitConverter.GetBytes((ushort)4).CopyTo(vtf, 18);
        BitConverter.GetBytes((ushort)1).CopyTo(vtf, 24);
        BitConverter.GetBytes(13).CopyTo(vtf, 52);  // DXT1
        vtf[56] = 1;                                  // one mip
        BitConverter.GetBytes(-1).CopyTo(vtf, 57);    // no low-res image
        BitConverter.GetBytes((ushort)1).CopyTo(vtf, 63);
        block.CopyTo(vtf, 80);

        Assert.True(Vtf.TryDecode(vtf, 1024, out var image, out var problem), problem);
        Assert.Equal((4, 4), (image.Width, image.Height));
        byte[] Pixel(int x) => image.Pixels[(x * 4)..(x * 4 + 4)];
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(0));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(1));
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, Pixel(2));
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, Pixel(3));
    }

    [Fact]
    public void MaterialsReadKeysWithoutCaseAndSkipComments()
    {
        var kv = KeyValues.Parse(Encoding.ASCII.GetBytes("""
            "VertexLitGeneric"
            {
                // the base
                "$baseTexture" "models/Npc_Orc/Orc"
                $alphatest 1
                "vertexlitgeneric_dx9" { "$envmaptint" "[.5 .5 .5]" }
            }
            """))!;
        Assert.Equal("VertexLitGeneric", kv.Name);
        Assert.Equal("models/Npc_Orc/Orc", kv["$basetexture"]);
        Assert.Equal("1", kv["$ALPHATEST"]);
        Assert.Equal("[.5 .5 .5]", kv.Block("VertexLitGeneric_DX9")!["$envmaptint"]);
    }

    [Fact]
    public void MaterialPathsNormaliseLikeTheEngine()
    {
        Assert.Equal("models/npc_orc/orc", Materials.Normalize(@"models\NPC_Orc\\Orc.vmt"));
        Assert.Equal("debug/debugempty", Materials.Normalize("materials/./debug//debugempty"));
    }
}

/// <summary>
/// Imports a real character when the donor's VPKs are in local/donor/vpks
/// (they never leave the machine); without them there is nothing to check.
/// </summary>
public sealed class DonorImportTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static string? Vpks()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string vpks = Path.Combine(dir.FullName, "local", "donor", "vpks");
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) return Directory.Exists(vpks) ? vpks : null;
        }
        return null;
    }

    [Fact]
    public void OrcClipsFollowTheWiresSequenceNumbering()
    {
        if (Vpks() is not string vpks) { output.WriteLine("no local/donor/vpks; skipped"); return; }
        var files = new DonorFiles(new RustyBridge.Wire.Donor.VpkIndex(vpks));
        var log = new ImportLog();
        string dir = Directory.CreateTempSubdirectory("dm-import-").FullName;
        try
        {
            const string orc = "models/npc/orc/npc_orc.mdl";
            var result = new ModelImport(files, new Materials(files, log), log).Import(orc, n => Path.Combine(dir, $"orc{n}.glb"));
            var names = RustyBridge.Wire.Donor.StudioModel.SequenceNames(orc, files.Read);
            Assert.Equal(names, result.Sequences);
            Assert.Equal(names.Count, result.Clips);
            Assert.Equal(54, result.Bones);

            var clipNames = new List<string>();
            foreach (var file in result.Files)
            {
                var glb = File.ReadAllBytes(file);
                var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)))!;
                var animations = json["animations"]!.AsArray();
                Assert.InRange(animations.Count, 1, ModelImport.MaxClips);
                Assert.InRange(animations.Sum(a => a!["channels"]!.AsArray().Count), 1, ModelImport.MaxChannels);
                clipNames.AddRange(animations.Select(a => a!["name"]!.GetValue<string>()));
            }
            // Every sequence is a clip, in the Wire's order across the packs.
            Assert.Equal(names, clipNames);
        }
        finally { Directory.Delete(dir, true); }
    }
}
