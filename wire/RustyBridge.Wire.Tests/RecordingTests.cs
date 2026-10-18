using RustyBridge.Wire;
using Xunit;

namespace RustyBridge.Wire.Tests;

public sealed class RecordingTests : IDisposable
{
    readonly string _wire = Path.Combine(Path.GetTempPath(), $"wire-{Guid.NewGuid():N}.wire");

    // Frames 0..3: world and a prop stand still, the player walks along x,
    // and the prop is gone from frame 2 on.
    TestWriter FourFrames()
    {
        var writer = new TestWriter(_wire, "l01_a");
        writer.Class(0, "worldspawn");
        writer.Class(1, "player");
        writer.Class(2, "prop_physics");
        writer.Model(1, "maps/l01_a.bsp");
        writer.Model(565, "models/props/tools/humans/crates/crated_new.mdl");
        writer.Frame(tick: 10, (0, 0, 1, 0f), (1, 1, 0, 0f), (5, 2, 565, 100f));
        writer.Frame(tick: 11, (0, 0, 1, 0f), (1, 1, 0, 1f), (5, 2, 565, 100f));
        writer.Frame(tick: 12, (0, 0, 1, 0f), (1, 1, 0, 2f));
        writer.Frame(tick: 13, (0, 0, 1, 0f), (1, 1, 0, 3f));
        return writer;
    }

    static List<(ulong frame, int tick, bool gap, Dictionary<ushort, float> x, string cls5, string model5)> Record(
        WireReader reader, params ulong[] frames)
    {
        var buffer = new MemoryStream();
        var snap = new WireSnapshot();
        using (var recorder = new Recorder(buffer))
        {
            foreach (ulong f in frames)
            {
                Assert.True(reader.TryReadFrame(f, snap));
                recorder.Write(snap, reader.Map, reader.Header.TickInterval, reader);
            }
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        return RecordingReader.Frames(buffer).Select(r => (
            r.Frame, r.Tick, r.Gap,
            r.Entities.ToDictionary(kv => kv.Key, kv => kv.Value.OriginX),
            r.Entities.TryGetValue(5, out var p) ? r.Classes[p.ClassId] : "",
            r.Entities.TryGetValue(5, out p) ? r.Models[p.ModelIndex] : "")).ToList();
    }

    [Fact]
    public void ReplayRebuildsEveryFrame()
    {
        using var writer = FourFrames();
        using var reader = new WireReader(_wire);

        var frames = Record(reader, 0, 1, 2, 3);

        Assert.Equal(new ulong[] { 0, 1, 2, 3 }, frames.Select(f => f.frame));
        Assert.Equal(new[] { 10, 11, 12, 13 }, frames.Select(f => f.tick));
        Assert.Equal(new float[] { 0, 1, 2, 3 }, frames.Select(f => f.x[1]));
        Assert.Equal("prop_physics", frames[1].cls5);
        Assert.Equal("models/props/tools/humans/crates/crated_new.mdl", frames[1].model5);
        Assert.False(frames[2].x.ContainsKey(5));
        Assert.Equal(2, frames[3].x.Count);
        Assert.All(frames, f => Assert.False(f.gap));
    }

    [Fact]
    public void AMissedFrameIsMarkedAndRecoveredByAKeyframe()
    {
        using var writer = FourFrames();
        using var reader = new WireReader(_wire);

        var frames = Record(reader, 0, 1, 3);

        Assert.True(frames[2].gap);
        Assert.Equal(3f, frames[2].x[1]);
        Assert.False(frames[2].x.ContainsKey(5));
    }

    [Fact]
    public void UnchangedEntitiesCostNothing()
    {
        using var writer = FourFrames();
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        using var recorder = new Recorder(new MemoryStream());

        Assert.True(reader.TryReadFrame(0, snap));
        recorder.Write(snap, reader.Map, 0.015f, reader);
        long afterKey = recorder.Bytes;
        Assert.True(reader.TryReadFrame(1, snap));
        recorder.Write(snap, reader.Map, 0.015f, reader);

        // Record header (5) + frame fields (21) + one changed entity (56) + removed count (4)
        // + damage, defense and layer dropped-and-count pairs (8 each).
        Assert.Equal(110, recorder.Bytes - afterKey);
    }

    [Fact]
    public void ALevelChangeResetsNames()
    {
        using var writer = FourFrames();
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer))
        {
            Assert.True(reader.TryReadFrame(0, snap));
            recorder.Write(snap, reader.Map, 0.015f, reader);
            writer.NewLevel("l02_a");
            writer.Class(0, "npc_goblin");
            writer.Frame(tick: 1, (7, 0, 0, 5f));
            Assert.True(reader.TryReadFrame(4, snap));
            recorder.Write(snap, reader.Map, 0.015f, reader);
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }

        var last = RecordingReader.Frames(buffer).Last();
        Assert.Equal("l02_a", last.Map);
        Assert.Equal(new ushort[] { 7 }, last.Entities.Keys);
        Assert.Equal("npc_goblin", last.Classes[0]);
    }

    [Fact]
    public void SequenceNamesAreRecordedOncePerModel()
    {
        using var writer = FourFrames();
        using var reader = new WireReader(_wire);
        var asked = new List<string>();
        var buffer = new MemoryStream();
        var snap = new WireSnapshot();
        using (var recorder = new Recorder(buffer, path => { asked.Add(path); return new[] { "idle", "attack_1" }; }))
        {
            foreach (ulong f in new ulong[] { 0, 1 })
            {
                Assert.True(reader.TryReadFrame(f, snap));
                recorder.Write(snap, reader.Map, 0.015f, reader);
            }
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }

        Assert.Equal(new[] { "models/props/tools/humans/crates/crated_new.mdl" }, asked);
        var last = RecordingReader.Frames(buffer).Last();
        Assert.Equal(new[] { "idle", "attack_1" }, last.Sequences[565]);
        Assert.False(last.Sequences.ContainsKey(1));
    }

    [Fact]
    public void ReadsVersion1Recordings()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(RecordingFormat.Magic); w.Write(1u); w.Write(0u);
        void Rec(byte kind, byte[] body) { w.Write(kind); w.Write((uint)body.Length); w.Write(body); }
        var level = new MemoryStream(); var lw = new BinaryWriter(level);
        lw.Write(1u); lw.Write(0.015f); lw.Write((ushort)5); lw.Write("l01_a"u8.ToArray());
        Rec(RecordingFormat.LevelRecord, level.ToArray());
        var frame = new MemoryStream(); var fw = new BinaryWriter(frame);
        fw.Write(0ul); fw.Write(42); fw.Write(1f); fw.Write(RecordingFormat.KeyFlag); fw.Write(1);
        fw.Write((ushort)7); fw.Write((ushort)0); fw.Write(0); fw.Write(10f); fw.Write(20f); fw.Write(30f); fw.Write(0f); fw.Write(90f); fw.Write(0f);
        fw.Write(0);
        Rec(RecordingFormat.FrameRecord, frame.ToArray());
        ms.Position = 0;

        var only = Assert.Single(RecordingReader.Frames(ms));
        var e = only.Entities[7];
        Assert.Equal(42, only.Tick);
        Assert.Equal((10f, 20f, 30f, 90f), (e.OriginX, e.OriginY, e.OriginZ, e.Yaw));
        Assert.Equal(-1, e.Sequence);
        Assert.Equal(WireEntityFlags.None, e.Flags);
    }

    [Fact]
    public void DamageTravelsThroughWireAndRecording()
    {
        using var writer = FourFrames();
        writer.Frame(tick: 14, new[] { (0, 0, 1, 0f), (1, 1, 0, 4f) }, new WireDamage
        {
            Tick = 14, Victim = 1, Attacker = 5, Inflictor = 5, TraceEntity = WireLayout.NoEntity,
            Damage = 12, HealthBefore = 35, HealthAfter = 23, DamageType = 4, HitGroup = 2, TraceValid = 1,
            PositionX = 1, TraceEndZ = 64, PushForce = 3, SlowTimeFactor = 0.25f,
        });
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(4, snap));
        var hit = Assert.Single(snap.DamageSpan.ToArray());
        Assert.Equal((1, 5, 12f, 23f, (short)2), (hit.Victim, hit.Attacker, hit.Damage, hit.HealthAfter, hit.HitGroup));

        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer))
        {
            foreach (ulong f in new ulong[] { 3, 4 })
            {
                Assert.True(reader.TryReadFrame(f, snap));
                recorder.Write(snap, reader.Map, 0.015f, reader);
            }
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        var frames = RecordingReader.Frames(buffer).Select(f => f.Damage.ToArray()).ToList();
        Assert.Empty(frames[0]);
        var recorded = Assert.Single(frames[1]);
        Assert.Equal((64f, 3, 0.25f), (recorded.TraceEndZ, recorded.PushForce, recorded.SlowTimeFactor));
    }

    [Fact]
    public void DefenseTravelsThroughWireAndRecording()
    {
        using var writer = FourFrames();
        writer.Defend(new WireDefense { Tick = 14, Victim = 5, Attacker = 1, Weapon = 7, Result = DefenseResult.Shield, DirectionX = 1 });
        writer.Frame(tick: 14, (0, 0, 1, 0f));
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(4, snap));
        Assert.Equal(DefenseResult.Shield, Assert.Single(snap.DefenseSpan.ToArray()).Result);

        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer))
        {
            recorder.Write(snap, reader.Map, 0.015f, reader);
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        var recorded = Assert.Single(RecordingReader.Frames(buffer).Single().Defense);
        Assert.Equal((5, 1, 7, DefenseResult.Shield, 1f), (recorded.Victim, recorded.Attacker, recorded.Weapon, recorded.Result, recorded.DirectionX));
    }

    [Fact]
    public void LayersTravelThroughWireAndRecording()
    {
        using var writer = new TestWriter(_wire, "l01_a");
        writer.Layer(new WireLayer { Index = 7, Slot = 1, Flags = 1, Sequence = 37, Order = 2, Cycle = 0.25f, Weight = 0.8f, PlaybackRate = 1 });
        writer.FrameEntities(10, new WireEntity { Index = 7, Serial = 1 });
        writer.FrameEntities(11, new WireEntity { Index = 7, Serial = 1 });
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(0, snap));
        Assert.Equal((short)37, Assert.Single(snap.LayerSpan.ToArray()).Sequence);

        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer))
        {
            foreach (ulong f in new ulong[] { 0, 1 })
            {
                Assert.True(reader.TryReadFrame(f, snap));
                recorder.Write(snap, reader.Map, 0.015f, reader);
            }
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        var frames = RecordingReader.Frames(buffer).Select(f => f.Layers.ToArray()).ToList();
        var layer = Assert.Single(frames[0]);
        Assert.Equal((7, 1, 0.25f, 0.8f, (short)2), (layer.Index, layer.Slot, layer.Cycle, layer.Weight, layer.Order));
        Assert.Empty(frames[1]);
    }

    [Fact]
    public void AReusedIndexKeepsEachEntitysSerial()
    {
        // An orc dies at index 7 and another orc takes the index: same class and
        // model, so only the serial tells them apart.
        using var writer = new TestWriter(_wire, "l01_a");
        writer.Class(0, "npc_orc_sword");
        writer.FrameEntities(10, new WireEntity { Index = 7, Serial = 0x123, OriginX = 1 });
        writer.FrameEntities(11, new WireEntity { Index = 7, Serial = 0x9AB, OriginX = 1 });
        using var reader = new WireReader(_wire);
        var buffer = new MemoryStream();
        var snap = new WireSnapshot();
        using (var recorder = new Recorder(buffer))
        {
            foreach (ulong f in new ulong[] { 0, 1 })
            {
                Assert.True(reader.TryReadFrame(f, snap));
                recorder.Write(snap, reader.Map, 0.015f, reader);
            }
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        Assert.Equal(new uint[] { 0x123, 0x9AB }, RecordingReader.Frames(buffer).Select(f => f.Entities[7].Serial).ToArray());
    }

    [Fact]
    public void ReadsVersion4DamageAndDefenseWithoutSerials()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(RecordingFormat.Magic); w.Write(4u); w.Write(0u);
        void Rec(byte kind, byte[] body) { w.Write(kind); w.Write((uint)body.Length); w.Write(body); }
        var frame = new MemoryStream(); var fw = new BinaryWriter(frame);
        fw.Write(0ul); fw.Write(5); fw.Write(0f); fw.Write(RecordingFormat.KeyFlag); fw.Write(0); fw.Write(0);
        fw.Write(0u); fw.Write(1); var dmg = new byte[128]; BitConverter.GetBytes(5).CopyTo(dmg, 0); BitConverter.GetBytes(12f).CopyTo(dmg, 12); fw.Write(dmg);
        fw.Write(0u); fw.Write(1); var def = new byte[32]; BitConverter.GetBytes(8).CopyTo(def, 12); fw.Write(def);
        Rec(RecordingFormat.FrameRecord, frame.ToArray());
        ms.Position = 0;

        var only = Assert.Single(RecordingReader.Frames(ms));
        var hit = Assert.Single(only.Damage);
        Assert.Equal((5, 12f, 0u), (hit.Tick, hit.Damage, hit.VictimSerial));
        Assert.Equal(DefenseResult.Shield, Assert.Single(only.Defense).Result);
    }

    public void Dispose() => File.Delete(_wire);
}
