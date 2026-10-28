using System.IO.MemoryMappedFiles;
using System.Text;
using RustyBridge.Wire;

namespace RustyBridge.Wire.Tests;

/// <summary>Writes the file the way the tap does, for tests on any OS.</summary>
sealed unsafe class TestWriter : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;

    public TestWriter(string path, string map)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.CreateNew, null, WireLayout.FileBytes);
        _view = _file.CreateViewAccessor();
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        var h = (WireHeader*)_base;
        h->Version = WireLayout.Version;
        h->HeaderBytes = (uint)sizeof(WireHeader);
        h->SlotCount = WireLayout.SlotCount;
        h->SlotBytes = (uint)WireLayout.SlotBytes;
        h->TickInterval = 0.015f;
        h->Magic = WireLayout.Magic;
        NewLevel(map);
    }

    WireHeader* H => (WireHeader*)_base;

    public void NewLevel(string map)
    {
        new Span<byte>(_base + WireLayout.ClassesOffset, WireLayout.SlotsOffset - WireLayout.ClassesOffset).Clear();
        var bytes = new Span<byte>(H->Map, 64);
        bytes.Clear();
        Encoding.ASCII.GetBytes(map).CopyTo(bytes);
        H->LevelSerial++;
    }

    public void Class(int id, string name) => Put(WireLayout.ClassesOffset + id * WireLayout.ClassNameBytes, name);
    public void Model(int index, string path) => Put(WireLayout.ModelsOffset + index * WireLayout.ModelPathBytes, path);

    void Put(int offset, string s) => Encoding.ASCII.GetBytes(s).CopyTo(new Span<byte>(_base + offset, s.Length));

    WireSlotHeader* Slot(ulong f) => (WireSlotHeader*)(_base + WireLayout.SlotsOffset + (long)(f % WireLayout.SlotCount) * WireLayout.SlotBytes);

    public void BeginFrame(ulong f) => Volatile.Write(ref Slot(f)->Seq, 2 * f + 1);

    WireDefense[] _defense = Array.Empty<WireDefense>();

    WireLayer[] _layers = Array.Empty<WireLayer>();

    /// <summary>Overlay layers for the next frame written by <see cref="FrameEntities"/>.</summary>
    public void Layer(params WireLayer[] layers) => _layers = layers;

    /// <summary>Writes a frame from whole entity records.</summary>
    public void FrameEntities(int tick, params WireEntity[] entities)
    {
        ulong f = H->FramesWritten;
        var s = Slot(f);
        BeginFrame(f);
        entities.CopyTo(new Span<WireEntity>((byte*)s + sizeof(WireSlotHeader), entities.Length));
        s->Frame = f;
        s->Tick = tick;
        s->LevelSerial = H->LevelSerial;
        s->EntityCount = (uint)entities.Length;
        _damage.CopyTo(new Span<WireDamage>((byte*)s + WireLayout.DamageOffset, _damage.Length));
        s->DamageCount = (uint)_damage.Length;
        _damage = Array.Empty<WireDamage>();
        s->DefenseCount = 0;
        s->Player = _player;
        s->WallSeconds = _wall;
        _slowTime.CopyTo(new Span<WireSlowTime>((byte*)s + WireLayout.SlowTimeOffset, _slowTime.Length));
        s->SlowTimeCount = (uint)_slowTime.Length;
        _slowTime = Array.Empty<WireSlowTime>();
        *(WireClientView*)((byte*)s + WireLayout.ClientViewOffset) = _client;
        *(WirePlayerInput*)((byte*)s + WireLayout.InputOffset) = _input;
        *(WirePlayerStats*)((byte*)s + WireLayout.StatsOffset) = _stats;
        *(uint*)((byte*)s + WireLayout.SweepsOffset) = (uint)_sweeps.Length;
        *(uint*)((byte*)s + WireLayout.SweepsOffset + 4) = 0;
        _sweeps.CopyTo(new Span<WireSweep>((byte*)s + WireLayout.SweepsOffset + 8, _sweeps.Length));
        _sweeps = Array.Empty<WireSweep>();
        *(WireWizardEye*)((byte*)s + WireLayout.WizardEyeOffset) = _wizardEye;
        *(WirePlayerPose*)((byte*)s + WireLayout.PoseOffset) = _pose;
        _layers.CopyTo(new Span<WireLayer>((byte*)s + WireLayout.LayersOffset, _layers.Length));
        s->LayerCount = (uint)_layers.Length;
        _layers = Array.Empty<WireLayer>();
        Volatile.Write(ref s->Seq, 2 * f + 2);
        Volatile.Write(ref H->FramesWritten, f + 1);
    }

    /// <summary>Player state and wall clock for the next frame written by <see cref="FrameEntities"/>.</summary>
    public void Player(WirePlayerState player, double wallSeconds) { _player = player; _wall = wallSeconds; }

    /// <summary>Bullet-time calls for the next frame written by <see cref="FrameEntities"/>.</summary>
    public void SlowTime(params WireSlowTime[] calls) => _slowTime = calls;
    public void Client(WireClientView view) => _client = view;
    public void Input(WirePlayerInput input) => _input = input;
    public void Stats(WirePlayerStats stats) => _stats = stats;
    /// <summary>Melee sweeps for the next frame written by <see cref="FrameEntities"/>.</summary>
    public void Sweeps(params WireSweep[] sweeps) => _sweeps = sweeps;
    public void WizardEye(WireWizardEye eye) => _wizardEye = eye;
    WireWizardEye _wizardEye = WireWizardEye.None;
    public void Pose(WirePlayerPose pose) => _pose = pose;
    WirePlayerPose _pose = WirePlayerPose.None;
    WireSweep[] _sweeps = Array.Empty<WireSweep>();
    WirePlayerStats _stats = WirePlayerStats.None;
    WirePlayerInput _input = WirePlayerInput.None;
    WireClientView _client = WireClientView.None;

    WirePlayerState _player = new() { Index = WireLayout.NoEntity };
    double _wall;
    WireSlowTime[] _slowTime = Array.Empty<WireSlowTime>();

    /// <summary>Damage records for the next frame written by <see cref="FrameEntities"/>.</summary>
    public void Damage(params WireDamage[] damage) => _damage = damage;

    WireDamage[] _damage = Array.Empty<WireDamage>();

    /// <summary>Defense records for the next frame written.</summary>
    public void Defend(params WireDefense[] defense) => _defense = defense;

    public void Frame(int tick, params (ushort index, ushort classId, int model, float x)[] entities) => Frame(tick, entities, Array.Empty<WireDamage>());

    public void Frame(int tick, (int index, int classId, int model, float x)[] entities, params WireDamage[] damage) =>
        Frame(tick, entities.Select(e => ((ushort)e.index, (ushort)e.classId, e.model, e.x)).ToArray(), damage);

    public void Frame(int tick, (ushort index, ushort classId, int model, float x)[] entities, WireDamage[] damage)
    {
        ulong f = H->FramesWritten;
        var s = Slot(f);
        BeginFrame(f);
        var out_ = new Span<WireEntity>((byte*)s + sizeof(WireSlotHeader), entities.Length);
        for (int i = 0; i < entities.Length; i++)
            out_[i] = new WireEntity { Index = entities[i].index, ClassId = entities[i].classId, ModelIndex = entities[i].model, OriginX = entities[i].x };
        s->Frame = f;
        s->Tick = tick;
        s->LevelSerial = H->LevelSerial;
        s->EntityCount = (uint)entities.Length;
        damage.CopyTo(new Span<WireDamage>((byte*)s + WireLayout.DamageOffset, damage.Length));
        s->DamageCount = (uint)damage.Length;
        _defense.CopyTo(new Span<WireDefense>((byte*)s + WireLayout.DefenseOffset, _defense.Length));
        s->DefenseCount = (uint)_defense.Length;
        _defense = Array.Empty<WireDefense>();
        Volatile.Write(ref s->Seq, 2 * f + 2);
        Volatile.Write(ref H->FramesWritten, f + 1);
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
