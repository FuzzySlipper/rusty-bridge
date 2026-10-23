using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// The donor's fall damage (R10159-1; docs/lab.md, "Falls"), measured in <c>l01_a-v18-fall-1</c> and <c>-2</c> (the
/// <c>fall</c> scenario: god off, health set before each drop). It is the Source SDK's: the landing speed over
/// <see cref="SafeSpeed"/> (526.5 units/s) takes 100 health for each <see cref="FatalSpeed"/> less that (396 units/s),
/// the landing speed being <see cref="SpeedPerRootUnit"/> times the square root of the drop in units (fitted: 8.14 health
/// a √unit from 271 units against the formula's 8.08 from 271; every landing within 1.3 of it). Drops of 277 and 284 units,
/// which it prices at 1.4 and 3.3, took none, and 316 took 10, so none is taken under <see cref="MinimumDrop"/>.
/// </summary>
internal static class FallRules
{
    internal const float SafeSpeed = 526.5f, FatalSpeed = 922.5f, SpeedPerRootUnit = 32, MinimumDrop = 300;

    /// <summary>The health a landing takes after a drop of <paramref name="dropUnits"/> (the highest point to the landing).</summary>
    internal static float Damage(float dropUnits) =>
        dropUnits < MinimumDrop ? 0 : MathF.Max(0, (SpeedPerRootUnit * MathF.Sqrt(dropUnits) - SafeSpeed) * 100 / (FatalSpeed - SafeSpeed));

    internal static float DropUnits(float metres) => metres / SourceSpace.MetresPerUnit;
}
