using System.Globalization;
using Rusty.Engine;

namespace DarkMessiah.Lab.Time;

/// <summary>
/// The lab's world clock policy over the Engine's <see cref="IGameplayTimeService"/>: run at a chosen rate (1 is
/// realtime, below it slow motion), hold the world, or step it by a bounded amount and hold. Look, the camera and
/// the HUD keep updating while the world holds, because selecting gameplay time makes every host observation an
/// update. This is the product's own world time; the Engine's inspection time (<c>engine.time.mode manual</c>,
/// <c>engine.time.advance MS</c>) is a separate harness hold that overrides it and is left untouched here.
/// Provenance: rusty-riders <c>Time/TimeFlow</c> (the one-owner shape over GameplayTime) and rusty-craftsurvive
/// (a hold that lets already-admitted steps finish); the developer rate/hold/step controls are fptester's.
/// </summary>
internal sealed class WorldTime(IEngineContext engine)
{
    internal const double StepSeconds = 1d / 60;

    /// <summary>The running rate chosen, kept while held so resuming returns to it.</summary>
    internal double Rate { get; private set; } = GameplayTimeConvenience.RealtimeRate;
    internal bool Held { get; private set; }
    /// <summary>The selection as the last update reported it.</summary>
    internal (double Rate, uint AdvanceSteps) Reported { get; private set; }

    /// <summary>Selects the current choice; call at start and after an Engine restart (which returns to realtime).</summary>
    internal void Apply()
    {
        if (Held) engine.GameplayTime.Hold();
        else engine.GameplayTime.SetRate(Rate);
    }

    internal void Observe(ProductUpdateFacts facts) => Reported = (facts.GameplayRate, facts.GameplayAdvanceRemainingSteps);

    /// <summary>Runs the world at <paramref name="rate"/> of realtime, (0, 1].</summary>
    internal string? SetRate(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0 || rate > 1) return "Rate is above 0 and at most 1 (the Engine refuses faster than realtime).";
        Rate = rate;
        Held = false;
        Apply();
        return null;
    }

    internal void Hold() { Held = true; Apply(); }
    internal void Run() { Held = false; Apply(); }
    internal void Toggle() { if (Held) Run(); else Hold(); }

    /// <summary>Runs <paramref name="seconds"/> of world time (rounded up to whole steps) at the chosen rate, then holds.</summary>
    internal string? Step(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 10) return "Step seconds are above 0 and at most 10.";
        Held = true;
        engine.GameplayTime.Advance(seconds, Rate);
        return null;
    }

    internal string Describe() => Reported.AdvanceSteps > 0
        ? string.Create(CultureInfo.InvariantCulture, $"stepping · {Reported.AdvanceSteps} steps left")
        : Held ? "held" : Rate >= 1 ? "realtime" : string.Create(CultureInfo.InvariantCulture, $"slow · {Rate:0.00}×");
}
