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

        // Record header (5) + frame fields (21) + one changed entity (80) + removed count (4)
        // + damage, defense and layer dropped-and-count pairs (8 each)
        // + v7: wall clock (8), player state (24), slow-time dropped-and-count (8)
        // + v8/v9: the client view (232) + v12/v14: the player's input (48).
        Assert.Equal(454, recorder.Bytes - afterKey);
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
    public void Version14CarriesWallClockPlayerStateSlowTimeLinksParentsTheClientViewAndInput()
    {
        // An orc knocked down: its ragdoll links back to it and it to the ragdoll. The player is in slow motion.
        using var writer = new TestWriter(_wire, "l01_a");
        writer.Class(0, "npc_orc_sword");
        writer.Class(1, "prop_ragdoll");
        writer.Class(2, "player");
        var client = new WireClientView
        {
            Player = 1, Viewmodel = 7, Flags = ClientViewFlags.ViewAngles | ClientViewFlags.Player | ClientViewFlags.Viewmodel | ClientViewFlags.Camera | ClientViewFlags.ViewmodelAnimation | ClientViewFlags.ViewmodelLayers,
            ViewPitch = 12, ViewYaw = 90, OriginX = 100, ViewmodelX = 101,
            CameraZ = 50, CameraYaw = 91, Fov = 101.4f, ScreenWidth = 1280, ScreenHeight = 720, ViewmodelSequence = 4, ViewmodelCycle = 0.25f,
        };
        client.SetViewmodelLayers([new WireClientLayer { Sequence = 30, Order = 2, Cycle = 0.4f, Weight = 1 }]);
        writer.Client(client);
        writer.Input(new WirePlayerInput { Player = 1, Valid = 1, Buttons = InputButtons.Attack | InputButtons.Forward, Pressed = InputButtons.Attack,
            Commands = 2, ForwardMove = 450, SideMove = -450, CommandButtons = InputButtons.Attack | InputButtons.Forward, Actions = PlayerActions.Kick, CommandNumber = 1234 });
        writer.Player(new WirePlayerState { Index = 1, InBulletTime = 1, BulletTimeStart = 151.2f, BulletTimeDuration = 2, BulletTimeFactor = 0.1f, Adrenaline = 40, MaxAdrenaline = 100 }, wallSeconds: 12.5);
        writer.SlowTime(new WireSlowTime { Tick = 10, Player = 1, PlayerSerial = 1, Source = SlowTimeSource.Request, Start = 151.2f, Duration = 2, Factor = 0.1f, Caller = 0x3B2447 });
        writer.FrameEntities(10,
            new WireEntity { Index = 1, ClassId = 2, Serial = 1, Link = WireLayout.NoEntity },
            new WireEntity { Index = 7, ClassId = 0, Serial = 0x123, Link = 9, LinkSerial = 0x55 },
            new WireEntity { Index = 9, ClassId = 1, Serial = 0x55, Link = 7, LinkSerial = 0x123, Parent = 7, ParentAttachment = 3, ParentSerial = 0x123, Owner = 7, OwnerSerial = 0x123 });
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(0, snap));
        Assert.Equal(12.5, snap.WallSeconds);
        Assert.Equal(0.1f, snap.Player.BulletTimeFactor);
        Assert.Equal(0x3B2447u, Assert.Single(snap.SlowTimeSpan.ToArray()).Caller);

        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer))
        {
            recorder.Write(snap, reader.Map, 0.015f, reader);
            recorder.Flush();
            buffer = new MemoryStream(buffer.ToArray());
        }
        var f = Assert.Single(RecordingReader.Frames(new MemoryStream(buffer.ToArray())));
        Assert.Equal(14u, f.Version);
        Assert.Equal((2, 450f, -450f, PlayerActions.Kick, 1234), (f.Input.Commands, f.Input.ForwardMove, f.Input.SideMove, f.Input.Actions, f.Input.CommandNumber));
        Assert.Equal(InputButtons.Attack | InputButtons.Forward, f.Input.Buttons);
        Assert.Equal(InputButtons.Attack, f.Input.Pressed);
        Assert.Equal((ushort)7, f.Entities[9].Parent);
        Assert.Equal((byte)3, f.Entities[9].ParentAttachment);
        Assert.Equal(0x123u, f.Entities[9].OwnerSerial);
        Assert.Equal(90f, f.ClientView.ViewYaw);
        Assert.Equal(91f, f.ClientView.CameraYaw);
        Assert.Equal((ushort)1280, f.ClientView.ScreenWidth);
        Assert.Equal((short)4, f.ClientView.ViewmodelSequence);
        Assert.Equal(0.25f, f.ClientView.ViewmodelCycle);
        Assert.Equal((short)30, Assert.Single(f.ClientView.ViewmodelLayers).Sequence);

        // The same frame as a v8 recording wrote it: its client view stops after the viewmodel's angles.
        var v8 = AsVersion8(buffer.ToArray());
        var old = Assert.Single(RecordingReader.Frames(new MemoryStream(v8)));
        Assert.Equal(8u, old.Version);
        Assert.Equal(90f, old.ClientView.ViewYaw);
        Assert.Equal(101f, old.ClientView.ViewmodelX);
        Assert.Equal(0f, old.ClientView.CameraYaw);
        Assert.Equal((short)-1, old.ClientView.ViewmodelSequence);
        Assert.Equal(WireLayout.NoEntity, old.Entities[9].Parent);
        Assert.Equal(WireLayout.NoEntity, old.Input.Player);
        Assert.Equal((ushort)9, old.Entities[7].Link);
        Assert.Equal((ushort)7, f.ClientView.Viewmodel);
        Assert.Equal(101f, f.ClientView.ViewmodelX);
        Assert.Equal(12.5, f.WallSeconds);
        Assert.Equal((byte)1, f.Player.InBulletTime);
        Assert.Equal(40f, f.Player.Adrenaline);
        Assert.Equal(2f, Assert.Single(f.SlowTime).Duration);
        Assert.Equal(SlowTimeSource.Request, f.SlowTime[0].Source);
        Assert.Equal((ushort)9, f.Entities[7].Link);
        Assert.Equal(0x123u, f.Entities[9].LinkSerial);
        Assert.Equal(WireLayout.NoEntity, f.Entities[1].Link);
    }

    [Fact]
    public void EventsNameTheEntityTheirSerialNames()
    {
        // The player kills the orc at index 7; a ragdoll takes index 7 in the same tick. The killing
        // blow names (7, 0x123), the orc, not the ragdoll now at 7. A blow naming an identity the
        // recording never captured stays unresolved.
        using var writer = new TestWriter(_wire, "l01_a");
        writer.Class(0, "npc_orc_sword");
        writer.Class(1, "prop_ragdoll");
        writer.Class(2, "player");
        writer.FrameEntities(10, new WireEntity { Index = 1, ClassId = 2, Serial = 1 }, new WireEntity { Index = 7, ClassId = 0, Serial = 0x123 });
        writer.Damage(
            new WireDamage { Tick = 11, Attacker = 1, AttackerSerial = 1, Victim = 7, VictimSerial = 0x123, Inflictor = WireLayout.NoEntity, TraceEntity = WireLayout.NoEntity, Weapon = WireLayout.NoEntity },
            new WireDamage { Tick = 11, Attacker = 1, AttackerSerial = 1, Victim = 8, VictimSerial = 0x55, Inflictor = WireLayout.NoEntity, TraceEntity = WireLayout.NoEntity, Weapon = WireLayout.NoEntity });
        writer.FrameEntities(11, new WireEntity { Index = 1, ClassId = 2, Serial = 1 }, new WireEntity { Index = 7, ClassId = 1, Serial = 0x9AB });
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
        var names = new EventNames();
        var blows = new List<string>();
        foreach (var f in RecordingReader.Frames(buffer))
        {
            names.Observe(f);
            foreach (var d in f.Damage) blows.Add($"{names.Name(f, d.Attacker, d.AttackerSerial)} -> {names.Name(f, d.Victim, d.VictimSerial)}");
        }
        Assert.Equal(new[] { "player -> npc_orc_sword", "player -> #8.55 (not captured)" }, blows);
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

    /// <summary>
    /// A current recording rewritten as v8 wrote it: the version, each entity
    /// cut to its first 64 bytes (no parent or owner), each frame's client
    /// view cut to its first 80 bytes, and no input.
    /// </summary>
    /// <summary>From v13 a damage record carries the victim's animation as the blow arrived, before TakeDamage ran.</summary>
    [Fact]
    public void Version13DamageCarriesTheVictimsAnimationAsTheBlowArrived()
    {
        using var writer = new TestWriter(_wire, "l01_a");
        writer.Class(0, "npc_orc_sword");
        writer.Class(1, "player");
        // The player's blow on the orc, mid attack, aiming, with one overlay.
        var blow = new WireDamage { Tick = 10, Victim = 7, VictimSerial = 0x123, Attacker = 1, AttackerSerial = 1, Damage = 3, DamageType = 4, TraceValid = 1,
            Inflictor = WireLayout.NoEntity, TraceEntity = WireLayout.NoEntity, Weapon = WireLayout.NoEntity };
        var pose = new float[WireDamage.PoseParameters];
        pose[0] = 0.25f;
        pose[23] = -1;
        blow.SetVictimAnimation(12, 0.4f, 0.8f, 151.1f, pose, [(31, 1, 0.6f, 0.9f)]);
        writer.Damage(blow);
        writer.FrameEntities(10,
            new WireEntity { Index = 1, ClassId = 1, Serial = 1, Link = WireLayout.NoEntity },
            new WireEntity { Index = 7, ClassId = 0, Serial = 0x123, Link = WireLayout.NoEntity });
        using var reader = new WireReader(_wire);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(0, snap));
        var buffer = new MemoryStream();
        using (var recorder = new Recorder(buffer)) { recorder.Write(snap, reader.Map, 0.015f, reader); recorder.Flush(); }
        var d = Assert.Single(Assert.Single(RecordingReader.Frames(new MemoryStream(buffer.ToArray()))).Damage);
        Assert.Equal((1, (short)12, 0.4f, 0.8f, 151.1f), (d.VictimAnimValid, d.VictimSequence, d.VictimCycle, d.VictimPlaybackRate, d.VictimAnimTime));
        Assert.Equal(0.25f, d.VictimPose[0]);
        Assert.Equal(-1f, d.VictimPose[23]);
        Assert.Equal(((short)31, (short)1, 0.6f, 0.9f), Assert.Single(d.VictimOverlays));
    }

    static byte[] AsVersion8(byte[] current)
    {
        int entityNow = System.Runtime.InteropServices.Marshal.SizeOf<WireEntity>(), entityThen = 64;
        int viewTail = System.Runtime.InteropServices.Marshal.SizeOf<WireClientView>() - WireClientView.V8Bytes
            + System.Runtime.InteropServices.Marshal.SizeOf<WirePlayerInput>();
        var output = new MemoryStream();
        output.Write(current, 0, 16);
        BitConverter.GetBytes(8u).CopyTo(output.GetBuffer(), 8);
        for (int at = 16; at < current.Length;)
        {
            byte kind = current[at];
            int length = BitConverter.ToInt32(current, at + 1);
            var payload = current.AsSpan(at + 5, length);
            var record = new MemoryStream();
            if (kind == 4)
            {
                // frame u64, tick i32, curtime f32, flags u8, then the changed entities.
                record.Write(payload[..17]);
                int changed = BitConverter.ToInt32(payload[17..]);
                record.Write(payload.Slice(17, 4));
                int p = 21;
                for (int i = 0; i < changed; i++, p += entityNow) record.Write(payload.Slice(p, entityThen));
                record.Write(payload[p..^viewTail]);
            }
            else record.Write(payload);
            output.WriteByte(kind);
            output.Write(BitConverter.GetBytes((int)record.Length));
            record.WriteTo(output);
            at += 5 + length;
        }
        return output.ToArray();
    }
}
