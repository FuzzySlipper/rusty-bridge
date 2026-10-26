using System.Numerics;
using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;
using Rusty.Engine;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// How far the lab's orc hangs an item from where the donor's was, over a recording's ticks (#10395): the cleaver
/// (attachment 7, <c>RightHand</c>) or the shield (11, <c>LeftShield</c>). Medians and 90th percentiles.
/// </summary>
internal sealed record HeldItemError(int Attachment, int Ticks, float Metres, float Metres90, float Degrees, float Degrees90)
{
    internal string Item => Attachment == 7 ? "cleaver" : "shield";

    public override string ToString() => FormattableString.Invariant(
        $"{Item} over {Ticks} ticks: off by {Metres * 100:0.0} cm (90%: {Metres90 * 100:0.0}), turned {Degrees:0.0}° (90%: {Degrees90:0.0})");
}

/// <summary>
/// The orc's held items against a recording (#10395): each orc that holds its cleaver or shield is posed as the
/// recording streams it (<see cref="Orc.Follow"/>, every overlay at its weight), and where the lab hangs the item
/// (<see cref="Orc.Held"/>) is compared with the item's streamed origin and angles. The donor's server places a held item
/// at its holder's attachment as its client draws an NPC's (the mirror's draws match the game's screenshots); a frozen
/// NPC (<c>ai_disable</c>, as in <c>held-look</c>) doesn't update it, so only fighting orcs are fair.
/// </summary>
internal static class HeldItemCalibration
{
    internal static List<HeldItemError> Measure(Orc orc, string path, int every = 4)
    {
        var errors = new Dictionary<int, List<(float Metres, float Degrees)>> { [7] = [], [11] = [] };
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Tick % every != 0 || f.Version < 10) continue;
                foreach (var (index, e) in f.Entities)
                {
                    if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass || !e.Flags.HasFlag(WireEntityFlags.HasAnimation) || e.LifeState != 0) continue;
                    var items = f.Entities.Values.Where(c => c.Parent == index && c.ParentSerial == e.Serial && c.ParentAttachment is 7 or 11).ToList();
                    if (items.Count == 0) continue;
                    var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                    string Name(int s) => s >= 0 && s < names.Length ? names[s] : "";
                    float yaw = e.Yaw * MathF.PI / 180;
                    var layers = f.Layers.Where(l => l.Index == index && l.Weight > 0 && Name(l.Sequence).Length > 0).OrderBy(l => l.Order)
                        .Select(l => (Name(l.Sequence), l.Cycle, l.Weight)).ToList();
                    orc.Follow(new OrcPose($"#{index}.{e.Serial:x}", SourceSpace.Position(new Vector3(e.OriginX, e.OriginY, e.OriginZ)),
                        SourceSpace.Direction(new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0)), e.Health, Name(e.Sequence), e.Cycle, e.PlaybackRate, null, 0, true,
                        items.Select(c => f.Models.GetValueOrDefault(c.ModelIndex, "")).ToList(), f.Models.GetValueOrDefault(e.ModelIndex), layers), 0);
                    // Drawn once, so its pose is sampled with its overlays.
                    _ = orc.Facts().ToList();
                    var (cleaver, shield) = orc.Held();
                    foreach (var c in items)
                    {
                        if ((c.ParentAttachment == 7 ? cleaver : shield) is not Transform lab) continue;
                        var turn = SourceSpace.Rotation(SourceSpace.FromAngles(c.Pitch, c.Yaw, c.Roll));
                        var off = Quaternion.Normalize(Quaternion.Inverse(turn) * lab.Rotation);
                        errors[c.ParentAttachment].Add((Vector3.Distance(SourceSpace.Position(new Vector3(c.OriginX, c.OriginY, c.OriginZ)), lab.Translation),
                            2 * MathF.Acos(Math.Clamp(MathF.Abs(off.W), 0, 1)) * 180 / MathF.PI));
                    }
                }
            }
        orc.StopFollowing();
        orc.Spawn();
        return [.. errors.Where(e => e.Value.Count > 0).Select(e =>
        {
            var m = e.Value.Select(x => x.Metres).Order().ToList();
            var d = e.Value.Select(x => x.Degrees).Order().ToList();
            return new HeldItemError(e.Key, e.Value.Count, m[m.Count / 2], m[m.Count * 9 / 10], d[d.Count / 2], d[d.Count * 9 / 10]);
        })];
    }
}
