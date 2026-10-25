using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// The blow that killed a shrunk orc (#10348): seconds after the press, what it asked, its damage type and weapon
/// flags, its health then, and how far the player stood from it (2D, units).
/// </summary>
internal readonly record struct ShrunkDeath(double After, float Asked, uint Type, int WeaponFlags, float HealthBefore, float Units)
{
    /// <summary>The player's kick: the kick's weapon flags (0x20). The body's crush carries none.</summary>
    internal bool Kick => (WeaponFlags & 0x20) != 0;
}

/// <summary>
/// A shrink in a recording (#10348): an air_4 press that set an orc's health to 1. The mana and the gauge before and
/// after; the orc's health before; its size (Wire v19's <c>m_fMMSize</c>) each time it changed until it wore its
/// <c>_shrinked.mdl</c>, and when it did; its clips and its health's rises after; its blows; the last time it was seen
/// small and alive; and the blow that killed it, if one did.
/// </summary>
internal sealed record ShrinkCast(double Pressed, float Mana, float AdrenalineBefore, float AdrenalineAfter, int Target, float HealthBefore,
    IReadOnlyList<(double After, float Size)> Sizes, double Swapped, IReadOnlyList<string> SmallClips, IReadOnlyList<(double After, float Amount)> Rises,
    int Blows, double LastSmall, ShrunkDeath? Death)
{
    public override string ToString() =>
        FormattableString.Invariant($"shrink {Pressed:0.00} s: mana {Mana:0.#}, adrenaline {AdrenalineBefore:0}→{AdrenalineAfter:0}, #{Target} {HealthBefore:0.#}→1; size from {Sizes.FirstOrDefault(s => s.Size < .99f).After:0.00} s to {Sizes.LastOrDefault().Size:0.###} at {Sizes.LastOrDefault().After:0.00}, small at {Swapped:0.00}; ")
        + FormattableString.Invariant($"{string.Join("/", SmallClips)}, {Rises.Count} rises, {Blows} blows, small to {LastSmall:0} s; ")
        + (Death is { } d ? FormattableString.Invariant($"{(d.Kick ? "kicked" : "crushed")} {d.After:0.0} s on, {d.Asked:0.#} asked at {d.HealthBefore:0.##}, {d.Units:0.#} units off") : "not killed");
}

/// <summary>Reads the shrinks from a v19 recording (#10348; docs/lab.md, "Shrink").</summary>
internal static class ShrinkCalibration
{
    internal static List<ShrinkCast> Measure(string path)
    {
        var names = new EventNames();
        double interval = .015;
        var frames = new List<(double T, float Mana, float Adrenaline, bool Attack, bool Air4)>();
        var orcs = new Dictionary<int, List<(double T, float Health, float Size, int Shrinked, bool SmallModel, string Clip, float Units)>>();
        var damage = new List<(double T, int Victim, int Attacker, float Asked, uint Type, int Flags, float Before, float Units)>();
        var last = new Dictionary<int, (float, float)>();
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                names.Observe(f);
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double t = f.Tick * interval;
                var vm = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "viewmodel");
                bool air4 = f.ClientView.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.ModelIndex) is { } arms
                    && f.ClientView.ViewmodelLayers.ToArray().Any(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < arms.Length && arms[l.Sequence].EndsWith("_air_4", StringComparison.Ordinal));
                frames.Add((t, f.Stats.Valid != 0 ? f.Stats.Mana : float.NaN, f.Player.Adrenaline, f.Input.Valid != 0 && f.Input.Buttons.HasFlag(InputButtons.Attack), air4));
                var me = f.Entities.TryGetValue(f.Player.Index, out var p) ? p : default;
                foreach (var (i, e) in f.Entities)
                {
                    if (!f.Classes.GetValueOrDefault(e.ClassId, "").StartsWith("npc_orc", StringComparison.Ordinal) || !e.Flags.HasFlag(WireEntityFlags.HasSize)) continue;
                    string clip = f.Sequences.GetValueOrDefault(e.ModelIndex) is { } seqs && e.Sequence >= 0 && e.Sequence < seqs.Length ? seqs[e.Sequence] : "";
                    if (!orcs.TryGetValue(i, out var list)) orcs[i] = list = [];
                    float off = MathF.Sqrt((me.OriginX - e.OriginX) * (me.OriginX - e.OriginX) + (me.OriginY - e.OriginY) * (me.OriginY - e.OriginY));
                    last[i] = (e.OriginX, e.OriginY);
                    list.Add((t, e.Health, e.Size, e.Shrinked, f.Models.GetValueOrDefault(e.ModelIndex, "").Contains("_shrinked", StringComparison.Ordinal), clip, off));
                }
                foreach (var d in f.Damage)
                {
                    if (!names.Name(f, d.Victim, d.VictimSerial).StartsWith("npc_orc", StringComparison.Ordinal)) continue;
                    // How far the player stood from it: the victim leaves the Wire in the tick it dies, so from where it last stood.
                    var at = f.Entities.TryGetValue(d.Victim, out var v) ? (v.OriginX, v.OriginY) : last.GetValueOrDefault(d.Victim, (float.NaN, float.NaN));
                    damage.Add((d.Tick * interval, d.Victim, d.Attacker, d.Damage, d.DamageType, d.WeaponFlags, d.HealthBefore,
                        MathF.Sqrt((me.OriginX - at.Item1) * (me.OriginX - at.Item1) + (me.OriginY - at.Item2) * (me.OriginY - at.Item2))));
                }
            }
        var casts = new List<ShrinkCast>();
        for (int k = 1; k < frames.Count; k++)
        {
            if (!frames[k].Attack || frames[k - 1].Attack || !frames[k].Air4) continue;
            double press = frames[k].T;
            // The orc whose health was set to 1 within a few ticks of the press, at its size's cast value; the cast is
            // that tick (the press's, or the next).
            var hit = orcs.Select(o => (o.Key, Samples: o.Value)).FirstOrDefault(o =>
                o.Samples.Any(s => s.T >= press - interval / 2 && s.T <= press + 4 * interval && s.Health == 1 && s.Size < 1 && s.Shrinked == 0)
                && o.Samples.Any(s => s.T < press && s.T > press - .5 && s.Health > 1));
            if (hit.Samples is null) continue;
            double pressed = hit.Samples.First(s => s.T >= press - interval / 2 && s.Health == 1).T;
            var before = hit.Samples.Last(s => s.T < pressed);
            var after = hit.Samples.Where(s => s.T >= pressed - interval).ToList();
            var small = after.FirstOrDefault(s => s.SmallModel && s.Shrinked == 1);
            double swapped = small.Clip is null ? double.NaN : small.T - pressed;
            var sizes = new List<(double, float)>();
            foreach (var s in after.TakeWhile(s => !s.SmallModel))
                if (sizes.Count == 0 || sizes[^1].Item2 != s.Size) sizes.Add((s.T - pressed, s.Size));
            var smallSamples = after.Where(s => s.SmallModel && s.Shrinked == 1 && s.Health > 0).ToList();
            var rises = new List<(double, float)>();
            for (int i = 1; i < smallSamples.Count; i++)
                if (smallSamples[i].Health > smallSamples[i - 1].Health) rises.Add((smallSamples[i].T - pressed, smallSamples[i].Health - smallSamples[i - 1].Health));
            double lastSmall = smallSamples.Count > 0 ? smallSamples[^1].T - pressed : double.NaN;
            var death = damage.FirstOrDefault(d => d.Victim == hit.Key && d.T > pressed && d.T <= pressed + lastSmall + .1 && d.Before > 0 && smallSamples.Count > 0);
            float mana = frames.Where(f => f.T >= press - .05 && f.T < press && !float.IsNaN(f.Mana)).Select(f => f.Mana).DefaultIfEmpty(float.NaN).Last()
                - frames.Where(f => f.T >= press && f.T <= pressed + .015 && !float.IsNaN(f.Mana)).Select(f => f.Mana).DefaultIfEmpty(float.NaN).Min();
            casts.Add(new ShrinkCast(pressed, mana, frames[k - 1].Adrenaline, frames.First(f => f.T >= pressed).Adrenaline, hit.Key, before.Health, sizes, swapped,
                smallSamples.Select(s => s.Clip).Distinct().ToList(), rises,
                damage.Count(d => d.Attacker == hit.Key && d.T > pressed && d.T <= pressed + lastSmall), lastSmall,
                death.T > 0 ? new ShrunkDeath(death.T - pressed, death.Asked, death.Type, death.Flags, death.Before, death.Units) : null));
        }
        return casts;
    }
}
