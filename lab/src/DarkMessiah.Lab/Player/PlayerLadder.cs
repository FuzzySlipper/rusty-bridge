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
/// The donor's ladder numbers (#10163; docs/lab.md, "Ladders"), server.dll's ConVar defaults, read from its ConVar
/// registrations: <c>mm_player_ladder_speed</c> 80 units/s, <c>mm_player_ladder_offset</c> 9 units from the ladder,
/// <c>mm_dist_to_exit_ladder</c> 5 units past the top to step off, <c>sv_ladderautomountdot</c> 0.4 (mounting takes a
/// look at least that much toward the ladder), <c>sv_autoladderdismount</c> 1 (getting off at either end by moving
/// on), <c>mm_ladder_friction_pitch</c> 60 (taken as: looking more than 60° down, forward climbs down) and
/// <c>mm_time_to_exit_enter_ladder</c> 1 s (the in and out clips' time; the lab mounts and dismounts at once). None is
/// recorded: l01_a has no ladder.
/// </summary>
internal static class LadderRules
{
    internal const float Speed = 80, Offset = 9, ExitDistance = 5, MountDot = .4f, DownPitchDegrees = 60, EnterExitSeconds = 1;

    /// <summary>How far, horizontally, the body may be from the rail and still mount it or hold on, metres.</summary>
    internal const float Reach = .35f;

    internal static float SpeedMetres => Speed * SourceSpace.MetresPerUnit;
    internal static float OffsetMetres => Offset * SourceSpace.MetresPerUnit;
    internal static float ExitMetres => ExitDistance * SourceSpace.MetresPerUnit;

    /// <summary>The rail's bottom and top centres for a body of <paramref name="radius"/> and half height <paramref name="half"/>.</summary>
    internal static (Vector3 Bottom, Vector3 Top) Rail(Ladder ladder, float radius, float half)
    {
        var facing = Vector3.Normalize(ladder.Facing with { Y = 0 });
        var line = ladder.Foot - facing * (radius + OffsetMetres);
        return (line with { Y = ladder.Foot.Y + half }, line with { Y = ladder.Top + half + .02f });
    }

    /// <summary>The climb's vertical intent: forward climbs (down when looking more than 60° down), back descends.</summary>
    internal static float Intent(float forward, float pitchDownDegrees) =>
        Math.Clamp(forward > 0 && pitchDownDegrees > DownPitchDegrees ? -forward : forward, -1, 1);
}
