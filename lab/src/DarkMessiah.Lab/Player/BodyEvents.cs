namespace DarkMessiah.Lab.Player;

/// <summary>What the body did in one controller step that a product may want to hear, see or count.</summary>
[Flags]
internal enum BodyEvent
{
    None = 0,
    Footstep = 1,
    Jump = 2,
    Land = 4,
    HardLanding = 8,
}

/// <summary>
/// Reads body events from one step's before and after motion: a footstep every stride walked on the ground, a jump
/// when the body starts rising from the ground, and a landing (hard after a fast fall) when it comes back down. Keeps
/// only the distance walked since the last footstep and counts for the observation. Products raise their cues
/// (sound, dust, camera kick, fall damage) from the returned flags.
/// Provenance: rusty-craftsurvive <c>PlayerFootfalls</c>, without its water and climbing events.
/// </summary>
internal sealed class BodyEvents
{
    internal const float StrideMetres = 1.7f, JumpRiseSpeed = 2, LandFallSpeed = 3, HardLandingFallSpeed = 9;

    private float walked;

    internal uint Jumps { get; private set; }
    internal uint Landings { get; private set; }
    internal uint HardLandings { get; private set; }
    /// <summary>How fast the body was falling at its last landing, metres per second.</summary>
    internal float LastLandingSpeed { get; private set; }

    internal BodyEvent Hear(bool groundedBefore, bool grounded, float fallSpeedBefore, float riseSpeed, float planarMetres)
    {
        BodyEvent heard = BodyEvent.None;
        if (groundedBefore && grounded && (walked += planarMetres) >= StrideMetres)
        {
            walked -= StrideMetres;
            heard |= BodyEvent.Footstep;
        }
        // The step that launches a jump may still report the body grounded, so a jump is starting to rise from the ground.
        if (groundedBefore && riseSpeed >= JumpRiseSpeed && -fallSpeedBefore < JumpRiseSpeed)
        {
            heard |= BodyEvent.Jump;
            Jumps++;
        }
        if (!groundedBefore && grounded && fallSpeedBefore >= LandFallSpeed)
        {
            walked = 0; // a landing is a footfall too: the next stride starts here
            LastLandingSpeed = fallSpeedBefore;
            Landings++;
            if (fallSpeedBefore >= HardLandingFallSpeed) { heard |= BodyEvent.HardLanding; HardLandings++; }
            else heard |= BodyEvent.Land;
        }
        return heard;
    }

    internal void Reset()
    {
        walked = 0;
        Jumps = Landings = HardLandings = 0;
        LastLandingSpeed = 0;
    }
}
