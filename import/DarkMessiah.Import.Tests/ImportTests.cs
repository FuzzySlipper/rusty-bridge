using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using DarkMessiah.Import;
using DarkMessiah.Import.Gltf;
using DarkMessiah.Import.Source;
using RustyBridge.Wire.Donor;
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
    public void ClipsPastTheEnginesBudgetAreLeftOutFromTheFirstThatDoesNotFit()
    {
        // 334 orc-sized clips (3,300 keyframes, 13,000 values) fit one GLB whole.
        var kept = ModelImport.WithinBudget(Enumerable.Range(0, 334), _ => (3_300, 13_000), _ => Assert.Fail("none over"));
        Assert.Equal(Enumerable.Range(0, 334), kept);

        // Once one clip doesn't fit, it and every later one are left out, so the kept clips are a prefix.
        var over = new List<int>();
        kept = ModelImport.WithinBudget([0, 1, 2, 3], i => (i == 1 ? ModelImport.MaxKeyframes : 1, 1), over.Add);
        Assert.Equal([0], kept);
        Assert.Equal([1, 2, 3], over);
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
    public void ItemModelsAreWeaponsAndWholeShields()
    {
        Assert.True(LevelImport.ItemModel("models/items/weapons/daggers/daggers.mdl"));
        Assert.True(LevelImport.ItemModel("models/items/armors/shield_orc/shield_orc_small.mdl"));
        Assert.False(LevelImport.ItemModel("models/items/armors/shield_orc/shield_orc_chunk1.mdl"));
        Assert.False(LevelImport.ItemModel("models/items/armors/helmets/helmet_wisdom/helmet_wisdom.mdl"));
        Assert.False(LevelImport.ItemModel("models/items/weapons/daggers/daggers.qct"));
    }

    [Fact]
    public void GameplayTablesCopyScriptsItemTablesAndItemModels()
    {
        if (Vpks() is not string vpks) { output.WriteLine("no local/donor/vpks; skipped"); return; }
        var files = new DonorFiles(new RustyBridge.Wire.Donor.VpkIndex(vpks));
        string dir = Directory.CreateTempSubdirectory("dm-import-").FullName;
        try
        {
            var tables = new LevelImport(files, new ImportLog(), dir).Tables();
            Assert.Equal(LevelImport.Scripts, tables.Scripts);
            Assert.All(LevelImport.Scripts, s => Assert.True(File.Exists(Path.Combine(dir, s)), s));
            Assert.Contains("models/items/weapons/sword_long/sword_long.qct", tables.ItemTables);
            Assert.Contains("models/items/weapons/bow_classic/bow_classic.qct", tables.ItemTables);
            Assert.Contains(tables.Models, m => m.DonorPath == "models/items/weapons/daggers/daggers.mdl");
            Assert.Empty(tables.Unresolved);
            Assert.True(File.Exists(Path.Combine(dir, "models/items/weapons/daggers/daggers.glb")));
            Assert.True(File.Exists(Path.Combine(dir, "models/items/weapons/daggers/daggers.qct")));
        }
        finally { Directory.Delete(dir, true); }
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
            var result = new ModelImport(files, new Materials(files, log), log).Import(orc, Path.Combine(dir, "orc.glb"));
            var names = RustyBridge.Wire.Donor.StudioModel.SequenceNames(orc, files.Read);
            Assert.Equal(names, result.Sequences);
            Assert.Equal(names.Count, result.Clips);
            Assert.Equal(54, result.Bones);

            // One GLB holds every sequence as a clip, in the Wire's order.
            var glb = File.ReadAllBytes(result.File);
            var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)))!;
            Assert.Equal(names, json["animations"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));

            // Its default hitbox set, in order: hitbox 6 (group 4, the left arm) is on SK_L_Elbow, from
            // -1 to 13 units along the bone's X, which is glTF's Z.
            var hitboxes = json["asset"]!["extras"]!["hitboxes"]!.AsArray();
            Assert.Equal(18, hitboxes.Count);
            Assert.Equal("SK_L_Elbow", hitboxes[6]!["joint"]!.GetValue<string>());
            Assert.Equal(4, hitboxes[6]!["group"]!.GetValue<int>());
            float centerZ = hitboxes[6]!["center"]![2]!.GetValue<float>(), halfZ = hitboxes[6]!["halfExtents"]![2]!.GetValue<float>();
            Assert.Equal(-1f * 0.0254f, centerZ - halfZ, 3);
            Assert.Equal(13f * 0.0254f, centerZ + halfZ, 3);

            // Each clip's root motion (mstudiomovement_t), which Source strips from the root bone (#10150): attack_Jump
            // carries the orc 120 units forward, 100 by its strike (its 2102 at 0.37; the recordings show 93).
            var animations = json["animations"]!.AsArray();
            var jump = animations.First(a => a!["name"]!.GetValue<string>() == "attack_Jump")!["extras"]!["movement"]!.AsArray();
            Assert.Equal(1f, jump[^1]![0]!.GetValue<float>());
            Assert.Equal(120f, jump[^1]![1]!.GetValue<float>(), 1);
            Assert.Equal(0f, jump[^1]![2]!.GetValue<float>(), 1);
            Assert.Contains(jump, p => p![0]!.GetValue<float>() is > .38f and < .4f && p[1]!.GetValue<float>() is > 99 and < 101);
            // A strafe moves left (+Y); a clip that stays put has none.
            var strafe = animations.First(a => a!["name"]!.GetValue<string>() == "Combatmove_left_small")!["extras"]!["movement"]!.AsArray();
            Assert.Equal(50f, strafe[^1]![2]!.GetValue<float>(), 1);
            Assert.Null(animations.First(a => a!["name"]!.GetValue<string>() == "attack_Uppercut")!["extras"]!["movement"]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
