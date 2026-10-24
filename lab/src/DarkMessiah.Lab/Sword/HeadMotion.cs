using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// How the donor's first-person camera turns with the body (#10161; docs/lab.md, "The camera"): Arkane's
/// <c>player_head_mvt</c> model, one node with a clip per move, plays the clip named as the arms' move
/// (<c>layer_bigslash_sword_noshield_out</c>, <c>layer_combo_sword_huge_noshield</c>...) at its cycle, and its fall
/// (<c>jump_out_idle</c>, <c>_walk</c>, <c>_run</c>, <c>_sprint</c>, by the body's speed) once the body falls faster than
/// <c>mm_player_speed_to_active_fall</c> (125 units/s). Moves the head model has no clip for (the sword's fast attacks
/// and kicks, whose head clips are named <c>layer_sword_kick_*</c>) leave the camera still, as the recordings show; so do
/// its walk and run clips, which the donor doesn't play (no bob in the recordings). Source's strafe roll goes on top
/// (<see cref="StrafeRoll"/>).
/// </summary>
internal sealed class HeadMotion : IDisposable
{
    internal const string Model = "models/npc/player/player_head_mvt.mdl";
    internal const ulong Id = 0x4C41_0400;

    /// <summary>A fall this fast (units/s, downward) starts the head's fall clip: <c>mm_player_speed_to_active_fall</c>.</summary>
    internal const float FallSpeed = 125;

    readonly IEngineContext engine;
    readonly Puppet puppet;
    readonly HashSet<string> clips;
    readonly int node;
    readonly Quaternion rest;
    string? fall;
    float fallCycle;
    bool fell;

    internal HeadMotion(IEngineContext engine, DonorContent donor)
    {
        this.engine = engine;
        var model = donor.Model(Model);
        puppet = new Puppet(engine, model, Id);
        clips = [.. model.Info.Clips.Keys];
        node = model.Joints.Values.Max();
        rest = Node("idle", 0).Rotation;
    }

    /// <summary>
    /// A blow that lands on the player kicks the view (#10161; the head model has no clip for it): it rises to its peak
    /// in <see cref="PunchPeak"/> s and is gone at <see cref="PunchEnd"/> s, half a cosine each way. Measured on the hand
    /// fights, <c>arena-bot</c> and <c>chained-slash</c> (84 orc blows): peaks 0.15 to 0.18 s after the blow, gone by
    /// about 0.5 s. Its size goes by the orc's attack (<see cref="PunchOf"/>), not its damage nor its <c>impactdir</c>.
    /// </summary>
    internal const float PunchPeak = .17f, PunchEnd = .5f;

    (float Pitch, float Yaw) punch;
    float punchSeconds = PunchEnd;

    /// <summary>
    /// The kick at its peak, Source degrees (pitch down, yaw left), for the orc's <paramref name="attack"/> and the
    /// blow's <c>HitPow</c>: 7.8° down for most (the top, bottom, kick-swing and turned strikes, 51 blows: 7.0° to 8.6°);
    /// 23.5° down for the jump attack (5 of 5); 15° left for <c>attack_left</c> (14° to 23.5°, 3); 10° left for the fast
    /// attack's second blow (HitPow 0.8: 7.6° to 18.4°, 4); 2.6° right for <c>attack_right</c>, the uppercut, the
    /// forward thrust and the fast attack's first blow (15 of 16 at 2.6°).
    /// </summary>
    internal static (float Pitch, float Yaw) PunchOf(string attack, float hitPow) => attack.ToLowerInvariant() switch
    {
        "attack_jump" => (23.5f, 0),
        "attack_left" => (0, 15),
        "attack_fast" => hitPow > .5f ? (0, 10) : (0, -2.6f),
        "attack_right" or "attack_uppercut" or "attack_forward" => (0, -2.6f),
        _ => (7.8f, 0),
    };

    /// <summary>A blow lands on the player: the view's kick, <paramref name="kick"/> at its peak.</summary>
    internal void Punch((float Pitch, float Yaw) kick)
    {
        punch = kick;
        punchSeconds = 0;
    }

    /// <summary>
    /// An orc's grab: the head plays <c>player_grabed</c> from the hands' taking, cut at <see cref="GrabEnds"/> s (the
    /// donor's camera is back at the view there, its last 0.15 s unplayed), then <c>player_knocked_down</c>
    /// <see cref="KnockedAfter"/> s after the grab began, as the player is thrown (the grab itself is #10185). Measured on
    /// the three recorded grabs (<c>l01_a-v14-arena-hand</c>, <c>l01_a-v16-chained-slash</c>'s two): the camera within
    /// 1° of the clip through the grab, 40.2° at its height, then the knockdown's −28.7° 122 to 126 ticks after it.
    /// </summary>
    internal const float GrabEnds = 2.35f, KnockedAfter = 3.12f;

    float grabbed = float.NaN;

    /// <summary>The hands are taken by an orc's grab: the head's grab and throw begin.</summary>
    internal void Grabbed() => grabbed = 0;

    (string Clip, float Cycle)? Body(float seconds)
    {
        if (float.IsNaN(grabbed)) return null;
        grabbed += seconds;
        float grab = puppet.Model.Clip("player_grabed").Seconds, knock = puppet.Model.Clip("player_knocked_down").Seconds;
        if (grabbed < MathF.Min(grab, GrabEnds)) return ("player_grabed", grabbed / grab);
        if (grabbed >= KnockedAfter && grabbed < KnockedAfter + knock) return ("player_knocked_down", (grabbed - KnockedAfter) / knock);
        if (grabbed >= KnockedAfter + knock) grabbed = float.NaN;
        return null;
    }

    /// <summary>The kick <paramref name="seconds"/> after the blow, as a fraction of its peak.</summary>
    internal static float PunchCurve(float seconds) =>
        seconds <= 0 || seconds >= PunchEnd ? 0
        : seconds < PunchPeak ? (1 - MathF.Cos(MathF.PI * seconds / PunchPeak)) / 2
        : (1 + MathF.Cos(MathF.PI * (seconds - PunchPeak) / (PunchEnd - PunchPeak))) / 2;

    /// <summary>A comfort scale on the landing's dip (<c>player.json</c>'s <c>landingDip</c>): 1 as the donor's, 0 none (R10161-1).</summary>
    internal float LandingScale { get; set; } = 1;

    /// <summary>A head clip's length, s (0 when the model lacks it).</summary>
    internal float ClipSeconds(string clip) => clips.Contains(clip) ? puppet.Model.Clip(clip).Seconds : 0;

    /// <summary>The head's turn now, Source degrees: pitch down, yaw left, roll right side down.</summary>
    internal (float Pitch, float Yaw, float Roll) Turn { get; private set; }
    /// <summary>The clip it plays now, or null for none.</summary>
    internal string? Playing { get; private set; }

    Transform Node(string clip, float cycle)
    {
        puppet.Play(clip, cycle);
        return Rig.Pose(engine, puppet, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One))[node].World;
    }

    /// <param name="move">The arms' move clip and its cycle, or null at rest.</param>
    /// <param name="airborne">The body is off the ground.</param>
    /// <param name="velocity">The body's velocity, m/s (Y up).</param>
    /// <param name="weight">How much of the move's clip turns the head: the bow's sway fades (#10168).</param>
    internal void Step(float seconds, (string Clip, float Cycle)? move, bool airborne, Vector3 velocity, float weight = 1)
    {
        float fallUnits = -velocity.Y / SourceSpace.MetresPerUnit, planar = new Vector2(velocity.X, velocity.Z).Length() / SourceSpace.MetresPerUnit;
        if (!airborne) fell = false;
        else if (!fell && fallUnits > FallSpeed)
        {
            fell = true;
            fall = $"jump_out_{(planar < 30 ? "idle" : planar < 150 ? "walk" : planar < 230 ? "run" : "sprint")}";
            fallCycle = 0;
        }
        if (fall is not null && (fallCycle += seconds / MathF.Max(.01f, puppet.Model.Clip(fall).Seconds)) >= 1) fall = null;
        var body = Body(seconds);
        (string Clip, float Cycle)? play = body ?? (move is { } m && clips.Contains(m.Clip) ? m : fall is not null ? (fall, fallCycle) : null);
        Playing = play?.Clip;
        punchSeconds = MathF.Min(punchSeconds + seconds, PunchEnd);
        float kick = PunchCurve(punchSeconds);
        if (play is not { } p) { Turn = (punch.Pitch * kick, punch.Yaw * kick, 0); return; }
        // The node's turn from its rest, in the model's frame (+Z forward, +Y up). Its axes are the camera's turned:
        // a turn about the model's Z is the camera's pitch, about X its yaw, about Y its roll (fitted to the recorded
        // camera: the power attack's 6.2°, 6.2° and -1.4° at cycle 0.3, recorded 6.5°, 6.0°, -1.4°).
        var turn = Quaternion.Inverse(rest) * Node(p.Clip, Math.Clamp(p.Cycle, 0, 1)).Rotation;
        var f = Vector3.Transform(Vector3.UnitZ, turn);
        var u = Vector3.Transform(Vector3.UnitY, turn);
        float aboutX = MathF.Asin(Math.Clamp(-f.Y, -1, 1)) * 180 / MathF.PI, aboutY = MathF.Atan2(f.X, f.Z) * 180 / MathF.PI,
            aboutZ = MathF.Atan2(-u.X, u.Y) * 180 / MathF.PI;
        // The landing's dip at the comfort scale; every other clip as the donor plays it.
        float scale = p.Clip.StartsWith("jump_out_", StringComparison.Ordinal) ? LandingScale : body is null && move is { } asked && asked.Clip == p.Clip ? weight : 1;
        Turn = (-aboutZ * scale + punch.Pitch * kick, aboutX * scale + punch.Yaw * kick, -aboutY * scale);
    }

    /// <summary>The view's roll at most, degrees, strafing: <c>sv_rollangle</c>.</summary>
    internal const float RollAngle = 3;
    /// <summary>The sideways speed (units/s) that rolls it fully: <c>sv_rollspeed</c>.</summary>
    internal const float RollSpeed = 200;

    /// <summary>
    /// Source's strafe roll (<c>CBasePlayer::CalcViewRoll</c>), which the donor adds to every view: the body's speed to
    /// the view's right (<paramref name="velocity"/> in m/s, Y up), scaled to <see cref="RollAngle"/> at
    /// <see cref="RollSpeed"/>; positive rolls the view's right side down. Fitted on <c>l01_a-v14-arena-hand</c>'s ticks at
    /// rest: the camera's roll 1.7° (RMS) off the view, 0.4° off this.
    /// </summary>
    internal static float StrafeRoll(Vector3 velocity, Vector3 forward)
    {
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-6f) return 0;
        float side = Vector3.Dot(velocity, Vector3.Normalize(right)) / SourceSpace.MetresPerUnit;
        return MathF.Sign(side) * (MathF.Abs(side) < RollSpeed ? MathF.Abs(side) * RollAngle / RollSpeed : RollAngle);
    }

    /// <summary>
    /// The turn as a rotation in the world, about the view's own axes: yaw about its up, then pitch about its right, then
    /// roll about its forward (Source's order).
    /// </summary>
    internal static Quaternion Rotation((float Pitch, float Yaw, float Roll) turn, Vector3 forward, Vector3 up)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        const float D = MathF.PI / 180;
        var yaw = Quaternion.CreateFromAxisAngle(up, turn.Yaw * D);
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.Transform(right, yaw), -turn.Pitch * D);
        var roll = Quaternion.CreateFromAxisAngle(Vector3.Transform(forward, pitch * yaw), turn.Roll * D);
        return Quaternion.Normalize(roll * pitch * yaw);
    }

    public void Dispose() => puppet.Dispose();
}
