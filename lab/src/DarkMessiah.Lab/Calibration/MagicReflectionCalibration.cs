using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A magic reflection shield in a recording (#10349): its entity's first and last tick, where it stood, and the mana that went as it came.</summary>
internal sealed record RecordedShield(double From, double To, Vector3 At, float Mana);

/// <summary>
/// An enemy missile in a recording (#10349): its class, its samples (time, origin, the player's origin then) from its
/// launch (when it first moved at more than 400 units/s) on, the sample it turned at (-1 if it didn't), and whether it
/// struck the player.
/// </summary>
internal sealed record RecordedMissile(string Class, IReadOnlyList<(double T, Vector3 At, Vector3 Player)> Samples, int Turned, bool StruckPlayer)
{
    internal double Launched => Samples[0].T;

    /// <summary>Its velocity between samples <paramref name="a"/> and <paramref name="a"/> + 1.</summary>
    internal Vector3 Velocity(int a) => (Samples[a + 1].At - Samples[a].At) / (float)(Samples[a + 1].T - Samples[a].T);

    public override string ToString() =>
        FormattableString.Invariant($"{Class} {Launched:0.00} s, {Velocity(0).Length():0} units/s: ")
        + (Turned >= 0 ? FormattableString.Invariant($"turned at {Samples[Turned].T:0.00} s, {(Samples[Turned].At - Samples[Turned].Player):F0} from the player's feet, off at {Velocity(Math.Min(Turned + 1, Samples.Count - 2)).Length():0}")
            : StruckPlayer ? "struck the player" : "flew on");
}

/// <summary>Reads the shields and the missiles at the player from a recording (#10349; docs/lab.md, "Magic reflection").</summary>
internal static class MagicReflectionCalibration
{
    internal static (List<RecordedShield> Shields, List<RecordedMissile> Missiles) Measure(string path)
    {
        var names = new EventNames();
        double interval = .015;
        var shields = new Dictionary<(int, uint), (double From, double To, Vector3 At)>();
        var tracks = new Dictionary<(int, uint), (string Class, List<(double T, Vector3 At, Vector3 Player)> Samples)>();
        var mana = new List<(double T, float Mana)>();
        var struck = new HashSet<(int, uint)>();
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                names.Observe(f);
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double t = f.Tick * interval;
                if (f.Stats.Valid != 0) mana.Add((t, f.Stats.Mana));
                var me = f.Entities.TryGetValue(f.Player.Index, out var p) ? new Vector3(p.OriginX, p.OriginY, p.OriginZ) : Vector3.Zero;
                foreach (var (i, e) in f.Entities)
                {
                    string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    // A shield's entity stands at the origin for a tick before it's placed.
                    if (cls == "spell_earth_2" && at != Vector3.Zero)
                        shields[(i, e.Serial)] = shields.TryGetValue((i, e.Serial), out var s) ? (s.From, t, s.At) : (t, t, at);
                    if (cls is not ("arxspellmissileshot" or "arrowshot")) continue;
                    if (!tracks.TryGetValue((i, e.Serial), out var track)) tracks[(i, e.Serial)] = track = (cls, []);
                    if (track.Samples.Count == 0 || track.Samples[^1].At != at) track.Samples.Add((t, at, me));
                }
                foreach (var d in f.Damage)
                    if (names.Name(f, d.Victim, d.VictimSerial).StartsWith("player", StringComparison.Ordinal)
                        && names.Name(f, d.Inflictor, d.InflictorSerial) is "arxspellmissileshot" or "arrowshot")
                        struck.Add((d.Inflictor, d.InflictorSerial));
            }
        var recordedShields = shields.Values.OrderBy(s => s.From).Select(s => new RecordedShield(s.From, s.To, s.At,
            // The mana goes a tick or two before the entity is placed.
            mana.Where(m => m.T >= s.From - .2 && m.T < s.From - .06).Select(m => m.Mana).DefaultIfEmpty(float.NaN).Last()
            - mana.Where(m => m.T >= s.From - .06 && m.T <= s.From + .1).Select(m => m.Mana).DefaultIfEmpty(float.NaN).Min())).ToList();
        var missiles = new List<RecordedMissile>();
        foreach (var (key, (cls, samples)) in tracks)
        {
            Vector3 V(int a) => (samples[a + 1].At - samples[a].At) / (float)(samples[a + 1].T - samples[a].T);
            int launch = Enumerable.Range(0, Math.Max(0, samples.Count - 1)).FirstOrDefault(a => V(a).Length() > 400, -1);
            if (launch < 0 || samples.Count - launch < 3) continue;
            var flight = samples.Skip(launch).ToList();
            var first = V(launch);
            // It turned at the sample after which it flew against its launch.
            int turned = Enumerable.Range(0, flight.Count - 1).FirstOrDefault(a => Vector3.Dot((flight[a + 1].At - flight[a].At), first) < 0, -1);
            missiles.Add(new RecordedMissile(cls, flight, turned, struck.Contains(key)));
        }
        return (recordedShields, missiles.OrderBy(m => m.Launched).ToList());
    }
}
