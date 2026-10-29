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
/// <param name="Hazards">What kicked orcs meet (#10157): spike racks and fires (a pit is the volumes').</param>
internal sealed record LabDefinition(float VoxelSize, float Gravity, float[] SpawnFeet, float SpawnYawDegrees,
    InteractionTuning Interaction, VolumeDefinition[] Volumes, RampDefinition[] Ramps, BlockDefinition[] Blocks, CubeDefinition[] Cubes,
    float[] ReplayFeet, LadderDefinition[] Ladders, HazardDefinitions Hazards)
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
        for (int i = 0; i < lab.Hazards.Spikes.Length; i++)
        {
            var rack = lab.Hazards.Spikes[i];
            Authored.Require(Vector(rack.Max).X > Vector(rack.Min).X && Vector(rack.Max).Y > Vector(rack.Min).Y && Vector(rack.Max).Z > Vector(rack.Min).Z,
                Path, $"hazards.spikes[{i}]", "max must be above min on every axis.");
            Authored.Require((Vector(rack.Facing) with { Y = 0 }).LengthSquared() > 1e-6f, Path, $"hazards.spikes[{i}].facing", "must point across the ground.");
        }
        for (int i = 0; i < lab.Hazards.Fires.Length; i++) Authored.Positive(Path, $"hazards.fires[{i}].radius", lab.Hazards.Fires[i].Radius);
        return lab;
    }

    internal static Vector3 Vector(float[] xyz) => Authored.Vector(xyz);

    internal static Color Color(float[] rgb) => Authored.Color(rgb);
}
/// <summary>How the held body's tether pulls (its reach, hold distance and throw are telekinesis', #10164: <c>TelekinesisRules</c>).</summary>
internal sealed record InteractionTuning(float HoldSlack, float HoldReelSpeed);
internal sealed record VolumeDefinition(string Label, float[] Min, float[] Max, uint Material);
internal sealed record RampDefinition(string Label, float[] Origin, float Width, float Length, float AngleDegrees, float[] Color);
internal sealed record BlockDefinition(string Label, float[] Center, float[] Size);
/// <summary>
/// A loose body: a coloured cube of <c>size</c> and <c>mass</c>, or a donor prop (#10164, R10164-2): <c>model</c>, an
/// l01_a model the import has, drawn as itself, its box its collision's and its mass its <c>.phy</c>'s.
/// </summary>
internal sealed record CubeDefinition(string Label, float[] Center, float Size = 0, float Mass = 0, float[]? Color = null, string? Model = null);
internal sealed record LadderDefinition(string Label, float[] Foot, float Top, float[] Facing);
internal sealed record HazardDefinitions(SpikeDefinition[] Spikes, FireDefinition[] Fires);
/// <summary>A spike rack (#10157): its box, and the way its spikes point.</summary>
internal sealed record SpikeDefinition(string Label, float[] Min, float[] Max, float[] Facing);
/// <summary>A fire on the ground (#10157): its middle and its flames' radius (an orc burns within it and its own 0.4 m).</summary>
internal sealed record FireDefinition(string Label, float[] Center, float Radius);

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LabDefinition))]
internal sealed partial class LabJson : JsonSerializerContext;
