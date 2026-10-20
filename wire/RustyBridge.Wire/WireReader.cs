using System.IO.MemoryMappedFiles;
using System.Text;

namespace RustyBridge.Wire;

/// <summary>One donor tick copied out of the ring.</summary>
public sealed class WireSnapshot
{
    public ulong Frame;
    public int Tick;
    public float Curtime;
    public uint LevelSerial;
    public int Count;
    public readonly WireEntity[] Entities = new WireEntity[WireLayout.MaxEntities];
    /// <summary>Damage applied since the previous frame, in order.</summary>
    public int DamageCount;
    public uint DamageDropped;
    public readonly WireDamage[] Damage = new WireDamage[WireLayout.MaxDamage];

    public ReadOnlySpan<WireEntity> Span => Entities.AsSpan(0, Count);
    public ReadOnlySpan<WireDamage> DamageSpan => Damage.AsSpan(0, DamageCount);
    /// <summary>Melee defense checks since the previous frame, in order.</summary>
    public int DefenseCount;
    public uint DefenseDropped;
    public readonly WireDefense[] Defense = new WireDefense[WireLayout.MaxDefense];
    public ReadOnlySpan<WireDefense> DefenseSpan => Defense.AsSpan(0, DefenseCount);
    /// <summary>Active overlay layers of every entity this frame.</summary>
    public int LayerCount;
    public uint LayerDropped;
    public readonly WireLayer[] Layers = new WireLayer[WireLayout.MaxLayers];
    public ReadOnlySpan<WireLayer> LayerSpan => Layers.AsSpan(0, LayerCount);
    /// <summary>The tap's wall clock at the write (Wire v7).</summary>
    public double WallSeconds;
    /// <summary>The player's slow motion and adrenaline (Wire v7).</summary>
    public WirePlayerState Player;
    /// <summary>Bullet-time setter calls since the previous frame (Wire v7).</summary>
    public int SlowTimeCount;
    public uint SlowTimeDropped;
    public readonly WireSlowTime[] SlowTime = new WireSlowTime[WireLayout.MaxSlowTime];
    public ReadOnlySpan<WireSlowTime> SlowTimeSpan => SlowTime.AsSpan(0, SlowTimeCount);
    /// <summary>v8: the client's view of the local player.</summary>
    public WireClientView ClientView = WireClientView.None;
    /// <summary>v12: the player's input this tick.</summary>
    public WirePlayerInput Input = WirePlayerInput.None;
    /// <summary>v15: the player's stats and equipment.</summary>
    public WirePlayerStats Stats = WirePlayerStats.None;
    /// <summary>v17: the melee sweeps since the previous frame.</summary>
    public int SweepCount;
    public uint SweepDropped;
    public readonly WireSweep[] Sweeps = new WireSweep[WireLayout.MaxSweeps];
    public ReadOnlySpan<WireSweep> SweepSpan => Sweeps.AsSpan(0, SweepCount);
}

/// <summary>
/// Reads the tap's Wire file. The tap is the only writer; a read copies a
/// slot out and checks its seqlock afterwards, so the tap never waits on us.
/// </summary>
public sealed unsafe class WireReader : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    readonly Dictionary<int, string> _classes = new();
    readonly Dictionary<int, string> _models = new();
    uint _namesSerial;

    public const string DefaultPath =
        @"C:\Program Files (x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player\mm\addons\rustybridge.wire";

    public WireReader(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        if (_view.Capacity < WireLayout.FileBytes)
            throw new InvalidDataException($"{path} is {_view.Capacity} bytes, Wire v1 needs {WireLayout.FileBytes}");
    }

    public ref readonly WireHeader Header => ref *(WireHeader*)_base;

    /// <summary>The tap has initialised the file with the layout this reader knows.</summary>
    public bool IsLive => Volatile.Read(ref ((WireHeader*)_base)->Magic) == WireLayout.Magic && Header.Version == WireLayout.Version;

    public ulong FramesWritten => Volatile.Read(ref ((WireHeader*)_base)->FramesWritten);

    public string Map
    {
        get
        {
            fixed (byte* m = Header.Map) return CString(m, 64);
        }
    }

    public bool TryReadLatest(WireSnapshot snapshot)
    {
        ulong written = FramesWritten;
        return written != 0 && TryReadFrame(written - 1, snapshot);
    }

    /// <summary>
    /// Copies frame <paramref name="frame"/> if its slot still holds it and no
    /// write overlapped the copy. False means not written yet, overwritten by
    /// a newer frame, or torn; the caller decides whether to retry.
    /// </summary>
    public bool TryReadFrame(ulong frame, WireSnapshot snapshot)
    {
        byte* slot = _base + WireLayout.SlotsOffset + (long)(frame % WireLayout.SlotCount) * WireLayout.SlotBytes;
        var header = (WireSlotHeader*)slot;
        ulong seq = Volatile.Read(ref header->Seq);
        if (seq != 2 * frame + 2) return false;

        int count = (int)Math.Min(header->EntityCount, (uint)WireLayout.MaxEntities);
        snapshot.Frame = header->Frame;
        snapshot.Tick = header->Tick;
        snapshot.Curtime = header->Curtime;
        snapshot.LevelSerial = header->LevelSerial;
        new ReadOnlySpan<WireEntity>(slot + sizeof(WireSlotHeader), count).CopyTo(snapshot.Entities);
        int damage = (int)Math.Min(header->DamageCount, (uint)WireLayout.MaxDamage);
        snapshot.DamageDropped = header->DamageDropped;
        new ReadOnlySpan<WireDamage>(slot + WireLayout.DamageOffset, damage).CopyTo(snapshot.Damage);
        int defense = (int)Math.Min(header->DefenseCount, (uint)WireLayout.MaxDefense);
        snapshot.DefenseDropped = header->DefenseDropped;
        new ReadOnlySpan<WireDefense>(slot + WireLayout.DefenseOffset, defense).CopyTo(snapshot.Defense);
        int layers = (int)Math.Min(header->LayerCount, (uint)WireLayout.MaxLayers);
        snapshot.LayerDropped = header->LayerDropped;
        new ReadOnlySpan<WireLayer>(slot + WireLayout.LayersOffset, layers).CopyTo(snapshot.Layers);
        int slow = (int)Math.Min(header->SlowTimeCount, (uint)WireLayout.MaxSlowTime);
        snapshot.SlowTimeDropped = header->SlowTimeDropped;
        new ReadOnlySpan<WireSlowTime>(slot + WireLayout.SlowTimeOffset, slow).CopyTo(snapshot.SlowTime);
        snapshot.ClientView = *(WireClientView*)(slot + WireLayout.ClientViewOffset);
        snapshot.Input = *(WirePlayerInput*)(slot + WireLayout.InputOffset);
        snapshot.Stats = *(WirePlayerStats*)(slot + WireLayout.StatsOffset);
        int sweeps = (int)Math.Min(*(uint*)(slot + WireLayout.SweepsOffset), (uint)WireLayout.MaxSweeps);
        snapshot.SweepDropped = *(uint*)(slot + WireLayout.SweepsOffset + 4);
        new ReadOnlySpan<WireSweep>(slot + WireLayout.SweepsOffset + 8, sweeps).CopyTo(snapshot.Sweeps);
        snapshot.WallSeconds = header->WallSeconds;
        snapshot.Player = header->Player;

        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref header->Seq) != seq) return false;
        snapshot.Count = count;
        snapshot.DamageCount = damage;
        snapshot.DefenseCount = defense;
        snapshot.LayerCount = layers;
        snapshot.SlowTimeCount = slow;
        snapshot.SweepCount = sweeps;
        return true;
    }

    /// <summary>Class name for a class id from a snapshot of the current level.</summary>
    public string ClassName(WireSnapshot snapshot, ushort classId)
    {
        if (classId >= WireLayout.MaxClasses) return "";
        return Name(snapshot, _classes, classId, WireLayout.ClassesOffset + classId * WireLayout.ClassNameBytes, WireLayout.ClassNameBytes);
    }

    /// <summary>Model path (the asset join key) for a model index from a snapshot of the current level.</summary>
    public string ModelPath(WireSnapshot snapshot, int modelIndex)
    {
        if (modelIndex <= 0 || modelIndex >= WireLayout.MaxModels) return "";
        return Name(snapshot, _models, modelIndex, WireLayout.ModelsOffset + modelIndex * WireLayout.ModelPathBytes, WireLayout.ModelPathBytes);
    }

    string Name(WireSnapshot snapshot, Dictionary<int, string> cache, int key, int offset, int bytes)
    {
        if (snapshot.LevelSerial != _namesSerial)
        {
            _classes.Clear();
            _models.Clear();
            _namesSerial = snapshot.LevelSerial;
        }
        if (!cache.TryGetValue(key, out var name))
        {
            name = CString(_base + offset, bytes);
            if (name.Length > 0) cache[key] = name;
        }
        return name;
    }

    static string CString(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.ASCII.GetString(p, n);
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
