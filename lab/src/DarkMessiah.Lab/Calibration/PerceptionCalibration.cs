using System.Numerics;
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
/// <param name="InSight">The player came into its sight by the lab's rule (R10154-2) before the donor's orc noticed.</param>
/// <param name="Hidden">The scenario hid the player (<c>notarget</c>) until after placing it, which the Wire doesn't carry: when
/// the donor's orc could first see it isn't known, only whether it did.</param>
/// <param name="Rise">The player's distance (Source units) and angle off the orc's facing (degrees) as the donor's noticed.</param>
/// <param name="Unseen">The player's nearest distance within <paramref name="UnseenAngle"/> degrees of the orc's facing while it
/// stayed unaware, for 1.5 s or more and still: how near sight didn't reach.</param>
internal sealed record ApproachCase(string Recording, string Orc, string Gaits, float Closest, double? Donor, double? Lab, bool InSight = false,
    bool Hidden = false, (float Distance, float Angle)? Rise = null, float Unseen = float.NaN, float UnseenAngle = float.NaN);

/// <summary>
/// One orc becoming aware with another unaware within reach (R10154-1): whether and when the other followed.
/// </summary>
/// <param name="Apart">The two orcs' distance as the first became aware, Source units.</param>
/// <param name="CallerStruck">The first became aware by the player's blow (else by sensing the player).</param>
/// <param name="Delay">Seconds from the first's rise to the other's; null if it never rose while the first lived.</param>
/// <param name="OwnSense">Seconds from the first's rise to when the other would have noticed the player itself by the
/// lab's rule; null if it wouldn't have.</param>
internal sealed record CallCase(string Recording, string Caller, string Mate, float Apart, bool CallerStruck, double? Delay, double? OwnSense);

/// <summary>
/// The donor's orcs noticing the player against the lab's rule (#10154): every <c>npc_orc_sword</c> in a Wire v18
/// recording that began unaware and wasn't struck, nor a spell out (its fearfactor alerts NPCs), nor found a body, before
/// its alert level rose.
/// </summary>
internal static class PerceptionCalibration
{
    /// <summary>A gait is the player's speed over this many seconds.</summary>
    const float SpeedWindow = .2f;

    /// <summary>An orc's facing on the Wire (its yaw, Source degrees) in Source XY, as the lab's sight takes it (x, 0, y).</summary>
    static Vector3 Facing(float yaw) => new(MathF.Cos(yaw * MathF.PI / 180), 0, MathF.Sin(yaw * MathF.PI / 180));

    internal static List<ApproachCase> Measure(IEnumerable<string> recordings, OrcPerceptionData data) => Read(recordings, data).Approaches;

    /// <summary>Every orc that became aware with another unaware within 1100 units (R10154-1).</summary>
    internal static List<CallCase> Calls(IEnumerable<string> recordings, OrcPerceptionData data) => Read(recordings, data).Calls;

    /// <summary>A second orc this far from one becoming aware or nearer is a call case, Source units.</summary>
    const float CallReach = 1100;

    static (List<ApproachCase> Approaches, List<CallCase> Calls) Read(IEnumerable<string> recordings, OrcPerceptionData data)
    {
        var cases = new List<ApproachCase>();
        var calls = new List<CallCase>();
        foreach (var path in recordings)
        {
            var orcs = new Dictionary<(ushort, uint), (double Start, double? Rose, bool Struck, OrcPerception Lab, double? LabRose, HashSet<PlayerGait> Gaits, float Closest, bool InSight,
                (float, float)? Rise, float Unseen, float UnseenAngle, double StillSince, float StillX, float StillY)>();
            bool hidden = File.Exists(Path.ChangeExtension(path, ".log")) && File.ReadAllText(Path.ChangeExtension(path, ".log")).Contains("exec notarget", StringComparison.Ordinal);
            // Where each orc stood and when it was last seen on the Wire; the pairs open at a first orc's rise.
            var at = new Dictionary<(ushort, uint), (float X, float Y, double Time)>();
            var open = new List<(string Caller, (ushort, uint) Mate, double Rose, float Apart, bool Struck)>();
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
                    // A spell out, in hand or cast (its entity, which waits at the world's origin in hand): its fearfactor alerts
                    // NPCs (the fireball's 50, the lightning bolt's 300, which struck from the origin: #10166), which the lab's
                    // perception doesn't model yet (#10165): left out like a blow.
                    bool spellOut = f.Entities.Values.Any(x => f.Classes.GetValueOrDefault(x.ClassId, "").StartsWith("spell_", StringComparison.Ordinal));
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
                            o = (time, null, false, lab, null, [], float.MaxValue, false, null, float.NaN, float.NaN, time, player.OriginX, player.OriginY);
                        }
                        float distance = MathF.Sqrt((player.OriginX - e.OriginX) * (player.OriginX - e.OriginX) + (player.OriginY - e.OriginY) * (player.OriginY - e.OriginY));
                        var toPlayer = new Vector3(player.OriginX - e.OriginX, 0, player.OriginY - e.OriginY);
                        bool inSight = data.Sees(f.Map) && data.InSight(Facing(e.Yaw), toPlayer, distance);
                        float angle = toPlayer.LengthSquared() > 1e-6f ? MathF.Acos(Math.Clamp(Vector3.Dot(Facing(e.Yaw), Vector3.Normalize(toPlayer)), -1, 1)) * 180 / MathF.PI : 0;
                        // The player standing in one place (within 5 units) since when.
                        if (MathF.Abs(player.OriginX - o.StillX) > 5 || MathF.Abs(player.OriginY - o.StillY) > 5) (o.StillSince, o.StillX, o.StillY) = (time, player.OriginX, player.OriginY);
                        if (o.Rose is null)
                        {
                            // An orc's body within 500 units alerts it too (an attack-spells orc rose 0.18 s after it was spawned beside
                            // the last one's and ran to check_corpse it): left out.
                            bool corpse = f.Entities.Values.Any(x => f.Classes.GetValueOrDefault(x.ClassId) == "prop_ragdoll"
                                && f.Models.GetValueOrDefault(x.ModelIndex, "").Contains("npc_orc", StringComparison.OrdinalIgnoreCase)
                                && MathF.Sqrt((x.OriginX - e.OriginX) * (x.OriginX - e.OriginX) + (x.OriginY - e.OriginY) * (x.OriginY - e.OriginY)) <= 500);
                            if (struck.Contains(key) || spellOut || corpse) o.Struck = true;
                            if (e.Alert > 0)
                            {
                                o.Rose = time - o.Start;
                                o.Rise = (distance, angle);
                                // The others unaware near it: does the call reach them? Not while a spell is out: it alerts them all.
                                foreach (var (other, p) in at)
                                    if (!spellOut && other != key && time - p.Time < .5 && orcs.TryGetValue(other, out var m) && m.Rose is null
                                        && MathF.Sqrt((p.X - e.OriginX) * (p.X - e.OriginX) + (p.Y - e.OriginY) * (p.Y - e.OriginY)) is var apart && apart <= CallReach)
                                        open.Add(($"#{index}.{e.Serial:x}", other, time, apart, o.Struck));
                            }
                            else
                            {
                                o.Closest = MathF.Min(o.Closest, distance);
                                if (gait != PlayerGait.Still) o.Gaits.Add(gait);
                                if (inSight) o.InSight = true;
                                if (time - o.StillSince >= 1.5 && !(distance >= o.Unseen)) (o.Unseen, o.UnseenAngle) = (distance, angle);
                            }
                        }
                        if (o.LabRose is null && o.Lab.Step(f.TickInterval, distance, gait, inSight) != AwareBy.None) o.LabRose = time - o.Start;
                        at[key] = (e.OriginX, e.OriginY, time);
                        orcs[key] = o;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            foreach (var ((index, serial), o) in orcs)
                if (!o.Struck)
                    cases.Add(new ApproachCase(Path.GetFileNameWithoutExtension(path), $"#{index}.{serial:x}",
                        o.Gaits.Count == 0 ? "still" : string.Join("+", o.Gaits.Order().Select(g => g.ToString().ToLowerInvariant())), o.Closest, o.Rose, o.LabRose, o.InSight, hidden, o.Rise, o.Unseen, o.UnseenAngle));
            foreach (var (caller, mate, rose, apart, callerStruck) in open)
            {
                var m = orcs[mate];
                // The mate's own rise counts if it wasn't struck first; its own sensing by the lab's rule, from its start.
                double? delay = m.Rose is double r && !m.Struck ? m.Start + r - rose : null;
                double? own = m.LabRose is double l ? m.Start + l - rose : null;
                calls.Add(new CallCase(Path.GetFileNameWithoutExtension(path), caller, $"#{mate.Item1}.{mate.Item2:x}", apart, callerStruck, delay, own));
            }
        }
        // An orc that followed another's alarm is a call's case, not its own sighting.
        var followed = calls.Where(c => c.Delay is not null).Select(c => (c.Recording, c.Mate)).ToHashSet();
        return (cases.Where(a => !followed.Contains((a.Recording, a.Orc))).ToList(), calls);
    }
}
