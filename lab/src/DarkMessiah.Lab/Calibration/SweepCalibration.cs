using System.Numerics;
using DarkMessiah.Lab.Sword;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>One recorded player sweep (Wire v17) and the lab's arc at its clip and cycle, degrees against the player's view.</summary>
internal sealed record SweepComparison(int Tick, string Recording, string Clip, float Cycle, float DonorSide, float DonorUp, float LabSide, float LabUp)
{
    internal float Off => MathF.Max(MathF.Abs(DonorSide - LabSide), MathF.Abs(DonorUp - LabUp));

    public override string ToString() => FormattableString.Invariant(
        $"{Recording} tick {Tick}: {Clip}@{Cycle:0.000} donor ({DonorSide:0.#}, {DonorUp:0.#}), lab ({LabSide:0.#}, {LabUp:0.#})");
}

/// <summary>
/// The lab's swing arcs (<c>combat.json</c> <c>swingArcs</c>) against the donor's recorded sweeps (docs/lab.md, "The blade's
/// trace"; #10179): for each tick a player's swing swept (type 0), its last ray off the view's axis, against the arc at the
/// clip the view model plays and its cycle at the frame. A tick's earlier rays sit at earlier cycles, so only the last is
/// compared.
/// </summary>
internal static class SweepCalibration
{
    internal static List<SweepComparison> Measure(IEnumerable<string> recordings, CombatTuning tuning, SwordClips clips)
    {
        var results = new List<SweepComparison>();
        var byClip = clips.Moves.ToDictionary(m => m.Clip);
        foreach (var path in recordings)
        {
            using var stream = File.OpenRead(path);
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Version < 17) break;
                var last = f.Sweeps.Where(s => s.Type == 0 && f.Classes.GetValueOrDefault(f.Entities.GetValueOrDefault(s.Attacker).ClassId) == "player")
                    .Select(s => (Sweep: s, Off: Against(s))).Where(x => MathF.Abs(x.Off.Side) > .05f).Select(x => x.Off).ToList();
                if (last.Count == 0) continue;
                // The clip still playing (a finished one can still be weighted under the next).
                var playing = DamageCalibration.MoveLayers(f).Where(m => m.Value < .999f && byClip.ContainsKey(m.Key)).ToList();
                if (playing.Count != 1) continue;
                var (clip, cycle) = (playing[0].Key, playing[0].Value);
                var move = byClip[clip];
                int w = move.WindowAt(cycle, cycle);
                if (w < 0 || tuning.ArcFor(clip) is not { } arc) continue;
                var window = move.Windows[w];
                var (side, up) = arc.At(Math.Clamp((cycle - window.Open) / (window.Close - window.Open), 0, 1));
                results.Add(new SweepComparison(f.Tick, Path.GetFileNameWithoutExtension(path), clip, cycle, last[^1].Side, last[^1].Up, side, up));
            }
        }
        return results;
    }

    /// <summary>A sweep's direction against the player's view (its +0x1F88 angles): degrees left and up.</summary>
    static (float Side, float Up) Against(WireSweep s)
    {
        float pitch = s.ViewPitch * MathF.PI / 180, yaw = s.ViewYaw * MathF.PI / 180;
        var view = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
        var level = Vector3.Normalize(view with { Z = 0 });
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        var d = new Vector3(s.DirectionX, s.DirectionY, s.DirectionZ);
        float side = MathF.Atan2(Vector3.Dot(d, left), Vector3.Dot(d, level)) * 180 / MathF.PI;
        float up = (MathF.Asin(Math.Clamp(d.Z, -1, 1)) - MathF.Asin(Math.Clamp(view.Z, -1, 1))) * 180 / MathF.PI;
        return (side, up);
    }
}
