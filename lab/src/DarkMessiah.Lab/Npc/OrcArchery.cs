using System.Numerics;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// How the orc archer fights (#10169; docs/lab.md, "NPC archery"): the maps' <c>npc_orc_bow</c> (l05_a, l05_b, l07_a,
/// l09_a), its <c>rangeweapon</c> the long bow and its <c>additionalequipment</c> the cleaver. Measured over
/// <c>l01_a-v18-npc-archery</c> to <c>-6</c> (an archer woken by the player's arrow, the player 550 to 900 units off):
/// <list type="bullet">
/// <item>it runs at the player (<see cref="RunUnits"/>, 95 units/s) to <see cref="StandOff"/> (471 units, most
/// stops), then stands in <c>bow_attack</c>; past <see cref="StandFarthest"/> (559 units, the farthest it shot from
/// standing) it runs in again;</item>
/// <item>an arrow is nocked <see cref="FirstNock"/> (1.2 s) after it woke and every <see cref="Every"/> (2.16 s) after;
/// standing anew, <see cref="StandNock"/> (0.36 s) after it stood, then every 2.16 s. Each is let go
/// <see cref="LeaveStanding"/> (1.08 s) after its nock standing and <see cref="LeaveMoving"/> (1.2 s) on the move
/// (<c>layer_Bow_Attack</c> over its run); one nocked as it stopped is put away;</item>
/// <item>let go, it flies the faster the nearer the player (<see cref="SpeedAt"/>: 7311 − 6.29 units/s a unit off, 5140
/// at 345 units, 2540 at 758; 92 arrows within 3%, half within 0.2%) at where the player is: its line passed the
/// player <see cref="AimBelowEye"/> (11 units) below the eye standing (median of 68 arrows, 6 to 12; crouched ones
/// too), <see cref="AimAboveEye"/> (11.5) above on the move (27). None
/// missed a player standing, strafing or backing off, 160 to 811 units off: no <c>bow_precision</c> error and no
/// lead were seen;</item>
/// <item>after <see cref="StandingShots"/> arrows standing (10; 1 on 2 of 9 arrivals, <see cref="BriefChance"/>, which
/// first moved off <see cref="BriefStrafe"/>, 1.3 s, before standing) it moves off, looses one more on the move, moves
/// on <see cref="MoveOnSeconds"/> (1.8 s, both ways) and rushes the player (<see cref="RushUnits"/>, 190 units/s); out
/// of sight it rushes at once (the player in the wooden building);</item>
/// <item>within <c>bow_kick</c>'s <c>range</c> it kicks (the clip at its own speed: the bow has no <c>speed_attack</c>),
/// <c>drop_bow</c> <see cref="KickSeconds"/> (0.96 s, of its 1.04) after, <c>unsheathe_sword</c> <see cref="DropSeconds"/>
/// (0.36, of its 0.5) after that (all 7 recorded kicks), and fights on with the cleaver as any orc;</item>
/// <item>nearer than <see cref="SurprisedUnits"/> (server.dll's <c>mm_ai_dist_surprised</c> 314) it plays
/// <c>surprised</c> and backs off to <see cref="BackOffTo"/> (350: the recorded ones stood again 338 to 362 off).</item>
/// </list>
/// </summary>
internal sealed record ArcheryData(string Bow, float KickRange, float StandOff = 471, float RunUnits = 95, float RushUnits = 190, float FirstNock = 1.2f,
    float Every = 2.16f, float LeaveStanding = 1.08f, float LeaveMoving = 1.2f, float SpeedNear = 7311, float SpeedFall = 6.294f, float AimBelowEye = 11, float AimAboveEye = 11.5f,
    int StandingShots = 10, float BriefChance = 2f / 9, float SurprisedUnits = 314, float BackOffTo = 350, float KickSeconds = .96f, float DropSeconds = .36f, float BriefStrafe = 1.3f, float MoveOnSeconds = 1.8f, float StandNock = .36f,
    float StandFarthest = 559)
{
    internal const string LongBow = "models/items/weapons/bow_long/bow_long.mdl";
    internal const string Kick = "bow_kick", Shoot = "bow_attack", ShootLayer = "layer_Bow_Attack", Drop = "drop_bow", Unsheathe = "unsheathe_sword";

    /// <summary>Its arrow's speed at <paramref name="units"/> from the player (Source units/s), not under 2000 past the measured 758.</summary>
    internal float SpeedAt(float units) => MathF.Max(2000, SpeedNear - SpeedFall * units);

    /// <summary>The archer's numbers: its kick's reach from the orc's <c>.qct</c> (<c>bow_kick</c> <c>range</c> 60), the rest measured.</summary>
    internal static ArcheryData For(DonorContent donor, string bow) =>
        new(bow, donor.Combat.Character(Orc.Model)?.Attacks.GetValueOrDefault(Kick)?.Events.FirstOrDefault()?.Range ?? 60);
}

/// <summary>An arrow the archer let go this step: standing or on the move (its aim's height).</summary>
internal readonly record struct OrcShot(bool Standing);

internal enum ArcheryPhase { Approach, Strafe, Stand, BackOff, Reposition, Rush, Kick, Drop, Unsheathe, Blade }

internal sealed partial class Orc
{
    ArcheryData? archery;
    ArcheryPhase phase = ArcheryPhase.Blade;
    float sinceAware, nextNock, nocked = float.NaN, phaseAge;
    bool nockedStanding;
    int standingShots, standingTarget;

    /// <summary>The archer's numbers, or null for the swordsman (<c>combat.json</c> <c>orc.bow</c>).</summary>
    internal ArcheryData? Archery => archery;
    /// <summary>Where its fight with the bow stands (<see cref="ArcheryPhase.Blade"/> once it fights with the cleaver, or never had a bow).</summary>
    internal ArcheryPhase Phase => phase;
    /// <summary>The bow in its hand: its fight at range, the kick and the drop.</summary>
    internal bool HoldsBow => archery is not null && phase is not (ArcheryPhase.Unsheathe or ArcheryPhase.Blade);
    /// <summary>How many arrows it stands for, in place of its roll (the smoke's two ways).</summary>
    internal int? FixedStandingShots { get; set; }
    /// <summary>The arrows it let go this step.</summary>
    internal List<OrcShot> Loosed { get; } = [];
    /// <summary>Its arrows let go, and those standing, this fight.</summary>
    internal (int Loosed, int Standing) Shots { get; private set; }
    /// <summary>The weapon it strikes with and its model: the bow until it draws the cleaver; its kind's (the necromancer's hook, #10357).</summary>
    internal (string Weapon, string Model) Weapon => HoldsBow ? ("weapon_mm_bow_long", archery!.Bow) : (Kind.WeaponClass, Kind.Weapon);

    void ResetArchery()
    {
        archery = tuning.Bow is { } bow ? ArcheryData.For(donor, bow) : null;
        phase = archery is null ? ArcheryPhase.Blade : ArcheryPhase.Approach;
        sinceAware = 0;
        nextNock = archery?.FirstNock ?? 0;
        nocked = float.NaN;
        standingShots = 0;
        standingTarget = 0;
        Shots = default;
        holdsCleaver = archery is null;
    }

    /// <summary>
    /// The archer's step while it fights with the bow (true when it took the step): running in, standing to shoot,
    /// backing off, moving off and rushing in, kicking, then the cleaver. Its reactions (a hit, a stun, a fall) play as
    /// any orc's, and it goes on after.
    /// </summary>
    /// <param name="clearShot">Nothing of the arena between its aim and the player's eye.</param>
    bool StepArchery(float seconds, float units, Vector3 toPlayer, bool clearShot)
    {
        // Down, it's the orc's: it lies until it respawns (R10169-3).
        if (archery is not { } a || phase == ArcheryPhase.Blade || !perception.Aware || !Alive) return false;
        // Its reactions (a hit, a stun, a fall, a push) are the orc's too: they play out, and it goes on after.
        bool free = State is OrcState.Ready or OrcState.Ranged or OrcState.Approach;
        // The kick (its strike as any attack's), the drop and the draw, each on at the donor's time.
        switch (phase)
        {
            case ArcheryPhase.Kick:
                if (State == OrcState.Attack && Clip == ArcheryData.Kick) { if ((phaseAge += seconds) < a.KickSeconds) return false; }
                else if (!free) return false;
                Play(OrcState.Alarmed, ArcheryData.Drop);
                (phase, phaseAge) = (ArcheryPhase.Drop, 0);
                return true;
            case ArcheryPhase.Drop:
                if (State == OrcState.Alarmed && Clip == ArcheryData.Drop) { if ((phaseAge += seconds) < a.DropSeconds) return true; }
                else if (!free) return false;
                Play(OrcState.Alarmed, ArcheryData.Unsheathe);
                (phase, phaseAge, holdsCleaver) = (ArcheryPhase.Unsheathe, 0, true);
                return true;
            case ArcheryPhase.Unsheathe:
                if (!free) return false;
                (phase, Layer) = (ArcheryPhase.Blade, null);
                return false;
        }
        if (!free) return false;
        sinceAware += seconds;
        var away = toPlayer.LengthSquared() > 1e-6f ? -Vector3.Normalize(toPlayer) : -Facing;
        float metres = SourceSpace.MetresPerUnit * seconds;
        if (!clearShot && phase is ArcheryPhase.Approach or ArcheryPhase.Strafe or ArcheryPhase.Stand or ArcheryPhase.BackOff) phase = ArcheryPhase.Rush;
        if (units < a.SurprisedUnits && phase is ArcheryPhase.Approach or ArcheryPhase.Stand)
        {
            phase = ArcheryPhase.BackOff;
            nocked = float.NaN;
            Play(OrcState.Alarmed, ClipNamed("surprised") ?? "combatidle");
            return true;
        }
        bool standing = phase == ArcheryPhase.Stand;
        switch (phase)
        {
            case ArcheryPhase.Approach:
                if (units > a.StandOff) { Run(); Feet -= away * a.RunUnits * metres; }
                else Arrive();
                break;
            case ArcheryPhase.Strafe:
                Run();
                Feet += new Vector3(Facing.Z, 0, -Facing.X) * a.RunUnits * metres;
                if ((phaseAge += seconds) >= a.BriefStrafe) Stand();
                break;
            case ArcheryPhase.BackOff:
                if (units < a.BackOffTo) { Run(); Feet += away * a.RunUnits * metres; }
                else Stand();
                break;
            case ArcheryPhase.Stand:
                // The player off too far, it runs in again.
                if (units > a.StandFarthest) { phase = ArcheryPhase.Approach; Run(); break; }
                if (Clip != ArcheryData.Shoot) Play(OrcState.Ready, ArcheryData.Shoot);
                break;
            case ArcheryPhase.Reposition:
                Run();
                Feet += new Vector3(Facing.Z, 0, -Facing.X) * a.RunUnits * metres;
                // Its last arrow let go, it moves on a while before it rushes.
                if (!float.IsNaN(phaseAge) && (phaseAge += seconds) >= a.MoveOnSeconds) phase = ArcheryPhase.Rush;
                break;
            case ArcheryPhase.Rush:
                Layer = null;
                nocked = float.NaN;
                if (units > a.KickRange) { Run(); Feet -= away * a.RushUnits * metres; }
                else { Play(OrcState.Attack, ArcheryData.Kick); (phase, phaseAge) = (ArcheryPhase.Kick, 0); AttacksMade++; }
                return true;
        }
        standing = phase == ArcheryPhase.Stand;
        // Its arrows: nocked on the cadence, let go after its stance's delay; one nocked as it stopped is put away. Held
        // (a charmed archer with no one to fight, R10169-2; the smoke's still orc), it nocks none.
        if (Hold) nocked = float.NaN;
        else if (float.IsNaN(nocked))
        {
            if (sinceAware >= nextNock) { nocked = 0; nockedStanding = standing; nextNock += a.Every; }
        }
        else if (standing != nockedStanding) nocked = float.NaN;
        else if ((nocked += seconds) >= (standing ? a.LeaveStanding : a.LeaveMoving))
        {
            nocked = float.NaN;
            Loosed.Add(new OrcShot(standing));
            Shots = (Shots.Loosed + 1, Shots.Standing + (standing ? 1 : 0));
            if (standing && ++standingShots >= standingTarget) (phase, phaseAge) = (ArcheryPhase.Reposition, float.NaN);
            else if (phase == ArcheryPhase.Reposition && float.IsNaN(phaseAge)) phaseAge = 0;
        }
        // On the move, the shot plays over its run.
        Layer = !standing && !float.IsNaN(nocked) ? ArcheryData.ShootLayer : null;
        LayerCycle = Layer is null ? 0 : Math.Clamp(nocked / a.LeaveMoving, 0, 1);
        return true;

        void Run() { if (Clip != "Run" || State != OrcState.Ranged) Play(OrcState.Ranged, "Run"); }
        // On arriving, how many arrows it stands for: 10, or 1 (2 of 9 recorded arrivals), which moved off a while first.
        void Arrive()
        {
            nocked = float.NaN;
            if (standingTarget == 0) standingTarget = FixedStandingShots ?? (random.NextSingle() < a.BriefChance ? 1 : a.StandingShots);
            if (standingTarget == 1 && standingShots == 0 && phase == ArcheryPhase.Approach) { (phase, phaseAge) = (ArcheryPhase.Strafe, 0); return; }
            Stand();
        }
        // Standing anew: what it had nocked put away, its next a moment after.
        void Stand()
        {
            phase = ArcheryPhase.Stand;
            nocked = float.NaN;
            nextNock = sinceAware + a.StandNock;
            Play(OrcState.Ready, ArcheryData.Shoot);
        }
    }
}
