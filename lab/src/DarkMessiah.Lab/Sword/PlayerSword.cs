using DarkMessiah.Shared;

namespace DarkMessiah.Lab.Sword;

/// <summary>What the player's sword arm is doing.</summary>
internal enum SwordMove { Ready, Charge, Fast, Power, Parry, ParryOut, Kick, Bounce, Broken }

/// <summary>
/// One first-person clip as a move plays it: how long it runs, whether it holds its last frame or loops, and the
/// cycles its blade is traced between (Arkane's 2186 and 2190 events; NaN when it has none).
/// </summary>
/// <param name="Phases">How long each phase plays against the clip's own timing: before the hit window (windup), in it
/// (the active, committed part) and after it (recovery). Null plays the clip as the donor does.</param>
internal sealed record MoveClip(string Clip, float Seconds, bool Loop, float Open, float Close, MovePhases? Phases = null)
{
    internal bool Traced => float.IsFinite(Open) && float.IsFinite(Close);

    /// <summary>How long one play takes, its phases scaled.</summary>
    internal float Duration => !Traced || Phases is null ? Seconds
        : Seconds * (Open * Phases.Windup + (Close - Open) * Phases.Active + (1 - Close) * Phases.Recovery);

    /// <summary>The cycle <paramref name="seconds"/> after <paramref name="cycle"/>, each phase played at its own scale.</summary>
    internal float Advance(float cycle, float seconds)
    {
        if (Seconds <= 0) return cycle + 1;
        if (!Traced || Phases is null) return cycle + seconds / Seconds;
        while (seconds > 0)
        {
            var (end, scale) = cycle < Open ? (Open, Phases.Windup) : cycle < Close ? (Close, Phases.Active) : (float.PositiveInfinity, Phases.Recovery);
            float need = (end - cycle) * Seconds * scale;
            if (seconds < need) return cycle + seconds / (Seconds * scale);
            cycle = end;
            seconds -= need;
        }
        return cycle;
    }

    /// <summary>A clip's timing as dm-import recorded it, played at <paramref name="rate"/>, with its first hit window.</summary>
    internal static MoveClip Of(ModelClip clip, float rate = 1, bool loop = false) =>
        new(clip.Name, clip.Seconds / rate, loop, clip.At(2186).DefaultIfEmpty(float.NaN).First(), clip.At(2190).DefaultIfEmpty(float.NaN).First());
}

/// <summary>The clips the player's sword moves play: the short sword without a shield.</summary>
internal sealed record SwordClips(MoveClip Idle, MoveClip ChargeIn, MoveClip ChargeHold, MoveClip Fast, MoveClip Power,
    MoveClip ParryIn, MoveClip ParryHold, MoveClip ParryOut, MoveClip Kick, MoveClip Bounce, MoveClip Broken)
{
    internal const string Model = "models/npc/player/first_basic.mdl";

    /// <summary>
    /// The clips from the first-person model, attacks at the sword's <c>speed_attack</c> (1.10 for the short sword),
    /// the kick's contact window from the tuning (its clip has no hit events), and each move's phase timing from it.
    /// </summary>
    internal static SwordClips From(ModelInfo model, float attackSpeed, CombatTuning tuning)
    {
        MoveClip C(string name, float rate = 1, bool loop = false) => MoveClip.Of(model.Clips[name], rate, loop);
        return new SwordClips(C("layer_sword_noshield_idle", loop: true), C("layer_bigslash_sword_noshield_in", attackSpeed),
            C("layer_bigslash_sword_noshield_idle", loop: true), C("layer_fastattack_sword_noshield_A", attackSpeed) with { Phases = tuning.Phases.Fast },
            C("layer_bigslash_sword_noshield_out", attackSpeed) with { Phases = tuning.Phases.Power }, C("layer_parry_sword_noshield_in"),
            C("layer_parry_sword_noshield_idle", loop: true), C("layer_parry_sword_noshield_out"),
            C("layer_sword_noshield_kick_medium") with { Open = tuning.KickWindow[0], Close = tuning.KickWindow[1], Phases = tuning.Phases.Kick },
            C("layer_bounce_sword_noshield"), C("layer_state_sword_parry_breaked"));
    }
}

/// <summary>
/// The player's sword arm as Dark Messiah plays it, on admitted world seconds:
/// <list type="bullet">
/// <item>pressing attack winds up (<c>layer_bigslash_*_in</c>, then its held idle);</item>
/// <item>releasing it within <see cref="CombatTuning.PowerHoldSeconds"/> is a fast attack (<c>layer_fastattack_*_A</c>), later a power attack (<c>layer_bigslash_*_out</c>);</item>
/// <item>holding block parries (<c>layer_parry_*_in</c>, held, then <c>_out</c> on release);</item>
/// <item>kick plays <c>layer_sword_noshield_kick_medium</c>, from ready, a parry, or a swing whose hit window has
/// passed (recorded kicks cut power attacks off at about cycle 0.45);</item>
/// <item>a blow the orc parries bounces the sword (<c>layer_bounce_*</c>), and a broken parry staggers it (<c>layer_state_sword_parry_breaked</c>).</item>
/// </list>
/// While a move's hit window is open (<see cref="Traced"/>) the lab traces the blade every step. No Engine calls:
/// the state and its clip and cycle are all it owns.
/// </summary>
internal sealed class PlayerSword(SwordClips clips, CombatTuning tuning)
{
    SwordClips clips = clips;
    CombatTuning tuning = tuning;
    float held;

    internal SwordMove Move { get; private set; } = SwordMove.Ready;
    internal MoveClip Clip { get; private set; } = clips.Idle;
    internal float Cycle { get; private set; }
    /// <summary>Counts swings and kicks; a swing hits each target at most once.</summary>
    internal int Swing { get; private set; }
    /// <summary>The blade is traced this step: a swing's or kick's hit window is open.</summary>
    internal bool Traced => Move is SwordMove.Fast or SwordMove.Power or SwordMove.Kick && Clip.Traced && Cycle >= Clip.Open && Cycle <= Clip.Close;
    /// <summary>Parrying: the guard is up (in, or held), which turns an orc's blow aside unless it breaks parries.</summary>
    internal bool Parrying => Move == SwordMove.Parry;
    /// <summary>A swing or kick is under way: the body moves at <see cref="CombatTuning.AttackMovementScale"/>.</summary>
    internal bool Busy => Move is not (SwordMove.Ready or SwordMove.Parry);
    /// <summary>True on the step a swing (not a wind-up) begins: what an orc sees coming.</summary>
    internal bool SwingStarted { get; private set; }

    internal void Retune(SwordClips nextClips, CombatTuning nextTuning) { clips = nextClips; tuning = nextTuning; }

    /// <summary>Advances one step with the buttons as they are now (held) and the kick's press edge.</summary>
    internal void Step(float seconds, bool attackHeld, bool blockHeld, bool kickPressed)
    {
        SwingStarted = false;
        Cycle = Clip.Advance(Cycle, seconds);
        bool done = !Clip.Loop && Cycle >= 1;
        if (Clip.Loop && Cycle >= 1) Cycle -= MathF.Floor(Cycle);
        switch (Move)
        {
            case SwordMove.Ready:
                if (kickPressed) Play(SwordMove.Kick, clips.Kick, swing: true);
                else if (attackHeld) { held = 0; Play(SwordMove.Charge, clips.ChargeIn); }
                else if (blockHeld) Play(SwordMove.Parry, clips.ParryIn);
                break;
            case SwordMove.Charge:
                held += seconds;
                if (!attackHeld) Play(held < tuning.PowerHoldSeconds ? SwordMove.Fast : SwordMove.Power,
                    held < tuning.PowerHoldSeconds ? clips.Fast : clips.Power, swing: true);
                else if (done && Clip == clips.ChargeIn) Play(SwordMove.Charge, clips.ChargeHold);
                break;
            case SwordMove.Parry:
                if (!blockHeld) Play(SwordMove.ParryOut, clips.ParryOut);
                else if (kickPressed) Play(SwordMove.Kick, clips.Kick, swing: true);
                else if (done && Clip == clips.ParryIn) Play(SwordMove.Parry, clips.ParryHold);
                break;
            case SwordMove.Fast or SwordMove.Power when kickPressed && Clip.Traced && Cycle > Clip.Close:
                Play(SwordMove.Kick, clips.Kick, swing: true);
                break;
            default:
                if (done) Play(SwordMove.Ready, clips.Idle);
                break;
        }
    }

    /// <summary>The orc parried the blade: the swing ends in a bounce.</summary>
    internal void Bounce() => Play(SwordMove.Bounce, clips.Bounce);

    /// <summary>An orc's blow broke the parry: the guard drops and the arm staggers.</summary>
    internal void Break() => Play(SwordMove.Broken, clips.Broken);

    internal void Reset() => Play(SwordMove.Ready, clips.Idle);

    void Play(SwordMove move, MoveClip clip, bool swing = false)
    {
        Move = move;
        Clip = clip;
        Cycle = 0;
        if (swing) { Swing++; SwingStarted = true; }
    }
}
