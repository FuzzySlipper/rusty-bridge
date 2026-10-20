using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Player;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// Dark Messiah's stamina numbers for the player (docs/lab.md, "Stamina"):
/// <list type="bullet">
/// <item><c>player_basic.qct</c>'s <c>skills</c> block: the maximum (<c>player_stamina_base_max</c>, 9; with the
/// <c>double_sprint</c> module <c>player_stamina_double_sprint_max</c>, 12, as a v15 recording shows) and the regeneration,
/// <c>player_stamina_add_value</c> (0.06) every <c>player_stamina_add_interval</c> (0.12 s);</item>
/// <item><c>scripts/mm_game_settings.txt</c>: a kick costs <c>stamina_kick_npc</c> (3) when it lands on an NPC,
/// <c>stamina_kick_all</c> (1) otherwise;</item>
/// <item>server.dll's ConVar defaults, which no content file holds: a kick needs
/// <c>mm_player_stamina_needed_for_kick</c> (3), and sprinting costs <c>mm_player_stamina_megasprint_cost_per_second</c> (2);</item>
/// <item>measured (#10144, <c>l01_a-v16-rush-attack</c>): a sprint starts only with <see cref="NeededForSprint"/> (3). Asked
/// with 2.52 to 2.82 the body ran, and the sprint began the tick stamina reached 3.00; once going it went on below.</item>
/// </list>
/// </summary>
internal sealed record StaminaRules(float Max, float AddValue, float AddInterval, float NeededForKick, float SprintCostPerSecond, float KickNpc, float KickOther,
    float DoubleSprintMax = 12, float NeededForSprint = 3)
{
    internal const string PlayerModel = "models/npc/player/player_basic.mdl";
    /// <summary>The shipped values, for content that doesn't have them.</summary>
    internal static readonly StaminaRules Shipped = new(9, .06f, .12f, 3, 2, 3, 1);

    internal static StaminaRules From(DonorContent donor)
    {
        float? Skill(string key) => donor.Combat.PlayerSkillNumber(key);
        var settings = donor.Combat.Settings;
        return Shipped with
        {
            Max = Skill("player_stamina_base_max") ?? Shipped.Max,
            DoubleSprintMax = Skill("player_stamina_double_sprint_max") ?? Shipped.DoubleSprintMax,
            AddValue = Skill("player_stamina_add_value") ?? Shipped.AddValue,
            AddInterval = Skill("player_stamina_add_interval") ?? Shipped.AddInterval,
            KickNpc = settings.StaminaKickNpc,
            KickOther = settings.StaminaKickAll,
        };
    }

    /// <summary>The rules for <paramref name="build"/>: its <c>double_sprint</c> module raises the maximum.</summary>
    internal StaminaRules For(CharacterBuild build) => build.Has("double_sprint") ? this with { Max = DoubleSprintMax } : this;
}

/// <summary>
/// The player's stamina, as the hand-played fights bear it out (docs/lab.md, "Stamina"): it starts full, sprinting
/// while moving drains it, otherwise it regains <see cref="StaminaRules.AddValue"/> each
/// <see cref="StaminaRules.AddInterval"/>; a sprint may start only with <see cref="StaminaRules.NeededForSprint"/>, a kick
/// only with <see cref="StaminaRules.NeededForKick"/>, which it costs once it has landed or missed.
/// </summary>
internal sealed class PlayerStamina(StaminaRules rules)
{
    float regained;

    internal StaminaRules Rules { get; private set; } = rules;

    /// <summary>New rules (another build's maximum); the stamina stays, within the new maximum.</summary>
    internal void Retune(StaminaRules next)
    {
        Rules = next;
        Value = MathF.Min(Value, next.Max);
    }

    internal float Value { get; private set; } = rules.Max;
    internal bool MayKick => Value >= Rules.NeededForKick;

    /// <summary>The body sprints: asked to while moving, and started with the stamina for it.</summary>
    internal bool Sprinting { get; private set; }

    /// <param name="sprintAsked">Sprint is held while the body moves.</param>
    internal void Step(float seconds, bool sprintAsked)
    {
        Sprinting = sprintAsked && Value > 0 && (Sprinting || Value >= Rules.NeededForSprint);
        if (Sprinting)
        {
            Value = MathF.Max(0, Value - Rules.SprintCostPerSecond * seconds);
            return;
        }
        for (regained += seconds; regained >= Rules.AddInterval; regained -= Rules.AddInterval)
            Value = MathF.Min(Rules.Max, Value + Rules.AddValue);
    }

    /// <summary>A kick was decided: it landed on an NPC, or it didn't.</summary>
    internal void Kicked(bool npc) => Value = MathF.Max(0, Value - (npc ? Rules.KickNpc : Rules.KickOther));

    internal void Reset()
    {
        Sprinting = false;
        Value = Rules.Max;
        regained = 0;
    }
}
