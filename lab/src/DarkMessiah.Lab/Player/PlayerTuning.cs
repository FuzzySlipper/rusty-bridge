using System.Text.Json.Serialization;
using DarkMessiah.Lab.Content;
using Rusty.Engine;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// The authored feel of the first-person body and camera, from <c>content/player.json</c>. Live content: an edit, or a
/// save from the tunables panel, reaches the running lab as a content change and is applied without a restart.
/// </summary>
/// <param name="EyeBelowTop">How far below the top of the capsule the eye sits, in either stance.</param>
/// <param name="JumpBufferSeconds">How long the Engine controller keeps a jump pressed in the air, to start on landing.</param>
/// <param name="CoyoteSeconds">How long after walking off a ledge the Engine controller still accepts a jump.</param>
/// <param name="HeadBobMetres">Camera bob height at full walking speed; 0 turns it off. Presentation only.</param>
/// <param name="HeadBobStrideMetres">Ground walked per bob cycle.</param>
/// <param name="Speed">Running forward or strafing, m/s: the donor's 180 units a second (#10159, MovementCalibration).</param>
/// <param name="BackSpeed">Backing off with the back key full, m/s: the donor's 60 units a second (its cl_backspeed command, 150).</param>
/// <param name="CrouchSpeed">Moving crouched, m/s: 1.29 runs at the donor's 50 units a second (51) against the controller's friction.</param>
/// <param name="Acceleration">The Engine controller's ground acceleration, braking, friction and stop speed (m/s², 1/s and
/// m/s), fitted so the lab's start and stop take the donor's time and slide (#10159).</param>
/// <param name="JumpReleaseGravity">Gravity's multiple once jump is let go while rising (1: none): the donor's jump rose 34
/// units held 0.15 s and 41 held to its peak (l01_a-v18-jump-hold; Arkane's mm_jump_rework).</param>
/// <param name="LandingDip">A comfort scale on the landing's head dip (<c>jump_out_*</c>, #10161): 1 as the donor's (7.9° at its
/// peak), 0 none. Presentation only.</param>
internal sealed record PlayerTuning(float Height, float CrouchedHeight, float Radius, float EyeBelowTop,
    float Speed, float SprintSpeed, float JumpSpeed, float Gravity, float MaximumStepHeight, float MaximumSlopeDegrees,
    float JumpBufferSeconds, float CoyoteSeconds, float PointerRadiansPerUnit, float FieldOfViewDegrees, float FarPlane,
    float HeadBobMetres, float HeadBobStrideMetres, float BackSpeed, float CrouchSpeed, float Acceleration, float Braking, float Friction, float StopSpeed,
    float JumpReleaseGravity, float LandingDip)
{
    internal const string Path = "player.json";

    internal static PlayerTuning Load(IEngineContext engine) => Validate(Authored.Read(engine, Path, PlayerJson.Default.PlayerTuning));

    internal static PlayerTuning Validate(PlayerTuning t)
    {
        Authored.Positive(Path, "height", t.Height);
        Authored.Within(Path, "crouchedHeight", t.CrouchedHeight, 2 * t.Radius, t.Height);
        Authored.Positive(Path, "radius", t.Radius);
        Authored.Within(Path, "eyeBelowTop", t.EyeBelowTop, 0, t.CrouchedHeight / 2);
        Authored.Positive(Path, "speed", t.Speed);
        Authored.AtLeast(Path, "sprintSpeed", t.SprintSpeed, t.Speed);
        Authored.Positive(Path, "backSpeed", t.BackSpeed);
        Authored.Positive(Path, "crouchSpeed", t.CrouchSpeed);
        Authored.Positive(Path, "acceleration", t.Acceleration);
        Authored.AtLeast(Path, "braking", t.Braking, 0);
        Authored.AtLeast(Path, "friction", t.Friction, 0);
        Authored.AtLeast(Path, "stopSpeed", t.StopSpeed, 0);
        Authored.AtLeast(Path, "jumpReleaseGravity", t.JumpReleaseGravity, 1);
        Authored.Positive(Path, "jumpSpeed", t.JumpSpeed);
        Authored.Positive(Path, "gravity", t.Gravity);
        Authored.AtLeast(Path, "maximumStepHeight", t.MaximumStepHeight, 0);
        Authored.Within(Path, "maximumSlopeDegrees", t.MaximumSlopeDegrees, 0, 89);
        Authored.AtLeast(Path, "jumpBufferSeconds", t.JumpBufferSeconds, 0);
        Authored.AtLeast(Path, "coyoteSeconds", t.CoyoteSeconds, 0);
        Authored.Positive(Path, "pointerRadiansPerUnit", t.PointerRadiansPerUnit);
        Authored.Within(Path, "fieldOfViewDegrees", t.FieldOfViewDegrees, 30, 120);
        Authored.Positive(Path, "farPlane", t.FarPlane);
        Authored.AtLeast(Path, "headBobMetres", t.HeadBobMetres, 0);
        Authored.Positive(Path, "headBobStrideMetres", t.HeadBobStrideMetres);
        Authored.Within(Path, "landingDip", t.LandingDip, 0, 1);
        return t;
    }
}

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PlayerTuning))]
internal sealed partial class PlayerJson : JsonSerializerContext;
