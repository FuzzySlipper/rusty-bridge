using System.Text.Json.Serialization;

namespace DarkMessiah.Lab.Ui;

internal sealed record LabObservation(LabStamp Stamp, string Readiness, string WorldTime, LabPlayerFacts Player, LabFightFacts Fight,
    LabCubeFacts[] Cubes, string Target, string Message, ulong? HeldCube, uint Pickups, uint Throws, uint Drops,
    bool BumpPushSupported, string Input, string Notice);
internal sealed record LabStamp(ulong Generation, ulong Step);
internal sealed record LabPlayerFacts(float[] Position, float[] Feet, float[] Eye, float[] Velocity, float YawDegrees, float PitchDegrees,
    bool Grounded, string Stance, ulong? SupportEntity, string Contact, ulong? ContactEntity, string BlockFlags,
    float JumpBufferRemaining, float CoyoteRemaining, uint Jumps, uint Landings, uint HardLandings, float LastLandingSpeed,
    uint Recoveries, string LastRecovery);
/// <summary>The fight: the sword's move, clip and cycle, both sides' health and counts, the orc's state, and the last blow.</summary>
internal sealed record LabFightFacts(string Move, string Clip, float Cycle, bool Traced, float PlayerHealth, bool God,
    int Swings, int Hits, int Parried, int Blocked, int Missed, float Dealt,
    string OrcState, string OrcClip, float OrcCycle, float OrcHealth, float[] OrcFeet, float OrcDistance,
    int OrcBlows, int OrcReached, int PlayerParries, int ParriesBroken, float OrcDealt, int Deaths, LabBlowFacts? LastBlow, string Donor);
internal sealed record LabBlowFacts(int Swing, string Move, float Cycle, float[] Start, float[] End, float[]? Point, string Outcome);
internal sealed record LabCubeFacts(ulong Id, string Label, float Size, float Mass, float[] Position, float[] Velocity, bool Sleeping, bool Held);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LabObservation))]
internal sealed partial class ObservationJson : JsonSerializerContext;
