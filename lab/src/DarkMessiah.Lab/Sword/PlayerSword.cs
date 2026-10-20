using System.Numerics;
using DarkMessiah.Shared;

namespace DarkMessiah.Lab.Sword;

/// <summary>What the player's sword arm is doing.</summary>
internal enum SwordMove { Ready, Charge, Fast, FastEnd, Power, Parry, ParryOut, Kick, Broken }

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

    /// <summary>
    /// A clip's timing as dm-import recorded it, played at <paramref name="rate"/>, with its first hit window: 2186 to
    /// 2190. The T and B power attacks have neither (they start with 2170 instead): their window is 2107 to 2110, which
    /// brackets 2186–2190 in the clips that have both, and holds the one recorded <c>B_out</c> blow (cycle 0.17 in 0.11–0.21).
    /// </summary>
    internal static MoveClip Of(ModelClip clip, float rate = 1, bool loop = false)
    {
        float First(int e) => clip.At(e).DefaultIfEmpty(float.NaN).First();
        bool thrust = float.IsNaN(First(2186)) && float.IsNaN(First(2190)) && float.IsFinite(First(2170));
        return new(clip.Name, clip.Seconds / rate, loop, First(thrust ? 2107 : 2186), First(thrust ? 2110 : 2190));
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
internal sealed record SwordClips(MoveClip Idle, IReadOnlyDictionary<PowerDirection, PowerClips> Charges, IReadOnlyList<MoveClip> Chain, MoveClip FastEnd,
    MoveClip ParryIn, MoveClip ParryHold, MoveClip ParryOut, MoveClip Kick, MoveClip Broken)
{
    internal const string Model = "models/npc/player/first_basic.mdl";

    /// <summary>The fast attack a chain starts with, A.</summary>
    internal MoveClip Fast => Chain[0];
    /// <summary>The second, B.</summary>
    internal MoveClip FastB => Chain[1];

    /// <summary>The neutral power attack's release.</summary>
    internal MoveClip Power => Charges[PowerDirection.Neutral].Out;

    /// <summary>Every move that swings or kicks: the fast attacks, each power attack's release and the kick.</summary>
    internal IEnumerable<MoveClip> Moves => [.. Chain, .. Charges.Values.Select(c => c.Out), Kick];

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
            C("layer_state_sword_parry_breaked"));
    }
}

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
/// <item>a press late in a kick (from <see cref="KickAttackOpen"/>) released before the kick ends releases the neutral
/// power attack at once; still held, the arm winds up when the kick ends;</item>
/// <item>holding block parries (<c>layer_parry_*_in</c>, held, then <c>_out</c> on release);</item>
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
    PowerDirection direction;
    bool chained, attackWas, kickWas, kickWanted, kickAttack;

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
    internal bool Traced => Move is SwordMove.Fast or SwordMove.Power or SwordMove.Kick && Clip.Traced && stepFrom <= Clip.Close && Cycle >= Clip.Open;
    /// <summary>The cycle to pose the blade at for this step's trace: inside the window, when the step ran past it.</summary>
    internal float TraceCycle => Clip.Traced ? Math.Clamp(Cycle, Clip.Open, Clip.Close) : Cycle;
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
    /// <summary>A kick was asked for and hasn't started: it starts on the first step it may.</summary>
    internal bool KickWanted => kickWanted;

    /// <summary>
    /// From this cycle of <c>fastattack_end</c> a press starts A at once; before it the press is dropped. The hand-played
    /// fights show one of each (a press at 0.60 played A two ticks later, one at 0.32 nothing): the midpoint, not measured finer.
    /// </summary>
    internal const float FastEndOpen = .5f;

    /// <summary>
    /// From this cycle of the kick a tap of attack releases the neutral power attack (three presses at 0.84–1.00 did,
    /// two at about 0.65 did nothing): between them, not measured finer.
    /// </summary>
    internal const float KickAttackOpen = .75f;

    internal void Retune(SwordClips nextClips) => clips = nextClips;

    /// <summary>Advances one step with the buttons as they are now (held); presses are their going down between steps.</summary>
    /// <param name="move">The movement input (side, forward), which picks a charge's direction.</param>
    /// <param name="mayKick">The body may kick now: on the ground, with the stamina for it.</param>
    internal void Step(float seconds, bool attackHeld, bool blockHeld, bool kickHeld, Vector2 move = default, bool mayKick = true)
    {
        SwingStarted = false;
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
        switch (Move)
        {
            case SwordMove.Ready:
                if (attackHeld) Charge(move, 0);
                else if (blockHeld && CanParry) Play(SwordMove.Parry, clips.ParryIn);
                break;
            case SwordMove.Charge:
                var charge = clips.Charge(direction);
                if (!attackHeld) { if (Clip == charge.Hold) Play(SwordMove.Power, charge.Out, swing: true); else PlayFast(clips.Fast); }
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
                kickAttack |= attackPressed && Cycle >= KickAttackOpen;
                if (kickAttack && !attackHeld) Play(SwordMove.Power, clips.Power, swing: true);
                else if (done) Play(SwordMove.Ready, clips.Idle);
                break;
            case SwordMove.FastEnd:
                if (attackPressed && Cycle >= FastEndOpen) PlayFast(clips.Fast);
                else if (done) Play(SwordMove.Ready, clips.Idle);
                break;
            default:
                if (done) Play(SwordMove.Ready, clips.Idle);
                break;
        }
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
        under = float.NaN;
        Play(SwordMove.Ready, clips.Idle);
    }

    void Play(SwordMove move, MoveClip clip, bool swing = false)
    {
        Move = move;
        Clip = clip;
        Cycle = 0;
        stepFrom = 0;
        kickAttack = false;
        if (swing) { Swing++; SwingStarted = true; }
    }
}
