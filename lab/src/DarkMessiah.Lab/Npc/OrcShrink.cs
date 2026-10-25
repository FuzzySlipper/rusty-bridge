using DarkMessiah.Lab.Magic;

namespace DarkMessiah.Lab.Npc;

/// <summary>The orc shrunk (#10348; <see cref="ShrinkRules"/>, docs/lab.md "Shrink"): its size falling, then small for good.</summary>
internal sealed partial class Orc
{
    float shrinkAge = float.NaN;

    /// <summary>It wears the small model (<see cref="ShrinkRules.SwapSeconds"/> after the cast): it stands and strikes nothing.</summary>
    internal bool Shrunk { get; private set; }

    /// <summary>Cast at, not yet small: its size falling.</summary>
    internal bool Shrinking => !float.IsNaN(shrinkAge) && !Shrunk;

    /// <summary>Arkane's <c>m_fMMSize</c>: 1 unscaled, <see cref="ShrinkRules.CastSize"/> from the cast, down to <see cref="ShrinkRules.Floor"/>.</summary>
    internal float Size { get; private set; } = 1;

    /// <summary>How big it's drawn and struck: its size, then the small model's <see cref="ShrinkRules.ShrunkScale"/>.</summary>
    internal float Scale => Shrunk ? ShrinkRules.ShrunkScale : Size;

    /// <summary>Shrinks it: health 1 now, small <see cref="ShrinkRules.SwapSeconds"/> on. An orc already shrinking keeps its own.</summary>
    internal void Shrink()
    {
        if (!Alive || Following || !float.IsNaN(shrinkAge)) return;
        Health = 1;
        WeakenedFor = CharmedFor = 0;
        shrinkAge = 0;
        Size = ShrinkRules.CastSize;
    }

    /// <summary>The player's body or kick on it small: it dies, whatever the blow asked.</summary>
    internal void Crush()
    {
        if (Alive && Shrunk) Damage(Health);
    }

    /// <summary>Its shrink this step; true once it's small, when it does nothing else but stand.</summary>
    bool StepShrink(float seconds)
    {
        if (float.IsNaN(shrinkAge) || !Alive) return false;
        if (!Shrunk)
        {
            shrinkAge += seconds;
            Size = ShrinkRules.SizeAt(shrinkAge);
            if (shrinkAge + 1e-4f < ShrinkRules.SwapSeconds) return false;
            Shrunk = true;
            Size = ShrinkRules.CastSize;
            Layer = null;
            hitLayer = null;
            Play(OrcState.Ready, "combatidle");
        }
        Advance(seconds);
        return true;
    }

    void ResetShrink()
    {
        shrinkAge = float.NaN;
        Shrunk = false;
        Size = 1;
    }
}
