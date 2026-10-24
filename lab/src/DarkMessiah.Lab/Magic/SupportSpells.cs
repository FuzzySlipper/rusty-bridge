using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// Cure wounds (<c>spells.txt</c>'s <c>air_2</c>; #10167, docs/lab.md "Support spells"): its <c>cost</c> (10) and
/// <c>effect</c> (15). Measured (<c>l01_a-v18-support-spells</c>, <c>-2</c>): the donor casts it as it's put in hand,
/// the mana going then; a third of the effect at once (5, both casts), and the rest a point at a time at
/// <see cref="PointSeconds"/> after the cast (the same in both, within a tick and a half). It's back to the weapon after:
/// two taps later healed nothing (one press 2 s after a cast, in the first run, gave 5 more for no mana).
/// </summary>
internal sealed record CureRules(float Cost, float Effect, float AdrenalineAdd)
{
    internal const string Spell = "air_2";

    internal static readonly CureRules Shipped = new(10, 15, 0);

    /// <summary>When each point after the first third came, seconds after the cast: the recorded casts' (the first at 0.09 and 0.12).</summary>
    internal static readonly float[] PointSeconds = [.105f, .21f, .3f, .45f, .6f, .75f, .99f, 1.29f, 1.71f, 2.61f];

    /// <summary>What comes at once: a third of the effect, the rest a point at each of <see cref="PointSeconds"/>.</summary>
    internal float AtOnce => Effect - MathF.Min(PointSeconds.Length, MathF.Floor(Effect * 2 / 3));

    internal static CureRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(CureRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Effect = Spell("effect") ?? Shipped.Effect,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
        };
    }
}

/// <summary>
/// Weaken (<c>air_4</c>; #10167): its <c>cost</c> (15) at the press, and the orc it's cast at wounded for
/// <c>weaken_duration</c> (15 s), striking with its wounded attacks only. Measured (<c>l01_a-v18-support-spells</c>, <c>-2</c>):
/// it walked in <c>Hurtwalk</c>, strafed in <c>Wounded_combatmove_*</c> and struck with <c>attack_wounded_*</c> (all 7 of
/// its attacks), whose <c>HitPow</c> 0.5 makes the cleaver's 8 ask 4, up to 13.8 s after the press; its last wounded clip
/// began at 15.5 s, its own attacks from 17.1 asking 8. <c>weaken_damage_multiplicator</c> (0.5) multiplies nothing more
/// (no blow asked 2), and the player's sword on it asked what it asks of any orc (<c>weaken_reduction_factor</c> 1).
/// </summary>
internal sealed record WeakenRules(float Cost, float Seconds, float AdrenalineAdd, float RangeUnits = 2000, float ConeDegrees = 10)
{
    internal const string Spell = "air_4";
    internal const string ReadyClip = "layer_spellcasting_ready_air_4", CastClip = "layer_spellcasting_cast_air_4";

    internal static readonly WeakenRules Shipped = new(15, 15, 2);

    /// <summary>Its numbers from the donor. Its reach and width are the lab's, the lightning bolt's: the recorded orc stood 200 units off, in the view.</summary>
    internal static WeakenRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(WeakenRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Seconds = Spell("weaken_duration") ?? Shipped.Seconds,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
        };
    }
}

/// <summary>
/// Charm (<c>water_4</c>; #10167): its <c>cost</c> (15) at the press, and the orc it's cast at on the player's side for
/// its <c>effect</c> (25 s: "the duration in Magic code", its comment says). Measured (<c>l01_a-v18-support-spells-2</c>,
/// the player in <c>god</c> and seen): neither orc struck the player; the other orc ran at the charmed one 8 s after the
/// press and struck it twice (12, <c>attack_Forward</c>, and 8, <c>attack_bottom</c>: its blows on the player), the
/// charmed one fought back and landed nothing; 25.2 s after the press it stood idle again. The arms play the drain's
/// clips (<c>layer_spellcasting_*_earth_4</c>).
/// </summary>
internal sealed record CharmRules(float Cost, float Seconds, float AdrenalineAdd, float RangeUnits = 2000, float ConeDegrees = 10)
{
    internal const string Spell = "water_4";
    internal const string ReadyClip = "layer_spellcasting_ready_earth_4", CastClip = "layer_spellcasting_cast_earth_4";

    internal static readonly CharmRules Shipped = new(15, 25, 0);

    internal static CharmRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(CharmRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Seconds = Spell("effect") ?? Shipped.Seconds,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
        };
    }
}

/// <summary>A cure under way: the points still to come, by the seconds since its cast.</summary>
internal sealed class LabCure(CureRules rules)
{
    float age;
    int given;

    internal bool Done => given >= CureRules.PointSeconds.Length || given >= rules.Effect - rules.AtOnce;

    /// <summary>The points due this step.</summary>
    internal int Step(float seconds)
    {
        age += seconds;
        int due = 0;
        while (!Done && age >= CureRules.PointSeconds[given]) { given++; due++; }
        return due;
    }
}
