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
internal sealed record PlayerTuning(float Height, float CrouchedHeight, float Radius, float EyeBelowTop,
    float Speed, float SprintSpeed, float JumpSpeed, float Gravity, float MaximumStepHeight, float MaximumSlopeDegrees,
    float JumpBufferSeconds, float CoyoteSeconds, float PointerRadiansPerUnit, float FieldOfViewDegrees, float FarPlane,
    float HeadBobMetres, float HeadBobStrideMetres)
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
        return t;
    }
}

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PlayerTuning))]
internal sealed partial class PlayerJson : JsonSerializerContext;
