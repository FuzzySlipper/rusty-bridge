using System.Numerics;
using DarkMessiah.Lab.Player;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>One of the donor's arrows (#10168): when it left the hand, how it flew, and what it struck.</summary>
/// <param name="Left">When it left the hand (its first step faster than 500 units/s), game seconds.</param>
/// <param name="ViewPitch">The view's pitch as it left, degrees (Source's: down positive).</param>
/// <param name="Pitch">Its launch pitch, degrees up, and <paramref name="Speed"/> (units/s, along its flight) and
/// <paramref name="Gravity"/> (units/s²): a parabola fitted to its free flight (until a step's speed was a tenth off the
/// first's), NaN for one of under five samples.</param>
/// <param name="Flight">Its samples from the hand on: seconds after it left, and where (Source units).</param>
/// <param name="Bow">The bow in hand (its model path), and the build's <c>extra_damage</c> level.</param>
/// <param name="Hit">Its damage record: the victim's class, the damage asked, the hit group (null untraced), and the
/// victim's alert level and NPC state (null unread); null for an arrow that struck nothing that took damage.</param>
internal sealed record ArrowShot(double Left, float ViewPitch, float Pitch, float Speed, float Gravity, IReadOnlyList<(double After, Vector3 At)> Flight,
    string Bow, int ExtraDamage, (string Victim, float Asked, int? Group, int? Alert, int NpcState)? Hit)
{
    /// <summary>How many of <see cref="Flight"/>'s samples are its free flight, the hand's first among them.</summary>
    internal int Free { get; init; }
    /// <summary>
    /// Its speed after its free flight ended against the world, over its speed before (NaN if it stopped there or struck
    /// an NPC), and how many samples it flew on before it stopped (R10168-1).
    /// </summary>
    internal (float Ratio, int Samples) Bounce { get; init; } = (float.NaN, 0);
    /// <summary>When its damage record came (game seconds), and the struck NPC's origin from a second before it left
    /// to then (R10168-3: an orc running in); NaN and empty for none.</summary>
    internal double HitAt { get; init; } = double.NaN;
    internal IReadOnlyList<(double Time, Vector3 At)> Target { get; init; } = [];
    public override string ToString() =>
        FormattableString.Invariant($"{Left:0.00} s: {Path.GetFileNameWithoutExtension(Bow)}, view {ViewPitch:0.0}° down, left {Pitch:0.00}° up at {Speed:0} units/s, falling {Gravity:0}/s²")
        + (Hit is { } h ? FormattableString.Invariant($"; {h.Victim} asked {h.Asked:0.##} (hit group {(h.Group is int g ? g : "-")}, alert {(h.Alert is int a ? a : "-")}, state {h.NpcState})") : "; no damage")
        + (float.IsNaN(Bounce.Ratio) ? "" : FormattableString.Invariant($"; then {Bounce.Ratio:0.00} of its speed, {Bounce.Samples} samples"));
}

/// <summary>The donor's bow in a recording (#10168; docs/lab.md, "The bow").</summary>
/// <param name="Input">Each tick's attack and parry held, from the recording's input (Wire v12), in order.</param>
/// <param name="Clips">The arms' bow clips as each started playing, the jumps' with the bow among them: game seconds and the clip.</param>
/// <param name="Fov">The camera's horizontal field of view where it changed: game seconds and degrees.</param>
/// <param name="Camera">The camera's pitch off the view while attack is held (Source degrees, negative up): game seconds and the offset.</param>
/// <param name="Modules">The build's skill modules when the bow first came to hand.</param>
/// <param name="Blasts">Each explosive arrow's blast (R10168-1): when and where its caster (<c>weapon_MMDummyCaster</c>)
/// appeared, the nearest live orc's origin's distance from it (units), and the damage its missile took from orcs within
/// 0.2 s.</param>
/// <param name="Ropes">The ropes made (R10168-2): when, and where their keyframes (<c>keyframe_ropeserver</c>) stood.</param>
internal sealed record BowRecording(double TickSeconds, IReadOnlyList<(double Time, bool Attack, bool Parry)> Input, IReadOnlyList<(double Time, string Clip)> Clips,
    IReadOnlyList<ArrowShot> Shots, IReadOnlyList<(double Time, float Degrees)> Fov, IReadOnlyList<(double Time, float Offset)> Camera, IReadOnlySet<string> Modules)
{
    internal IReadOnlyList<(double Time, Vector3 At, float Nearest, float Taken)> Blasts { get; init; } = [];
    internal IReadOnlyList<(double Time, IReadOnlyList<Vector3> Keyframes)> Ropes { get; init; } = [];
}

internal static class BowCalibration
{
    internal static BowRecording Measure(string path)
    {
        var arrows = new Arrows();
        var eventNames = new EventNames();
        var input = new List<(double, bool, bool)>();
        var clips = new List<(double, string)>();
        var fov = new List<(double, float)>();
        var camera = new List<(double, float)>();
        var playing = new HashSet<string>();
        var tracks = new Dictionary<(ushort, uint), List<(double Time, Vector3 At, float ViewPitch)>>();
        var hits = new Dictionary<(ushort, uint), (string, float, int?, int?, int)>();
        var hitAt = new Dictionary<(ushort, uint), (double Time, ushort Victim, uint Serial)>();
        var npcTracks = new Dictionary<(ushort, uint), List<(double, Vector3)>>();
        var bows = new Dictionary<(ushort, uint), (string Bow, int ExtraDamage)>();
        IReadOnlySet<string> modules = new HashSet<string>();
        var casters = new List<(double Time, Vector3 At, float Nearest)>();
        var blastDamage = new List<(double Time, float Taken)>();
        var ropes = new List<(double Time, List<Vector3> Keyframes)>();
        var seenCasters = new HashSet<(ushort, uint)>();
        var seenRopes = new HashSet<(ushort, uint)>();
        double interval = .015;
        int lastTick = int.MinValue;
        using (var stream = File.OpenRead(path))
            foreach (var f in arrows.Frames(stream))
            {
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double time = f.Tick * interval;
                eventNames.Observe(f);
                var view = f.ClientView;
                if (f.Input.Valid != 0 && f.Tick != lastTick)
                {
                    bool attack = f.Input.Buttons.HasFlag(InputButtons.Attack);
                    input.Add((time, attack, f.Input.Buttons.HasFlag(InputButtons.Attack2)));
                    if (attack && view.Flags != 0) camera.Add((time, view.CameraPitch - view.ViewPitch));
                    lastTick = f.Tick;
                }
                if (fov.Count == 0 || MathF.Abs(fov[^1].Item2 - view.Fov) > .05f) fov.Add((time, view.Fov));
                var vm = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "viewmodel");
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.ModelIndex) is { } names)
                {
                    var layers = view.ViewmodelLayers.ToArray().Where(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length).Select(l => names[l.Sequence])
                        .Where(n => n.StartsWith("layer_bow", StringComparison.Ordinal) || n.StartsWith("jump_", StringComparison.Ordinal) && n.EndsWith("_bow", StringComparison.Ordinal)).ToHashSet();
                    foreach (var clip in layers.Where(c => !playing.Contains(c))) clips.Add((time, clip));
                    playing = layers;
                }
                string? weapon = f.Stats.Valid != 0 && f.Stats.Weapon != WireLayout.NoEntity && f.Entities.TryGetValue(f.Stats.Weapon, out var held)
                    ? f.Models.GetValueOrDefault(held.ModelIndex) : null;
                int extraDamage = f.Version >= 16 && f.Stats.Valid != 0 ? f.Stats.SkillLevel("extra_damage") : 0;
                if (weapon is not null && Ranged.BowRules.IsBow(weapon) && modules.Count == 0)
                    modules = CharacterBuild.FromRecording(f.Stats, f.Version, weapon, null, Path.GetFileName(path)).Modules;
                foreach (var (index, e) in f.Entities)
                {
                    if (f.Classes.GetValueOrDefault(e.ClassId) != Arrows.Class) continue;
                    var key = (index, e.Serial);
                    if (!tracks.TryGetValue(key, out var track)) tracks[key] = track = [];
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    if (track.Count == 0 || track[^1].At != at) track.Add((time, at, view.ViewPitch));
                    if (weapon is not null && Ranged.BowRules.IsBow(weapon)) bows.TryAdd(key, (VpkIndex.Normalize(weapon), extraDamage));
                }
                foreach (var (index, e) in f.Entities)
                {
                    string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    if (cls == Replay.InputTimeline.OrcClass)
                    {
                        if (!npcTracks.TryGetValue((index, e.Serial), out var npcTrack)) npcTracks[(index, e.Serial)] = npcTrack = [];
                        npcTrack.Add((time, at));
                    }
                    if (cls == "weapon_MMDummyCaster" && seenCasters.Add((index, e.Serial)))
                    {
                        float nearest = f.Entities.Values.Where(o => f.Classes.GetValueOrDefault(o.ClassId) == Replay.InputTimeline.OrcClass && o.Health > 0)
                            .Select(o => Vector3.Distance(at, new Vector3(o.OriginX, o.OriginY, o.OriginZ))).DefaultIfEmpty(float.PositiveInfinity).Min();
                        casters.Add((time, at, nearest));
                    }
                    if (cls == "keyframe_ropeserver" && seenRopes.Add((index, e.Serial)))
                    {
                        if (ropes.Count == 0 || time - ropes[^1].Time > .1) ropes.Add((time, []));
                        ropes[^1].Keyframes.Add(at);
                    }
                }
                foreach (var d in f.Damage)
                    if (eventNames.Name(f, d.Inflictor, d.InflictorSerial) == "arxspellmissileshot" && d.HealthBefore - d.HealthAfter > 0
                        && f.Entities.TryGetValue(d.Victim, out var hurt) && f.Classes.GetValueOrDefault(hurt.ClassId) == Replay.InputTimeline.OrcClass)
                        blastDamage.Add((d.Tick * interval, d.HealthBefore - d.HealthAfter));
                foreach (var d in f.Damage)
                    if (arrows.Is(f, d) && d.Damage > 0)
                    {
                        string victim = f.Entities.TryGetValue(d.Victim, out var v) ? f.Classes.GetValueOrDefault(v.ClassId, "?") : "?";
                        hits.TryAdd((d.Inflictor, d.InflictorSerial), (victim, d.Damage, d.TraceValid != 0 && victim.StartsWith("npc_", StringComparison.Ordinal) ? d.HitGroup : null,
                            d.VictimAlertValid != 0 ? d.VictimAlert : null, d.VictimNpcState));
                        hitAt.TryAdd((d.Inflictor, d.InflictorSerial), (d.Tick * interval, d.Victim, d.VictimSerial));
                    }
            }
        var shots = new List<ArrowShot>();
        foreach (var (key, track) in tracks)
        {
            // The arrow rides in the hand through the draw, then flies: its first step at 1000 to 3500 units/s (arrow_speed
            // 2100; a setpos jumps the nocked arrow with the player faster).
            int leave = -1;
            double StepSpeed(int i) => Vector3.Distance(track[i].At, track[i - 1].At) / Math.Max(interval, track[i].Time - track[i - 1].Time);
            for (int i = 1; i < track.Count && leave < 0; i++)
                if (StepSpeed(i) is > 1000 and < 3500) leave = i - 1;
            // A real release has its shoot clip (a short setpos can jump the nocked arrow at an arrow's speed).
            if (leave >= 0 && !clips.Any(c => c.Item2 == Ranged.BowRules.Shoot && Math.Abs(c.Item1 - track[leave].Time) <= .1)) leave = -1;
            if (leave < 0) continue;
            var flight = track.Skip(leave).Select(p => (After: p.Time - track[leave].Time, p.At)).ToList();
            // Its free flight: until a step's speed is a tenth off the first's (it struck something, or bounced).
            var arc = new List<(double T, Vector3 At)> { (flight[0].After, flight[0].At) };
            float first = flight.Count > 1 ? Vector3.Distance(flight[1].At, flight[0].At) / (float)Math.Max(interval, flight[1].After - flight[0].After) : 0;
            for (int i = 1; i < flight.Count; i++)
            {
                float step = Vector3.Distance(flight[i].At, flight[i - 1].At) / (float)Math.Max(interval, flight[i].After - flight[i - 1].After);
                if (MathF.Abs(step - first) > .1f * first) break;
                arc.Add((flight[i].After, flight[i].At));
            }
            float pitch = float.NaN, speed = float.NaN, gravity = float.NaN;
            if (arc.Count >= 5)
            {
                // z = z0 + vz t - g t²/2, and the ground distance linear in t: least squares.
                var (z0, vz, a) = Quadratic(arc.Select(p => (p.T, (double)p.At.Z)).ToList());
                var ground = arc.Select(p => (p.T, (double)new Vector2(p.At.X - arc[0].At.X, p.At.Y - arc[0].At.Y).Length())).ToList();
                double vg = Slope(ground);
                (pitch, speed, gravity) = ((float)(Math.Atan2(vz, vg) * 180 / Math.PI), (float)Math.Sqrt(vg * vg + vz * vz), (float)(-2 * a));
            }
            var (bow, extra) = bows.GetValueOrDefault(key, ("", 0));
            // After its free flight: the step after the one it met the world in, against its first step (a bounce flies on).
            (float, int) bounce = (float.NaN, 0);
            if (!(hits.TryGetValue(key, out var struck) && struck.Item1.StartsWith("npc_", StringComparison.Ordinal)) && arc.Count + 1 < flight.Count && first > 0)
            {
                int k = arc.Count;
                float after = Vector3.Distance(flight[k + 1].At, flight[k].At) / (float)Math.Max(interval, flight[k + 1].After - flight[k].After);
                int on = 0;
                for (int i = k + 1; i < flight.Count && Vector3.Distance(flight[i].At, flight[i - 1].At) / (float)Math.Max(interval, flight[i].After - flight[i - 1].After) > .3f * after; i++) on++;
                if (after > .2f * first) bounce = (after / first, on);
            }
            shots.Add(new ArrowShot(track[leave].Time, track[leave].ViewPitch, pitch, speed, gravity, flight, bow, extra,
                hits.TryGetValue(key, out var hit) ? hit : null)
            {
                Free = arc.Count,
                Bounce = bounce,
                HitAt = hitAt.TryGetValue(key, out var when) ? when.Time : double.NaN,
                Target = hitAt.TryGetValue(key, out var who) && npcTracks.TryGetValue((who.Victim, who.Serial), out var t)
                    ? t.Where(p => p.Item1 >= track[leave].Time - 1 && p.Item1 <= who.Time).ToList() : [],
            });
        }
        return new BowRecording(interval, input, clips, shots.OrderBy(s => s.Left).ToList(), fov, camera, modules)
        {
            Blasts = casters.Select(c => (c.Time, c.At, c.Nearest, blastDamage.Where(d => d.Time >= c.Time - .05 && d.Time <= c.Time + .2).Sum(d => d.Taken))).ToList(),
            Ropes = ropes.Select(r => (r.Time, (IReadOnlyList<Vector3>)r.Keyframes)).ToList(),
        };
    }

    static (double C, double B, double A) Quadratic(List<(double X, double Y)> points)
    {
        // Normal equations for y = c + b x + a x².
        double s0 = points.Count, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var (x, y) in points) { s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x; t0 += y; t1 += x * y; t2 += x * x * y; }
        var m = new[,] { { s0, s1, s2 }, { s1, s2, s3 }, { s2, s3, s4 } };
        var r = new[] { t0, t1, t2 };
        for (int i = 0; i < 3; i++)
            for (int k = i + 1; k < 3; k++)
            {
                double factor = m[k, i] / m[i, i];
                for (int j = i; j < 3; j++) m[k, j] -= factor * m[i, j];
                r[k] -= factor * r[i];
            }
        double a = r[2] / m[2, 2], b = (r[1] - m[1, 2] * a) / m[1, 1], c = (r[0] - m[0, 1] * b - m[0, 2] * a) / m[0, 0];
        return (c, b, a);
    }

    static double Slope(List<(double X, double Y)> points)
    {
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        return points.Sum(p => (p.X - mx) * (p.Y - my)) / points.Sum(p => (p.X - mx) * (p.X - mx));
    }
}
