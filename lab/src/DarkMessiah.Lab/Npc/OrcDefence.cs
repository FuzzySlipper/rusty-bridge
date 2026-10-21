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

    internal void Reset()
    {
        random = new Random(Seed);
        lastDodge = double.NegativeInfinity;
    }

    /// <param name="now">The fight's time, seconds.</param>
    internal Defence Decide(IncomingSwing swing, double now)
    {
        if (swing == IncomingSwing.None) return Defence.None;
        if (Forced is { } forced) return forced;
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
