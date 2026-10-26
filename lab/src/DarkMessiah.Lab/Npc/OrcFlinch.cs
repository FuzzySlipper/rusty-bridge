using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;
using Rusty.Engine;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The procedural hit reaction's numbers (#10382; <c>combat.json</c>'s <c>orc.flinch</c>), fitted to the donor's recorded
/// blows by <c>FlinchCalibration.Fit</c>. A blow carries momentum, from its move, not its damage; the torso is a damped
/// spring that it leans; past <see cref="Balance"/> the orc loses its footing: its action is cut short, it slides and
/// turns, and it is busy until the lean is back under <see cref="Recover"/> of its balance, less the harder it was hurt
/// (<see cref="Pain"/>). The recordings split the two: whether a blow cut the donor's orc short went by the move (power
/// attacks 151 of 154, kicks 63 of 79, fast attacks rarely, whatever their damage), how long it was out by the damage
/// (a medium hit's 0.6 s to 9 damage, a long one's 1.1 s from 12).
/// </summary>
/// <param name="On">The orc reacts this way, in place of the authored clips (<see cref="OrcReactions"/>).</param>
/// <param name="SwingMomentum">A swing's momentum at its <c>impactcoeff</c> 1: the lean's starting rate, radians a second.</param>
/// <param name="KickMomentum">A kick's.</param>
/// <param name="PushBoost">How much more a swing that pushes carries (<c>alwayspush</c>, or <c>push</c> on a critical).</param>
/// <param name="Pain">How much longer a harder blow keeps it off its footing: it acts again once its lean is under
/// <paramref name="Recover"/> of its balance times (10 / damage) to this power.</param>
/// <param name="Frequency">The torso spring's natural frequency, radians a second.</param>
/// <param name="Damping">Its damping ratio (1 is critical).</param>
/// <param name="Balance">The lean, radians, past which it loses its footing.</param>
/// <param name="Recover">The share of <paramref name="Balance"/> it has to be back under to act again.</param>
/// <param name="StunnedBalance">The share of its balance it keeps stunned.</param>
/// <param name="Slide">How fast it slides off its feet, metres a second for each radian a second of lean.</param>
/// <param name="Friction">How fast the slide slows, metres a second, a second.</param>
/// <param name="Twist">How far a swing across it turns it, degrees for each radian a second of lean (its sign: the swing's way).</param>
internal sealed record FlinchTuning(bool On, float SwingMomentum, float KickMomentum, float PushBoost, float Pain,
    float Frequency, float Damping, float Balance, float Recover, float StunnedBalance, float Slide, float Friction, float Twist)
{
    /// <summary>How long a lean is followed, seconds: longer than any recorded reaction (1.5 s).</summary>
    internal const float Horizon = 3;
    internal const float Step = 1 / 120f;
}

/// <summary>What the procedural reaction makes of one blow, as the recordings measure the donor's.</summary>
/// <param name="Interrupted">Its footing lost: its action cut short, as a hit or push in its place cuts the donor's.</param>
/// <param name="Busy">Seconds until it may act again (0 when it kept its footing).</param>
/// <param name="Moved">How far it slides, metres.</param>
/// <param name="Turned">How far it turns, degrees, positive to its left.</param>
/// <param name="Peak">The lean's peak, radians.</param>
internal readonly record struct FlinchOutcome(bool Interrupted, float Busy, float Moved, float Turned, float Peak);

/// <summary>
/// The procedural hit reaction (#10382): no reaction clip. A blow leans the orc's torso along the way its impact travels
/// (the move's <c>impactdirX</c>/<c>Y</c> turned by the attacker's facing, as <see cref="OrcReactions.Side"/> reads it,
/// but kept as an angle, not a quarter), higher hits leaning it more, drawn as turns of its spine, neck and head over
/// whatever clip it plays. One past its balance cuts its action short, slides it and turns it.
/// </summary>
internal sealed class OrcFlinch
{
    /// <summary>The torso's joints, from the hips up, and each one's share of the lean.</summary>
    static readonly (string Joint, float Share)[] Chain =
        [("SK_Spine_1", .15f), ("SK_Spine_2", .2f), ("SK_Spine_3", .2f), ("SK_Spine_4", .2f), ("SK_Neck", .1f), ("SK_Head", .15f)];

    /// <summary>A hit group's height on the orc, Source units above its feet (Source's groups: 1 head, 2 chest, 3 stomach, 4–5 arms, 6–7 legs).</summary>
    internal static float GroupHeight(int? group) => group switch { 1 => 68, 2 => 52, 3 => 40, 4 or 5 => 50, 6 or 7 => 20, _ => 45 };

    /// <summary>How much a hit at a height leans the torso: a chest hit 1, the head's 1.3, the legs' 0.4.</summary>
    internal static float Leverage(float height) => Math.Clamp(height / 52, .2f, 1.5f);

    /// <summary>
    /// The blow's momentum, radians a second of lean at a chest hit: a kick's, or a swing's from its <c>impactcoeff</c>
    /// and its push flags.
    /// </summary>
    internal static float Momentum(FlinchTuning t, ReactionBlow blow)
    {
        if (blow.Kick) return t.KickMomentum;
        var e = blow.Event;
        bool pushes = (e?.AlwaysPush ?? 0) > 0 || (e?.Push ?? 0) > 0 && blow.Critical;
        return t.SwingMomentum * (e?.ImpactCoeff ?? 1) * (pushes ? 1 + t.PushBoost : 1);
    }

    /// <summary>The lean it has to be back under to act again, radians: less the harder the blow hurt it.</summary>
    internal static float Settled(FlinchTuning t, ReactionBlow blow, float balance) =>
        t.Recover * balance * MathF.Pow(Math.Clamp(10 / MathF.Max(blow.Damage, 1e-3f), .1f, 10), t.Pain);

    /// <summary>The way the impact travels in the orc's frame, degrees from its facing, positive to its left (a kick's straight on).</summary>
    internal static float Travel(ReactionBlow blow)
    {
        float x = blow.Kick ? 1 : blow.Event?.ImpactX ?? 0, y = blow.Kick ? 0 : blow.Event?.ImpactY ?? 0;
        if (x == 0 && y == 0) x = 1;
        return blow.AttackerFacing + MathF.Atan2(y, x) * 180 / MathF.PI;
    }

    /// <summary>
    /// How much of the impact runs across the orc rather than into it: a swing to the attacker's left (<c>impactdirY</c>
    /// &gt; 0) turns it one way, to the right the other; a straight thrust or a kick not at all.
    /// </summary>
    internal static float Across(ReactionBlow blow)
    {
        if (blow.Kick) return 0;
        float x = blow.Event?.ImpactX ?? 0, y = blow.Event?.ImpactY ?? 0;
        float length = MathF.Sqrt(x * x + y * y);
        return length > 1e-4f ? y / length : 0;
    }

    /// <summary>The lean's starting rate, radians a second: its momentum by where it struck.</summary>
    internal static float Kick(FlinchTuning t, ReactionBlow blow) =>
        Momentum(t, blow) * Leverage(blow.HitGroup is int g ? GroupHeight(g) : GroupHeight(null));

    /// <summary>What the blow makes of the orc, worked out ahead: the spring stepped to its horizon.</summary>
    internal static FlinchOutcome Predict(FlinchTuning t, ReactionBlow blow, bool stunned, float lean = 0, float rate = 0)
    {
        float v0 = Kick(t, blow);
        float balance = t.Balance * (stunned ? t.StunnedBalance : 1);
        // Starting from a lean already there, in the blow's direction (the lab's orc mid-flinch; a recorded one stands).
        float theta = lean, omega = rate + v0, peak = MathF.Abs(theta), busy = 0, settled = Settled(t, blow, balance);
        for (float at = FlinchTuning.Step; at <= FlinchTuning.Horizon; at += FlinchTuning.Step)
        {
            (theta, omega) = Spring(t, theta, omega, FlinchTuning.Step);
            peak = MathF.Max(peak, MathF.Abs(theta));
            if (MathF.Abs(theta) >= settled) busy = at;
            // Its energy can't carry it past the settled lean again (a damped spring's only falls): nothing more to see.
            else if (theta * theta + omega * omega / (t.Frequency * t.Frequency) < settled * settled) break;
        }
        if (peak < balance) return new(false, 0, 0, 0, peak);
        float slide = t.Slide * v0;
        float moved = t.Friction > 0 ? slide * slide / (2 * t.Friction) : 0;
        return new(true, busy, moved, t.Twist * v0 * Across(blow), peak);
    }

    /// <summary>One step of the damped spring (semi-implicit Euler).</summary>
    static (float Theta, float Omega) Spring(FlinchTuning t, float theta, float omega, float seconds)
    {
        float w = t.Frequency;
        omega += (-w * w * theta - 2 * t.Damping * w * omega) * seconds;
        theta += omega * seconds;
        return (theta, omega);
    }

    // The lean now: its size and rate, along a direction in the orc's frame (radians from its facing, to its left).
    float theta, omega, direction;
    // Its slide (metres a second, along a direction in the world) and its turn still to come (degrees).
    Vector3 slideDirection;
    float slideSpeed, turnLeft, turnRate;

    /// <summary>Seconds it is still off its footing; 0 when it stands.</summary>
    internal float Busy { get; private set; }

    /// <summary>The lean now, radians.</summary>
    internal float Lean => MathF.Abs(theta);

    /// <summary>The way it leans in the world, for an orc facing <paramref name="facing"/>: the last impact's travel.</summary>
    internal Vector3 Toward(Vector3 facing) =>
        Vector3.Transform(facing with { Y = 0 }, Quaternion.CreateFromAxisAngle(Vector3.UnitY, direction));

    /// <summary>The last blow's outcome, as predicted when it landed.</summary>
    internal FlinchOutcome Last { get; private set; }

    /// <summary>
    /// A blow lands: the lean it adds (along the impact's way; a lean the other way is taken as its size along it), and,
    /// past its balance, its action cut short (<see cref="FlinchOutcome.Interrupted"/>) with its slide and turn.
    /// </summary>
    /// <param name="facing">The orc's facing in the world, for the slide's direction.</param>
    internal FlinchOutcome Hit(FlinchTuning t, ReactionBlow blow, bool stunned, Vector3 facing)
    {
        float travel = Travel(blow) * MathF.PI / 180;
        float along = theta * MathF.Cos(travel - direction), alongRate = omega * MathF.Cos(travel - direction);
        var outcome = Predict(t, blow, stunned, along, alongRate);
        direction = travel;
        theta = along;
        omega = alongRate + Kick(t, blow);
        Last = outcome;
        if (!outcome.Interrupted) return outcome;
        Busy = MathF.Max(Busy, outcome.Busy);
        // The slide runs along the impact's way in the world: the orc's facing turned by its travel.
        slideDirection = Vector3.Transform(facing with { Y = 0 }, Quaternion.CreateFromAxisAngle(Vector3.UnitY, travel));
        if (slideDirection.LengthSquared() > 1e-6f) slideDirection = Vector3.Normalize(slideDirection);
        slideSpeed = t.Slide * Kick(t, blow);
        turnLeft = outcome.Turned;
        turnRate = outcome.Busy > 0 ? MathF.Abs(outcome.Turned) / outcome.Busy : 0;
        return outcome;
    }

    /// <summary>One step: the spring, the slide (metres moved, returned) and the turn (degrees, returned).</summary>
    internal (Vector3 Moved, float Turned) Advance(FlinchTuning t, float seconds)
    {
        for (float left = seconds; left > 1e-6f; left -= FlinchTuning.Step)
            (theta, omega) = Spring(t, theta, omega, MathF.Min(left, FlinchTuning.Step));
        Busy = MathF.Max(0, Busy - seconds);
        Vector3 moved = default;
        if (slideSpeed > 0)
        {
            float next = MathF.Max(0, slideSpeed - t.Friction * seconds);
            moved = slideDirection * (slideSpeed + next) / 2 * seconds;
            slideSpeed = next;
        }
        float turned = 0;
        if (turnLeft != 0)
        {
            turned = MathF.Sign(turnLeft) * MathF.Min(MathF.Abs(turnLeft), turnRate * seconds);
            turnLeft -= turned;
        }
        return (moved, turned);
    }

    internal void Reset()
    {
        theta = omega = direction = slideSpeed = turnLeft = turnRate = Busy = 0;
        Last = default;
    }

    /// <summary>
    /// The torso's lean as joint turns over the clip, from the pose the clip puts it in: each joint of the chain turned
    /// by its share, about the axis that tips it along the lean, in the joint's own frame.
    /// </summary>
    internal JointOverride[] Overrides(DonorModel model, JointPose[] pose, Vector3 facing)
    {
        if (MathF.Abs(theta) < 1e-4f) return [];
        var lean = Toward(facing);
        if (lean.LengthSquared() < 1e-6f) return [];
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Vector3.Normalize(lean)));
        var turns = new List<JointOverride>(Chain.Length);
        foreach (var (joint, share) in Chain)
        {
            if (!model.Joints.TryGetValue(joint, out int j) || j >= pose.Length) continue;
            var local = Vector3.Transform(axis, Quaternion.Conjugate(pose[j].World.Rotation));
            if (local.LengthSquared() < 1e-6f) continue;
            turns.Add(JointOverride.AddLocalRotation((uint)j, Quaternion.CreateFromAxisAngle(Vector3.Normalize(local), theta * share), 1));
        }
        return [.. turns];
    }
}
