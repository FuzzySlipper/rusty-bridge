using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One recorded approach on an unaware orc (#10154): the player's gaits on the way, the alert level's first rise
/// (Wire v18) and the lab's rule (<see cref="OrcPerception"/>) on the same path.
/// </summary>
/// <param name="Donor">Seconds from the orc's first tick to its alert level's first rise; null when it never rose.</param>
/// <param name="Lab">The same for the lab's rule on the recorded path; null when it never hears the player.</param>
/// <param name="Closest">The player's closest approach before the donor's orc noticed (or at all), Source units.</param>
internal sealed record ApproachCase(string Recording, string Orc, string Gaits, float Closest, double? Donor, double? Lab);

/// <summary>
/// The donor's orcs noticing the player against the lab's rule (#10154): every <c>npc_orc_sword</c> in a Wire v18
/// recording that began unaware and wasn't struck before its alert level rose.
/// </summary>
internal static class PerceptionCalibration
{
    /// <summary>A gait is the player's speed over this many seconds.</summary>
    const float SpeedWindow = .2f;

    internal static List<ApproachCase> Measure(IEnumerable<string> recordings, OrcPerceptionData data)
    {
        var cases = new List<ApproachCase>();
        foreach (var path in recordings)
        {
            var orcs = new Dictionary<(ushort, uint), (double Start, double? Rose, bool Struck, OrcPerception Lab, double? LabRose, HashSet<PlayerGait> Gaits, float Closest)>();
            var trail = new Queue<(double Time, float X, float Y)>();
            uint version = 0;
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var f in RecordingReader.Frames(stream))
                {
                    version = f.Version;
                    if (version < 18) break;
                    double time = f.Tick * (double)f.TickInterval;
                    var player = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "player");
                    if (f.Classes.GetValueOrDefault(player.ClassId) != "player") continue;
                    trail.Enqueue((time, player.OriginX, player.OriginY));
                    while (trail.Count > 1 && time - trail.Peek().Time > SpeedWindow) trail.Dequeue();
                    var (t0, x0, y0) = trail.Peek();
                    float speed = time > t0 ? MathF.Sqrt((player.OriginX - x0) * (player.OriginX - x0) + (player.OriginY - y0) * (player.OriginY - y0)) / (float)(time - t0) * SourceSpace.MetresPerUnit : 0;
                    bool crouched = f.Input.Buttons.HasFlag(InputButtons.Duck), sprinting = (f.Input.Actions & PlayerActions.Sprint) != 0;
                    // A setpos jumps the player: no steps (faster than 15 m/s is no gait).
                    var gait = speed > 15 ? PlayerGait.Still : OrcPerception.GaitOf(speed, crouched, sprinting);
                    var struck = f.Damage.Where(d => d.Damage > 0).Select(d => (d.Victim, d.VictimSerial)).ToHashSet();
                    foreach (var (index, e) in f.Entities)
                    {
                        if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass || !e.Flags.HasFlag(WireEntityFlags.HasAlert)) continue;
                        if (e.Flags.HasFlag(WireEntityFlags.HasHealth) && (e.LifeState != 0 || e.Health <= 0)) continue;
                        var key = (index, e.Serial);
                        if (!orcs.TryGetValue(key, out var o))
                        {
                            if (e.Alert != 0) continue;
                            var lab = new OrcPerception(data);
                            lab.Reset(aware: false);
                            o = (time, null, false, lab, null, [], float.MaxValue);
                        }
                        float distance = MathF.Sqrt((player.OriginX - e.OriginX) * (player.OriginX - e.OriginX) + (player.OriginY - e.OriginY) * (player.OriginY - e.OriginY));
                        if (o.Rose is null)
                        {
                            if (struck.Contains(key)) o.Struck = true;
                            if (e.Alert > 0) o.Rose = time - o.Start;
                            else { o.Closest = MathF.Min(o.Closest, distance); if (gait != PlayerGait.Still) o.Gaits.Add(gait); }
                        }
                        if (o.LabRose is null && o.Lab.Step(f.TickInterval, distance, gait)) o.LabRose = time - o.Start;
                        orcs[key] = o;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            foreach (var ((index, serial), o) in orcs)
                if (!o.Struck)
                    cases.Add(new ApproachCase(Path.GetFileNameWithoutExtension(path), $"#{index}.{serial:x}",
                        o.Gaits.Count == 0 ? "still" : string.Join("+", o.Gaits.Order().Select(g => g.ToString().ToLowerInvariant())), o.Closest, o.Rose, o.LabRose));
        }
        return cases;
    }
}
