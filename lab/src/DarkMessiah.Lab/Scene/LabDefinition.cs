using System.Numerics;
using System.Text.Json.Serialization;
using DarkMessiah.Lab.Content;
using Rusty.Engine;

namespace DarkMessiah.Lab.Scene;

/// <summary>
/// The lab's authored scene, from <c>content/lab.json</c>: geometry, spawn feet and facing, world gravity for the
/// loose cubes, and the pickup tuning. An edit restages the product under <c>rusty dev</c>; the player's feel and the
/// actions are separate live files (<c>player.json</c>, <c>actions.json</c>).
/// </summary>
/// <param name="ReplayFeet">Where a replay puts the lab's body when a scenario's setpos takes the donor back to its start
/// (#10159, #10160): open ground clear of the pillars and walls for the movement scenarios' runs and sprints.</param>
/// <param name="Ladders">The ladders (#10163): each one's foot against the face it stands on, its top's height, and the
/// way a climber faces it.</param>
internal sealed record LabDefinition(float VoxelSize, float Gravity, float[] SpawnFeet, float SpawnYawDegrees,
    InteractionTuning Interaction, VolumeDefinition[] Volumes, RampDefinition[] Ramps, BlockDefinition[] Blocks, CubeDefinition[] Cubes,
    float[] ReplayFeet, LadderDefinition[] Ladders)
{
    internal const string Path = "lab.json";

    internal static LabDefinition Load(IEngineContext engine)
    {
        LabDefinition lab = Authored.Read(engine, Path, LabJson.Default.LabDefinition);
        Authored.Positive(Path, "voxelSize", lab.VoxelSize);
        Authored.Positive(Path, "gravity", lab.Gravity);
        for (int i = 0; i < lab.Ladders.Length; i++)
        {
            Authored.Require(lab.Ladders[i].Top > Vector(lab.Ladders[i].Foot).Y, Path, $"ladders[{i}].top", "must be above its foot.");
            Authored.Require((Vector(lab.Ladders[i].Facing) with { Y = 0 }).LengthSquared() > 1e-6f, Path, $"ladders[{i}].facing", "must point across the ground.");
        }
        return lab;
    }

    internal static Vector3 Vector(float[] xyz) => Authored.Vector(xyz);

    internal static Color Color(float[] rgb) => Authored.Color(rgb);
}
internal sealed record InteractionTuning(float Reach, float HoldDistance, float HoldSlack, float HoldReelSpeed, float ThrowSpeed);
internal sealed record VolumeDefinition(string Label, float[] Min, float[] Max, uint Material);
internal sealed record RampDefinition(string Label, float[] Origin, float Width, float Length, float AngleDegrees, float[] Color);
internal sealed record BlockDefinition(string Label, float[] Center, float[] Size);
internal sealed record CubeDefinition(string Label, float[] Center, float Size, float Mass, float[] Color);
internal sealed record LadderDefinition(string Label, float[] Foot, float Top, float[] Facing);

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LabDefinition))]
internal sealed partial class LabJson : JsonSerializerContext;
