using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// Leaning, as the donor's client does it (R10161-2; docs/lab.md, "The camera"; <c>l01_a-v18-lean</c>): <c>+leanleft</c>
/// and <c>+leanright</c> (Z and X in its <c>config.cfg</c>, nothing in the user command) roll the view
/// <see cref="Angle"/> (<c>cl_leanangle</c>, 15°) and move the eye <see cref="Move"/> (<c>cl_leanmove</c>, 25 units) to
/// that side, both easing toward it at <see cref="Speed"/> (<c>cl_leanspeed</c>, 10 a second: 70% of the way in 0.12
/// s, as recorded going in and coming back). The camera stays in the world: a ray from the eye to the leaned camera
/// stops <see cref="Hull"/> (<c>mm_player_lean_hull</c>, 5 units) short of what it meets, and the camera comes back
/// along it (recorded against a wall: 11 of the 27.3 units out, at the height on that line).
/// </summary>
internal sealed class PlayerLean
{
    internal const float Angle = 15, Move = 25, Speed = 10, Hull = 5;

    /// <summary>−1 leaning fully left, 1 fully right.</summary>
    internal float Value { get; private set; }

    /// <summary>A lean held whatever the keys (<c>lab.dev.lean</c>), to pose the view as a donor screenshot was.</summary>
    internal float? Held { get; set; }

    /// <summary>The view's roll, Source degrees (right side down positive): −15 leaning left.</summary>
    internal float RollDegrees => Value * Angle;

    /// <summary>The eye's move to the right, metres.</summary>
    internal float SideMetres => Value * Move * SourceSpace.MetresPerUnit;

    /// <summary>The hull the camera keeps from what it meets, metres.</summary>
    internal static float HullMetres => Hull * SourceSpace.MetresPerUnit;

    internal void Step(float seconds, bool left, bool right)
    {
        float target = Held ?? (right ? 1 : 0) - (left ? 1 : 0);
        Value += (target - Value) * (1 - MathF.Exp(-Speed * seconds));
        if (MathF.Abs(Value - target) < 1e-4f) Value = target;
    }

    internal void Reset() => Value = 0;
}
