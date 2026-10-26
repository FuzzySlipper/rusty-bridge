using DarkMessiah.Shared;
using DarkMessiah.Lab.Npc;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// What a reaction rule makes of the recorded blows, as the player feels it (#10382): whether it cut the orc's action
/// short as the donor's did, and, over every donor reaction in its place that ran to its end, how far off its busy
/// time, slide and turn were (medians of the differences; a rule that left the orc standing is off by all of them).
/// </summary>
internal sealed record FlinchScore(int Blows, int Agree, int Measured, float Busy, float Moved, float Turned, int DonorInterrupted, int RuleInterrupted)
{
    internal float Agreement => Blows > 0 ? (float)Agree / Blows : 0;

    /// <summary>What the fit minimises: 10% of blows cut short wrongly weigh as much as 0.3 s of busy time, 0.6 m of slide or 20° of turn off.</summary>
    internal float Loss => 10 * (1 - Agreement) + Busy / .3f + Moved / .6f + Turned / 20;

    public override string ToString() => FormattableString.Invariant(
        $"cut short as the donor's on {Agree} of {Blows} ({100f * Agreement:0}%; the donor's {DonorInterrupted}, the rule's {RuleInterrupted}); over {Measured} the donor's ran to their end, busy off by {Busy:0.00} s, slide by {Moved:0.00} m, turn by {Turned:0}° (medians)");
}

/// <summary>
/// The procedural reaction (<see cref="OrcFlinch"/>) and the authored rule (<see cref="OrcReactions"/>) against every
/// recorded blow a reaction rule decides (<see cref="ReactionCalibration.Blows"/>), and the fit of the procedural one's
/// numbers (#10382).
/// </summary>
internal static class FlinchCalibration
{
    /// <summary>
    /// Whether the donor's reaction cut its action short: a hit or push in its place (a small hit over it, or none, didn't).
    /// </summary>
    internal static bool DonorInterrupted(ReactionCase c) => ReactionCalibration.IsReaction(c.Reaction);

    /// <summary>The authored rule as the player feels it: its clip's length and root motion at rate 1 (it turns nothing).</summary>
    internal static FlinchScore Authored(IEnumerable<(ReactionCase Case, ReactionBlow Blow, bool Stunned, string Donor)> blows, ModelInfo orc)
    {
        var lastSmall = new Dictionary<(string, string), int>();
        return Score(blows, (c, blow, stunned) =>
        {
            int since = lastSmall.TryGetValue((c.Recording, c.Victim), out int t) ? c.Tick - t : int.MaxValue;
            var r = OrcReactions.Pick(blow, stunned, since == int.MaxValue ? double.PositiveInfinity : since / 66.0);
            if (r.Overlay) lastSmall[(c.Recording, c.Victim)] = c.Tick;
            if (r.Clip is null || r.Overlay) return default;
            var clip = orc.Clips.Values.FirstOrDefault(k => k.Name.Equals(r.Clip, StringComparison.OrdinalIgnoreCase));
            return clip is null ? new FlinchOutcome(true, 0, 0, 0, 0) : new FlinchOutcome(true, clip.Seconds, clip.MovedAt(1).Length() * SourceSpace.MetresPerUnit, 0, 0);
        });
    }

    internal static FlinchScore Procedural(IEnumerable<(ReactionCase Case, ReactionBlow Blow, bool Stunned, string Donor)> blows, FlinchTuning tuning) =>
        Score(blows, (_, blow, stunned) => OrcFlinch.Predict(tuning, blow, stunned));

    static FlinchScore Score(IEnumerable<(ReactionCase Case, ReactionBlow Blow, bool Stunned, string Donor)> blows, Func<ReactionCase, ReactionBlow, bool, FlinchOutcome> rule)
    {
        int count = 0, agree = 0, donorCut = 0, ruleCut = 0;
        List<float> busy = [], moved = [], turned = [];
        foreach (var (c, blow, stunned, _) in blows)
        {
            var outcome = rule(c, blow, stunned);
            bool donor = DonorInterrupted(c);
            count++;
            if (donor) donorCut++;
            if (outcome.Interrupted) ruleCut++;
            if (donor == outcome.Interrupted) agree++;
            // Every donor reaction that ran to its end counts, whether or not the rule cut the action short (then it
            // stood: no busy time, slide or turn), so a rule can't score by leaving the hard ones out.
            if (!donor || float.IsNaN(c.Busy)) continue;
            if (!outcome.Interrupted) outcome = default;
            busy.Add(MathF.Abs(outcome.Busy - c.Busy));
            moved.Add(MathF.Abs(outcome.Moved - c.Moved));
            turned.Add(MathF.Abs(outcome.Turned - c.Turned));
        }
        return new FlinchScore(count, agree, busy.Count, Median(busy), Median(moved), Median(turned), donorCut, ruleCut);
    }

    static float Median(List<float> values)
    {
        if (values.Count == 0) return float.NaN;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>Each fitted number's range: the fit searches inside it. <see cref="FlinchTuning.Balance"/> stays as given (the momenta scale to it).</summary>
    static readonly (string Name, float Low, float High)[] Ranges =
    [
        ("swingMomentum", .2f, 10), ("kickMomentum", .2f, 10), ("pushBoost", 0, 4), ("pain", 0, 1.5f),
        ("frequency", 2, 14), ("damping", .3f, 1.6f), ("recover", .05f, .9f), ("stunnedBalance", .05f, 1),
        ("slide", .02f, 2), ("friction", .3f, 8), ("twist", -30, 30),
    ];

    static float[] Values(FlinchTuning t) =>
        [t.SwingMomentum, t.KickMomentum, t.PushBoost, t.Pain, t.Frequency, t.Damping, t.Recover, t.StunnedBalance, t.Slide, t.Friction, t.Twist];

    static FlinchTuning With(FlinchTuning t, float[] v) => t with
    {
        SwingMomentum = v[0], KickMomentum = v[1], PushBoost = v[2], Pain = v[3], Frequency = v[4], Damping = v[5],
        Recover = v[6], StunnedBalance = v[7], Slide = v[8], Friction = v[9], Twist = v[10],
    };

    /// <summary>
    /// The procedural reaction's numbers that best match the recorded blows (<see cref="FlinchScore.Loss"/>): a seeded
    /// random search over <see cref="Ranges"/>, then a search along each number in shrinking steps from the best.
    /// Deterministic for its seed.
    /// </summary>
    internal static FlinchTuning Fit(IReadOnlyList<(ReactionCase Case, ReactionBlow Blow, bool Stunned, string Donor)> blows, FlinchTuning start, int samples = 3000, int seed = 10382)
    {
        var random = new Random(seed);
        var best = start;
        float bestLoss = Procedural(blows, start).Loss;
        for (int s = 0; s < samples; s++)
        {
            var v = Ranges.Select(r => r.Low + (float)random.NextDouble() * (r.High - r.Low)).ToArray();
            var t = With(start, v);
            float loss = Procedural(blows, t).Loss;
            if (loss < bestLoss) (best, bestLoss) = (t, loss);
        }
        for (float scale = .25f; scale > .01f; scale /= 2)
            for (bool better = true; better;)
            {
                better = false;
                for (int i = 0; i < Ranges.Length; i++)
                    foreach (float sign in new[] { -1f, 1f })
                    {
                        var v = Values(best);
                        v[i] = Math.Clamp(v[i] + sign * scale * (Ranges[i].High - Ranges[i].Low), Ranges[i].Low, Ranges[i].High);
                        var t = With(best, v);
                        float loss = Procedural(blows, t).Loss;
                        if (loss < bestLoss - 1e-5f) { (best, bestLoss) = (t, loss); better = true; }
                    }
            }
        return best;
    }
}
