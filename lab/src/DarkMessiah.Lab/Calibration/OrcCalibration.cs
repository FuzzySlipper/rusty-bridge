using System.Numerics;
using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// How an orc fights (#10150), the donor's from recordings or the lab's from a scripted fight, in Source units and
/// seconds: each attack's distance from the player as it began, the time from one attack's start to the next, and its
/// walking in (<c>CombatWalk</c>).
/// </summary>
/// <param name="WalkRates">The walk clip's playback, cycles a second (a diagnostic: the ground speed isn't the clip's).</param>
/// <param name="WalkSpeeds">Its ground speed while walking, Source units a second: for the donor, from one position update
/// to the next (an NPC's position reaches the Wire when it thinks); for the lab, step to step.</param>
/// <param name="WalkEnds">How far from the player each walk of half a second or more ended, Source units.</param>
/// <param name="Cut">Recordings whose tail couldn't be read (cut short as they were written): what came before is counted.</param>
internal sealed record OrcBehaviour(IReadOnlyDictionary<string, List<float>> AttackDistances, List<float> Gaps, List<float> WalkRates,
    List<float> WalkSpeeds, List<float> WalkEnds, IReadOnlyList<string>? Cut = null)
{
    internal static float Quantile(IReadOnlyList<float> values, float p)
    {
        if (values.Count == 0) return float.NaN;
        var sorted = values.Order().ToList();
        return sorted[(int)MathF.Round((sorted.Count - 1) * p)];
    }

    internal float GapMedian => Quantile(Gaps, .5f);
    internal float WalkRate => Quantile(WalkRates, .5f);
    internal float WalkSpeed => Quantile(WalkSpeeds, .5f);
}

/// <summary>
/// The orc's behaviour against the donor's (#10150; docs/lab.md, "The orc"): the donor's measured over every recorded
/// <c>npc_orc_sword</c> that fought the player, the lab's over a scripted fight (<see cref="Lab"/>).
/// </summary>
internal static class OrcCalibration
{
    /// <summary>A walk shorter than this, seconds, is a step, not walking in.</summary>
    const double MinWalk = .5;

    /// <summary>A gap longer than this is a lull between fights, not the orc's rhythm.</summary>
    const float LongestGap = 30;

    static bool IsAttack(string clip) => clip.StartsWith("attack", StringComparison.OrdinalIgnoreCase);

    /// <summary>The donor's orcs: attacks started as main sequences (each attack's start), their gaps, their walking speed.</summary>
    internal static OrcBehaviour Donor(IEnumerable<string> recordings)
    {
        var distances = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        var gaps = new List<float>();
        var walks = new List<float>();
        var speeds = new List<float>();
        var ends = new List<float>();
        var cut = new List<string>();
        foreach (var path in recordings)
        {
            var last = new Dictionary<(ushort, uint), (string Sequence, double LastAttack, Vector2 At, double Time)>();
            var walkCycle = new Dictionary<(ushort, uint), (float Cycle, double Time)>();
            // Each walk's last position update and start: speeds between updates, and where walks of 0.5 s or more end.
            var walkMoved = new Dictionary<(ushort, uint), (Vector2 At, double Time)>();
            var walkStart = new Dictionary<(ushort, uint), double>();
            using var stream = File.OpenRead(path);
            using var frames = RecordingReader.Frames(stream).GetEnumerator();
            while (true)
            {
                try { if (!frames.MoveNext()) break; }
                catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { cut.Add(Path.GetFileName(path)); break; }
                var f = frames.Current;
                var player = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "player");
                if (f.Classes.GetValueOrDefault(player.ClassId) != "player") continue;
                double time = f.Tick * (double)f.TickInterval;
                foreach (var (index, e) in f.Entities)
                {
                    if (f.Classes.GetValueOrDefault(e.ClassId) != InputTimelineOrc || !e.Flags.HasFlag(WireEntityFlags.HasAnimation)) continue;
                    if (e.Flags.HasFlag(WireEntityFlags.HasHealth) && (e.LifeState != 0 || e.Health <= 0)) continue;
                    var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                    string sequence = e.Sequence >= 0 && e.Sequence < names.Length ? names[e.Sequence] : "";
                    var at = new Vector2(e.OriginX, e.OriginY);
                    float distance = Vector2.Distance(at, new Vector2(player.OriginX, player.OriginY));
                    var key = (index, e.Serial);
                    var was = last.TryGetValue(key, out var w) ? w : (Sequence: "", LastAttack: double.NaN, At: at, Time: time);
                    if (IsAttack(sequence) && sequence != was.Sequence)
                    {
                        if (!distances.TryGetValue(sequence, out var list)) distances[sequence] = list = [];
                        list.Add(distance);
                        if (!double.IsNaN(was.LastAttack) && time - was.LastAttack < LongestGap) gaps.Add((float)(time - was.LastAttack));
                        was.LastAttack = time;
                    }
                    if (sequence == "CombatWalk")
                    {
                        if (was.Sequence != "CombatWalk") { walkStart[key] = time; walkMoved[key] = (at, time); }
                        else if (walkMoved.TryGetValue(key, out var moved) && at != moved.At)
                        {
                            if (time - moved.Time is > 0 and < .3) speeds.Add(Vector2.Distance(at, moved.At) / (float)(time - moved.Time));
                            walkMoved[key] = (at, time);
                        }
                        // The walk's playback, from one streamed cycle to the next (around the loop).
                        if (was.Sequence == "CombatWalk" && walkCycle.TryGetValue(key, out var c) && time > c.Time && e.Cycle != c.Cycle)
                            walks.Add((e.Cycle - c.Cycle + (e.Cycle < c.Cycle ? 1 : 0)) / (float)(time - c.Time));
                        if (!walkCycle.TryGetValue(key, out var seen) || e.Cycle != seen.Cycle) walkCycle[key] = (e.Cycle, time);
                    }
                    else if (was.Sequence == "CombatWalk" && walkStart.Remove(key, out double began) && time - began >= MinWalk) ends.Add(distance);
                    last[key] = (sequence, was.LastAttack, at, time);
                }
            }
        }
        return new OrcBehaviour(distances, gaps, walks, speeds, ends, cut);
    }

    /// <summary>
    /// Where the donor's attacks landed: for each attack recorded at least <paramref name="least"/> times, the middle half
    /// (25th to 75th percentile) of its start distance less its travel to the strike (Source units); the outer quarters
    /// are mostly players backing off as it struck. <c>combat.json</c>'s
    /// <c>orc.attackLandings</c> are fitted to these.
    /// </summary>
    internal static Dictionary<string, (float Low, float High)> Landings(OrcBehaviour donor, OrcMindData data, int least = 10) =>
        data.Attacks.Where(a => donor.AttackDistances.GetValueOrDefault(a.Clip) is { } d && d.Count >= least).ToDictionary(a => a.Clip, a =>
        {
            var landed = donor.AttackDistances[a.Clip].Select(d => d - a.Travel).ToList();
            return (OrcBehaviour.Quantile(landed, .25f), OrcBehaviour.Quantile(landed, .75f));
        });

    const string InputTimelineOrc = Replay.InputTimeline.OrcClass;

    /// <summary>
    /// The lab's orc over <paramref name="seconds"/> of a scripted fight: the player stands, swings every
    /// <paramref name="swingEvery"/> seconds while the orc is within 2.5 m, and every <paramref name="retreatEvery"/>
    /// seconds backs off 4 m over 1.5 s, so the orc has distances to close and walks to make as the recorded players
    /// gave it.
    /// </summary>
    internal static OrcBehaviour Lab(Orc orc, float seconds, float step = 1 / 66f, float swingEvery = 1.5f, float retreatEvery = 8)
    {
        var distances = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        var gaps = new List<float>();
        var walks = new List<float>();
        var speeds = new List<float>();
        var ends = new List<float>();
        double walkBegan = 0;
        Vector3 before = orc.Feet;
        orc.Spawn();
        var player = Vector3.Zero;
        double lastAttack = double.NaN;
        string lastClip = "";
        float lastCycle = 0;
        for (float t = 0; t < seconds; t += step)
        {
            float distance = Vector3.Distance(orc.Feet with { Y = 0 }, player with { Y = 0 });
            bool swing = distance < 2.5f && t % swingEvery < step;
            // Backing off: 4 m away from the orc over 1.5 s.
            if (t % retreatEvery < 1.5f && distance > 1e-3f) player += Vector3.Normalize((player - orc.Feet) with { Y = 0 }) * (4 / 1.5f) * step;
            orc.Step(step, player, swing ? IncomingSwing.Parryable : IncomingSwing.None);
            if (!orc.Alive) { orc.Spawn(); lastAttack = double.NaN; }
            if (orc.State == OrcState.Attack && orc.Clip != lastClip)
            {
                if (!distances.TryGetValue(orc.Clip, out var list)) distances[orc.Clip] = list = [];
                list.Add(distance / SourceSpace.MetresPerUnit);
                if (!double.IsNaN(lastAttack) && t - lastAttack < LongestGap) gaps.Add((float)(t - lastAttack));
                lastAttack = t;
            }
            if (orc.Clip == "CombatWalk" && lastClip == "CombatWalk")
            {
                if (orc.Cycle != lastCycle) walks.Add((orc.Cycle - lastCycle + (orc.Cycle < lastCycle ? 1 : 0)) / step);
                speeds.Add(Vector3.Distance(orc.Feet with { Y = 0 }, before with { Y = 0 }) / step / SourceSpace.MetresPerUnit);
            }
            if (orc.Clip == "CombatWalk" && lastClip != "CombatWalk") walkBegan = t;
            else if (orc.Clip != "CombatWalk" && lastClip == "CombatWalk" && t - walkBegan >= MinWalk) ends.Add(distance / SourceSpace.MetresPerUnit);
            before = orc.Feet;
            lastCycle = orc.Cycle;
            lastClip = orc.Clip;
        }
        orc.Spawn();
        return new OrcBehaviour(distances, gaps, walks, speeds, ends);
    }
}
