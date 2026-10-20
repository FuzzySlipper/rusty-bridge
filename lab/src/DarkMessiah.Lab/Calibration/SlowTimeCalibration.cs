using DarkMessiah.Combat;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>One recorded slow-motion request, or a Fatality kill, and what the lab's rule makes of it.</summary>
/// <param name="Cause">"Fatality kill", "Whirlwind", "scripted" (a map's input, from outside server.dll), or "unexplained".</param>
/// <param name="Lab">What the lab asks at that moment: the same request, or null where it asks none.</param>
internal sealed record SlowTimeCase(int Tick, string Recording, string Cause, SlowTime? Donor, SlowTime? Lab)
{
    internal bool Agrees => Cause == "scripted" || Donor == Lab;

    public override string ToString() => FormattableString.Invariant(
        $"{Recording} tick {Tick}: {Cause}, donor {Describe(Donor)}, lab {Describe(Lab)}");

    static string Describe(SlowTime? s) => s is null ? "none" : FormattableString.Invariant($"×{s.Factor:0.##} for {s.Duration:0.##} s after {s.Delay:0.##} s");
}

/// <summary>
/// The lab's slow-motion rule against the recordings (docs/lab.md, "Slow motion"; #10147): each slow-motion request the
/// tap saw (Wire v7), and each player blow that killed with a Fatality's clip. The lab asks
/// <see cref="SlowTime.FatalityKill"/> on a Fatality's killing blow, and the Whirlwind's <c>.qct</c> slow time as it begins.
/// </summary>
internal static class SlowTimeCalibration
{
    internal static List<SlowTimeCase> Measure(IEnumerable<string> recordings, CombatModel model)
    {
        var cases = new List<SlowTimeCase>();
        var whirl = model.EventOf(new Party("player", "", ["layer_combo_sword_trample_noshield"], 0, "#1"))?.SlowTime;
        foreach (var path in recordings)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var whirlStarts = new List<int>();
            var kills = new List<int>();
            var requests = new List<(int Tick, WireSlowTime Slow)>();
            bool whirling = false;
            using (var stream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(stream))
                {
                    if (f.Version < 7) break;
                    var moves = DamageCalibration.MoveLayers(f);
                    bool now = moves.Keys.Any(k => Replay.InputTimeline.Family(k) == "whirlwind");
                    if (now && !whirling) whirlStarts.Add(f.Tick);
                    whirling = now;
                    foreach (var s in f.SlowTime.Where(s => s.Source == SlowTimeSource.Request && s.Duration > 0)) requests.Add((f.Tick, s));
                    foreach (var d in f.Damage.Where(d => d.Attacker == f.Player.Index && d.HealthBefore > 0 && d.HealthAfter <= 0))
                        // A kick under a fading Fatality's layer kills by the kick (v11-slowmo 13589).
                        if (moves.Keys.Any(k => Replay.InputTimeline.Family(k) == "fatality") && !moves.Keys.Any(k => Replay.InputTimeline.Family(k) == "kick")) kills.Add(f.Tick);
                }
            foreach (var (tick, s) in requests)
            {
                var donor = new SlowTime(s.Start, s.Duration, s.Factor);
                if (s.Caller == WireSlowTime.CallerOutside) cases.Add(new(tick, name, "scripted", donor, null));
                else if (kills.Any(k => Math.Abs(k - tick) <= 2)) cases.Add(new(tick, name, "Fatality kill", donor, SlowTime.FatalityKill));
                else if (whirlStarts.Any(w => Math.Abs(w - tick) <= 2)) cases.Add(new(tick, name, "Whirlwind", donor, whirl));
                else cases.Add(new(tick, name, "unexplained", donor, null));
            }
            // A Fatality kill the donor asked nothing for would be a difference too.
            foreach (var k in kills.Where(k => !requests.Any(r => Math.Abs(r.Tick - k) <= 2)))
                cases.Add(new(k, name, "Fatality kill", null, SlowTime.FatalityKill));
        }
        return cases;
    }
}
