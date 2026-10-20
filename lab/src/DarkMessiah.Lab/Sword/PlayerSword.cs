using System.Numerics;
using DarkMessiah.Shared;

namespace DarkMessiah.Lab.Sword;

/// <summary>What the player's sword arm is doing.</summary>
internal enum SwordMove { Ready, Charge, Fast, FastEnd, Power, Parry, ParryOut, Kick, Broken, Leap, LeapOut, LeapCancel, Rush }

/// <summary>
/// One first-person clip as a move plays it: how long it runs, whether it holds its last frame or loops, and the
/// cycles its blade is traced between (Arkane's 2186 and 2190 events; NaN when it has none).
/// </summary>
/// <param name="Phases">How long each phase plays against the clip's own timing: before the hit window (windup), in it
/// (the active, committed part) and after it (recovery). Null plays the clip as the donor does.</param>
/// <param name="Later">The hit windows after the first, of a move that strikes more than once (the rush attack's two
/// parts); phases time the first only.</param>
internal sealed record MoveClip(string Clip, float Seconds, bool Loop, float Open, float Close, MovePhases? Phases = null,
    (float Open, float Close)[]? Later = null)
{
    internal bool Traced => float.IsFinite(Open) && float.IsFinite(Close);

    /// <summary>Every hit window, in order.</summary>
    internal IReadOnlyList<(float Open, float Close)> Windows => [(Open, Close), .. Later ?? []];

    /// <summary>Where the last hit window closes.</summary>
    internal float LastClose => Later is { Length: > 0 } later ? later[^1].Close : Close;

    /// <summary>The hit window the cycles <paramref name="from"/> to <paramref name="to"/> overlap, or -1.</summary>
    internal int WindowAt(float from, float to)
    {
        if (!Traced) return -1;
        var windows = Windows;
        for (int i = 0; i < windows.Count; i++) if (from <= windows[i].Close && to >= windows[i].Open) return i;
        return -1;
    }

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

    /// <summary>
    /// A clip's timing as dm-import recorded it, played at <paramref name="rate"/>, with its first hit window: 2186 to
    /// 2190. Where a clip lacks one of them, 2107 opens it and 2110 closes it, which bracket 2186–2190 in the clips that
    /// have both:
    /// <list type="bullet">
    /// <item>the T and B power attacks have neither (they start with 2170): 2107 to 2110 holds the one recorded <c>B_out</c>
    /// blow (cycle 0.17 in 0.11–0.21);</item>
    /// <item>the jump attack's <c>combo_sword_fall_*_out</c> has 2107 and 2190: 0.03 to 0.27 holds its recorded blows
    /// (cycles 0.15 to 0.20).</item>
    /// </list>
    /// A clip that closes more than one window (2190) strikes in parts, the later ones from 2107 to 2190 (<see cref="Later"/>):
    /// the rush attack's two, at 0.09–0.17 and 0.47–0.57. Its second part's blows landed at cycles 0.466 and 0.48, before
    /// that part's 2186 (0.49).
    /// </summary>
    internal static MoveClip Of(ModelClip clip, float rate = 1, bool loop = false)
    {
        float First(int e) => clip.At(e).DefaultIfEmpty(float.NaN).First();
        float open = First(2186), close = First(2190);
        if (float.IsNaN(open)) open = First(2107);
        if (float.IsNaN(close)) close = First(2110);
        var opens = clip.At(2107).ToArray();
        var closes = clip.At(2190).ToArray();
        (float, float)[]? later = closes.Length > 1 && opens.Length == closes.Length ? opens.Zip(closes).Skip(1).ToArray() : null;
        return new(clip.Name, clip.Seconds / rate, loop, open, close, Later: later);
    }
}

/// <summary>
/// Which way a power attack goes, picked by the movement as its charge begins (docs/lab.md, "Fast and power attacks"):
/// back picks <c>B</c>, otherwise a strafe <c>L</c> or <c>R</c>, otherwise forward <c>T</c>; standing still the
/// neutral one. The hand-played fights show the order: a strafe with forward gives <c>L</c>/<c>R</c>, a strafe with back <c>B</c>.
/// </summary>
internal enum PowerDirection { Neutral, T, B, L, R }

/// <summary>One power attack's clips: its charge (<c>_in</c>), the held charge (<c>_idle</c>) and its release (<c>_out</c>).</summary>
internal sealed record PowerClips(MoveClip In, MoveClip Hold, MoveClip Out);

/// <summary>
/// The clips the player's sword moves play: the short sword without a shield. <see cref="Charges"/> holds the neutral
/// power attack and each directional one the model has.
/// </summary>
/// <param name="Chain">The fast attacks in the order they chain, <c>layer_fastattack_*_A</c> to <c>_E</c>.</param>
/// <param name="FastEnd">The recovery after an A or an E nothing chains onto, <c>layer_fastattack_*_end</c>.</param>
/// <param name="Leap">The jump attack's clips (the game's Leaping Attack); null when the model has none.</param>
/// <param name="Rush">The rush attack (the game's Charge, <c>layer_combo_sword_rush_noshield</c>, two strikes); null when the model has none.</param>
internal sealed record SwordClips(MoveClip Idle, IReadOnlyDictionary<PowerDirection, PowerClips> Charges, IReadOnlyList<MoveClip> Chain, MoveClip FastEnd,
    MoveClip ParryIn, MoveClip ParryHold, MoveClip ParryOut, MoveClip Kick, MoveClip Broken, LeapClips? Leap = null, MoveClip? Rush = null)
{
    internal const string Model = "models/npc/player/first_basic.mdl";

    /// <summary>The fast attack a chain starts with, A.</summary>
    internal MoveClip Fast => Chain[0];
    /// <summary>The second, B.</summary>
    internal MoveClip FastB => Chain[1];

    /// <summary>The neutral power attack's release.</summary>
    internal MoveClip Power => Charges[PowerDirection.Neutral].Out;

    /// <summary>Every move that swings or kicks: the fast attacks, each power attack's release, the kick, the jump attack's strike and the rush.</summary>
    internal IEnumerable<MoveClip> Moves => [.. Chain, .. Charges.Values.Select(c => c.Out), Kick, .. Leap is null ? [] : new[] { Leap.Out }, .. Rush is null ? [] : new[] { Rush }];

    /// <summary>The power attack's clips for <paramref name="direction"/>, the neutral ones when the model has none for it.</summary>
    internal PowerClips Charge(PowerDirection direction) => Charges.GetValueOrDefault(direction) ?? Charges[PowerDirection.Neutral];

    /// <summary>The direction the movement picks (side, forward; any non-zero component counts): see <see cref="PowerDirection"/>.</summary>
    internal static PowerDirection Direction(Vector2 move) =>
        move.Y < 0 ? PowerDirection.B : move.X < 0 ? PowerDirection.L : move.X > 0 ? PowerDirection.R : move.Y > 0 ? PowerDirection.T : PowerDirection.Neutral;

    /// <summary>
    /// The clips from the first-person model, attacks at the sword's <c>speed_attack</c> (1.10 for the short sword),
    /// the kick's contact window from the tuning (its clip has no hit events), and each move's phase timing from it.
    /// </summary>
    internal static SwordClips From(ModelInfo model, float attackSpeed, CombatTuning tuning)
    {
        MoveClip C(string name, float rate = 1, bool loop = false) => MoveClip.Of(model.Clips[name], rate, loop);
        var charges = new Dictionary<PowerDirection, PowerClips>();
        foreach (var direction in Enum.GetValues<PowerDirection>())
        {
            string at = direction == PowerDirection.Neutral ? "" : $"{direction}_";
            if (!model.Clips.ContainsKey($"layer_bigslash_sword_noshield_{at}out")) continue;
            charges[direction] = new PowerClips(C($"layer_bigslash_sword_noshield_{at}in", attackSpeed),
                C($"layer_bigslash_sword_noshield_{at}idle", loop: true), C($"layer_bigslash_sword_noshield_{at}out", attackSpeed) with { Phases = tuning.Phases.Power });
        }
        return new SwordClips(C("layer_sword_noshield_idle", loop: true), charges,
            "ABCDE".Select(c => C($"layer_fastattack_sword_noshield_{c}", attackSpeed) with { Phases = tuning.Phases.Fast }).ToList(),
            C("layer_fastattack_sword_noshield_end", attackSpeed),
            C("layer_parry_sword_noshield_in"), C("layer_parry_sword_noshield_idle", loop: true), C("layer_parry_sword_noshield_out"),
            C("layer_sword_noshield_kick_medium") with { Open = tuning.KickWindow[0], Close = tuning.KickWindow[1], Phases = tuning.Phases.Kick },
            C("layer_state_sword_parry_breaked"),
            model.Clips.ContainsKey("layer_combo_sword_fall_noshield_out")
                ? new LeapClips(C("layer_combo_sword_fall_noshield_jump_in"), C("layer_combo_sword_fall_noshield_idle", loop: true),
                    C("layer_combo_sword_fall_noshield_out"), C("layer_combo_sword_fall_noshield_cancel"))
                : null,
            model.Clips.ContainsKey("layer_combo_sword_rush_noshield") ? C("layer_combo_sword_rush_noshield") : null);
    }
}

/// <summary>
/// The jump attack's clips, <c>layer_combo_sword_fall_noshield_*</c>: the wind-up from a jump (<c>jump_in</c>), the
/// held pose (<c>idle</c>), the strike (<c>out</c>, HitPow 2) and the landing that cuts it short (<c>cancel</c>).
/// </summary>
internal sealed record LeapClips(MoveClip In, MoveClip Idle, MoveClip Out, MoveClip Cancel);

/// <summary>
/// The player's sword arm as Dark Messiah plays it, on admitted world seconds, as the hand-played fights show it
/// (docs/lab.md, "Fast and power attacks"):
/// <list type="bullet">
/// <item>pressing attack winds up the power attack the movement picks (<see cref="SwordClips.Direction"/>):
/// <c>layer_bigslash_*_in</c>, then its held idle;</item>
/// <item>releasing it before the wind-up has played is a fast attack (<c>layer_fastattack_*_A</c>), from the held idle
/// that power attack (<c>_out</c>): the directional wind-ups are longer than the neutral one;</item>
/// <item>a press while A plays chains B (<c>_B</c>) when A ends, and winds up under it: still held when B ends, the
/// charge goes on from there, its direction picked then. With <c>combat_chained_slash</c> the chain goes on through
/// C, D and E (<see cref="ChainLength"/>). An A or an E nothing chains onto recovers (<c>_end</c>), and a press in its
/// second half starts A again at once;</item>
/// <item>attack pressed in a jump (in the air, or just after landing, <see cref="Landing"/>) does nothing until
/// <see cref="AirDelay"/>, and is dropped if let go before <see cref="AirHoldMin"/>. Then, still held, in the air and with
/// <c>combat_vertical_attack</c> (<see cref="CanLeap"/>), the jump attack winds up (<c>combo_sword_fall_*_jump_in</c>),
/// holds (<c>_idle</c>) and strikes on release (<c>_out</c>); released early in its wind-up (before
/// <see cref="LeapReleaseOpen"/>) it is the neutral power attack instead, and a landing cuts it short (<c>_cancel</c>),
/// to the neutral power attack's hold. Otherwise the press holds the neutral power attack, released as it (at once if
/// let go already). Late in a kick (from <see cref="KickAttackOpen"/>)
/// a press in the air cuts the kick short the same way;</item>
/// <item>while sprinting, a charge released from its hold is the rush attack with <c>combat_rush_attack</c>
/// (<see cref="CanRush"/>; the game's Charge, Sprint+Power Strike), and one released before its hold plays nothing;</item>
/// <item>holding block parries (<c>layer_parry_*_in</c>, held, then <c>_out</c> on release), from the ground;</item>
/// <item>a kick asked for (pressed, and still held) plays <c>layer_sword_noshield_kick_medium</c> as soon as it may:
/// from any move but a kick or a broken parry, when the caller allows it (on the ground, with stamina,
/// <see cref="PlayerStamina"/>). Starting consumes the press;</item>
/// <item>a broken parry staggers it (<c>layer_state_sword_parry_breaked</c>). A blow the orc parries or blocks doesn't
/// stop the swing: the donor plays <c>layer_bounce_*</c> on none of the 23 recorded, and chains and kicks on after them.</item>
/// </list>
/// While a move's hit window is open (<see cref="Traced"/>) the lab traces the blade every step. No Engine calls:
/// the state and its clip and cycle are all it owns.
/// </summary>
internal sealed class PlayerSword(SwordClips clips)
{
    SwordClips clips = clips;
    // How long a charge has wound up under A and B (NaN when none): a press while A plays starts one.
    float under = float.NaN;
    // Seconds until a released charge's fast attack begins (NaN when none is due).
    float fastIn = float.NaN;
    // Seconds since an attack pressed in a jump (NaN when none is waiting), and since the body last landed.
    float airSince = float.NaN, landed = float.PositiveInfinity;
    PowerDirection direction;
    bool chained, chainDue, attackWas, kickWas, kickWanted, kickAttack;
    // How long the jump attack has held its pose, and how long a press in a jump was held.
    float leapHeld, airHold;

    internal SwordMove Move { get; private set; } = SwordMove.Ready;
    internal MoveClip Clip { get; private set; } = clips.Idle;
    internal float Cycle { get; private set; }
    /// <summary>The power attack the current charge would release.</summary>
    internal PowerDirection Direction => direction;
    /// <summary>Counts swings and kicks; a swing hits each target at most once.</summary>
    internal int Swing { get; private set; }
    /// <summary>The blade is traced this step: a swing's or kick's hit window is open.</summary>
    /// A step that crosses the whole window (a phase tuned shorter than a step) still counts: the window overlaps the
    /// cycles the step covered.
    internal bool Traced => Swinging && Window >= 0;
    /// <summary>The hit window this step's cycles overlap (the rush attack has two), or -1.</summary>
    internal int Window => Swinging ? Clip.WindowAt(stepFrom, Cycle) : -1;
    /// <summary>A move that swings or kicks plays: its hit window may be open.</summary>
    internal bool Swinging => Move is SwordMove.Fast or SwordMove.Power or SwordMove.Kick or SwordMove.LeapOut or SwordMove.Rush;
    /// <summary>The cycle to pose the blade at for this step's trace: inside the window, when the step ran past it.</summary>
    internal float TraceCycle => Window is int w and >= 0 ? Math.Clamp(Cycle, Clip.Windows[w].Open, Clip.Windows[w].Close) : Cycle;
    float stepFrom;
    /// <summary>Parrying: the guard is up (in, or held), which turns an orc's blow aside unless it breaks parries.</summary>
    internal bool Parrying => Move == SwordMove.Parry;
    /// <summary>A swing or kick is under way: the body moves at <see cref="CombatTuning.AttackMovementScale"/>.</summary>
    internal bool Busy => Move is not (SwordMove.Ready or SwordMove.Parry);
    /// <summary>True on the step a swing (not a wind-up) begins: what an orc sees coming.</summary>
    internal bool SwingStarted { get; private set; }
    /// <summary>
    /// The build has <c>combat_parry</c>: holding block parries. Every build has it (the combat skill's level 0); the gate
    /// is the module's name, not checked against the donor.
    /// </summary>
    internal bool CanParry { get; set; } = true;
    /// <summary>
    /// How many fast attacks chain: A and B without <c>combat_chained_slash</c>, A to E with it (the build sets it,
    /// docs/lab.md, "Fast and power attacks").
    /// </summary>
    internal int ChainLength { get; set; } = 2;
    /// <summary>The build has <c>combat_vertical_attack</c> (the combat skill's level 3, the game's Leaping Attack).</summary>
    internal bool CanLeap { get; set; }
    /// <summary>The build has <c>combat_rush_attack</c> (the combat skill's level 1, the game's Charge).</summary>
    internal bool CanRush { get; set; }
    /// <summary>A kick was asked for and hasn't started: it starts on the first step it may.</summary>
    internal bool KickWanted => kickWanted;

    /// <summary>
    /// From this cycle of <c>fastattack_end</c> a press starts A at once; before it the press is dropped. The hand-played
    /// fights show one of each (a press at 0.60 played A two ticks later, one at 0.32 nothing): the midpoint, not measured finer.
    /// </summary>
    internal const float FastEndOpen = .5f;

    /// <summary>
    /// From this cycle of a kick, attack pressed in the air releases the neutral power attack (three presses at
    /// 0.84–1.00 did, all mid-jump; one in the air at about 0.65 did nothing): between them, not measured finer.
    /// </summary>
    internal const float KickAttackOpen = .75f;

    /// <summary>
    /// How long after the release (or a press in A's recovery) the fast attack begins: the donor's A starts 2 to 3
    /// ticks later, 2.6 on average over 12 hand-played releases (0.04 s). Its chain keeps the donor's timing from there.
    /// </summary>
    internal float FastDelay { get; set; } = .04f;

    /// <summary>
    /// How long attack pressed in a jump must be held before anything plays: the jump attack's wind-up, or the neutral
    /// power attack's hold, began 8 or 9 ticks after the press in all 31 recorded (#10144: 8.4 on average, 0.125 s).
    /// Every press released sooner (4 or 5 ticks) played nothing, with or without the jump attack's skill.
    /// </summary>
    internal float AirDelay { get; set; } = .125f;

    /// <summary>
    /// An attack pressed in a jump and let go sooner than this is dropped: taps of 4 and 5 ticks played nothing (13), presses
    /// let go after 8 ticks still played the power attack at 9 (126588, 128150). Between them, not measured finer.
    /// </summary>
    internal float AirHoldMin { get; set; } = .1f;

    /// <summary>
    /// How long after landing an attack still counts as pressed in the jump: taps up to 16 ticks after touching down
    /// were dropped (or, held, made the jump's power attack), from 30 ticks they were ordinary presses (126664, 128223).
    /// Between them, not measured finer.
    /// </summary>
    internal float Landing { get; set; } = .35f;

    /// <summary>
    /// Released before this cycle of the jump attack's wind-up, the press is the neutral power attack; from it the wind-up
    /// plays out and strikes. Releases at 0.1 to 0.2 (six) gave the power attack, one at 0.9 the strike: between them, not
    /// measured finer.
    /// </summary>
    internal const float LeapReleaseOpen = .5f;

    internal void Retune(SwordClips nextClips) => clips = nextClips;

    /// <summary>Advances one step with the buttons as they are now (held); presses are their going down between steps.</summary>
    /// <param name="move">The movement input (side, forward), which picks a charge's direction.</param>
    /// <param name="mayKick">The body may kick now: on the ground, with the stamina for it.</param>
    /// <param name="airborne">The body is in the air: attack makes the jump attack, or the neutral power attack.</param>
    /// <param name="sprinting">The body sprints: a charge's release is the rush attack, or nothing.</param>
    internal void Step(float seconds, bool attackHeld, bool blockHeld, bool kickHeld, Vector2 move = default, bool mayKick = true, bool airborne = false,
        bool sprinting = false)
    {
        SwingStarted = false;
        landed = airborne ? 0 : landed + seconds;
        bool inJump = airborne || landed < Landing;
        stepFrom = Cycle;
        Cycle = Clip.Advance(Cycle, seconds);
        bool done = !Clip.Loop && Cycle >= 1;
        if (Clip.Loop && Cycle >= 1) Cycle -= MathF.Floor(Cycle);
        bool attackPressed = attackHeld && !attackWas;
        attackWas = attackHeld;
        // A kick press stands while its button is held, until a kick starts.
        if (kickHeld && !kickWas) kickWanted = true;
        else if (!kickHeld) kickWanted = false;
        kickWas = kickHeld;
        if (!attackHeld) under = float.NaN;
        else if (!float.IsNaN(under)) under += seconds;
        if (kickWanted && mayKick && Move is not (SwordMove.Kick or SwordMove.Broken))
        {
            kickWanted = false;
            Play(SwordMove.Kick, clips.Kick, swing: true);
            return;
        }
        if (!float.IsNaN(airSince))
        {
            // A press in a jump waits: dropped if let go soon, else the jump attack, the neutral power attack's hold, or
            // (let go already) the neutral power attack.
            airSince += seconds;
            if (attackHeld) airHold = airSince;
            if (!attackHeld && airHold < AirHoldMin) airSince = float.NaN;
            else if (airSince >= AirDelay)
            {
                airSince = float.NaN;
                if (!attackHeld) Play(SwordMove.Power, clips.Power, swing: true);
                else if (airborne && CanLeap && clips.Leap is { } leap) Play(SwordMove.Leap, leap.In);
                else AirCharge();
            }
            return;
        }
        if (!float.IsNaN(fastIn))
        {
            // A press while A is due chains B already, and winds up under it, as one in A does.
            if (attackPressed) { chainDue = true; under = 0; }
            fastIn -= seconds;
            if (fastIn <= 0)
            {
                fastIn = float.NaN;
                PlayFast(clips.Fast);
                chained = chainDue && ChainLength > 1;
            }
            return;
        }
        switch (Move)
        {
            case SwordMove.Ready:
                if (attackHeld && inJump) airSince = airHold = 0;
                else if (attackHeld) Charge(move, 0);
                // No parry starts in the air: block pressed mid-jump (128149, held 4 ticks) played none.
                else if (blockHeld && CanParry && !airborne) Play(SwordMove.Parry, clips.ParryIn);
                break;
            case SwordMove.Charge:
                var charge = clips.Charge(direction);
                if (!attackHeld)
                {
                    if (Clip == charge.Hold)
                    {
                        if (sprinting && CanRush && clips.Rush is { } rush) Play(SwordMove.Rush, rush, swing: true);
                        else Play(SwordMove.Power, charge.Out, swing: true);
                    }
                    else if (sprinting) Play(SwordMove.Ready, clips.Idle);
                    else StartFast();
                }
                else if (done && Clip == charge.In) Play(SwordMove.Charge, charge.Hold);
                break;
            case SwordMove.Parry:
                if (!blockHeld) Play(SwordMove.ParryOut, clips.ParryOut);
                else if (done && Clip == clips.ParryIn) Play(SwordMove.Parry, clips.ParryHold);
                break;
            case SwordMove.Fast:
                int at = ChainIndex();
                // A press while one plays chains the next (one deep), up to the build's chain length; it also winds up under it.
                if (attackPressed && !chained && at + 1 < Math.Min(ChainLength, clips.Chain.Count)) { chained = true; under = 0; }
                if (!done) break;
                if (chained) PlayFast(clips.Chain[at + 1]);
                // A, and E (the last), recover when nothing chains onto them; the others go on winding up if attack is
                // held, from the press under them or from now, or end ready.
                else if (at == 0 || at == clips.Chain.Count - 1) Play(SwordMove.FastEnd, clips.FastEnd);
                else if (attackHeld) Charge(move, float.IsNaN(under) ? 0 : under);
                else Play(SwordMove.Ready, clips.Idle);
                break;
            case SwordMove.Kick:
                kickAttack |= attackPressed && airborne && Cycle >= KickAttackOpen;
                if (kickAttack && !attackHeld) Play(SwordMove.Power, clips.Power, swing: true);
                else if (done) Play(SwordMove.Ready, clips.Idle);
                break;
            case SwordMove.Leap:
                Leap(seconds, attackHeld, airborne, done);
                break;
            case SwordMove.LeapCancel:
                if (done) { if (attackHeld) AirCharge(); else Play(SwordMove.Power, clips.Power, swing: true); }
                break;
            case SwordMove.FastEnd:
                if (attackPressed && Cycle >= FastEndOpen) StartFast();
                else if (done) Play(SwordMove.Ready, clips.Idle);
                break;
            default:
                if (done) Play(SwordMove.Ready, clips.Idle);
                break;
        }
    }

    /// <summary>
    /// The jump attack's wind-up and hold (#10144): released early in the wind-up it is the neutral power attack; else the
    /// wind-up plays out, the hold plays once at least, and the release strikes. Landing first cuts it short.
    /// </summary>
    void Leap(float seconds, bool attackHeld, bool airborne, bool done)
    {
        var leap = clips.Leap!;
        if (!airborne) Play(SwordMove.LeapCancel, leap.Cancel);
        else if (Clip == leap.In)
        {
            if (!attackHeld && Cycle < LeapReleaseOpen) Play(SwordMove.Power, clips.Power, swing: true);
            else if (done) { Play(SwordMove.Leap, leap.Idle); leapHeld = 0; }
        }
        else
        {
            // The pose plays through once (4 ticks in every recorded strike) before the release strikes.
            if ((leapHeld += seconds) >= leap.Idle.Seconds && !attackHeld) Play(SwordMove.LeapOut, leap.Out, swing: true);
        }
    }

    /// <summary>The neutral power attack's hold at once, no wind-up: what a press in a jump holds without the jump attack.</summary>
    void AirCharge()
    {
        direction = PowerDirection.Neutral;
        Play(SwordMove.Charge, clips.Charge(PowerDirection.Neutral).Hold);
    }

    /// <summary>Winds up the power attack <paramref name="move"/> picks, <paramref name="wound"/> seconds in already.</summary>
    void Charge(Vector2 move, float wound)
    {
        direction = SwordClips.Direction(move);
        var charge = clips.Charge(direction);
        float duration = charge.In.Duration;
        if (duration > 0 && wound >= duration) Play(SwordMove.Charge, charge.Hold);
        else
        {
            Play(SwordMove.Charge, charge.In);
            Cycle = stepFrom = duration > 0 ? wound / duration : 0;
        }
    }

    int ChainIndex()
    {
        for (int i = 0; i < clips.Chain.Count; i++) if (ReferenceEquals(clips.Chain[i], Clip)) return i;
        return 0;
    }

    /// <summary>A fast attack A, after <see cref="FastDelay"/>.</summary>
    void StartFast()
    {
        chainDue = false;
        if (FastDelay <= 0) PlayFast(clips.Fast);
        else fastIn = FastDelay;
    }

    void PlayFast(MoveClip clip)
    {
        chained = false;
        Play(SwordMove.Fast, clip, swing: true);
    }

    /// <summary>An orc's blow broke the parry: the guard drops and the arm staggers.</summary>
    internal void Break() => Play(SwordMove.Broken, clips.Broken);

    internal void Reset()
    {
        attackWas = kickWas = kickWanted = chained = false;
        under = fastIn = airSince = float.NaN;
        landed = float.PositiveInfinity;
        Play(SwordMove.Ready, clips.Idle);
    }

    void Play(SwordMove move, MoveClip clip, bool swing = false)
    {
        Move = move;
        Clip = clip;
        Cycle = 0;
        stepFrom = 0;
        kickAttack = false;
        fastIn = float.NaN;
        if (swing) { Swing++; SwingStarted = true; }
    }
}
