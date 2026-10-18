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
        s->DamageCount = 0;
        s->DefenseCount = 0;
        Volatile.Write(ref s->Seq, 2 * f + 2);
        Volatile.Write(ref H->FramesWritten, f + 1);
    }

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
