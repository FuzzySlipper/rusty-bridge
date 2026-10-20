using System.Text.Json.Serialization;
using DarkMessiah.Lab.Content;
using Rusty.Engine;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The lab's own combat numbers, from <c>content/combat.json</c>: what isn't in Arkane's data. The moves' clips, their
/// hit windows, the sword's damage, range and attack speed, the orc's attacks and every damage rule come from the
/// imported .qct files and clip events instead (docs/lab.md). Live content, like <c>player.json</c>.
/// </summary>
/// <param name="AttackMovementScale">How much of the movement input moves the body while a swing or kick runs.</param>
/// <param name="KickWindow">The kick's contact cycles: its clip has no hit events; recorded kicks land at cycle 0.38–0.41.</param>
/// <param name="KickRange">How far in front of the eye, metres, a kick reaches an orc.</param>
/// <param name="KickPush">How far a kick pushes the orc back, metres.</param>
/// <param name="Phases">Each move's windup, active and recovery timing against the donor's clip (1 plays it as the donor does).</param>
/// <param name="SwingArcs">Where each swing's trace points across its hit window, by first-person clip (docs/lab.md, "The
/// blade's trace"): calibrated against the donor's recorded traces. A clip not listed traces straight along the view.</param>
internal sealed record CombatTuning(float PlayerHealth, float AttackMovementScale, float[] KickWindow,
    float KickRange, float KickPush, SwordPhases Phases, OrcTuning Orc, Dictionary<string, SwingArc>? SwingArcs = null)
{
    internal const string Path = "combat.json";

    internal static CombatTuning Load(IEngineContext engine) => Validate(Authored.Read(engine, Path, CombatJson.Default.CombatTuning));

    internal static CombatTuning Validate(CombatTuning t)
    {
        Authored.Positive(Path, "playerHealth", t.PlayerHealth);
        Authored.Within(Path, "attackMovementScale", t.AttackMovementScale, 0, 1);
        Authored.Require(t.KickWindow.Length == 2 && t.KickWindow[0] >= 0 && t.KickWindow[1] <= 1 && t.KickWindow[0] < t.KickWindow[1],
            Path, "kickWindow", "must be two cycles, the first below the second, within [0, 1].");
        Authored.Positive(Path, "kickRange", t.KickRange);
        Authored.AtLeast(Path, "kickPush", t.KickPush, 0);
        foreach (var (name, p) in new[] { ("fast", t.Phases.Fast), ("power", t.Phases.Power), ("kick", t.Phases.Kick) })
        {
            Authored.Positive(Path, $"phases.{name}.windup", p.Windup);
            Authored.Positive(Path, $"phases.{name}.active", p.Active);
            Authored.Positive(Path, $"phases.{name}.recovery", p.Recovery);
        }
        foreach (var (clip, arc) in t.SwingArcs ?? [])
        {
            Authored.Require(arc.Side.Length == 2 && arc.Side.All(a => a is >= -90 and <= 90), Path, $"swingArcs.{clip}.side", "must be two angles, degrees, within ±90.");
            Authored.Require(arc.Up.Length == 2 && arc.Up.All(a => a is >= -90 and <= 90), Path, $"swingArcs.{clip}.up", "must be two angles, degrees, within ±90.");
        }
        OrcTuning o = t.Orc;
        Authored.Positive(Path, "orc.health", o.Health);
        Authored.Require(o.Spawn.Length == 3, Path, "orc.spawn", "must be [x, y, z].");
        Authored.Positive(Path, "orc.engageDistance", o.EngageDistance);
        Authored.Positive(Path, "orc.walkSpeed", o.WalkSpeed);
        Authored.AtLeast(Path, "orc.attackCooldown", o.AttackCooldown, 0);
        Authored.Within(Path, "orc.parryChance", o.ParryChance, 0, 1);
        Authored.AtLeast(Path, "orc.respawnSeconds", o.RespawnSeconds, 0);
        return t;
    }
}

/// <summary>
/// One move's timing against its donor clip, as multipliers of how long each phase plays: the windup before its hit
/// window opens (Arkane's 2186 event, or the kick's window), the active, committed part while the blade is traced,
/// and the recovery after the window closes (2190) to the clip's end. 1 is the donor's own timing.
/// </summary>
internal sealed record MovePhases(float Windup, float Active, float Recovery)
{
    internal static readonly MovePhases Donor = new(1, 1, 1);
}

/// <summary>
/// A swing's trace direction across its hit window, against the view: <paramref name="Side"/> degrees to the left (negative
/// right) and <paramref name="Up"/> degrees up, each from the window's opening to its close, linearly. A move in parts sweeps
/// each part's window the same way.
/// </summary>
internal sealed record SwingArc(float[] Side, float[] Up)
{
    internal (float Side, float Up) At(float fraction) =>
        (Side[0] + (Side[1] - Side[0]) * fraction, Up[0] + (Up[1] - Up[0]) * fraction);
}

/// <summary>The phase timing of the player's three traced moves.</summary>
internal sealed record SwordPhases(MovePhases Fast, MovePhases Power, MovePhases Kick);

/// <param name="EngageDistance">The orc walks toward the player until this close, metres, then fights.</param>
/// <param name="ParryChance">How often the orc parries a swing it sees coming, when it isn't attacking.</param>
internal sealed record OrcTuning(float Health, float[] Spawn, float EngageDistance, float WalkSpeed,
    float AttackCooldown, float ParryChance, float RespawnSeconds);

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CombatTuning))]
internal sealed partial class CombatJson : JsonSerializerContext;
