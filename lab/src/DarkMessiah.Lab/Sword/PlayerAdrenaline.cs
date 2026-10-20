namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The player's adrenaline (docs/lab.md, "Adrenaline"; #10145): each blow that lands gives its <c>.qct</c> event's
/// <c>adrenalineadd</c> (a fast attack 0.5, a power attack 3), up to <see cref="Max"/> (20, the Wire's
/// <c>MaxAdrenaline</c>). Full, the next power strike is a Fatality, and landing it spends the gauge.
/// </summary>
internal sealed class PlayerAdrenaline
{
    /// <summary>The maximum a new game's player has, as every recording shows.</summary>
    internal const float ShippedMax = 20;

    internal float Max { get; private set; } = ShippedMax;
    internal float Value { get; private set; }
    internal bool Full => Value >= Max;

    internal void Gain(float amount) => Value = Math.Clamp(Value + amount, 0, Max);

    /// <summary>A Fatality landed: the gauge empties.</summary>
    internal void Spend() => Value = 0;

    /// <summary>Takes a recording's gauge as a replay begins.</summary>
    internal void Set(float value, float max)
    {
        Max = max > 0 ? max : ShippedMax;
        Value = Math.Clamp(value, 0, Max);
    }

    internal void Reset()
    {
        Max = ShippedMax;
        Value = 0;
    }
}
