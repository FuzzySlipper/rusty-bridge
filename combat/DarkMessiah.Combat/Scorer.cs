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
    /// <summary>The report: every mismatch, blow by blow, or with <paramref name="brief"/> the first eight of each group.</summary>
    public string Format(bool brief = false)
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
            foreach (var m in brief ? group.Take(8) : group) s.AppendLine($"    {m.Case}: donor {m.Donor}, model {m.Model}");
            if (brief && group.Count() > 8) s.AppendLine($"    … {group.Count() - 8} more");
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

    public static ScoreReport Score(FightCases fight, CombatModel model)
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
            var verdict = CombatModel.Verdict(c);
            if (verdict == c.Donor) agree++;
            else mismatches.Add(new("NPC verdict", c.ToString(), c.Donor.ToString(), verdict.ToString(), VerdictReason(c, verdict)));
        }
        if (fight.Defense.Count > 0) scores.Add(new("NPC verdict", fight.Defense.Count, agree, 0));
        int stanceCases = fight.Defense.Count(c => c.Struck == Struck.Body && CombatModel.Stance(c.Victim) != CombatModel.DefenseStance.None);
        if (stanceCases == 0) untested.Add("a body blow on a defender in a parry or shield stance (no case reached it)");
        else tested.Add($"{stanceCases} body blows on a defender in a parry or shield stance");

        // The player's parry.
        agree = 0;
        foreach (var c in fight.Parries)
        {
            var outcome = model.PlayerParry(c);
            if (outcome == c.Donor) agree++;
            else mismatches.Add(new("player parry", c.ToString(), c.Donor.ToString(), outcome.ToString(), "unexplained"));
        }
        if (fight.Parries.Count > 0) scores.Add(new("player parry", fight.Parries.Count, agree, 0));

        // Requested damage, then applied damage.
        int unmodelled = 0;
        agree = 0;
        foreach (var c in fight.Damage)
        {
            float? asked = model.Damage(c);
            if (asked is null)
            {
                unmodelled++;
                mismatches.Add(new("damage", c.ToString(), F(c.Requested), "-", c.Requested == 0
                    ? "an attack its .qct doesn't list, and the donor asked nothing for it"
                    : "unexplained: no .qct attack or weapon for this blow"));
                continue;
            }
            if (MathF.Abs(asked.Value - c.Requested) <= Tolerance) agree++;
            else mismatches.Add(new("damage", c.ToString(), F(c.Requested), F(asked.Value), DamageReason(c, asked.Value, fight.Version)));
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
        c.State is not null ? "unexplained"
        : c.Struck == Struck.Body && c.Donor is DefenseResult.Parry or DefenseResult.Shield
            ? "the defender's state bits (0x4 parry, 0x10 shield up) are on the Wire only from v11"
            : c.Donor == DefenseResult.NoEffect ? "the no-effect byte is on the Wire only from v11"
            : "unexplained";

    /// <summary>
    /// Damage cases understood to differ for a reason the Wire can't show yet,
    /// by attacker and sequence ("" matches any). Everything else reads
    /// "unexplained", so a new gap stands out.
    /// </summary>
    static readonly (string Attacker, string Sequence, uint BeforeVersion, string Reason)[] KnownOpen =
    [
        // Twice its table, as a player critical doubles (the melee routine's DmgCrit, by the player's
        // byte +0x21F7, which isn't always marked in the damage type). Streamed from Wire v11.
        ("player", "", 11, "a player critical (×2) the Wire carries only from v11"),
        // Not taken further (#10077): one blow in a v3 recording, ×1.5 that no table or setting gives.
        ("npc_orc_sword", "attack_Fast", uint.MaxValue, "not taken further: one 4.8 where its .qct gives 3.2 (×1.5); mm_show_dmg_computing would show the factor if it recurs"),
        // Not taken further (#10077): the same attack at the same cycle asked 0 and 12 in one fight, and
        // nothing streamed (flags, type, hit group, force) tells the contacts apart.
        ("npc_ghoul", "Turn_strike_90_right", uint.MaxValue, "not taken further: asked 0 and 12 at the same cycle in one fight; nothing on the Wire tells them apart"),
    ];

    static string DamageReason(DamageCase c, float model, uint version) =>
        KnownOpen.FirstOrDefault(k => k.Attacker == c.Attacker.Class && version < k.BeforeVersion
            && (k.Sequence.Length == 0 || k.Sequence.Equals(c.Attacker.Sequence, StringComparison.OrdinalIgnoreCase))).Reason ?? "unexplained";
}
