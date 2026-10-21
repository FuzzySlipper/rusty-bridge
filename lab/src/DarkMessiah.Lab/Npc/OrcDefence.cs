using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>A player's swing as the orc sees it start (#10151): one it may parry, one that breaks parries, or a kick.</summary>
internal enum IncomingSwing { None, Parryable, BreaksParry, Kick }

/// <summary>
/// The numbers the orc defends by (#10151; docs/lab.md, "The orc's defence"): its <c>.qct</c>'s <c>difficulty</c> (0.6:
/// it defends on a roll over it, 40% of the swings it sees), <c>base_dodge_chance</c> (0.5),
/// <c>dodge_on_kick_init_chance</c> (0.2) and <c>dodge_on_kick_add_chance</c> (0.4), and the settings' <c>DodgeDelay</c>
/// (15 s between dodges).
/// </summary>
internal sealed record OrcDefenceData(float Difficulty, float DodgeChance, float KickDodgeChance, float KickDodgeAdd, float DodgeDelay)
{
    internal static OrcDefenceData Read(DonorContent donor, string model)
    {
        float N(string key, float shipped) => donor.Number(model, "entity_data", key) ?? shipped;
        var settings = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault()?.Block("ai") : null;
        return new OrcDefenceData(N("difficulty", .6f), N("base_dodge_chance", .5f), N("dodge_on_kick_init_chance", .2f),
            N("dodge_on_kick_add_chance", .4f), settings?.Number("DodgeDelay") ?? 15);
    }
}

/// <summary>What the orc does about a swing it sees coming.</summary>
internal enum Defence { None, Parry, Dodge }

/// <summary>
/// Whether the orc defends a swing it sees coming, standing ready, and how (#10151; server.dll's AI around +0x2A8968
/// and its dodge test at +0x2ACB30):
/// <list type="bullet">
/// <item>a swing it may parry it parries on a roll over its <c>difficulty</c> (a 0–100 roll against 60): the recorded
/// orcs parried 36% of the fast attacks they saw ready;</item>
/// <item>one that breaks parries (a power attack) it dodges on the same roll and then <c>base_dodge_chance</c>, at most
/// once every <c>DodgeDelay</c>: recorded, 12% dodged and 2% parried;</item>
/// <item>a kick it dodges on <c>dodge_on_kick_init_chance</c>, with no delay: recorded, 3 of 18 kicks on a ready orc
/// dodged. <c>dodge_on_kick_add_chance</c> (0.4 "added after every kick") isn't seen: one orc took 7 kicks ready and
/// dodged none, so the lab doesn't grow the chance.</item>
/// </list>
/// Seeded, so fights and the smoke repeat. <see cref="Forced"/> sets every swing's answer (the smoke's checks).
/// </summary>
internal sealed class OrcDefence(OrcDefenceData data, int seed = OrcDefence.Seed)
{
    internal const int Seed = 10151;
    Random random = new(seed);
    double lastDodge = double.NegativeInfinity;

    internal OrcDefenceData Data => data;

    /// <summary>When set, the orc answers every swing so (a parry, a dodge, or nothing), whatever the rules say.</summary>
    internal Defence? Forced { get; set; }

    internal void Reset(bool reseed = false)
    {
        if (reseed) random = new Random(Seed);
        lastDodge = double.NegativeInfinity;
    }

    /// <param name="now">The fight's time, seconds.</param>
    /// <param name="wounded">The orc is wounded (hurt or exhausted): it doesn't dodge. The recorded wounded orcs dodged 1
    /// of 57 power attacks and kicks, against 13–20% ready; the gate in server.dll isn't pinned down.</param>
    internal Defence Decide(IncomingSwing swing, double now, bool wounded = false)
    {
        if (swing == IncomingSwing.None) return Defence.None;
        if (Forced is { } forced) return forced;
        if (wounded && swing != IncomingSwing.Parryable) return Defence.None;
        if (swing == IncomingSwing.Kick)
        {
            bool dodges = random.NextSingle() < data.KickDodgeChance;
            if (dodges) lastDodge = now;
            return dodges ? Defence.Dodge : Defence.None;
        }
        // A roll over its difficulty: it defends.
        if (random.NextSingle() * 100 < data.Difficulty * 100) return Defence.None;
        if (swing == IncomingSwing.Parryable) return Defence.Parry;
        if (now - lastDodge < data.DodgeDelay || random.NextSingle() >= data.DodgeChance) return Defence.None;
        lastDodge = now;
        return Defence.Dodge;
    }
}

/// <summary>
/// The NPC stamina numbers (R10151-2; docs/lab.md, "The orc's defence"): its <c>.qct</c>'s <c>stamina</c> (30; its
/// <c>level_1</c> block's 50 applies only to an orc placed at that level), and the settings' <c>Stamina_Parry</c> (1),
/// <c>Stamina_Block</c> (3), <c>Stamina_Dodge</c> (4), <c>Stamina_Stun</c> (10) and <c>Stamina_Exhausted</c> (0).
/// </summary>
internal sealed record OrcStaminaData(float Stamina, float Parry, float Block, float Dodge, float Stun, float Exhausted)
{
    internal static OrcStaminaData Read(DonorContent donor, string model)
    {
        var settings = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault()?.Block("ai") : null;
        float S(string key, float shipped) => settings?.Number(key) ?? shipped;
        return new OrcStaminaData(donor.Number(model, "entity_data", "stamina") ?? 30, S("Stamina_Parry", 1), S("Stamina_Block", 3),
            S("Stamina_Dodge", 4), S("Stamina_Stun", 10), S("Stamina_Exhausted", 0));
    }
}

/// <summary>
/// An NPC's stamina as server.dll keeps it (R10151-2). It starts at the <c>.qct</c>'s <c>stamina</c> (NPC +0x2970,
/// read at +0x2BEB6E) and nothing restores it. A parry, block, dodge or stun spends its settings cost (+0x2BF250, from
/// the defence at +0x2E01C4, the dodge at +0x2DFF89 and the stun at +0x2C5C45), only while the NPC fights (its virtual
/// +0x704) and isn't yet exhausted (+0x8A8, the flag at +0x2978). Falling to <c>Stamina_Exhausted</c> exhausts it
/// (+0x2BF210): the flag server.dll's stun and knockdown tests read as wounded (<c>WoundedStunChance</c>,
/// <c>WoundedFallChance</c>) and its preventive dodge test skips. It goes on parrying and dodging: stamina gates no
/// defence choice. The recorded orcs the staff stunned again and again (each stun 10) turned to their wounded sequences
/// at full health once they had spent 38 and 44.
/// </summary>
internal sealed class OrcStamina(OrcStaminaData data)
{
    internal OrcStaminaData Data => data;
    internal float Left { get; private set; } = data.Stamina;
    internal bool Exhausted { get; private set; }

    internal void Reset()
    {
        Left = data.Stamina;
        Exhausted = false;
    }

    internal void Spend(float cost)
    {
        if (Exhausted) return;
        Left -= cost;
        if (Left > data.Exhausted) return;
        Exhausted = true;
        Left = data.Exhausted;
    }
}
