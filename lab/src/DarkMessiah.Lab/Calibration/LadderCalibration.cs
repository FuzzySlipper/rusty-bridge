using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A run of the donor's player at l01_a's ladder (#10439): its phase, how many ticks, where it began and ended, and its view's yaw at each end.</summary>
internal sealed record LadderRun(string Recording, string Phase, int Ticks, Vector3 From, Vector3 To, float FromYaw, float ToYaw);

/// <summary>A tick climbing the donor's ladder (#10439): the view's pitch (down positive), the forward key's sign, and the climb's speed, units/s up.</summary>
internal sealed record LadderClimb(float Pitch, float Key, float Speed);

/// <summary>
/// What l01_a's ladder recordings say of climbing it (#10439; docs/lab.md, "Ladders"): its phases' runs (on the rail, off
/// at the top, on from the top, off at the foot), from where the player is against the ladder's rail and how it moves
/// (the Wire has no move type), and every tick on the rail's climb against the view's pitch and the forward key.
/// </summary>
internal static class LadderCalibration
{
    /// <summary>
    /// l01_a's ladder (<c>room03.ladder.02</c>): its rail (the climber's origin, <c>mm_player_ladder_offset</c> 9 units out
    /// from the <c>func_useableladder</c> from (1797, −575, 554) to (1797, −543, 822)) from (1797, −584, 554) leaning 32
    /// units in 268, climbed to 757; its top's <c>info_ladder_dismount</c> at (1797, −511, 826).
    /// </summary>
    internal const float X = 1797, FootY = -584, FootZ = 554, Lean = 32f / 268, TopZ = 757, ExitY = -511, ExitZ = 826;

    internal static float RailY(float z) => FootY + (z - FootZ) * Lean;

    static IEnumerable<RecordedFrame> Frames(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = RecordingReader.Frames(stream).GetEnumerator();
        while (true)
        {
            try { if (!frames.MoveNext()) yield break; }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException or InvalidDataException) { yield break; }
            yield return frames.Current;
        }
    }

    /// <summary>The recording's ladder runs and climbing ticks.</summary>
    internal static (List<LadderRun> Runs, List<LadderClimb> Climbs) Read(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var runs = new List<LadderRun>();
        var climbs = new List<LadderClimb>();
        string phase = "None";
        int since = 0, lastTick = 0, prevTick = -1;
        Vector3 start = default, last = default, prev = default;
        float startYaw = 0, lastYaw = 0;
        foreach (var f in Frames(path))
        {
            var v = f.ClientView;
            if (!v.Flags.HasFlag(ClientViewFlags.Player)) continue;
            var at = new Vector3(v.OriginX, v.OriginY, v.OriginZ);
            float rail = RailY(at.Z);
            bool onRail = MathF.Abs(at.X - X) < 3 && MathF.Abs(at.Y - rail) < 3 && at.Z >= FootZ - 1 && at.Z < TopZ;
            bool atTop = MathF.Abs(at.X - X) < 8 && at.Z >= TopZ - 6 && at.Y > RailY(TopZ) - 4 && at.Y < ExitY + 1 && at.Z <= ExitZ + 1;
            string now = onRail ? "On"
                : atTop ? at.Z > prev.Z + .05f || at.Y > prev.Y + .05f ? "OffAtTop" : at.Z < prev.Z - .05f || at.Y < prev.Y - .05f ? "OnFromTop" : phase is "OffAtTop" or "OnFromTop" ? phase : "Top"
                : phase is "On" or "OffAtFoot" && MathF.Abs(at.X - X) < 10 && MathF.Abs(at.Z - FootZ) < 3 && at.Y < rail - .5f && at.Y < prev.Y - .05f ? "OffAtFoot"
                : "None";
            if (now != phase)
            {
                if (phase is not ("None" or "Top")) runs.Add(new LadderRun(name, phase, lastTick - since + 1, start, last, startYaw, lastYaw));
                (phase, since, start, startYaw) = (now, f.Tick, at, v.ViewYaw);
            }
            if (onRail && prevTick >= 0 && f.Tick - prevTick <= 3 && f.Input.Valid != 0 && f.Input.ForwardMove != 0
                && at.Z > FootZ + 2 && at.Z < TopZ - 2)
                climbs.Add(new LadderClimb(v.ViewPitch, MathF.Sign(f.Input.ForwardMove), (at.Z - prev.Z) / ((f.Tick - prevTick) * f.TickInterval)));
            (last, lastYaw, lastTick, prev, prevTick) = (at, v.ViewYaw, f.Tick, at, f.Tick);
        }
        if (phase is not ("None" or "Top")) runs.Add(new LadderRun(name, phase, lastTick - since + 1, start, last, startYaw, lastYaw));
        return (runs, climbs);
    }
}
