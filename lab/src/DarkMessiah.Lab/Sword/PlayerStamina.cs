using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Player;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// Dark Messiah's stamina numbers for the player (docs/lab.md, "Stamina"):
/// <list type="bullet">
/// <item><c>player_basic.qct</c>'s <c>skills</c> block: the maximum (<c>player_stamina_base_max</c>, 9; with the
/// <c>double_sprint</c> module <c>player_stamina_double_sprint_max</c>, 12, as a v15 recording shows) and the regeneration,
/// <c>player_stamina_add_value</c> (0.06) every <c>player_stamina_add_interval</c> (0.12 s);</item>
/// <item><c>scripts/mm_game_settings.txt</c>: a kick costs <c>stamina_kick_npc</c> (3) when it lands on an NPC; a kick
/// into the air costs nothing (#10160, <c>l01_a-v18-sprint</c>: 0 of 4 kicks there cost any). <c>stamina_kick_all</c> (1)
/// is for a kick on anything else, a prop or the world, which the lab's kick doesn't strike;</item>
/// <item>server.dll's ConVar defaults, which no content file holds: a kick needs
/// <c>mm_player_stamina_needed_for_kick</c> (3), and sprinting costs <c>mm_player_stamina_megasprint_cost_per_second</c> (2);</item>
/// <item>measured (#10144, <c>l01_a-v16-rush-attack</c>): a sprint starts only with <see cref="NeededForSprint"/> (3). Asked
/// with 2.52 to 2.82 the body ran, and the sprint began the tick stamina reached 3.00; once going it went on below.</item>
/// <item>measured (#10160, <c>l01_a-v18-sprint</c>, <c>-sprint-double</c>): regeneration runs on while sprinting, so a
/// sprint spends 1.5 a second (9 to 0 in 5.7 s); at 0 the sprint ends, the body runs and regains as ever, sprint held
/// or not.</item>
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
/// The player's stamina, as the recordings bear it out (docs/lab.md, "Stamina"): it starts full, sprinting while moving
/// drains it, and it always regains <see cref="StaminaRules.AddValue"/> each <see cref="StaminaRules.AddInterval"/>; a
/// sprint may start only with <see cref="StaminaRules.NeededForSprint"/>, a kick only with
/// <see cref="StaminaRules.NeededForKick"/>, which costs it once it lands on an NPC.
/// </summary>
internal sealed class PlayerStamina(StaminaRules rules)
{
    float regained;

    /// <summary>The game's tick, seconds (Dark Messiah runs at 66.7 a second).</summary>
    internal const float GameTick = .015f;

    /// <summary>The regeneration's real interval: the first whole game tick past <see cref="StaminaRules.AddInterval"/>.</summary>
    float Interval => (MathF.Floor(Rules.AddInterval / GameTick + 1e-3f) + 1) * GameTick;

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

    /// <summary>
    /// Whether the body sprints this step if asked: with stamina left, and, to start, the stamina to start with. The body's
    /// locomotion and the sword both take this (R10144-3), so neither sprints while the other doesn't.
    /// </summary>
    internal bool Admits(bool sprintAsked) => sprintAsked && Value > 0 && (Sprinting || Value >= Rules.NeededForSprint);

    /// <param name="sprintAsked">Sprint is held while the body moves.</param>
    internal void Step(float seconds, bool sprintAsked)
    {
        Sprinting = Admits(sprintAsked);
        if (Sprinting) Value = MathF.Max(0, Value - Rules.SprintCostPerSecond * seconds);
        // It regains while sprinting too (#10160), once more than AddInterval has passed in the game's 15 ms ticks: every
        // 9 ticks (0.135 s), 0.44 a second, as the streamed stamina rose (0.45 a second), not every 0.12 s (0.5).
        for (regained += seconds; regained >= Interval; regained -= Interval)
            Value = MathF.Min(Rules.Max, Value + Rules.AddValue);
    }

    /// <summary>
    /// The regeneration's clock starts over, its next step a full interval from now: a replay takes the donor's phase
    /// (R10160-1). The donor's clock runs on through sprints and rests alike (every 9 ticks, its rises 9 ticks apart
    /// throughout <c>l01_a-v18-sprint-double</c>), so one sync holds for the whole run.
    /// </summary>
    internal void Sync() => regained = 0;

    /// <summary>A kick was decided: it landed on an NPC, or it didn't.</summary>
    internal void Kicked(bool npc) => Value = MathF.Max(0, Value - (npc ? Rules.KickNpc : 0));

    internal void Reset()
    {
        Sprinting = false;
        Value = Rules.Max;
        regained = 0;
    }
}
