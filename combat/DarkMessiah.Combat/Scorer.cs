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

    /// <param name="contacts">Recomputed contacts for the fight's melee blows (#10078), when they were swept; null leaves the decision out.</param>
    /// <param name="reaches">The reach decision's cases (<see cref="Reaches.Read"/>); null leaves it out.</param>
    public static ScoreReport Score(FightCases fight, CombatModel model, IReadOnlyList<ContactCase>? contacts = null, IReadOnlyList<ReachCase>? reaches = null)
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

        if (contacts is { Count: > 0 }) ScoreContacts(contacts, scores, mismatches, tested);
        if (reaches is { Count: > 0 }) ScoreReaches(reaches, model, scores, mismatches, tested);

        return new ScoreReport(fight.Name, fight.Version, scores, mismatches, untested, excludedNotes, tested);
    }

    /// <summary>
    /// The contact decision (#10078): the first part the swept trace meets
    /// against what the donor's trace struck, scored as two decisions:
    /// <list type="bullet">
    /// <item>"contact": blows the donor traced, which name the entity and hit box met;</item>
    /// <item>"contact, untraced": blows that reached without a trace (on the player, or naming no hit box), where the sweep has to meet the victim at all.</item>
    /// </list>
    /// A mismatch is put down to one of:
    /// <list type="bullet">
    /// <item>pose timing: the blow's own frame, posed, agrees; or the donor's hit point lies off the part it named, as posed from the victim's last streamed state;</item>
    /// <item>swing path: the donor's part is where its trace ended, and a straight sweep meets another part first; or the blow wasn't traced, and its reach point falls short of the victim's shapes;</item>
    /// <item>shape fit: the donor's point is on its part, and the sweep still misses it.</item>
    /// </list>
    /// A struck entity the import has no model for can't be posed, and says so.
    /// </summary>
    static void ScoreContacts(IReadOnlyList<ContactCase> contacts, List<DecisionScore> scores, List<Mismatch> mismatches, List<string> tested)
    {
        foreach (bool traced in new[] { true, false })
        {
            string decision = traced ? "contact" : "contact, untraced";
            var cases = contacts.Where(c => c.Traced == traced).ToList();
            if (cases.Count == 0) continue;
            int agree = 0;
            foreach (var c in cases)
            {
                if (c.Agrees) agree++;
                else mismatches.Add(new(decision, c.ToString(), c.Donor.ToString(), c.Before?.ToString() ?? "nothing", ContactReason(c)));
            }
            scores.Add(new(decision, cases.Count, agree, 0));
        }
        var body = contacts.Where(c => c.Traced && c.Agrees && c.Donor.Struck == Struck.Body).ToList();
        if (body.Count > 0) tested.Add($"contact: {body.Count(c => c.Before!.Hitbox == c.Donor.Hitbox)} of {body.Count} agreeing body contacts on the donor's own hit box");
    }

    /// <summary>
    /// The reach decision (#10099): whether an NPC attack part's sweep reaches a victim it doesn't trace against
    /// shapes (<see cref="CombatModel.Reach"/>), against whether the donor's blow landed. Cases on the player include
    /// parts that missed; untraced blows on NPCs all landed.
    /// </summary>
    static void ScoreReaches(IReadOnlyList<ReachCase> cases, CombatModel model, List<DecisionScore> scores, List<Mismatch> mismatches, List<string> tested)
    {
        int agree = 0;
        foreach (var c in cases)
        {
            var r = model.Reach(c);
            if (r.Lands == c.Landed) { agree++; continue; }
            mismatches.Add(new("reach", c.ToString(), c.Landed ? "landed" : "missed",
                FormattableString.Invariant($"{(r.Lands ? "reaches" : "falls short")} (box at {r.Entry:F0} of range {r.Range:F0})"), ReachReason(c, r)));
        }
        scores.Add(new("reach", cases.Count, agree, 0));
        tested.Add($"reach: {cases.Count(c => c.Landed && !c.FromDamage)} landed and {cases.Count(c => !c.Landed)} missed attack parts on the player " +
            $"({cases.Count(c => c.Sweeps is { Count: > 0 })} turn-strike windows), {cases.Count(c => c.FromDamage && c.Victim == "player")} blows on the player " +
            $"scored at their own frame, {cases.Count(c => c.FromDamage && c.Victim != "player")} untraced blows on NPCs");
    }

    /// <summary>A sweep that ends within this many Source units of the victim's box is near the reach's end.</summary>
    const float ReachEdge = 12;

    static string ReachReason(ReachCase c, ReachResult r)
    {
        if (!c.Landed && c.StruckFirst.Length > 0)
            return c.StruckFirst == "?"
                ? "struck first: the sweep stops at the first entity it meets, and it met one gone from the Wire by its frame"
                : $"struck first: the sweep stops at the first entity it meets, and it met a {c.StruckFirst}";
        if (!c.Landed && c.FacingOther.Length > 0)
            return $"another enemy: the routine aims at the attacker's enemy, which the Wire doesn't carry, and a {c.FacingOther} was in its aim too";
        if (!c.Landed && !r.FacingLands)
            return "another enemy: when the attacker's enemy isn't the player (the Wire doesn't carry it), the sweep runs along its facing, which passes the player";
        if (MathF.Abs(r.Entry - r.Range) <= ReachEdge)
            return "near the reach's end (within 12 units): the Wire has the attacker and player as its frame ended, not at the sweep inside the attacker's think";
        if (r.Part is { Move: > 1 } part)
            return $"a lunge (AttackMove {part.Move}): the attacker moves through the attack, and the Wire has it only at its thinks";
        if (r.Part is null && c.Armed)
            return "no combatmovedatas entry for the attack: the model takes the move default (120)";
        return "unexplained";
    }

    /// <summary>A point within this many Source units of a hit box counts as on it: the Wire's float rounding and the box edge.</summary>
    const float OnPart = 1.5f;

    static string ContactReason(ContactCase c)
    {
        if (c.After is ContactPart after && c.Matches(after))
            return "pose timing: posed as the blow's own frame, the sweep meets the donor's part";
        if (!c.Traced)
            return "swing path: the donor reached without a trace, and its reach point falls short of the victim's hit boxes";
        if (float.IsNaN(c.Miss))
            return c.Donor.Class == "prop_ragdoll"
                ? "not posed: the trace struck a ragdoll, whose bones aren't on the Wire"
                : "not posed: the struck entity's model isn't in the import (import the level from a recording that has it)";
        if (c.Miss > OnPart) return "pose timing: the donor's hit point is off the part it named, as posed from the victim's last streamed state";
        if (c.Before is not null && c.DonorAlong is not null) return "swing path: the donor's part is where its trace ended, and the straight sweep meets another part first";
        return "shape fit: the donor's hit point is on its part, and the sweep misses it";
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
        // Its mirror, the same pattern: 0 on another ghoul where its table asks 12 (l01_a-v12-arena-ghoul, 2026-10-19).
        // A NoEffect verdict (its byte set) whose blow still asked 12 and took it: once, an orc's lunge on a ghoul
        // (l01_a-v13-arena-pose, tick 10615). Every other NoEffect zeroes the blow.
        ("npc_orc_sword", "attack_Forward", uint.MaxValue, "not taken further: once, a NoEffect verdict whose blow still asked and took its damage"),
        ("npc_ghoul", "Turn_strike_90_left", uint.MaxValue, "not taken further: asked 0 where its table gives 12, as its mirror Turn_strike_90_right does; nothing on the Wire tells them apart"),
    ];

    static string DamageReason(DamageCase c, float model, uint version) =>
        KnownOpen.FirstOrDefault(k => k.Attacker == c.Attacker.Class && version < k.BeforeVersion
            && (k.Sequence.Length == 0 || k.Sequence.Equals(c.Attacker.Sequence, StringComparison.OrdinalIgnoreCase))).Reason ?? "unexplained";
}
