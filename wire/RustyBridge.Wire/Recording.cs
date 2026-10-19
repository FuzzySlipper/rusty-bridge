using System.Runtime.InteropServices;
using System.Text;

namespace RustyBridge.Wire;

// Recording v6 (.rbrec): Wire frames saved to disk for the stepper.
// Format: docs/recording.md. Parsed by RecordingReader and viewer/timeline.html.
public static class RecordingFormat
{
    public const ulong Magic = 0x3130304345524252ul; // "RBREC001"
    public const uint Version = 15;      // v2: 52-byte entities, sequence names; v3: damage; v4: defense; v5: serials; v6: overlay layers;
                                        // v7: 64-byte entities (links), wall clock, player state, slow-time events; v8: the client's view;
                                        // v9: its camera and viewmodel animation; v10: 80-byte entities (parent, attachment, owner);
                                        // v11: 56-byte defense records (the NPC's defense state); v12: the player's input;
                                        // v13: 360-byte damage records (the victim's animation as the blow arrived);
                                        // v14: 48-byte player input (the user commands, the kick among Arkane's action bytes);
                                        // v15: the player's stats (stamina, mana, skill modules, equipment) after the input

    // Record sizes by version; older records are a prefix of the current ones.
    public static int EntityBytes(uint version) => version switch { 1 => 32, < 5 => 52, < 7 => 56, < 10 => 64, _ => Marshal.SizeOf<WireEntity>() };
    /// <summary>The player input record: 16 bytes in v12 and v13, the commands added from v14.</summary>
    public static int InputBytes(uint version) => version < 14 ? 16 : Marshal.SizeOf<WirePlayerInput>();
    public static int DamageBytes(uint version) => version < 5 ? 128 : version < 13 ? 152 : Marshal.SizeOf<WireDamage>();
    public static int DefenseBytes(uint version) => version < 5 ? 32 : version < 11 ? 48 : Marshal.SizeOf<WireDefense>();
    public const int KeyframeInterval = 330;  // 5 s at 66 ticks/s

    public const byte LevelRecord = 1;
    public const byte ClassRecord = 2;
    public const byte ModelRecord = 3;
    public const byte FrameRecord = 4;
    public const byte SequencesRecord = 5;

    public const byte KeyFlag = 1;  // the frame lists every live entity; drop prior state
    public const byte GapFlag = 2;  // frames before this one were missed
}

/// <summary>
/// Appends snapshots to a recording, delta-coded against the previous frame:
/// only entities whose 32-byte record changed, plus the indices that went
/// away. Every <see cref="RecordingFormat.KeyframeInterval"/> frames, and on
/// a level change or a gap, the frame is written whole.
/// </summary>
public sealed class Recorder : IDisposable
{
    readonly BinaryWriter _out;
    readonly WireEntity[] _prev = new WireEntity[WireLayout.MaxEntities];
    readonly bool[] _prevLive = new bool[WireLayout.MaxEntities];
    readonly bool[] _live = new bool[WireLayout.MaxEntities];
    readonly bool[] _classSent = new bool[WireLayout.MaxClasses];
    readonly bool[] _modelSent = new bool[WireLayout.MaxModels];
    readonly List<WireEntity> _changed = new();
    readonly List<ushort> _removed = new();
    readonly MemoryStream _payload = new();
    readonly Func<string, IReadOnlyList<string>>? _sequenceNames;
    uint _level = uint.MaxValue;
    ulong _lastFrame;
    int _sinceKey;

    public long FramesWritten { get; private set; }
    public long Bytes => _out.BaseStream.Position;

    /// <param name="sequenceNames">Sequence names for a model path, written once per model so a
    /// reader can name each entity's m_nSequence; null records none.</param>
    public Recorder(Stream stream, Func<string, IReadOnlyList<string>>? sequenceNames = null)
    {
        _sequenceNames = sequenceNames;
        _out = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        _out.Write(RecordingFormat.Magic);
        _out.Write(RecordingFormat.Version);
        _out.Write(0u);
    }

    /// <summary>Appends one snapshot; <paramref name="names"/> resolves its ids.</summary>
    public void Write(WireSnapshot snap, string map, float tickInterval, WireReader names)
    {
        bool key = false;
        byte flags = 0;
        if (snap.LevelSerial != _level)
        {
            _level = snap.LevelSerial;
            Array.Clear(_classSent);
            Array.Clear(_modelSent);
            Record(RecordingFormat.LevelRecord, w => { w.Write(_level); w.Write(tickInterval); Str(w, map); });
            key = true;
        }
        else if (snap.Frame != _lastFrame + 1)
        {
            key = true;
            flags |= RecordingFormat.GapFlag;
        }
        if (_sinceKey >= RecordingFormat.KeyframeInterval) key = true;
        _lastFrame = snap.Frame;

        _changed.Clear();
        _removed.Clear();
        Array.Clear(_live);
        foreach (ref readonly var e in snap.Span)
        {
            if (e.ClassId < WireLayout.MaxClasses && !_classSent[e.ClassId])
            {
                _classSent[e.ClassId] = true;
                string name = names.ClassName(snap, e.ClassId);
                ushort id = e.ClassId;
                Record(RecordingFormat.ClassRecord, w => { w.Write(id); Str(w, name); });
            }
            if (e.ModelIndex > 0 && e.ModelIndex < WireLayout.MaxModels && !_modelSent[e.ModelIndex])
            {
                _modelSent[e.ModelIndex] = true;
                string path = names.ModelPath(snap, e.ModelIndex);
                int index = e.ModelIndex;
                Record(RecordingFormat.ModelRecord, w => { w.Write(index); Str(w, path); });
                var sequences = path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? _sequenceNames?.Invoke(path) : null;
                if (sequences is { Count: > 0 })
                    Record(RecordingFormat.SequencesRecord, w =>
                    {
                        w.Write(index);
                        w.Write((ushort)sequences.Count);
                        foreach (var name in sequences) Str(w, name);
                    });
            }
            _live[e.Index] = true;
            if (key || !_prevLive[e.Index] || !Same(_prev[e.Index], e)) _changed.Add(e);
            _prev[e.Index] = e;
        }
        if (!key)
            for (int i = 0; i < WireLayout.MaxEntities; i++)
                if (_prevLive[i] && !_live[i]) _removed.Add((ushort)i);
        Array.Copy(_live, _prevLive, _live.Length);

        if (key) { flags |= RecordingFormat.KeyFlag; _sinceKey = 0; }
        _sinceKey++;
        Record(RecordingFormat.FrameRecord, w =>
        {
            w.Write(snap.Frame);
            w.Write(snap.Tick);
            w.Write(snap.Curtime);
            w.Write(flags);
            w.Write(_changed.Count);
            w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_changed)));
            w.Write(_removed.Count);
            foreach (ushort r in _removed) w.Write(r);
            w.Write(snap.DamageDropped);
            w.Write(snap.DamageCount);
            w.Write(MemoryMarshal.AsBytes(snap.DamageSpan));
            w.Write(snap.DefenseDropped);
            w.Write(snap.DefenseCount);
            w.Write(MemoryMarshal.AsBytes(snap.DefenseSpan));
            // Layers change every tick (their cycles advance), so each frame carries all of them.
            w.Write(snap.LayerDropped);
            w.Write(snap.LayerCount);
            w.Write(MemoryMarshal.AsBytes(snap.LayerSpan));
            // v7: the wall clock, the player's slow motion and adrenaline, and bullet-time calls.
            w.Write(snap.WallSeconds);
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<WirePlayerState>(in snap.Player)));
            w.Write(snap.SlowTimeDropped);
            w.Write(snap.SlowTimeCount);
            w.Write(MemoryMarshal.AsBytes(snap.SlowTimeSpan));
            // v8: the client's view.
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<WireClientView>(in snap.ClientView)));
            // v12: the player's input.
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<WirePlayerInput>(in snap.Input)));
            // v15: the player's stats.
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<WirePlayerStats>(in snap.Stats)));
        });
        FramesWritten++;
    }

    static bool Same(in WireEntity a, in WireEntity b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<WireEntity>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<WireEntity>(in b)));

    void Record(byte kind, Action<BinaryWriter> body)
    {
        _payload.SetLength(0);
        using (var w = new BinaryWriter(_payload, Encoding.ASCII, leaveOpen: true)) body(w);
        _out.Write(kind);
        _out.Write((uint)_payload.Length);
        _out.Write(_payload.GetBuffer(), 0, (int)_payload.Length);
    }

    static void Str(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    public void Flush() => _out.Flush();
    public void Dispose() => _out.Dispose();
}

/// <summary>One frame of a recording with the full state rebuilt.</summary>
public sealed class RecordedFrame
{
    /// <summary>The recording's format version; v5 and later carry EHANDLE serials.</summary>
    public uint Version;
    public ulong Frame;
    public int Tick;
    public float Curtime;
    public bool Gap;
    public uint LevelSerial;
    public string Map = "";
    public float TickInterval;
    /// <summary>Every live entity, by edict index.</summary>
    public readonly SortedDictionary<ushort, WireEntity> Entities = new();
    /// <summary>Damage applied since the previous frame (v3 recordings).</summary>
    public readonly List<WireDamage> Damage = new();
    public uint DamageDropped;
    /// <summary>Melee defense checks since the previous frame (v4 recordings).</summary>
    public readonly List<WireDefense> Defense = new();
    public uint DefenseDropped;
    /// <summary>Every active overlay layer this frame (v6 recordings).</summary>
    public readonly List<WireLayer> Layers = new();
    public uint LayerDropped;
    /// <summary>v7: the tap's wall clock at the frame (seconds); NaN in older recordings.</summary>
    public double WallSeconds = double.NaN;
    /// <summary>v7: the player's slow motion and adrenaline; Index is NoEntity in older recordings.</summary>
    public WirePlayerState Player = new() { Index = WireLayout.NoEntity };
    /// <summary>v7: bullet-time setter calls since the previous frame.</summary>
    public readonly List<WireSlowTime> SlowTime = new();
    public uint SlowTimeDropped;
    /// <summary>v8: the client's view of the local player; <see cref="WireClientView.None"/> in older recordings.</summary>
    public WireClientView ClientView = WireClientView.None;
    /// <summary>v12: the player's input as the server ran it this tick; <see cref="WirePlayerInput.None"/> in older recordings.</summary>
    public WirePlayerInput Input = WirePlayerInput.None;
    /// <summary>v15: the player's stats and equipment; <see cref="WirePlayerStats.None"/> in older recordings.</summary>
    public WirePlayerStats Stats = WirePlayerStats.None;
    public readonly Dictionary<ushort, string> Classes = new();
    public readonly Dictionary<int, string> Models = new();
    /// <summary>Sequence names by model index, in m_nSequence order.</summary>
    public readonly Dictionary<int, string[]> Sequences = new();
}

/// <summary>Replays a recording frame by frame, rebuilding full state from the deltas.</summary>
public static class RecordingReader
{
    public static IEnumerable<RecordedFrame> Frames(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (r.ReadUInt64() != RecordingFormat.Magic) throw new InvalidDataException("not a Rusty Bridge recording");
        uint version = r.ReadUInt32();
        if (version is < 1 or > RecordingFormat.Version) throw new InvalidDataException($"recording v{version}, reader knows v1..v{RecordingFormat.Version}");
        int entityBytes = RecordingFormat.EntityBytes(version);
        int damageBytes = RecordingFormat.DamageBytes(version), defenseBytes = RecordingFormat.DefenseBytes(version);
        r.ReadUInt32();

        var state = new RecordedFrame { Version = version };
        while (stream.Position < stream.Length)
        {
            byte kind = r.ReadByte();
            uint length = r.ReadUInt32();
            long end = stream.Position + length;
            switch (kind)
            {
                case RecordingFormat.LevelRecord:
                    state.LevelSerial = r.ReadUInt32();
                    state.TickInterval = r.ReadSingle();
                    state.Map = Str(r);
                    state.Entities.Clear();
                    state.Classes.Clear();
                    state.Models.Clear();
                    state.Sequences.Clear();
                    break;
                case RecordingFormat.ClassRecord:
                    state.Classes[r.ReadUInt16()] = Str(r);
                    break;
                case RecordingFormat.ModelRecord:
                    state.Models[r.ReadInt32()] = Str(r);
                    break;
                case RecordingFormat.SequencesRecord:
                {
                    int model = r.ReadInt32();
                    var names = new string[r.ReadUInt16()];
                    for (int i = 0; i < names.Length; i++) names[i] = Str(r);
                    state.Sequences[model] = names;
                    break;
                }
                case RecordingFormat.FrameRecord:
                    state.Frame = r.ReadUInt64();
                    state.Tick = r.ReadInt32();
                    state.Curtime = r.ReadSingle();
                    byte flags = r.ReadByte();
                    state.Gap = (flags & RecordingFormat.GapFlag) != 0;
                    if ((flags & RecordingFormat.KeyFlag) != 0) state.Entities.Clear();
                    int changed = r.ReadInt32();
                    var bytes = r.ReadBytes(changed * entityBytes);
                    for (int i = 0; i < changed; i++)
                    {
                        var e = Widen<WireEntity>(bytes.AsSpan(i * entityBytes, entityBytes), new WireEntity { Sequence = -1, Link = WireLayout.NoEntity, Parent = WireLayout.NoEntity, Owner = WireLayout.NoEntity });
                        state.Entities[e.Index] = e;
                    }
                    int removed = r.ReadInt32();
                    for (int i = 0; i < removed; i++) state.Entities.Remove(r.ReadUInt16());
                    state.Damage.Clear();
                    state.DamageDropped = 0;
                    if (version >= 3)
                    {
                        state.DamageDropped = r.ReadUInt32();
                        int damage = r.ReadInt32();
                        var records = r.ReadBytes(damage * damageBytes);
                        for (int i = 0; i < damage; i++) state.Damage.Add(Widen<WireDamage>(records.AsSpan(i * damageBytes, damageBytes), default));
                    }
                    state.Defense.Clear();
                    state.DefenseDropped = 0;
                    if (version >= 4)
                    {
                        state.DefenseDropped = r.ReadUInt32();
                        int defense = r.ReadInt32();
                        var records = r.ReadBytes(defense * defenseBytes);
                        for (int i = 0; i < defense; i++) state.Defense.Add(Widen<WireDefense>(records.AsSpan(i * defenseBytes, defenseBytes), default));
                    }
                    state.Layers.Clear();
                    state.LayerDropped = 0;
                    if (version >= 6)
                    {
                        state.LayerDropped = r.ReadUInt32();
                        int layers = r.ReadInt32();
                        state.Layers.AddRange(MemoryMarshal.Cast<byte, WireLayer>(r.ReadBytes(layers * Marshal.SizeOf<WireLayer>())).ToArray());
                    }
                    state.WallSeconds = double.NaN;
                    state.Player = new WirePlayerState { Index = WireLayout.NoEntity };
                    state.SlowTime.Clear();
                    state.SlowTimeDropped = 0;
                    if (version >= 7)
                    {
                        state.WallSeconds = r.ReadDouble();
                        state.Player = MemoryMarshal.Read<WirePlayerState>(r.ReadBytes(Marshal.SizeOf<WirePlayerState>()));
                        state.SlowTimeDropped = r.ReadUInt32();
                        int slow = r.ReadInt32();
                        state.SlowTime.AddRange(MemoryMarshal.Cast<byte, WireSlowTime>(r.ReadBytes(slow * Marshal.SizeOf<WireSlowTime>())).ToArray());
                    }
                    state.ClientView = WireClientView.None;
                    if (version >= 8)
                    {
                        // A v8 record is a prefix of the v9 one.
                        var viewBytes = new byte[Marshal.SizeOf<WireClientView>()];
                        r.ReadBytes(version >= 9 ? viewBytes.Length : WireClientView.V8Bytes).CopyTo(viewBytes, 0);
                        var view = MemoryMarshal.Read<WireClientView>(viewBytes);
                        if (version < 9) view.ViewmodelSequence = -1;
                        state.ClientView = view;
                    }
                    state.Input = version >= 12 ? Widen<WirePlayerInput>(r.ReadBytes(RecordingFormat.InputBytes(version)), WirePlayerInput.None) : WirePlayerInput.None;
                    state.Stats = version >= 15 ? MemoryMarshal.Read<WirePlayerStats>(r.ReadBytes(Marshal.SizeOf<WirePlayerStats>())) : WirePlayerStats.None;
                    yield return state;
                    break;
            }
            stream.Position = end;
        }
    }

    static string Str(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(r.ReadUInt16()));

    // An older record is a prefix of the current struct; fields it lacks keep their defaults.
    static T Widen<T>(ReadOnlySpan<byte> record, T value) where T : struct
    {
        record.CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }
}

/// <summary>
/// Names the entities an event record refers to. From v5 an event names an
/// entity by index and serial, and only that entity will do: one seen
/// earlier in the level keeps its class after its index is reused, and one
/// the recording never captured is reported as such instead of borrowing
/// the name of whatever holds the index now. Older recordings name by index
/// in the current frame.
/// </summary>
public sealed class EventNames
{
    readonly Dictionary<(ushort Index, uint Serial), string> _seen = new();
    uint _level;

    /// <summary>Remembers this frame's entities; call before naming the frame's events.</summary>
    public void Observe(RecordedFrame frame)
    {
        if (frame.LevelSerial != _level) { _seen.Clear(); _level = frame.LevelSerial; }
        if (frame.Version < 5) return;
        foreach (var (index, e) in frame.Entities) _seen[(index, e.Serial)] = frame.Classes.GetValueOrDefault(e.ClassId, "?");
    }

    public string Name(RecordedFrame frame, ushort index, uint serial)
    {
        if (index == WireLayout.NoEntity) return "-";
        if (frame.Version >= 5)
            return _seen.TryGetValue((index, serial), out var name) ? name : $"#{index}.{serial:x} (not captured)";
        return frame.Entities.TryGetValue(index, out var e) ? frame.Classes.GetValueOrDefault(e.ClassId, "?") : $"#{index}";
    }
}
