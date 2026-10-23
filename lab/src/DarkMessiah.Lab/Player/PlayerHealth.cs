using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// Dark Messiah's health numbers for the player (#10162; docs/lab.md, "Health"):
/// <list type="bullet">
/// <item>the maximum: <see cref="BaseMax"/> (50) and <see cref="PerLevel"/> (10) for each <c>extra_health</c> level, as
/// recorded (<c>l01_a-v18-health</c>: 50, 60, 70, 80 as the levels were learned; every recording without the skill 50,
/// with it at 3 80). No content file holds them: <c>player_basic.qct</c>'s <c>player_constitution_1</c>–<c>4</c> (5, 6,
/// 7, 8) don't fit;</item>
/// <item>the regeneration: <c>player_basic.qct</c>'s <c>player_metabolism</c> (0: none) a second, and
/// <c>player_metabolism_module</c> (0.25) with the <c>fast_metabolism</c> module, once a second (every recording with
/// it rose 0.25 every 66 or 67 ticks).</item>
/// </list>
/// </summary>
internal sealed record HealthRules(float Max, float Regeneration, float BaseMax = 50, float PerLevel = 10, float ModuleRegeneration = .25f, float Interval = 1)
{
    /// <summary>The shipped values, for content that doesn't have them.</summary>
    internal static readonly HealthRules Shipped = new(50, 0);

    internal static HealthRules From(DonorContent donor)
    {
        float? Skill(string key) => donor.Combat.PlayerSkillNumber(key);
        float regeneration = Skill("player_metabolism") ?? Shipped.Regeneration;
        return Shipped with
        {
            Regeneration = regeneration,
            ModuleRegeneration = Skill("player_metabolism_module") ?? Shipped.ModuleRegeneration,
        };
    }

    /// <summary>The rules for <paramref name="build"/>: its <c>extra_health</c> level raises the maximum, <c>fast_metabolism</c> regenerates.</summary>
    internal HealthRules For(CharacterBuild build) => this with
    {
        Max = BaseMax + PerLevel * build.Level("extra_health"),
        Regeneration = build.Has("fast_metabolism") ? ModuleRegeneration : Regeneration,
    };
}

/// <summary>
/// The player's health, as the recordings bear it out (docs/lab.md, "Health"): blows take it, it regains
/// <see cref="HealthRules.Regeneration"/> once each <see cref="HealthRules.Interval"/> up to the maximum, and what the
/// belt holds gives it back (<see cref="PlayerBelt"/>). A new maximum leaves it where it is.
/// </summary>
internal sealed class PlayerHealth(HealthRules rules)
{
    float clock;

    internal HealthRules Rules { get; private set; } = rules;
    internal float Value { get; private set; } = rules.Max;
    internal float Max => Rules.Max;

    /// <summary>New rules (another build's maximum); the health stays, within the new maximum.</summary>
    internal void Retune(HealthRules next)
    {
        Rules = next;
        Value = MathF.Min(Value, next.Max);
    }

    /// <summary>Full again: a new fight, or back on its feet.</summary>
    internal void Fill() => Value = Max;

    /// <summary>Held to <paramref name="value"/> (a replay starts from the donor's).</summary>
    internal void Set(float value) => Value = Math.Clamp(value, 0, Max);

    /// <summary>A blow takes <paramref name="amount"/>; true when it leaves none.</summary>
    internal bool Take(float amount)
    {
        Value = MathF.Max(0, Value - amount);
        return Value <= 0;
    }

    /// <summary>Gives back <paramref name="amount"/>, up to the maximum; what it gave.</summary>
    internal float Heal(float amount)
    {
        float before = Value;
        Value = MathF.Min(Max, Value + amount);
        return Value - before;
    }

    /// <summary>The regeneration's clock starts over: its next step a full interval from now (a replay takes the donor's phase).</summary>
    internal void Sync() => clock = 0;

    /// <summary>The regeneration's clock runs on, dead or not; it regains while alive and below the maximum.</summary>
    internal void Step(float seconds)
    {
        for (clock += seconds; clock >= Rules.Interval; clock -= Rules.Interval)
            if (Value > 0 && Rules.Regeneration > 0) Heal(Rules.Regeneration);
    }
}
