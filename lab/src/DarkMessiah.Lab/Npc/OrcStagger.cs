using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The numbers an orc is stunned and knocked down by (#10152; docs/lab.md, "Stuns and knockdowns"): its <c>.qct</c>'s
/// <c>nbhittostun</c> (6), <c>delaytostun</c> (10 s), <c>stunned_duration</c> (4 s) and <c>life_tohurt</c> (0.2, below
/// which it is wounded), and the settings' <c>WoundedStunChance</c> and <c>WoundedFallChance</c> (0.3 each) and
/// <c>KnockedDownTime</c> (2 s).
/// </summary>
internal sealed record OrcStaggerData(int HitsToStun, float StunWindow, float StunSeconds, float HurtFraction, float WoundedStunChance,
    float WoundedFallChance, float KnockedDownSeconds)
{
    internal static OrcStaggerData Read(DonorContent donor, string model)
    {
        float N(string key, float shipped) => donor.Number(model, "entity_data", key) ?? shipped;
        var settings = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault()?.Block("ai") : null;
        float S(string key, float shipped) => settings?.Number(key) ?? shipped;
        return new OrcStaggerData((int)N("nbhittostun", 6), N("delaytostun", 10), N("stunned_duration", 4), N("life_tohurt", .2f),
            S("WoundedStunChance", .3f), S("WoundedFallChance", .3f), S("KnockedDownTime", 2));
    }
}

/// <summary>What a blow did to the orc beyond its damage.</summary>
internal enum Stagger { None, Stunned, KnockedDown }

/// <summary>
/// Whether a player's blow stuns or knocks the orc down, by Dark Messiah's rules (#10152; server.dll's melee routine at
/// +0x407D70 and the NPC's stun test at +0x2AB04F):
/// <list type="bullet">
/// <item><b>knocked down</b> on a roll under the blow's <c>.qct</c> event's <c>chancetofall</c> (plus
/// <c>WoundedFallChance</c> on a wounded orc); a jump attack (<c>AttackMove</c> 12) only after the player dropped
/// <c>PlayerVerticalAttackHeightToMakeNPCFall</c>, which the lab doesn't track, so never;</item>
/// <item><b>stunned</b> by any blow of a staff (its weapon class's virtual at +0x704 says so; 18 of 19 recorded staff
/// blows on an alert orc that was up), or once it has taken <c>nbhittostun</c> hits within <c>delaytostun</c> seconds
/// on a roll under the event's <c>chancetostun</c> × the weapon's <c>stunmultiplicator</c> (plus
/// <c>WoundedStunChance</c> wounded). A knockdown wins over a stun.</item>
/// </list>
/// Seeded, so fights and the smoke repeat.
/// </summary>
internal sealed class OrcStagger(OrcStaggerData data, int seed = OrcStagger.Seed)
{
    internal const int Seed = 10152;
    /// <summary>The <c>AttackMove</c> of the jump attack, which fells only after a drop.</summary>
    internal const int JumpAttackMove = 12;
    Random random = new(seed);
    readonly Queue<double> hits = new();

    internal OrcStaggerData Data => data;

    internal void Reset()
    {
        random = new Random(Seed);
        hits.Clear();
    }

    /// <param name="now">The fight's time, seconds.</param>
    /// <param name="staff">The blow is a staff's.</param>
    /// <param name="stunMultiplier">The weapon's <c>stunmultiplicator</c> (1 for every recorded weapon, 2 for axes).</param>
    internal Stagger Decide(AttackEvent? blow, double now, bool wounded, bool staff, float stunMultiplier)
    {
        hits.Enqueue(now);
        while (hits.Count > Math.Max(1, data.HitsToStun)) hits.Dequeue();
        if (blow is null) return Stagger.None;
        float fall = blow.Move == JumpAttackMove ? 0 : blow.ChanceToFall + (wounded ? data.WoundedFallChance : 0);
        if (fall > 0 && random.NextSingle() < fall) return Stagger.KnockedDown;
        if (staff) return Stagger.Stunned;
        float stun = blow.ChanceToStun * stunMultiplier + (wounded ? data.WoundedStunChance : 0);
        bool enough = hits.Count >= Math.Max(1, data.HitsToStun) && now - hits.Peek() <= data.StunWindow;
        return enough && stun > 0 && random.NextSingle() < stun ? Stagger.Stunned : Stagger.None;
    }
}
