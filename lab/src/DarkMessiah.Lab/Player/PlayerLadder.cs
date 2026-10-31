using System.Numerics;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// A ladder in the arena (<c>lab.json</c>'s <c>ladders</c>; #10163): its foot against the face it stands on, the height
/// of its top, and the way a climber faces it (into the face). The player climbs it on the Engine's climb rail
/// (<c>CharacterMovementMode.Climbing</c>), a vertical line <see cref="LadderRules.Offset"/> plus the body's radius out
/// from the face.
/// </summary>
internal sealed record Ladder(string Label, Vector3 Foot, float Top, Vector3 Facing);

/// <summary>
/// The donor's ladder numbers (#10163, #10439; docs/lab.md, "Ladders"): server.dll's ConVar defaults
/// (<c>mm_player_ladder_speed</c> 80 units/s, <c>mm_player_ladder_offset</c> 9 units from the ladder,
/// <c>sv_ladderautomountdot</c> 0.4: mounting takes a look at least that much toward the ladder, or from its top that
/// much away), measured against l01_a's one ladder (<c>room03.ladder.02</c>; <c>ladder</c>, <c>ladder-2</c>,
/// <c>ladder-3</c>; <c>LadderCalibration</c>):
/// <list type="bullet">
/// <item>forward climbs the way the view points, at <see cref="Speed"/> times the sine of its pitch (13, 40, 58 units/s
/// at 10°, 30°, 40° up; looking level it doesn't move, looking down it climbs down), back the other way;</item>
/// <item>walked into from its foot, it's on the rail at once (2 ticks from 2 units off);</item>
/// <item>walked onto from its top facing away, it turns round onto the rail's top in <see cref="OnTopSeconds"/> (93
/// ticks), the view turning with the body's <c>Player_ladder_in_updown</c>;</item>
/// <item>off at the top in <see cref="OffTopSeconds"/> (71 ticks, twice) onto the top's dismount, <see cref="ExitDistance"/>
/// out from the rail; off at the foot climbing down in <see cref="OffFootSeconds"/> (8 and 9 ticks; 24 twice),
/// <see cref="FootExitDistance"/> back.</item>
/// </list>
/// </summary>
internal static class LadderRules
{
    internal const float Speed = 80, Offset = 9, ExitDistance = 41, FootExitDistance = 19, MountDot = .4f,
        OnTopSeconds = 1.40f, OffTopSeconds = 1.07f, OffFootSeconds = .12f;

    /// <summary>How far, horizontally, the body may be from the rail and still mount it or hold on, metres.</summary>
    internal const float Reach = .35f;

    internal static float SpeedMetres => Speed * SourceSpace.MetresPerUnit;
    internal static float OffsetMetres => Offset * SourceSpace.MetresPerUnit;
    internal static float ExitMetres => ExitDistance * SourceSpace.MetresPerUnit;
    internal static float FootExitMetres => FootExitDistance * SourceSpace.MetresPerUnit;

    /// <summary>The rail's bottom and top centres for a body of <paramref name="radius"/> and half height <paramref name="half"/>.</summary>
    internal static (Vector3 Bottom, Vector3 Top) Rail(Ladder ladder, float radius, float half)
    {
        var facing = Vector3.Normalize(ladder.Facing with { Y = 0 });
        var line = ladder.Foot - facing * (radius + OffsetMetres);
        return (line with { Y = ladder.Foot.Y + half }, line with { Y = ladder.Top + half + .02f });
    }

    /// <summary>
    /// What the ladder looks like (R10163-2): two side rails and a rung every 0.3 m, 0.5 m wide, against the face it
    /// stands on, from its foot to its top. Drawn only (the climb is the rail's); boxes as centre and size.
    /// </summary>
    internal static IReadOnlyList<(Vector3 Centre, Vector3 Size)> Pieces(Ladder ladder)
    {
        var facing = Vector3.Normalize(ladder.Facing with { Y = 0 });
        var right = Vector3.Normalize(Vector3.Cross(facing, Vector3.UnitY));
        float height = ladder.Top - ladder.Foot.Y, depth = .05f, halfWidth = .25f;
        var plane = ladder.Foot - facing * depth;
        // Box sizes along the world axes: the ladder's width runs along right, its depth along facing.
        Vector3 Size(float across, float tall, float deep) =>
            new(MathF.Abs(right.X) * across + MathF.Abs(facing.X) * deep, tall, MathF.Abs(right.Z) * across + MathF.Abs(facing.Z) * deep);
        var pieces = new List<(Vector3, Vector3)>();
        foreach (float side in new[] { -halfWidth, halfWidth })
            pieces.Add((plane + right * side + Vector3.UnitY * (height / 2), Size(.06f, height, .06f)));
        for (float y = .3f; y < height; y += .3f)
            pieces.Add((plane + Vector3.UnitY * y, Size(2 * halfWidth, .04f, .04f)));
        return pieces;
    }

    /// <summary>
    /// The climb's vertical intent, a fraction of <see cref="Speed"/>: forward climbs the way the view points by the
    /// sine of its pitch (up looking up, down looking down, not at all looking level), back the other way.
    /// </summary>
    internal static float Intent(float forward, float pitchDownDegrees) =>
        Math.Clamp(forward * MathF.Sin(-pitchDownDegrees * MathF.PI / 180), -1, 1);
}
