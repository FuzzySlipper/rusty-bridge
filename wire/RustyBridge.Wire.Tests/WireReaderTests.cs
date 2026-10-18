using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using RustyBridge.Wire;
using Xunit;

namespace RustyBridge.Wire.Tests;

public sealed unsafe class WireReaderTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"wire-{Guid.NewGuid():N}.wire");

    [Fact]
    public void LayoutMatchesTheTapHeader()
    {
        // The sizes wire.h static_asserts on the 32-bit tap build.
        Assert.Equal(144, Marshal.SizeOf<WireHeader>());
        Assert.Equal(136, (int)Marshal.OffsetOf<WireHeader>(nameof(WireHeader.FramesWritten)));
        Assert.Equal(56, Marshal.SizeOf<WireSlotHeader>());
        Assert.Equal(20, Marshal.SizeOf<WireLayer>());
        Assert.Equal(48, Marshal.SizeOf<WireDefense>());
        Assert.Equal(152, Marshal.SizeOf<WireDamage>());
        Assert.Equal(56, Marshal.SizeOf<WireEntity>());
        Assert.Equal(17_374_464, WireLayout.FileBytes);
    }

    [Fact]
    public void ReadsLatestFrameWithNamesAndModels()
    {
        using var writer = new TestWriter(_path, "l01_a");
        writer.Class(0, "worldspawn");
        writer.Class(1, "player");
        writer.Model(276, "models/npc/player/player_basic.mdl");
        writer.Frame(tick: 100, (0, 0, 1, 0f), (1, 1, 276, 12.5f));
        writer.Frame(tick: 101, (0, 0, 1, 0f), (1, 1, 276, 13.5f));

        using var reader = new WireReader(_path);
        var snap = new WireSnapshot();
        Assert.True(reader.IsLive);
        Assert.Equal("l01_a", reader.Map);
        Assert.True(reader.TryReadLatest(snap));
        Assert.Equal(1ul, snap.Frame);
        Assert.Equal(101, snap.Tick);
        Assert.Equal(2, snap.Count);
        var player = snap.Span[1];
        Assert.Equal("player", reader.ClassName(snap, player.ClassId));
        Assert.Equal("models/npc/player/player_basic.mdl", reader.ModelPath(snap, player.ModelIndex));
        Assert.Equal(13.5f, player.OriginX);
    }

    [Fact]
    public void RejectsASlotBeingWritten()
    {
        using var writer = new TestWriter(_path, "l01_a");
        writer.Frame(tick: 1, (0, 0, 1, 0f));
        writer.BeginFrame(1);

        using var reader = new WireReader(_path);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadFrame(0, snap));
        Assert.False(reader.TryReadFrame(1, snap));
    }

    [Fact]
    public void RejectsAFrameTheRingHasOverwritten()
    {
        using var writer = new TestWriter(_path, "l01_a");
        for (int t = 0; t < WireLayout.SlotCount + 3; t++) writer.Frame(tick: t, (0, 0, 1, t));

        using var reader = new WireReader(_path);
        var snap = new WireSnapshot();
        Assert.False(reader.TryReadFrame(2, snap));
        Assert.True(reader.TryReadFrame(3, snap));
        Assert.Equal(3, snap.Tick);
        Assert.True(reader.TryReadLatest(snap));
        Assert.Equal(WireLayout.SlotCount + 2, snap.Tick);
    }

    [Fact]
    public void NameCacheFollowsTheLevel()
    {
        using var writer = new TestWriter(_path, "l01_a");
        writer.Class(0, "prop_dynamic");
        writer.Frame(tick: 1, (5, 0, 0, 0f));
        using var reader = new WireReader(_path);
        var snap = new WireSnapshot();
        Assert.True(reader.TryReadLatest(snap));
        Assert.Equal("prop_dynamic", reader.ClassName(snap, 0));

        writer.NewLevel("l02_a");
        writer.Class(0, "npc_goblin");
        writer.Frame(tick: 1, (5, 0, 0, 0f));
        Assert.True(reader.TryReadLatest(snap));
        Assert.Equal("npc_goblin", reader.ClassName(snap, 0));
    }

    public void Dispose() => File.Delete(_path);
}
