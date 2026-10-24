using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One telekinesis grab in a recording (#10164): attack held with the spell in hand, and what it did.
/// </summary>
/// <param name="Time">When attack went down, game seconds.</param>
/// <param name="Model">What it gripped (the model path), or null when nothing rose.</param>
/// <param name="ManaBefore">The donor's mana the frame before the press.</param>
/// <param name="ManaCost">The mana taken as it gripped: the frame-to-frame drops over 0.5 within 1 s of the press.</param>
/// <param name="GripSeconds">From the press to the first frame the gripped thing had risen 2 units.</param>
/// <param name="HoldDistance">The median distance, units, from the eye to the gripped thing's origin over the 0.5 s
/// before the throw (or the release); NaN when it wasn't held that long.</param>
/// <param name="Thrown">Attack went down again within 0.6 s of letting it go: the throw (let go, it stays where it was held).</param>
/// <param name="ThrowSpeed">Its fastest speed, units/s, in the 0.15 s after the throw (or the release).</param>
/// <param name="HeldSpeed">Its fastest speed, units/s, over the 0.3 s before the throw.</param>
/// <param name="Hits">The damage records it made from the throw on: victim class, damage asked and health taken.</param>
internal sealed record TelekinesisGrab(double Time, double Held, string? Model, float ManaBefore, float ManaCost, float GripSeconds, float HoldDistance,
    bool Thrown, float ThrowSpeed, float HeldSpeed, IReadOnlyList<(string Victim, float Damage, float Taken)> Hits)
{
    /// <summary>How high it rose above where it lay, at most, before the throw (units): off the floor, or dragged along it.</summary>
    internal float Rose { get; init; } = float.NaN;

    public override string ToString() => FormattableString.Invariant(
        $"{Time:0.00} s held {Held:0.00} s: {Model ?? "nothing gripped"}, mana {ManaBefore:0.0} - {ManaCost:0.0}, risen in {GripSeconds:0.00} s ({Rose:0} units up), held {HoldDistance:0} units from the eye, {(Thrown ? "thrown" : "let go")} at {ThrowSpeed:0} units/s (held moving {HeldSpeed:0}){(Hits.Count > 0 ? "; hits " + string.Join(", ", Hits.Select(h => $"{h.Victim} {h.Damage:0.##} ({h.Taken:0.##} taken)")) : "")}");
}

/// <summary>Reads telekinesis from a recording: each grab's mana, rise, hold and throw (#10164; docs/lab.md, "Telekinesis").</summary>
internal static class TelekinesisCalibration
{
    const string Spell = "weapon_MMTelekinesis_physcannon";

    // A recording cut off mid-record (its recorder stopped) reads to its last whole frame.
    static IEnumerable<RecordedFrame> Complete(IEnumerable<RecordedFrame> frames)
    {
        using var e = frames.GetEnumerator();
        while (true)
        {
            try { if (!e.MoveNext()) yield break; }
            catch (EndOfStreamException) { yield break; }
            yield return e.Current;
        }
    }

    internal static List<TelekinesisGrab> Measure(string path)
    {
        var frames = new List<(double Time, float Mana, bool Attack, bool Attack2, Vector3 Eye, bool Spell)>();
        // Each thing's track, by entity index and model: a broken prop's index goes to its gibs.
        var tracks = new Dictionary<(int Index, int Model), List<(double Time, Vector3 At)>>();
        var names = new Dictionary<(int Index, int Model), string>();
        var damage = new List<(double Time, WireDamage Record, string Victim, int InflictorModel)>();
        double interval = .015;
        var npcHealth = new Dictionary<int, float>();
        using (var stream = File.OpenRead(path))
            foreach (var f in Complete(RecordingReader.Frames(stream)))
            {
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double time = f.Tick * interval;
                if (f.Player.Index == WireLayout.NoEntity || !f.Entities.TryGetValue(f.Player.Index, out var me)) continue;
                var eye = new Vector3(me.OriginX, me.OriginY, me.OriginZ + 61);
                if (f.ClientView.Player == f.Player.Index && f.ClientView.ViewOffsetZ > 0)
                    eye = new Vector3(f.ClientView.OriginX + f.ClientView.ViewOffsetX, f.ClientView.OriginY + f.ClientView.ViewOffsetY, f.ClientView.OriginZ + f.ClientView.ViewOffsetZ);
                bool spell = f.Stats.Valid != 0 && f.Entities.TryGetValue(f.Stats.Weapon, out var held) && f.Classes.GetValueOrDefault(held.ClassId) == Spell;
                frames.Add((time, f.Stats.Mana, f.Input.Buttons.HasFlag(InputButtons.Attack), f.Input.Buttons.HasFlag(InputButtons.Attack2), eye, spell));
                foreach (var e in f.Entities.Values)
                {
                    string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                    if (cls.StartsWith("npc_", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("RB_TKHITS") == "1"
                        && (!npcHealth.TryGetValue(e.Index, out float was) || was != e.Health))
                    {
                        Console.WriteLine(FormattableString.Invariant($"NPCHEALTH {time:0.00} s #{e.Index} {cls} {e.Health:0.#}/{e.MaxHealth:0.#} life {e.LifeState}"));
                        npcHealth[e.Index] = e.Health;
                    }
                    if (cls != "prop_physics" && cls != "func_physbox" && !cls.StartsWith("npc_", StringComparison.Ordinal)) continue;
                    var key = ((int)e.Index, e.ModelIndex);
                    if (!tracks.TryGetValue(key, out var track)) tracks[key] = track = [];
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    if (track.Count == 0 || track[^1].At != at) track.Add((time, at));
                    names[key] = f.Models.GetValueOrDefault(e.ModelIndex, cls);
                }
                foreach (var d in f.Damage)
                    damage.Add((d.Tick * interval, d, f.Entities.TryGetValue(d.Victim, out var v) ? f.Classes.GetValueOrDefault(v.ClassId, "?") : "?",
                        f.Entities.TryGetValue(d.Inflictor, out var by) ? by.ModelIndex : -1));
            }
        Vector3? At((int, int) key, double time)
        {
            if (!tracks.TryGetValue(key, out var track) || track.Count == 0 || track[0].Time > time) return null;
            return track[track.FindLastIndex(p => p.Time <= time)].At;
        }
        // RB_TKMODEL=part:from-to prints every track whose model holds part, between those times.
        if (Environment.GetEnvironmentVariable("RB_TKMODEL") is { Length: > 0 } wanted && wanted.Split(':', '-') is [var part, var from, var to])
        {
            double t0 = double.Parse(from, System.Globalization.CultureInfo.InvariantCulture), t1 = double.Parse(to, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var (key, track) in tracks.Where(t => names[t.Key].Contains(part, StringComparison.OrdinalIgnoreCase)))
                foreach (var p in track.Where(p => p.Time >= t0 && p.Time <= t1))
                    Console.WriteLine(FormattableString.Invariant($"TKM #{key.Index} {names[key]} {p.Time:0.000} at {p.At.X:0.0},{p.At.Y:0.0},{p.At.Z:0.0}"));
        }
        // RB_TKHITS=1 lists the damage records on NPCs.
        if (Environment.GetEnvironmentVariable("RB_TKHITS") == "1")
            foreach (var (time, d, victim, model) in damage.Where(x => x.Victim.StartsWith("npc_", StringComparison.Ordinal)))
                Console.WriteLine(FormattableString.Invariant($"HIT {time:0.00} s {victim}: damage {d.Damage:0.##} type {d.DamageType:x} health {d.HealthBefore:0.#} -> {d.HealthAfter:0.#} attacker #{d.Attacker} inflictor #{d.Inflictor} {names.FirstOrDefault(n => n.Key.Index == d.Inflictor && n.Key.Model == model).Value} force {new Vector3(d.ForceX, d.ForceY, d.ForceZ).Length():0}"));
        // RB_TKNPCS=1 lists the NPCs the recording saw: first and last seen, and where.
        if (Environment.GetEnvironmentVariable("RB_TKNPCS") == "1")
            foreach (var (key, track) in tracks.Where(t => names[t.Key].Contains("npc", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine(FormattableString.Invariant($"NPC #{key.Index} {names[key]} {track[0].Time:0.00}-{track[^1].Time:0.00} s from {track[0].At} to {track[^1].At}"));
        var grabs = new List<TelekinesisGrab>();
        for (int i = 1; i < frames.Count; i++)
        {
            if (!frames[i].Attack || frames[i - 1].Attack || !frames[i].Spell) continue;
            int release = i;
            while (release + 1 < frames.Count && frames[release + 1].Attack) release++;
            double press = frames[i].Time, let = frames[Math.Min(frames.Count - 1, release + 1)].Time;
            // The throw: attack pressed again within 0.6 s of letting it go (let go, the body stays where it was held).
            int throwAt = -1;
            for (int j = release + 1; j < frames.Count && frames[j].Time - let <= .6; j++)
                if (frames[j].Attack && !frames[j - 1].Attack) { throwAt = j; break; }
            double thrownAt = throwAt >= 0 ? frames[throwAt].Time : let;
            float cost = 0;
            for (int j = i; j < frames.Count && frames[j].Time - press <= 1; j++)
                if (frames[j - 1].Mana - frames[j].Mana > .5f) cost += frames[j - 1].Mana - frames[j].Mana;
            // What it gripped: the thing that rose most between the press and the throw.
            (int, int)? gripped = null;
            float most = 5;
            foreach (var key in tracks.Keys)
                if (At(key, press) is Vector3 a)
                {
                    float rise = tracks[key].Where(p => p.Time > press && p.Time <= thrownAt).Select(p => p.At.Z - a.Z).DefaultIfEmpty(0).Max();
                    if (rise > most) (gripped, most) = (key, rise);
                }
            float hold = float.NaN, thrown = 0, heldSpeed = 0, risen = float.NaN, rose = float.NaN;
            var hits = new List<(string, float, float)>();
            if (gripped is { } g)
            {
                var track = tracks[g];
                var start = At(g, press)!.Value;
                rose = track.Where(p => p.Time > press && p.Time <= thrownAt).Select(p => p.At.Z - start.Z).DefaultIfEmpty(0).Max();
                if (track.FirstOrDefault(p => p.Time > press && p.At.Z - start.Z > 2) is { Time: > 0 } up) risen = (float)(up.Time - press);
                var near = frames.Skip(i).TakeWhile(x => x.Time <= thrownAt).Where(x => x.Time >= thrownAt - .5)
                    .Select(x => At(g, x.Time) is Vector3 p ? Vector3.Distance(p, x.Eye) : float.NaN).Where(float.IsFinite).ToList();
                if (near.Count > 0) hold = MovementMeasure.Median(near);
                for (int k = 1; k < track.Count; k++)
                {
                    float v = Vector3.Distance(track[k].At, track[k - 1].At) / (float)Math.Max(.015, track[k].Time - track[k - 1].Time);
                    if (track[k].Time > thrownAt && track[k].Time <= thrownAt + .15) thrown = Math.Max(thrown, v);
                    if (track[k].Time > thrownAt - .3 && track[k].Time <= thrownAt) heldSpeed = Math.Max(heldSpeed, v);
                }
                // RB_TKTRACE=1 prints the gripped thing's samples around the throw, with the eye.
                if (Environment.GetEnvironmentVariable("RB_TKTRACE") == "1")
                    foreach (var p in track.Where(p => p.Time >= thrownAt - .3 && p.Time <= thrownAt + 5))
                        Console.WriteLine(FormattableString.Invariant($"TKT {names[g]} {p.Time - thrownAt:+0.000;-0.000} s at {p.At.X:0.0},{p.At.Y:0.0},{p.At.Z:0.0}"));
                foreach (var (time, d, victim, model) in damage)
                    if (time >= thrownAt - .05 && time <= thrownAt + 3 && d.Inflictor == g.Item1 && victim.StartsWith("npc_", StringComparison.Ordinal))
                        hits.Add((victim, d.Damage, d.HealthBefore - d.HealthAfter));
            }
            grabs.Add(new TelekinesisGrab(press, let - press, gripped is { } m ? names[m] : null, frames[i - 1].Mana, cost, risen, hold, throwAt >= 0, thrown, heldSpeed, hits) { Rose = rose });
            if (throwAt >= 0)
            {
                // The throw's own press, held, isn't another grab.
                release = throwAt;
                while (release + 1 < frames.Count && frames[release + 1].Attack) release++;
            }
            i = release;
        }
        return grabs;
    }
}
