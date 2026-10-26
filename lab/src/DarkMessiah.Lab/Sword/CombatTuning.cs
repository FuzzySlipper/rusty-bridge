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
/// blade's trace"): calibrated against the donor's recorded traces. A shield set's clip not listed takes its sword-only
/// twin's (<see cref="ArcFor"/>); any other clip not listed traces straight along the view.</param>
internal sealed record CombatTuning(float AttackMovementScale, float[] KickWindow,
    float KickRange, float KickPush, SwordPhases Phases, OrcTuning Orc, Dictionary<string, SwingArc>? SwingArcs = null)
{
    internal const string Path = "combat.json";

    /// <summary>
    /// A clip's swing arc: its own, else, for the <c>sword_shield</c> set, the same move's <c>sword_noshield</c> arc. Of
    /// the 13 sweeps <c>l01_a-v17-shield</c> recorded for the shield set's fast and power attacks, 9 lie within 10° of
    /// the sword-only arcs; the bash has its own.
    /// </summary>
    internal SwingArc? ArcFor(string clip) =>
        SwingArcs?.GetValueOrDefault(clip) ?? (clip.Contains("_shield", StringComparison.Ordinal) && !clip.Contains("_noshield", StringComparison.Ordinal)
            ? SwingArcs?.GetValueOrDefault(clip.Replace("_shield", "_noshield", StringComparison.Ordinal)) : null);

    internal static CombatTuning Load(IEngineContext engine) => Validate(Authored.Read(engine, Path, CombatJson.Default.CombatTuning));

    internal static CombatTuning Validate(CombatTuning t)
    {
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
        Authored.Require(o.Spawn.Length == 3, Path, "orc.spawn", "must be [x, y, z].");
        Authored.AtLeast(Path, "orc.respawnSeconds", o.RespawnSeconds, 0);
        Authored.Positive(Path, "orc.walkSpeed", o.WalkSpeed);
        foreach (var (attack, window) in o.AttackLandings)
            Authored.Require(window.Length == 2 && window[0] <= window[1], Path, $"orc.attackLandings.{attack}", "must be [low, high], Source units.");
        if (o.Flinch is { } f)
        {
            foreach (var (name, value) in new[] { ("swingMomentum", f.SwingMomentum), ("kickMomentum", f.KickMomentum), ("frequency", f.Frequency),
                ("damping", f.Damping), ("balance", f.Balance), ("recover", f.Recover), ("stunnedBalance", f.StunnedBalance) })
                Authored.Positive(Path, $"orc.flinch.{name}", value);
            Authored.Positive(Path, "orc.flinch.armLimit", f.ArmLimit);
            foreach (var (name, value) in new[] { ("pushBoost", f.PushBoost), ("pain", f.Pain), ("slide", f.Slide), ("friction", f.Friction),
                ("armKick", f.ArmKick), ("armInertia", f.ArmInertia) })
                Authored.AtLeast(Path, $"orc.flinch.{name}", value, 0);
        }
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

/// <summary>
/// The lab's own orc numbers: where it spawns and how long it stays down. How it defends is its .qct's (#10151,
/// <c>OrcDefenceData</c>). Its health, zones and attack rhythm come from its .qct (#10150, <c>OrcMindData</c>).
/// </summary>
/// <param name="AttackLandings">Where each attack's strike landed in the recordings (#10150): Source units off the
/// player once its travel to the strike is done, the middle half (25th to 75th percentile), fitted by <c>OrcCalibration.Landings</c>.
/// The orc chooses an attack only from where its strike lands inside this window.</param>
/// <param name="WalkSpeed">How fast it walks in, Source units a second: the donor's orcs covered a median 61 between
/// position updates while playing CombatWalk (whose root motion would be 96: Source's navigator moves an NPC slower
/// than its walk clip's feet), fitted by <c>OrcCalibration</c> (R10150-2).</param>
/// <param name="SpawnUnaware">A new orc is unaware of the player (#10154): it idles until it hears the player or is struck.</param>
/// <param name="Map">The donor map whose stealth rules the orcs follow (#10154): l01_a, the recordings' arena, is in the settings'
/// <c>nostealth</c> block, where the recorded orcs saw nothing; l05_a isn't, where they saw (R10154-2).</param>
/// <param name="SquadMate">Where a second orc of its squad stands (R10154-1), or none: it answers the first's call.</param>
/// <param name="Bow">The first orc's bow, an archer's (#10169: the maps' <c>npc_orc_bow</c>, its <c>rangeweapon</c> the long
/// bow), or none: a swordsman.</param>
/// <param name="Flinch">The procedural hit reaction's numbers (#10382, <see cref="Npc.FlinchTuning"/>), fitted to the recorded
/// blows; with <c>on</c> it reacts this way in place of the authored clips. None: the authored clips.</param>
internal sealed record OrcTuning(float[] Spawn, float RespawnSeconds, float WalkSpeed, Dictionary<string, float[]> AttackLandings, bool SpawnUnaware = false,
    string Map = "l01_a", float[]? SquadMate = null, string? Bow = null, Npc.FlinchTuning? Flinch = null);

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CombatTuning))]
internal sealed partial class CombatJson : JsonSerializerContext;
