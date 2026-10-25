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
        Assert.Equal(96, Marshal.SizeOf<WireSlotHeader>());
        Assert.Equal(56, (int)Marshal.OffsetOf<WireSlotHeader>(nameof(WireSlotHeader.WallSeconds)));
        Assert.Equal(72, (int)Marshal.OffsetOf<WireSlotHeader>(nameof(WireSlotHeader.Player)));
        Assert.Equal(24, Marshal.SizeOf<WirePlayerState>());
        Assert.Equal(32, Marshal.SizeOf<WireSlowTime>());
        Assert.Equal(20, Marshal.SizeOf<WireLayer>());
        Assert.Equal(56, Marshal.SizeOf<WireDefense>());
        Assert.Equal(48, (int)Marshal.OffsetOf<WireDefense>(nameof(WireDefense.VictimState)));
        Assert.Equal(360, Marshal.SizeOf<WireDamage>());
        Assert.Equal(152, (int)Marshal.OffsetOf<WireDamage>(nameof(WireDamage.VictimSequence)));
        Assert.Equal(88, Marshal.SizeOf<WireEntity>());
        // v19: Arkane's size and shrink after the owner (wire.h size, shrinked).
        Assert.Equal(80, (int)Marshal.OffsetOf<WireEntity>(nameof(WireEntity.Size)));
        Assert.Equal(84, (int)Marshal.OffsetOf<WireEntity>(nameof(WireEntity.Shrinked)));
        Assert.Equal(64, (int)Marshal.OffsetOf<WireEntity>(nameof(WireEntity.Parent)));
        // v18: an NPC's alert level and state in the entity's spare fields (wire.h alert, npc_state).
        Assert.Equal(58, (int)Marshal.OffsetOf<WireEntity>(nameof(WireEntity.Alert)));
        Assert.Equal(67, (int)Marshal.OffsetOf<WireEntity>(nameof(WireEntity.NpcState)));
        Assert.Equal(232, Marshal.SizeOf<WireClientView>());
        Assert.Equal(12, Marshal.SizeOf<WireClientLayer>());
        Assert.Equal(80, (int)Marshal.OffsetOf<WireClientView>(nameof(WireClientView.CameraX)));
        Assert.Equal(48, Marshal.SizeOf<WirePlayerInput>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WirePlayerInput>(nameof(WirePlayerInput.ForwardMove)));
        Assert.Equal(WireLayout.ClientViewOffset + 232, WireLayout.InputOffset);
        Assert.Equal(112, Marshal.SizeOf<WirePlayerStats>());
        Assert.Equal(56, Marshal.SizeOf<WireSweep>());
        Assert.Equal(80, (int)Marshal.OffsetOf<WirePlayerStats>(nameof(WirePlayerStats.SkillLevels)));
        Assert.Equal(28, (int)Marshal.OffsetOf<WirePlayerStats>(nameof(WirePlayerStats.SkillPoints)));
        Assert.Equal(36, (int)Marshal.OffsetOf<WirePlayerStats>(nameof(WirePlayerStats.Weapon)));
        Assert.Equal(48, (int)Marshal.OffsetOf<WirePlayerStats>(nameof(WirePlayerStats.WeaponSerial)));
        Assert.Equal(WireLayout.InputOffset + 48, WireLayout.StatsOffset);
        Assert.Equal(WireLayout.StatsOffset + 112, WireLayout.SweepsOffset);
        Assert.Equal(48, (int)Marshal.OffsetOf<WireSweep>(nameof(WireSweep.Caller)));
        // v20: the wizard eye after the sweeps (wire.h WizardEye).
        Assert.Equal(36, Marshal.SizeOf<WireWizardEye>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WireWizardEye>(nameof(WireWizardEye.LightmapAdd)));
        Assert.Equal(WireLayout.SweepsOffset + 8 + 32 * 56, WireLayout.WizardEyeOffset);
        Assert.Equal(27_855_616, WireLayout.FileBytes);
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
