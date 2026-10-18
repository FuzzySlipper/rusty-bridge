using System.Text;
using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>One decision the model got wrong or couldn't make, with the reason it is understood to differ (or "unexplained").</summary>
public sealed record Mismatch(string Decision, string Case, string Donor, string Model, string Reason);

/// <summary>Agreement on one kind of decision: cases scored, cases agreeing, and those the model had no answer for.</summary>
public sealed record DecisionScore(string Decision, int Cases, int Agree, int Unmodelled)
{
    public double Rate => Cases - Unmodelled == 0 ? 1 : (double)Agree / (Cases - Unmodelled);
}

/// <param name="Untested">Model branches no case in the fight reached.</param>
/// <param name="Excluded">Cases left out of a score, and why.</param>
/// <param name="Reached">Rarely reached model branches that cases did reach.</param>
public sealed record ScoreReport(string Fight, uint Version, IReadOnlyList<DecisionScore> Scores, IReadOnlyList<Mismatch> Mismatches,
    IReadOnlyList<string> Untested, IReadOnlyList<string> Excluded, IReadOnlyList<string> Reached)
{
    public string Format()
    {
        var s = new StringBuilder();
        s.AppendLine($"{Fight} (recording v{Version})");
        foreach (var d in Scores)
            s.AppendLine($"  {d.Decision,-22} {d.Agree,4} of {d.Cases - d.Unmodelled,4} agree ({d.Rate:P0}){(d.Unmodelled > 0 ? $", {d.Unmodelled} unmodelled" : "")}");
        foreach (var branch in Reached) s.AppendLine($"  reached: {branch}");
        foreach (var branch in Untested) s.AppendLine($"  untested: {branch}");
        foreach (var why in Excluded) s.AppendLine($"  excluded: {why}");
        foreach (var group in Mismatches.GroupBy(m => (m.Decision, m.Reason)))
        {
            s.AppendLine($"  {group.Key.Decision} mismatches, {group.Key.Reason}: {group.Count()}");
            foreach (var m in group.Take(8)) s.AppendLine($"    {m.Case}: donor {m.Donor}, model {m.Model}");
            if (group.Count() > 8) s.AppendLine($"    … {group.Count() - 8} more");
        }
        return s.ToString();
    }
}

/// <summary>
/// Runs <see cref="CombatModel"/> over a fight's captured decisions and
/// reports agreement blow by blow. A mismatch carries the reason it is
/// understood to differ, so a known gap reads as such and a new one stands
/// out as "unexplained".
/// </summary>
public static class Scorer
{
    /// <summary>Damage agrees within this, the Wire's float rounding.</summary>
    const float Tolerance = 0.01f;

    public static ScoreReport Score(FightCases fight)
    {
        var scores = new List<DecisionScore>();
        var mismatches = new List<Mismatch>();
        var untested = new List<string>();
        var tested = new List<string>();
        var excludedNotes = new List<string>();

        // NPC verdicts.
        int agree = 0;
        foreach (var c in fight.Defense)
        {
            var model = CombatModel.Verdict(c);
            if (model == c.Donor) agree++;
            else mismatches.Add(new("NPC verdict", c.ToString(), c.Donor.ToString(), model.ToString(), VerdictReason(c, model)));
        }
        if (fight.Defense.Count > 0) scores.Add(new("NPC verdict", fight.Defense.Count, agree, 0));
        int stanceCases = fight.Defense.Count(c => c.Struck == Struck.Body && CombatModel.Stance(c.Victim) != CombatModel.DefenseStance.None);
        if (stanceCases == 0) untested.Add("a body blow on a defender in a parry or shield stance (no case reached it)");
        else tested.Add($"{stanceCases} body blows on a defender in a parry or shield stance");

        // The player's parry.
        agree = 0;
        foreach (var c in fight.Parries)
        {
            var model = CombatModel.PlayerParry(c);
            if (model == c.Donor) agree++;
            else mismatches.Add(new("player parry", c.ToString(), c.Donor.ToString(), model.ToString(), "unexplained"));
        }
        if (fight.Parries.Count > 0) scores.Add(new("player parry", fight.Parries.Count, agree, 0));

        // Requested damage, then applied damage.
        int unmodelled = 0;
        agree = 0;
        foreach (var c in fight.Damage)
        {
            float? model = CombatModel.Damage(c);
            if (model is null) { unmodelled++; mismatches.Add(new("damage", c.ToString(), F(c.Requested), "-", "no base damage for this attacker")); continue; }
            if (MathF.Abs(model.Value - c.Requested) <= Tolerance) agree++;
            else mismatches.Add(new("damage", c.ToString(), F(c.Requested), F(model.Value), DamageReason(c, model.Value)));
        }
        if (fight.Damage.Count > 0) scores.Add(new("damage requested", fight.Damage.Count, agree, unmodelled));

        // What a blow did to the victim: its health after, or a kill however far below 0 the
        // donor left it (it sometimes floors a killing blow at 0 and sometimes doesn't).
        // Some victims are protected in a fight, which the Wire doesn't say: they never lose
        // health (the player under the staging cfgs' `god`, l01_a's villager the ghoul attacks
        // for two minutes) or never drop below 1 (l01_a's guards). Their blows are excluded.
        var protectedVictims = fight.Damage.GroupBy(c => c.Victim.Id)
            .Where(g => g.Any(c => c.Requested > 0) && (g.All(c => c.Applied == 0) || g.Min(c => c.HealthAfter) == 1 && g.Count(c => c.HealthAfter == 1) > 1))
            .ToDictionary(g => g.Key, g => g.All(c => c.Applied == 0) ? "never loses health" : "never drops below 1 health");
        agree = 0;
        int excluded = 0;
        foreach (var c in fight.Damage)
        {
            if (protectedVictims.ContainsKey(c.Victim.Id)) { excluded++; continue; }
            float predicted = CombatModel.HealthAfter(c);
            bool same = MathF.Abs(c.HealthAfter - predicted) <= Tolerance || (predicted <= 0 && c.HealthAfter <= 0);
            if (same) agree++;
            else mismatches.Add(new("damage applied", c.ToString(), $"{F(c.HealthBefore)} → {F(c.HealthAfter)}", $"{F(c.HealthBefore)} → {F(predicted)}", "unexplained"));
        }
        if (fight.Damage.Count - excluded > 0) scores.Add(new("damage applied", fight.Damage.Count - excluded, agree, 0));
        foreach (var group in fight.Damage.Where(c => protectedVictims.ContainsKey(c.Victim.Id)).GroupBy(c => (c.Victim.Class, Why: protectedVictims[c.Victim.Id])))
            excludedNotes.Add($"damage applied: {group.Count()} blows on a {group.Key.Class} that {group.Key.Why} in this fight");

        return new ScoreReport(fight.Name, fight.Version, scores, mismatches, untested, excludedNotes, tested);
    }

    static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    static string VerdictReason(DefenseCase c, DefenseResult model) =>
        c.Struck == Struck.Body && c.Donor is DefenseResult.Parry or DefenseResult.Shield
            ? "the defender's state bits (0x4 parry, 0x10 shield up) aren't on the Wire (#10077)"
            : c.Donor == DefenseResult.NoEffect ? "no-effect verdicts (4) aren't modelled yet"
            : "unexplained";

    /// <summary>
    /// Damage cases understood to differ for a reason the Wire can't show yet,
    /// by attacker and sequence ("" matches any). Everything else reads
    /// "unexplained", so a new gap stands out.
    /// </summary>
    static readonly (string Attacker, string Sequence, string Reason)[] KnownOpen =
    [
        ("player", "", "the player's charged swing: how long it was held isn't on the Wire (#10077)"),
        ("npc_orc_sword", "attack_Fast", "the first contact's strength varies, 3.2 six times and 4.8 once (#10077)"),
        ("npc_ghoul", "Turn_strike_90_right", "asks 0 on every contact in some fights and 12 in others (#10077)"),
        ("npc_ghoul", "attack_wounded_1", "asked 0 on its one recorded contact (#10077)"),
    ];

    static string DamageReason(DamageCase c, float model) =>
        KnownOpen.FirstOrDefault(k => k.Attacker == c.Attacker.Class
            && (k.Sequence.Length == 0 || k.Sequence.Equals(c.Attacker.Sequence, StringComparison.OrdinalIgnoreCase))).Reason ?? "unexplained";
}
