using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The numbers an orc is stunned and knocked down by (#10152; docs/lab.md, "Stuns and knockdowns"): its <c>.qct</c>'s
/// <c>nbhittostun</c> (6), <c>delaytostun</c> (10 s), <c>stunned_duration</c> (4 s) and <c>life_tohurt</c> (0.2, below
/// which it is wounded), and the settings' <c>WoundedStunChance</c> and <c>WoundedFallChance</c> (0.3 each),
/// <c>KnockedDownTime</c> (2 s) and <c>PlayerVerticalAttackHeightToMakeNPCFall</c> (50 units).
/// </summary>
internal sealed record OrcStaggerData(int HitsToStun, float StunWindow, float StunSeconds, float HurtFraction, float WoundedStunChance,
    float WoundedFallChance, float KnockedDownSeconds, float JumpDropToFall = 50)
{
    internal static OrcStaggerData Read(DonorContent donor, string model)
    {
        float N(string key, float shipped) => donor.Number(model, "entity_data", key) ?? shipped;
        var settings = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault()?.Block("ai") : null;
        float S(string key, float shipped) => settings?.Number(key) ?? shipped;
        return new OrcStaggerData((int)N("nbhittostun", 6), N("delaytostun", 10), N("stunned_duration", 4), N("life_tohurt", .2f),
            S("WoundedStunChance", .3f), S("WoundedFallChance", .3f), S("KnockedDownTime", 2), S("PlayerVerticalAttackHeightToMakeNPCFall", 50));
    }
}

/// <summary>What a blow did to the orc beyond its damage.</summary>
internal enum Stagger { None, Stunned, KnockedDown }

/// <summary>
/// Whether a player's blow stuns or knocks the orc down, by Dark Messiah's rules (#10152; server.dll's melee routine at
/// +0x407D70 and the NPC's stun test at +0x2AB04F):
/// <list type="bullet">
/// <item><b>knocked down</b> on a roll under the blow's <c>.qct</c> event's <c>chancetofall</c> (plus
/// <c>WoundedFallChance</c> on a wounded orc); a jump attack (the player's attack type +0x27A8 at 12, which server.dll sets for the falling attack; its <c>.qct</c>
/// event has no <c>AttackMove</c>) only when the player has dropped
/// <c>PlayerVerticalAttackHeightToMakeNPCFall</c> (50 units) below where they left the ground (R10152-1: server.dll's
/// melee routine at +0x40801D compares the player's last height on the ground, +0x1950, which its ground trace saves as
/// it first finds nothing below, +0x2243C63B, with the player's height now);</item>
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
    /// <summary>Whether a player's move is the jump attack (its strike, <c>*_fall_*_out</c>), whatever the weapon.</summary>
    internal static bool IsJumpAttack(string clip) => clip.Contains("_fall_", StringComparison.Ordinal) && clip.EndsWith("_out", StringComparison.Ordinal);
    Random random = new(seed);
    readonly Queue<double> hits = new();

    internal OrcStaggerData Data => data;

    /// <summary>A blow's chance to knock the orc down: its <c>chancetofall</c>, plus <c>WoundedFallChance</c> wounded; a jump attack's only after the drop.</summary>
    /// <param name="jumpDrop">For a jump attack, how far the player has dropped (Source units); null for any other move.</param>
    internal static float FallChance(AttackEvent blow, bool wounded, float? jumpDrop, OrcStaggerData data) =>
        jumpDrop is float drop && drop < data.JumpDropToFall ? 0 : blow.ChanceToFall + (wounded ? data.WoundedFallChance : 0);

    internal void Reset(bool reseed = false)
    {
        if (reseed) random = new Random(Seed);
        hits.Clear();
    }

    /// <param name="now">The fight's time, seconds.</param>
    /// <param name="staff">The blow is a staff's.</param>
    /// <param name="stunMultiplier">The weapon's <c>stunmultiplicator</c> (1 for every recorded weapon, 2 for axes).</param>
    /// <param name="jumpDrop">For a jump attack, how far the player is below where they left the ground, Source units; null for any other move.</param>
    internal Stagger Decide(AttackEvent? blow, double now, bool wounded, bool staff, float stunMultiplier, float? jumpDrop = null)
    {
        hits.Enqueue(now);
        while (hits.Count > Math.Max(1, data.HitsToStun)) hits.Dequeue();
        if (blow is null) return Stagger.None;
        float fall = FallChance(blow, wounded, jumpDrop, data);
        if (fall > 0 && random.NextSingle() < fall) return Stagger.KnockedDown;
        if (staff) return Stagger.Stunned;
        float stun = blow.ChanceToStun * stunMultiplier + (wounded ? data.WoundedStunChance : 0);
        bool enough = hits.Count >= Math.Max(1, data.HitsToStun) && now - hits.Peek() <= data.StunWindow;
        return enough && stun > 0 && random.NextSingle() < stun ? Stagger.Stunned : Stagger.None;
    }
}
