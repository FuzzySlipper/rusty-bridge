using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One spell projectile in a recording (#10165): the entity a cast put out (<c>spell_fire_3</c> for the fireball),
/// where it went, and what it did.
/// </summary>
/// <param name="Launched">When it appeared, game seconds.</param>
/// <param name="ManaCost">The donor's mana taken within 0.5 s before or after it appeared (drops over 0.5 frame to frame).</param>
/// <param name="Speed">Its median speed, units/s, over the samples it moved.</param>
/// <param name="Life">From its first sample to its last, s.</param>
/// <param name="End">Where it was last seen: where it went off.</param>
/// <param name="Hits">The damage records on NPCs from 0.1 s before its end to 0.5 s after: victim, damage asked, type,
/// health taken, the victim's distance from the end (units, to its origin), and to its body (a box around its origin of
/// the given half width and height, as the lab's orc is: 0 inside).</param>
/// <param name="Burn">The struck NPCs' health falls after the hit, over 7 s: time after the end and amount.</param>
internal sealed record SpellShot(double Launched, float ManaCost, float Speed, double Life, Vector3 Start, Vector3 End,
    IReadOnlyList<(string Victim, float Damage, uint Type, float Taken, float Distance, float BodyDistance)> Hits, IReadOnlyList<(double After, float Taken)> Burn)
{
    /// <summary>From its appearing at the hand to its flight's start, s.</summary>
    internal float Forming { get; init; } = float.NaN;
    /// <summary>The view model's clips from a second before it appeared to its end, with their start relative to it.</summary>
    internal IReadOnlyList<string> Clips { get; init; } = [];

    public override string ToString() =>
        FormattableString.Invariant($"{Launched:0.00} s: mana {ManaCost:0.#}, formed {Forming:0.00} s, then {Speed:0} units/s, {Life:0.00} s in all, {Vector3.Distance(Start, End):0} units to ({End.X:0}, {End.Y:0}, {End.Z:0})")
        + (Hits.Count > 0 ? "; hits " + string.Join(", ", Hits.Select(h => FormattableString.Invariant($"{h.Victim} {h.Damage:0.##} type {h.Type:x} ({h.Taken:0.##} taken) {h.Distance:0} units off, {h.BodyDistance:0} from its body"))) : "; no hit")
        + (Burn.Count > 0 ? FormattableString.Invariant($"; then {Burn.Sum(b => b.Taken):0.##} more over {Burn.Max(b => b.After):0.0} s") : "")
        + (Clips.Count > 0 ? "; arms " + string.Join(" ", Clips) : "");
}

/// <summary>Reads a spell's projectiles from a recording (#10165; docs/lab.md, "The fireball").</summary>
internal static class SpellCalibration
{
    internal static List<SpellShot> Measure(string path, string projectileClass, float bodyHalfWidth = 15.7f, float bodyHeight = 70.9f)
    {
        var mana = new List<(double Time, float Mana)>();
        var viewmodel = new List<(double Time, string Clip)>();
        var playing = new HashSet<string>();
        var tracks = new Dictionary<int, List<List<(double Time, Vector3 At)>>>();
        var npcHealth = new Dictionary<int, List<(double Time, float Health, Vector3 At)>>();
        var npcAt = new Dictionary<int, List<(double Time, Vector3 At)>>();
        var damage = new List<(double Time, WireDamage Record, string Victim)>();
        var seen = new HashSet<int>();
        double interval = .015;
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double time = f.Tick * interval;
                if (f.Stats.Valid != 0) mana.Add((time, f.Stats.Mana));
                // The arms' layers: each clip as it starts playing.
                var view = f.ClientView;
                var vm = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "viewmodel");
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.ModelIndex) is { } names)
                {
                    var layers = view.ViewmodelLayers.ToArray().Where(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length).Select(l => names[l.Sequence]).ToHashSet();
                    foreach (var clip in layers.Where(c => !playing.Contains(c))) viewmodel.Add((time, clip));
                    playing = layers;
                }
                var now = new HashSet<int>();
                foreach (var e in f.Entities.Values)
                {
                    string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    if (cls == projectileClass)
                    {
                        now.Add(e.Index);
                        if (!tracks.TryGetValue(e.Index, out var lives)) tracks[e.Index] = lives = [];
                        // A projectile's index comes back for the next one: a new life when it wasn't there a frame ago.
                        if (!seen.Contains(e.Index) || lives.Count == 0) lives.Add([]);
                        var track = lives[^1];
                        if (track.Count == 0 || track[^1].At != at) track.Add((time, at));
                    }
                    else if (cls.StartsWith("npc_", StringComparison.Ordinal) && e.Flags.HasFlag(WireEntityFlags.HasHealth))
                    {
                        if (!npcHealth.TryGetValue(e.Index, out var h)) npcHealth[e.Index] = h = [];
                        if (h.Count == 0 || h[^1].Health != e.Health) h.Add((time, e.Health, at));
                        if (!npcAt.TryGetValue(e.Index, out var where)) npcAt[e.Index] = where = [];
                        if (where.Count == 0 || where[^1].At != at) where.Add((time, at));
                    }
                }
                seen = now;
                foreach (var d in f.Damage)
                    if (f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId, "") is { } victim && victim.StartsWith("npc_", StringComparison.Ordinal))
                        damage.Add((d.Tick * interval, d, victim));
            }
        // RB_SPELLTRACE=n prints the n-th projectile's samples and the mana around it.
        if (int.TryParse(Environment.GetEnvironmentVariable("RB_SPELLTRACE"), out int traced)
            && tracks.Values.SelectMany(l => l).Where(t => t.Count >= 2).OrderBy(t => t[0].Time).ElementAtOrDefault(traced) is { } shown)
        {
            foreach (var p in shown) Console.WriteLine(FormattableString.Invariant($"ST {p.Time:0.000} ({p.At.X:0.0}, {p.At.Y:0.0}, {p.At.Z:0.0})"));
            foreach (var m in mana.Where(m => m.Time >= shown[0].Time - 1.5 && m.Time <= shown[^1].Time + .5).Where((m, i) => i % 10 == 0))
                Console.WriteLine(FormattableString.Invariant($"SM {m.Time:0.000} mana {m.Mana:0.0}"));
        }
        var shots = new List<SpellShot>();
        foreach (var whole in tracks.Values.SelectMany(l => l).OrderBy(t => t[0].Time))
        {
            // The spell's entity waits at the world's origin until it's cast; then it forms at the hand and flies.
            var track = whole.SkipWhile(p => p.At == Vector3.Zero).ToList();
            if (track.Count < 2) continue;
            double launched = track[0].Time, end = track[^1].Time;
            float cost = 0;
            for (int i = 1; i < mana.Count; i++)
                if (Math.Abs(mana[i].Time - launched) <= .5 && mana[i - 1].Mana - mana[i].Mana > .5f) cost += mana[i - 1].Mana - mana[i].Mana;
            // Its flight: from the first step faster than 300 units/s.
            var speeds = new List<float>();
            double flies = double.NaN;
            for (int i = 1; i < track.Count; i++)
            {
                float v = Vector3.Distance(track[i].At, track[i - 1].At) / (float)Math.Max(.015, track[i].Time - track[i - 1].Time);
                if (double.IsNaN(flies) && v > 300) flies = track[i - 1].Time;
                if (!double.IsNaN(flies)) speeds.Add(v);
            }
            var hits = new List<(string, float, uint, float, float, float)>();
            var struck = new HashSet<int>();
            foreach (var (time, d, victim) in damage)
                if (time >= end - .1 && time <= end + .5)
                {
                    var at = npcAt.TryGetValue(d.Victim, out var where) && where.Any(x => x.Time <= time) ? where.Last(x => x.Time <= time).At : track[^1].At;
                    float taken = d.HealthBefore - d.HealthAfter;
                    if (taken <= 0) continue;
                    var min = at - new Vector3(bodyHalfWidth, bodyHalfWidth, 0);
                    var max = at + new Vector3(bodyHalfWidth, bodyHalfWidth, bodyHeight);
                    // The projectile is sampled every few ticks: on along its last heading, up to two samples more and not
                    // through the victim's floor, does it reach the body? Then it struck (0 off); else its last sample's distance.
                    var last = track[^1].At;
                    var heading = track.Count >= 2 ? last - track[^2].At : Vector3.Zero;
                    bool reached = false;
                    for (int k = 1; k <= 20 && heading.LengthSquared() > 1e-6f; k++)
                    {
                        var p = last + heading * (k / 10f);
                        if (p.Z < at.Z) break;
                        if (Vector3.Clamp(p, min, max) == p) { reached = true; break; }
                    }
                    hits.Add((victim, d.Damage, d.DamageType, taken, Vector3.Distance(at, last), reached ? 0 : Vector3.Distance(last, Vector3.Clamp(last, min, max))));
                    struck.Add(d.Victim);
                }
            var burn = new List<(double, float)>();
            foreach (var victim in struck)
            {
                var h = npcHealth[victim];
                for (int i = 1; i < h.Count; i++)
                    if (h[i].Time > end + .5 && h[i].Time <= end + 7 && h[i].Health < h[i - 1].Health)
                        burn.Add((h[i].Time - end, h[i - 1].Health - h[i].Health));
            }
            var clips = viewmodel.Where(v => v.Time >= launched - 1 && v.Time <= end).Select(v => FormattableString.Invariant($"{v.Clip}@{v.Time - launched:+0.00;-0.00}")).ToList();
            shots.Add(new SpellShot(launched, cost, speeds.Count > 0 ? MovementMeasure.Median(speeds) : 0, end - launched, track[0].At, track[^1].At, hits, burn)
            {
                Forming = double.IsNaN(flies) ? float.NaN : (float)(flies - launched),
                Clips = clips,
            });
        }
        return shots;
    }
}
