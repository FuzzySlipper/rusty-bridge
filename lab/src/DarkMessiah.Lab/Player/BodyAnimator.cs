using System.Numerics;
using DarkMessiah.Shared;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// One step of what the player's body is told (#10394), in Source's terms: degrees (yaw counter-clockwise from +X, pitch
/// down positive) and units. <paramref name="Velocity"/> is the body's horizontal velocity; <paramref name="Moving"/> is
/// movement asked for (a movement key held); <paramref name="KickStarted"/> is the step a kick begins.
/// </summary>
internal readonly record struct BodyInput(float Seconds, Vector2 Velocity, float ViewYaw, float ViewPitch, bool Moving, bool Sprinting,
    bool Crouched, bool Grounded, bool KickStarted);

/// <summary>An overlay the body plays over its main clip: a clip at a cycle and weight.</summary>
internal readonly record struct BodyLayer(string Clip, float Cycle, float Weight);

/// <summary>
/// What Dark Messiah's server plays on the player's body, <c>player_basic</c> (#10394; docs/lab.md, "The player's
/// body"), by the rules the recordings show (<c>l01_a-v21-body-look</c>, <c>-movement</c>, the v12 and v14 arena fights):
/// <list type="bullet">
/// <item>Moving (a movement key held): <c>sprint</c> while sprinting, else <c>run</c>, or <c>Walk</c> below
/// <see cref="WalkSpeed"/> (backing and the speed key never pass it); <c>Crouch</c> crouched.
/// Started from standing it begins at <see cref="GaitStart"/>; the switches between gaits keep the cycle.</item>
/// <item>Stopped (no key held, however fast it still slides): an idle by where the gait's cycle was (<see cref="IdleAfter"/>),
/// <c>Crouch_Idle</c> crouched.</item>
/// <item>A kick plays <c>attack_kick_low</c> when the view is more than <see cref="LowKickPitch"/> down
/// (<c>mm_player_minangle_to_medium_kick</c>, as server.dll+0x44A145 compares it), else <c>attack_kick_medium</c>, to
/// <see cref="KickEnd"/>.</item>
/// <item>Facing: moving, the body faces the view; standing, it stays put until the view has been far enough to one side
/// long enough (<see cref="TurnAngle"/> and the rest, server.dll's cvars), then turns to it in <see cref="TurnSeconds"/>
/// playing <c>Player_Turn_45_Right</c> (view to its left) or <c>_Left</c>.</item>
/// <item><c>move_yaw</c> is the direction of travel from the body's facing, kept when it stops.</item>
/// <item>Off the ground <c>jump_idle</c> plays over it; landing, <c>jump_out_idle</c>, <c>_walk</c>, <c>_run</c> or
/// <c>_sprint</c> by the speed (as the head's, <see cref="Sword.HeadMotion"/>) plays once over it as <c>jump_idle</c> fades in
/// <see cref="JumpFade"/>.</item>
/// </list>
/// Every clip plays at its own rate (Source's playback rate 1), the turn at <see cref="TurnSeconds"/>.
/// </summary>
internal sealed class BodyAnimator(ModelInfo model)
{
    /// <summary>The speed, units/s, under which a moving body walks: its switches to <c>run</c> come at a median 78 (over two ticks, a tick behind).</summary>
    internal const float WalkSpeed = 75;
    /// <summary>How long <c>idle_middle</c> holds after a walk before it shifts onto the left foot (<c>idle_middle_to_leftfoot</c>): 2.03 s, every time.</summary>
    internal const float MiddleHold = 2.03f;
    /// <summary>Where a gait started from standing begins: the recordings' starts are at 0.78 a tick in, whatever the idle's cycle.</summary>
    internal const float GaitStart = .75f;
    /// <summary>Where a kick ends and the body stands: the recordings' kicks give way at 0.76 to 0.77 of the clip.</summary>
    internal const float KickEnd = .76f;
    /// <summary>The view pitch (degrees down) past which a kick is the low one.</summary>
    internal const float LowKickPitch = 50;
    /// <summary>The <c>body_yaw</c> pose parameter's range, degrees: how far the view turns from a standing body before it turns.</summary>
    internal const float BodyYawRange = 47.7f;
    /// <summary>
    /// When a standing body turns to the view, as server.dll's cvars have it: past <see cref="TurnAngle"/>
    /// (<c>mm_player_angle_for_turn</c>) for <see cref="TurnDelay"/> (<c>mm_player_time_to_turn</c>); past
    /// <see cref="FastTurnAngle"/> (<c>mm_player_max_angle_no_turn</c>) for <see cref="FastTurnDelay"/>
    /// (<c>mm_player_time_to_turn_fast</c>); and looking more than <see cref="SpecialTurnPitch"/> down
    /// (<c>mm_player_pitch_for_special_turn</c>) past <see cref="SpecialTurnAngle"/> (<c>mm_player_angle_for_special_turn</c>).
    /// </summary>
    internal const float TurnAngle = 90, TurnDelay = .5f, FastTurnAngle = 100, FastTurnDelay = .1f, SpecialTurnPitch = 60, SpecialTurnAngle = 45;
    /// <summary>A turn on the spot's length: <c>Player_Turn_45_*</c> from 0.09 to 0.99 in 30 ticks.</summary>
    internal const float TurnSeconds = .5f;
    /// <summary>How long <c>jump_idle</c> takes to fade in off the ground, and out on landing.</summary>
    internal const float JumpFadeIn = .1f, JumpFade = .3f;

    internal string Clip { get; private set; } = "idle_leftfoot";
    internal float Cycle { get; private set; }
    /// <summary>The body's facing, degrees.</summary>
    internal float Yaw { get; private set; }
    /// <summary>The <c>move_yaw</c> pose parameter, degrees.</summary>
    internal float MoveYaw { get; private set; }
    /// <summary>The <c>body_yaw</c> pose parameter, degrees: the view's turn from the body, within its range.</summary>
    internal float BodyYaw { get; private set; }
    /// <summary>The layers over the main clip: <c>jump_idle</c> and the landing.</summary>
    internal IReadOnlyList<BodyLayer> Layers => [.. jump is { Weight: > 0 } j ? [j] : Array.Empty<BodyLayer>(), .. landing is { } l ? [l] : Array.Empty<BodyLayer>()];

    BodyLayer? jump, landing;
    string lastIdle = "idle_leftfoot";
    float pastRange, turnFrom;
    bool wasGrounded = true;
    float lastSpeed, idleFor;
    bool walked;

    /// <summary>Starts it standing in <paramref name="clip"/> at <paramref name="cycle"/> (<c>idle_leftfoot</c> at 0), facing <paramref name="yaw"/>.</summary>
    internal void Reset(float yaw, string clip = "idle_leftfoot", float cycle = 0)
    {
        (Clip, Cycle, Yaw, MoveYaw, BodyYaw) = (clip, cycle, yaw, 0, 0);
        (jump, landing, pastRange, wasGrounded, lastIdle, idleFor, walked) = (null, null, 0, true, "idle_leftfoot", 0, false);
    }

    /// <summary>Put facing <paramref name="yaw"/> at once (a teleport, a console <c>setang</c>), playing what it plays.</summary>
    internal void Face(float yaw) => (Yaw, pastRange) = (yaw, 0);

    internal void Step(BodyInput input)
    {
        float s = input.Seconds;
        float speed = input.Velocity.Length();
        bool kicking = Clip.StartsWith("attack_kick_", StringComparison.Ordinal);
        bool turning = Clip.StartsWith("Player_Turn_", StringComparison.Ordinal);
        string? next = null;
        if (input.KickStarted && input.Grounded) next = input.ViewPitch > LowKickPitch ? "attack_kick_low" : "attack_kick_medium";
        else if (kicking && Cycle < KickEnd) next = Clip;
        else if (input.Moving) next = input.Crouched ? "Crouch" : input.Sprinting ? "sprint" : speed < WalkSpeed ? "Walk" : "run";
        else if (input.Crouched) next = "Crouch_Idle";
        else if (turning && Cycle < 1) next = Clip;
        // Let go, a gait plays on to its next stop point (StopsOf), where it plants its feet.
        bool stopping = next is null && Clip is "Walk" or "run" or "sprint";
        if (stopping) next = Clip;

        if (next is null)
        {
            // Standing: the idle it had, back to it after a turn on the spot, idle_leftfoot after anything else.
            next = Clip.StartsWith("idle", StringComparison.Ordinal) ? Clip : turning ? lastIdle : "idle_leftfoot";
            if (!Clip.StartsWith("idle", StringComparison.Ordinal) && !turning) (walked, idleFor) = (false, 0);
            idleFor += s;
            // After a walk, idle_middle shifts onto the left foot.
            if (next == "idle_middle" && walked && idleFor >= MiddleHold) (next, walked) = ("idle_middle_to_leftfoot", false);
            else if (next == "idle_middle_to_leftfoot" && Cycle >= 1) next = "idle_leftfoot";
            // Turning on the spot once the view has stayed far enough to one side long enough.
            float off = Wrap(input.ViewYaw - Yaw), away = MathF.Abs(off);
            bool past = away > TurnAngle || input.ViewPitch > SpecialTurnPitch && away > SpecialTurnAngle;
            pastRange = past ? pastRange + s : 0;
            if (past && pastRange >= (away > FastTurnAngle ? FastTurnDelay : TurnDelay))
            {
                next = off > 0 ? "Player_Turn_45_Right" : "Player_Turn_45_Left";
                turnFrom = Yaw;
                pastRange = 0;
            }
        }
        else pastRange = 0;

        // A gait takes up the cycle of the gait before it, and from standing starts at GaitStart; anything else starts its clip.
        bool restart = next != Clip && !(Gait(next) && Gait(Clip));
        if (next.StartsWith("attack_kick_", StringComparison.Ordinal) && input.KickStarted) restart = true;
        float was = Cycle;
        if (restart) Cycle = Gait(next) ? GaitStart : 0;
        else Cycle += next.StartsWith("Player_Turn_", StringComparison.Ordinal) ? s / TurnSeconds : Rate(next) * s;
        if (model.Clips.TryGetValue(next, out var clip) && clip.Looping) Cycle -= MathF.Floor(Cycle);
        else Cycle = MathF.Min(Cycle, 1);
        Clip = next;
        if (stopping && StopsOf(Clip).FirstOrDefault(p => Crossed(was, Cycle, p.At)).Idle is { } idle)
            (Clip, Cycle, walked, idleFor) = (idle, 0, Clip == "Walk", 0);
        if (Clip is "idle_leftfoot" or "idle_rightfoot" or "idle_middle") lastIdle = Clip;

        // Facing.
        if (Clip.StartsWith("Player_Turn_", StringComparison.Ordinal)) Yaw = Wrap(turnFrom + Wrap(input.ViewYaw - turnFrom) * Cycle);
        else if (!Clip.StartsWith("idle", StringComparison.Ordinal) && Clip != "Crouch_Idle") Yaw = input.ViewYaw;
        BodyYaw = Math.Clamp(Wrap(input.ViewYaw - Yaw), -BodyYawRange, BodyYawRange);
        if (speed > 1) MoveYaw = Wrap(MathF.Atan2(input.Velocity.Y, input.Velocity.X) * 180 / MathF.PI - Yaw);

        // Off the ground and landing.
        if (!input.Grounded)
        {
            var j = jump ?? new BodyLayer("jump_idle", 0, 0);
            jump = j with { Cycle = (j.Cycle + Rate("jump_idle") * s) % 1, Weight = MathF.Min(1, j.Weight + s / JumpFadeIn) };
            landing = null;
        }
        else if (jump is { } j)
        {
            if (!wasGrounded)
                landing = new BodyLayer($"jump_out_{(lastSpeed < 30 ? "idle" : lastSpeed < 150 ? "walk" : lastSpeed < 230 ? "run" : "sprint")}", 0, 1);
            jump = j.Weight - s / JumpFade > 0 ? j with { Cycle = (j.Cycle + Rate("jump_idle") * s) % 1, Weight = j.Weight - s / JumpFade } : null;
        }
        if (landing is { } l && wasGrounded && input.Grounded)
            landing = l.Cycle >= 1 ? null : l with { Cycle = MathF.Min(1, l.Cycle + Rate(l.Clip) * s) };
        wasGrounded = input.Grounded;
        lastSpeed = speed;
    }

    /// <summary>
    /// Where a gait let go stops, by its cycle, and the idle it stops into: the l01_a recordings' 140 stops, which play on
    /// from the key's release to the next of these. A run plants its right foot at 0.25 and its left at 0.75, or both at the
    /// end of its stride; a walk the left at 0.25 (5 of 8) and both at the middle and end; a sprint both at its end.
    /// </summary>
    internal static (float At, string Idle)[] StopsOf(string gait) => gait switch
    {
        "run" => [(.25f, "idle_rightfoot"), (.75f, "idle_leftfoot"), (1, "idle_middle")],
        "Walk" => [(.25f, "idle_leftfoot"), (.5f, "idle_middle"), (1, "idle_middle")],
        "sprint" => [(1, "idle_middle")],
        _ => [],
    };

    /// <summary>Whether a looping cycle going from <paramref name="from"/> to <paramref name="to"/> passed <paramref name="at"/> (1 is the wrap).</summary>
    static bool Crossed(float from, float to, float at) => to >= from ? from < at && to >= at : from < at || to >= at % 1 && at < 1;

    static bool Gait(string clip) => clip is "Walk" or "run" or "sprint" or "Crouch";

    float Rate(string clip) => model.Clips.TryGetValue(clip, out var c) && c.Seconds > 0 ? 1 / c.Seconds : 1;

    internal static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
}
