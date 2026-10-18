using System.Text;
using RustyBridge.Wire.Donor;
using Xunit;

namespace RustyBridge.Wire.Tests;

public sealed class DonorTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("vpk-").FullName;

    // A minimal version-44 studio header: sequence count/index at 188/192,
    // include count/index at 336/340, 212-byte sequence descriptors whose
    // label offset (at +4) is relative to the descriptor.
    static byte[] Mdl(string[] sequences, params string[] includes)
    {
        const int seqAt = 400, stride = 212;
        int stringsAt = seqAt + sequences.Length * stride + includes.Length * 8;
        var strings = new MemoryStream();
        var b = new byte[stringsAt];
        "IDST"u8.CopyTo(b);
        BitConverter.GetBytes(44).CopyTo(b, 4);
        BitConverter.GetBytes(sequences.Length).CopyTo(b, 188);
        BitConverter.GetBytes(seqAt).CopyTo(b, 192);
        int includeAt = seqAt + sequences.Length * stride;
        BitConverter.GetBytes(includes.Length).CopyTo(b, 336);
        BitConverter.GetBytes(includeAt).CopyTo(b, 340);
        int Put(string s) { int at = stringsAt + (int)strings.Length; strings.Write(Encoding.ASCII.GetBytes(s + "\0")); return at; }
        for (int i = 0; i < sequences.Length; i++)
        {
            int desc = seqAt + i * stride;
            BitConverter.GetBytes(-desc).CopyTo(b, desc);
            BitConverter.GetBytes(Put(sequences[i]) - desc).CopyTo(b, desc + 4);
        }
        for (int k = 0; k < includes.Length; k++)
        {
            int group = includeAt + k * 8;
            BitConverter.GetBytes(Put(includes[k]) - group).CopyTo(b, group + 4);
        }
        return b.Concat(strings.ToArray()).ToArray();
    }

    [Fact]
    public void SequencesFollowTheEnginesVirtualModelOrder()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["models/npc/orc/npc_orc.mdl"] = Mdl(new[] { "ragdoll", "reference" },
                @"models/NPC\Orc\npc_orc_animations_combat.mdl", @"models/NPC\Orc\npc_orc_animations_cinematics.mdl"),
            ["models/npc/orc/npc_orc_animations_combat.mdl"] = Mdl(new[] { "Reference", "idle_0", "attack_1" }, "models/npc/orc/shared.mdl"),
            ["models/npc/orc/shared.mdl"] = Mdl(new[] { "flinch" }),
            ["models/npc/orc/npc_orc_animations_cinematics.mdl"] = Mdl(new[] { "idle_0", "cheer" }),
        };
        var names = StudioModel.SequenceNames("models/npc/orc/npc_orc.mdl", p => files.GetValueOrDefault(VpkIndex.Normalize(p)));

        // Own sequences, then includes depth first; "Reference" and the second "idle_0" are duplicates.
        Assert.Equal(new[] { "ragdoll", "reference", "idle_0", "attack_1", "flinch", "cheer" }, names);
    }

    [Fact]
    public void VpkReadsPreloadAndArchiveData()
    {
        // Directory file: header, tree (ext / folder / name + entry), then embedded data.
        var tree = new MemoryStream();
        void Z(string s) => tree.Write(Encoding.ASCII.GetBytes(s + "\0"));
        void Entry(ushort preload, ushort archive, uint offset, uint length, byte[] preloadBytes)
        {
            var w = new BinaryWriter(tree, Encoding.ASCII, leaveOpen: true);
            w.Write(0u); w.Write(preload); w.Write(archive); w.Write(offset); w.Write(length); w.Write((ushort)0xFFFF); w.Write(preloadBytes);
        }
        Z("mdl"); Z("mm/models/npc"); Z("embedded"); Entry(2, 0x7FFF, 0, 3, "ab"u8.ToArray()); Z("archived"); Entry(0, 0, 4, 5, Array.Empty<byte>()); Z(""); Z(""); Z("");
        var dir = new MemoryStream();
        var dw = new BinaryWriter(dir);
        dw.Write(0x55AA1234u); dw.Write(1u); dw.Write((uint)tree.Length); dw.Write(tree.ToArray()); dw.Write("cde"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_dir, "depot_1_dir.vpk"), dir.ToArray());
        File.WriteAllBytes(Path.Combine(_dir, "depot_1_000.vpk"), "....hello"u8.ToArray());

        var vpk = new VpkIndex(_dir);
        Assert.Equal("abcde", Encoding.ASCII.GetString(vpk.Read(@"Models\NPC\embedded.mdl")!));
        Assert.Equal("hello", Encoding.ASCII.GetString(vpk.Read("mm/models/npc/archived.mdl")!));
        Assert.Null(vpk.Read("models/npc/missing.mdl"));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
