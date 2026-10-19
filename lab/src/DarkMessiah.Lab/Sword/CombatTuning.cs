using System.Text.Json.Serialization;
using DarkMessiah.Lab.Content;
using Rusty.Engine;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The lab's own combat numbers, from <c>content/combat.json</c>: what isn't in Arkane's data. The moves' clips, their
/// hit windows, the sword's damage, range and attack speed, the orc's attacks and every damage rule come from the
/// imported .qct files and clip events instead (docs/lab.md). Live content, like <c>player.json</c>.
/// </summary>
/// <param name="PowerHoldSeconds">How long the attack button is held before its release is a power attack, not a fast one.</param>
/// <param name="AttackMovementScale">How much of the movement input moves the body while a swing or kick runs.</param>
/// <param name="KickWindow">The kick's contact cycles: its clip has no hit events; recorded kicks land at cycle 0.38–0.41.</param>
/// <param name="KickRange">How far in front of the eye, metres, a kick reaches an orc.</param>
/// <param name="KickPush">How far a kick pushes the orc back, metres.</param>
internal sealed record CombatTuning(float PlayerHealth, float PowerHoldSeconds, float AttackMovementScale, float[] KickWindow,
    float KickRange, float KickPush, OrcTuning Orc)
{
    internal const string Path = "combat.json";

    internal static CombatTuning Load(IEngineContext engine) => Validate(Authored.Read(engine, Path, CombatJson.Default.CombatTuning));

    internal static CombatTuning Validate(CombatTuning t)
    {
        Authored.Positive(Path, "playerHealth", t.PlayerHealth);
        Authored.AtLeast(Path, "powerHoldSeconds", t.PowerHoldSeconds, 0);
        Authored.Within(Path, "attackMovementScale", t.AttackMovementScale, 0, 1);
        Authored.Require(t.KickWindow.Length == 2 && t.KickWindow[0] >= 0 && t.KickWindow[1] <= 1 && t.KickWindow[0] < t.KickWindow[1],
            Path, "kickWindow", "must be two cycles, the first below the second, within [0, 1].");
        Authored.Positive(Path, "kickRange", t.KickRange);
        Authored.AtLeast(Path, "kickPush", t.KickPush, 0);
        OrcTuning o = t.Orc;
        Authored.Positive(Path, "orc.health", o.Health);
        Authored.Require(o.Spawn.Length == 3, Path, "orc.spawn", "must be [x, y, z].");
        Authored.Positive(Path, "orc.engageDistance", o.EngageDistance);
        Authored.Positive(Path, "orc.walkSpeed", o.WalkSpeed);
        Authored.Positive(Path, "orc.reach", o.Reach);
        Authored.Within(Path, "orc.frontCosine", o.FrontCosine, -1, 1);
        Authored.AtLeast(Path, "orc.attackCooldown", o.AttackCooldown, 0);
        Authored.Within(Path, "orc.parryChance", o.ParryChance, 0, 1);
        Authored.AtLeast(Path, "orc.respawnSeconds", o.RespawnSeconds, 0);
        return t;
    }
}

/// <param name="EngageDistance">The orc walks toward the player until this close, metres, then fights.</param>
/// <param name="Reach">How far, metres, an orc's blow reaches the player: an orc's blow on the player isn't traced
/// (docs/combat.md, "Contacts"); recorded blows landed from up to 2.1 m, and #10099 will measure the donor's test.</param>
/// <param name="FrontCosine">How squarely in front of the orc the player must be for its blow to land.</param>
/// <param name="ParryChance">How often the orc parries a swing it sees coming, when it isn't attacking.</param>
internal sealed record OrcTuning(float Health, float[] Spawn, float EngageDistance, float WalkSpeed, float Reach, float FrontCosine,
    float AttackCooldown, float ParryChance, float RespawnSeconds);

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CombatTuning))]
internal sealed partial class CombatJson : JsonSerializerContext;
