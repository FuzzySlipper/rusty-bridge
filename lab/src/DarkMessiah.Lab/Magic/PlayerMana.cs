using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// Dark Messiah's mana numbers for the player (#10164; docs/lab.md, "Mana"), from <c>player_basic.qct</c>'s stat block:
/// <list type="bullet">
/// <item>the maximum: <see cref="PerIntelligence"/> (10) times <c>player_intelligence_{extra_mana level + 1}</c> (2, 4, 7,
/// 10): 20, 40, 70, 100. The factor is fitted: every recording with <c>extra_mana</c> 0 had 20, with it at 3 100;</item>
/// <item>the regeneration, once each <see cref="Interval"/>: <c>player_mana_recovery_module</c> (0.7) with the
/// <c>mana_recovery</c> module (<c>mana_regeneration</c>'s), else <c>player_mana_recovery_base</c> (0.1). Every recording
/// with the module rose 0.7 every 66 or 68 ticks; none without it showed a rise (each sat full).</item>
/// </list>
/// </summary>
internal sealed record ManaRules(float Max, float Recovery, float BaseRecovery = .1f, float ModuleRecovery = .7f, float Interval = 1,
    float PerIntelligence = 10, IReadOnlyList<float>? Intelligence = null)
{
    /// <summary>The shipped values, for content that doesn't have them.</summary>
    internal static readonly ManaRules Shipped = new(20, .1f, Intelligence: [0, 2, 4, 7, 10]);

    internal static ManaRules From(DonorContent donor)
    {
        float? Skill(string key) => donor.Combat.PlayerSkillNumber(key);
        var shipped = Shipped.Intelligence!;
        return Shipped with
        {
            BaseRecovery = Skill("player_mana_recovery_base") ?? Shipped.BaseRecovery,
            ModuleRecovery = Skill("player_mana_recovery_module") ?? Shipped.ModuleRecovery,
            Intelligence = Enumerable.Range(0, shipped.Count).Select(i => Skill($"player_intelligence_{i}") ?? shipped[i]).ToList(),
        };
    }

    /// <summary>The rules for <paramref name="build"/>: its <c>extra_mana</c> level sets the maximum, <c>mana_recovery</c> the regeneration.</summary>
    internal ManaRules For(CharacterBuild build) => this with
    {
        Max = PerIntelligence * Intelligence![Math.Clamp(build.Level("extra_mana") + 1, 0, Intelligence.Count - 1)],
        Recovery = build.Has("mana_recovery") ? ModuleRecovery : BaseRecovery,
    };
}

/// <summary>
/// The player's mana (#10164): spells spend it, it regains <see cref="ManaRules.Recovery"/> once each
/// <see cref="ManaRules.Interval"/> up to the maximum, and a mana potion gives it back. A new maximum leaves it where it is.
/// </summary>
internal sealed class PlayerMana(ManaRules rules)
{
    float clock;

    internal ManaRules Rules { get; private set; } = rules;
    internal float Value { get; private set; } = rules.Max;
    internal float Max => Rules.Max;

    /// <summary>New rules (another build's maximum); the mana stays, within the new maximum.</summary>
    internal void Retune(ManaRules next)
    {
        Rules = next;
        Value = MathF.Min(Value, next.Max);
    }

    internal void Fill() => Value = Max;

    /// <summary>Held to <paramref name="value"/> (a replay starts from the donor's).</summary>
    internal void Set(float value) => Value = Math.Clamp(value, 0, Max);

    /// <summary>Spends <paramref name="amount"/> if there's that much; false, and nothing spent, if not.</summary>
    internal bool Spend(float amount)
    {
        if (amount > Value) return false;
        Value -= amount;
        return true;
    }

    /// <summary>Gives back <paramref name="amount"/>, up to the maximum; what it gave.</summary>
    internal float Restore(float amount)
    {
        float before = Value;
        Value = MathF.Min(Max, Value + amount);
        return Value - before;
    }

    /// <summary>The regeneration's clock starts over (a replay takes the donor's phase).</summary>
    internal void Sync() => clock = 0;

    internal void Step(float seconds)
    {
        for (clock += seconds; clock >= Rules.Interval; clock -= Rules.Interval)
            if (Rules.Recovery > 0) Restore(Rules.Recovery);
    }
}
