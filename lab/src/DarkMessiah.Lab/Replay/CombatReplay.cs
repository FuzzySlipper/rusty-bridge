using System.Numerics;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Sword;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Replay;

/// <summary>A decided blow on one side of the comparison: the donor's, from the recording, or the lab's.</summary>
internal sealed record ReplayBlow(int Tick, double Time, string Move, DefenseResult Verdict, float Damage, int? Group)
{
    public override string ToString() =>
        Verdict is DefenseResult.Hit or DefenseResult.PlayerParryBroken
            ? FormattableString.Invariant($"{Verdict} {Damage:0.##}{(Group is int g ? $" (group {g})" : "")} by {Move}")
            : $"{Verdict} by {Move}";
}

/// <summary>How a donor blow and the lab's compare: <c>agree</c>, <c>differ</c>, <c>donor only</c> or <c>lab only</c>.</summary>
internal sealed record BlowMatch(string Result, ReplayBlow? Donor, ReplayBlow? Lab)
{
    public override string ToString()
    {
        var at = Donor ?? Lab!;
        return FormattableString.Invariant($"{Result,-9} {at.Time,7:0.00} s, tick {at.Tick}: donor {Donor?.ToString() ?? "nothing"}; lab {Lab?.ToString() ?? "nothing"}");
    }
}

/// <summary>One side's blows compared: the player's on the followed orc, or that orc's on the player.</summary>
internal sealed record BlowComparison(string Side, IReadOnlyList<BlowMatch> Matches)
{
    internal int Count(string result) => Matches.Count(m => m.Result == result);
    internal int Donor => Matches.Count(m => m.Donor is not null);
    internal int Lab => Matches.Count(m => m.Lab is not null);

    public override string ToString() =>
        $"{Side}: donor {Donor}, lab {Lab}; {Count("agree")} agree, {Count("differ")} differ, {Count("donor only")} donor only, {Count("lab only")} lab only";
}

/// <summary>
/// What a replay found (<see cref="CombatReplay"/>): the moves each side began, and both sides' blows paired up
/// within <see cref="CombatReplay.Window"/>.
/// </summary>
internal sealed record ReplayReport(string Name, int Ticks, double Seconds, int Skipped,
    IReadOnlyDictionary<string, (int Donor, int Lab, int Together)> Moves, double MeanLagSeconds,
    BlowComparison PlayerBlows, BlowComparison OrcBlows, IReadOnlyList<string> NotCompared,
    KickRequests? Kicks = null, BodyReplay? Body = null, IReadOnlyDictionary<string, (int Donor, int Lab, int Together)>? Clips = null,
    IReadOnlyList<(int Tick, bool ByPlayer, string Class)>? Excluded = null, string? Build = null, (int Near, int Ticks)? Adrenaline = null,
    IReadOnlyList<(int Tick, int Modules, float MaxStamina)>? Rebuilt = null, ShieldWear? Shield = null,
    (int Near, int Ticks, float Worst)? Stamina = null, (int Near, int Ticks, float Median)? Camera = null,
    (int Near, int Ticks, float Worst, int MaxAgree)? Health = null, (int Near, int Ticks, float Median, float Worst)? CameraPlace = null,
    (int Near, int Ticks, float Worst)? Mana = null, FlinchReplay? Flinch = null)
{
    internal IEnumerable<string> Lines()
    {
        yield return FormattableString.Invariant($"{Name}: {Ticks} ticks replayed ({Seconds:0.0} s), {Skipped} recorded frames without input");
        if (Health is var (healthNear, healthTicks, healthWorst, maxAgree))
            yield return FormattableString.Invariant($"  health: the lab's within {CombatReplay.HealthTolerance} of the donor's on {healthNear} of {healthTicks} ticks (worst {healthWorst:0.##}), its maximum the donor's on {maxAgree}");
        if (Mana is var (manaNear, manaTicks, manaWorst))
            yield return FormattableString.Invariant($"  mana: the lab's within {CombatReplay.ManaTolerance} of the donor's on {manaNear} of {manaTicks} ticks (worst {manaWorst:0.##})");
        if (Camera is var (near, turned, median))
            yield return FormattableString.Invariant($"  camera: where the donor's head turned it ({turned} ticks), the lab's within {CombatReplay.CameraTolerance}° on {near} (median {median:0.0}°)");
        if (CameraPlace is var (placedNear, placed, placeMedian, placeWorst))
            yield return FormattableString.Invariant($"  camera place: the lab's within {CombatReplay.CameraPlaceTolerance} units of the donor's on {placedNear} of {placed} ticks (median {placeMedian:0.00}, worst {placeWorst:0.0})");
        foreach (var (clip, m) in Moves)
            yield return $"  {clip}: donor {m.Donor}, lab {m.Lab}, {m.Together} begin together";
        if (Clips is { Count: > 0 } c)
            yield return $"  by clip: {string.Join("; ", c.OrderByDescending(x => x.Value.Donor).Select(x => $"{x.Key.Replace("layer_", "").Replace("_sword_noshield", "")} donor {x.Value.Donor}, lab {x.Value.Lab}, {x.Value.Together} together"))}";
        if (double.IsFinite(MeanLagSeconds))
            yield return FormattableString.Invariant($"  the lab begins a move {MeanLagSeconds * 1000:0} ms after the donor on average");
        if (Build is { } build) yield return $"  build: {build}";
        if (Kicks is { } k) yield return $"  {k}";
        if (Adrenaline is { Ticks: > 0 } a)
            yield return FormattableString.Invariant($"  adrenaline: the lab's gauge, on its own blows from the donor's start, within 1 of the donor's on {a.Near} of {a.Ticks} ticks ({100.0 * a.Near / a.Ticks:0}%)");
        if (Body is { } b) yield return $"  {b}";
        if (Shield is { } shield) yield return $"  {shield}";
        if (Flinch is { } flinch) yield return $"  {flinch}";
        foreach (var side in new[] { PlayerBlows, OrcBlows })
        {
            yield return $"  {side}";
            foreach (var m in side.Matches.Where(m => m.Result != "agree")) yield return $"    {m}";
        }
        foreach (var line in NotCompared) yield return $"  not compared: {line}";
    }

    public override string ToString() => string.Join(Environment.NewLine, Lines());
}

/// <summary>
/// One of the lab's blows on the followed orc with the procedural reaction on (R10382-1): what the model made of it, what
/// the lab's orc did (cut short: taken over until it had its footing back, sliding and turning; seconds, metres and
/// degrees), and what the donor's orc did (<see cref="CombatReplay.DonorReaction"/>).
/// </summary>
/// <param name="Released">Why the lab's orc was given back: <c>its footing back</c>, or the recording's doing (another orc
/// followed, the recorded orc dead), which cuts its busy time, slide and turn short.</param>
internal sealed record FlinchTakeover(int Tick, FlinchOutcome Model, bool LabCut, float LabBusy, float LabMoved, float LabTurned, int LabOrcBlows,
    bool DonorCut, float DonorBusy, float DonorMoved, float DonorTurned, string Released = "")
{
    internal bool OwnEnd => Released == "its footing back";
}

/// <summary>
/// The procedural reaction played in a replay (R10382-1, <c>orc.flinch.on</c>): the lab's blows on the followed orc;
/// on how many the reaction cut it short as the donor's orc was; where both were, the medians of how far the lab's
/// busy time, slide and turn were from the donor's; the lean's peak, drawn over the recorded pose.
/// </summary>
internal sealed record FlinchReplay(IReadOnlyList<FlinchTakeover> Blows, float PeakLean)
{
    internal int Agree => Blows.Count(b => b.LabCut == b.DonorCut);
    internal int DonorCut => Blows.Count(b => b.DonorCut);
    internal int LabCut => Blows.Count(b => b.LabCut);

    static float Median(IEnumerable<float> values) { var v = values.Where(float.IsFinite).Order().ToList(); return v.Count > 0 ? v[v.Count / 2] : float.NaN; }

    public override string ToString()
    {
        // Where both were cut short and the lab's orc had its footing back on its own (not given back by the recording).
        var both = Blows.Where(b => b.LabCut && b.DonorCut && b.OwnEnd).ToList();
        return FormattableString.Invariant(
            $"procedural reaction: {Blows.Count} of the lab's blows on the followed orc (lean peak {PeakLean:0.00} rad), cut short as the donor's orc was on {Agree} (the donor's {DonorCut}, the reaction's {LabCut}); ")
            + FormattableString.Invariant(
            $"where both were and the lab's ran its course ({both.Count}), busy off by {Median(both.Select(b => MathF.Abs(b.LabBusy - b.DonorBusy))):0.00} s, slide by {Median(both.Select(b => MathF.Abs(b.LabMoved - b.DonorMoved))):0.00} m, turn by {Median(both.Select(b => MathF.Abs(b.LabTurned - b.DonorTurned))):0}°; the orc's blows while taken over {Blows.Sum(b => b.LabOrcBlows)}");
    }
}

/// <summary>
/// The shield's wear (#10149): the blows each side blocked that took something off it, paired within
/// <see cref="CombatReplay.Window"/>, and how many pairs wore it alike (within 0.05).
/// </summary>
internal sealed record ShieldWear(int Donor, float DonorWorn, int Lab, float LabWorn, int Paired, int Alike)
{
    public override string ToString() => FormattableString.Invariant(
        $"shield: the donor's blocks wore it {Donor} times by {DonorWorn:0.#}, the lab's {Lab} times by {LabWorn:0.#}; {Paired} paired, {Alike} alike");
}

/// <summary>
/// From Wire v14, the kicks the player asked for (the command's kick action going down), and which of them each side
/// began within <see cref="CombatReplay.Window"/>: a request can be refused, by the donor or by the lab's sword.
/// </summary>
internal sealed record KickRequests(int Requests, int DonorBegan, int LabBegan, int Both)
{
    public override string ToString() =>
        $"kick requests: {Requests}; the donor began a kick on {DonorBegan}, the lab on {LabBegan}, both on {Both}";
}

/// <summary>
/// The lab player's body driven by the recorded movement, jump and use (Wire v14), against the donor's: mean planar
/// speed over the ticks with movement input, and jumps.
/// </summary>
/// <param name="Donor">The donor's movement tick by tick, and <paramref name="Lab"/> the lab body's on the same input, for
/// <see cref="Calibration.MovementCalibration.Analyze"/> (#10159).</param>
internal sealed record BodyReplay(int MoveTicks, float DonorSpeed, float LabSpeed, int JumpPresses, int LabJumps, int UsePresses,
    IReadOnlyList<Calibration.MovementSample>? Donor = null, IReadOnlyList<Calibration.MovementSample>? Lab = null)
{
    public override string ToString() => FormattableString.Invariant(
        $"movement: {MoveTicks} ticks with movement input, donor {DonorSpeed:0.00} m/s, lab {LabSpeed:0.00} m/s on average; jump presses {JumpPresses}, lab jumps {LabJumps}; use presses {UsePresses}");
}

/// <summary>
/// A lab player body a replay drives with the recorded movement (a unit side/forward direction), view, jump and use,
/// in the lab's own arena: the donor's level isn't the lab's, so the body's own position isn't compared, only how it
/// moves. <see cref="Step"/> returns its feet, whether it left the ground in a jump, and whether it stands on the ground
/// (the lab's sword doesn't kick in the air).
/// </summary>
internal interface IReplayBody
{
    void Begin();
    /// <param name="jump">Jump went down this tick.</param>
    /// <param name="jumpHeld">Jump is held: the recorded command still has it.</param>
    /// <param name="crouchHeld">Duck is held.</param>
    /// <param name="sprint">The lab's stamina lets it sprint this tick (the donor's sprint asked, #10160).</param>
    /// <param name="walk">The donor's walk key, +speed, held (R10159-1).</param>
    /// <returns>Its feet, whether it left the ground in a jump, whether it stands on the ground, and whether a wall of
    /// the lab's arena stopped it (R10159-2: those ticks aren't compared).</returns>
    (Vector3 Feet, bool Jumped, bool Grounded, bool Blocked) Step(Vector2 move, Vector3 forward, bool jump, bool jumpHeld, bool crouchHeld, bool use, float seconds,
        bool sprint = false, bool walk = false);
    /// <summary>Raises the body <paramref name="metres"/> where it stands, at rest: it falls from there.</summary>
    void Lift(float metres);
    /// <summary>Puts the body at rest on open ground in the lab's arena (<c>lab.json</c> <c>replayFeet</c>): a scenario's setpos took the donor back to its start
    /// (#10159), and the lab's body would otherwise run on into the arena's walls.</summary>
    void Return();
    void End();
}

/// <summary>
/// Replays a recording's input in the lab and compares outcomes with the donor's. Each recorded tick is one lab
/// step of the tick's game time:
/// <list type="bullet">
/// <item>the player's held attack and attack2 buttons, its kick, and from v14 its movement (a power attack's
/// direction) and sprint (stamina) drive the lab's sword (<see cref="PlayerSword"/>), from the recorded eye and view
/// angles; the replayed body's ground decides whether it may kick;</item>
/// <item>the lab's orc follows the donor orc the player faces (<see cref="Orc.Follow"/>), so the lab's rules decide
/// the same encounter: the lab's trace, verdict and damage for the player's blows, its reach and the player's parry
/// for the orc's.</item>
/// </list>
/// The donor's decisions come from the same recording (<see cref="DarkMessiah.Combat.Blows"/>). Blows by or on
/// NPCs the lab didn't follow are listed as not compared.
/// </summary>
internal static class CombatReplay
{
    /// <summary>How far, ticks, a lab blow may land from the donor's for the donor's critical to be its (the replays' moves begin within a few ticks).</summary>
    const int CriticalTicks = 12;

    /// <summary>
    /// Which of the donor's swings a blow at <paramref name="tick"/> belongs to: the latest move started on the view
    /// model at or before it (its index in <paramref name="swings"/>), or -1 before the first.
    /// </summary>
    /// <summary>
    /// Which of a replay's donor blows are compared, by index, in tick order (then <c>Order</c>): the first of each swing's
    /// window on each victim. A swing's later windows (the huge slash's and the rush's two) are their own, at least
    /// <see cref="WindowApart"/> ticks after the last kept one (#10198); whichever pass found a blow, a blocked first
    /// window and a damaging second one are both kept (R10198-1).
    /// </summary>
    internal static List<int> FirstPerWindow(IReadOnlyList<DonorSwing> swings, IReadOnlyList<(int Tick, int Order, string Victim)> blows)
    {
        var taken = new Dictionary<(int Swing, string Victim), int>();
        var kept = new List<int>();
        foreach (int i in Enumerable.Range(0, blows.Count).OrderBy(i => blows[i].Tick).ThenBy(i => blows[i].Order))
        {
            var (tick, _, victim) = blows[i];
            var key = (SwingAt(swings, tick), victim);
            if (taken.TryGetValue(key, out int last) && tick - last < WindowApart) continue;
            taken[key] = tick;
            kept.Add(i);
        }
        return kept;
    }

    internal static int SwingAt(IReadOnlyList<DonorSwing> swings, int tick)
    {
        int at = -1;
        for (int i = 0; i < swings.Count && swings[i].Tick <= tick; i++) at = i;
        return at;
    }

    /// <summary>
    /// How far above the lab body's feet the donor's must be, metres, to fall from geometry the lab lacks: the recorded
    /// ledges were 2.0 and 2.4 m up, a step is under 0.2.
    /// </summary>
    internal const float DonorAbove = .15f;

    /// <summary>RB_TRACE=FROM-TO prints the lab sword's move each replayed tick in that range (looking into one replay).</summary>
    static readonly (int, int)? Trace = Environment.GetEnvironmentVariable("RB_TRACE")?.Split('-') is [var from, var to]
        && int.TryParse(from, out int f) && int.TryParse(to, out int t) ? (f, t) : null;

    /// <summary>
    /// How many ticks apart two of a swing's blows on one victim must be to be two windows' (the huge slash's are 0.26
    /// of its clip apart, 40 ticks; one window's contacts come within a few ticks).
    /// </summary>
    internal const int WindowApart = 15;

    /// <summary>The lab's camera agrees with the donor's when its forward and up are this close, degrees (#10161).</summary>
    internal const float CameraTolerance = 1.5f;
    /// <summary>The lab's camera agrees with the donor's in place this close, Source units (R10161-4).</summary>
    internal const float CameraPlaceTolerance = 1;
    /// <summary>The lab's health agrees with the donor's this close (#10162): its regeneration steps 0.25.</summary>
    internal const float HealthTolerance = .3f;
    /// <summary>How near the lab's mana must be to the donor's streamed one (#10165).</summary>
    internal const float ManaTolerance = .3f;

    /// <summary>A donor blow and a lab blow this far apart in time pair up, seconds.</summary>
    internal const double Window = .3;

    static readonly OrcPose Absent = new("", new Vector3(0, -1000, 0), Vector3.UnitZ, 0, "combatidle", 0, 1, null, 0, true);

    /// <summary>Reads <paramref name="path"/> for replay with the lab's move clips, and replays it.</summary>
    /// <summary>
    /// What the donor's followed orc did about a blow at <paramref name="ticks"/>[<paramref name="at"/>] (R10382-1,
    /// R10382-4): cut short if, from the blow's own tick to <see cref="Calibration.ReactionCalibration.Within"/> ticks on,
    /// it began a hit or push (not a stun or a get-up) it wasn't already playing before the blow: another sequence than
    /// the pose before the blow's, or the same played again. Then where that reaction ended, by the timeline's index
    /// (another sequence, or it played again, within <see cref="Calibration.ReactionCalibration.Follow"/> ticks); null when
    /// it didn't within them or the orc changed.
    /// </summary>
    internal static (bool Cut, int? Start, int? End) DonorReaction(IReadOnlyList<ReplayTick> ticks, int at)
    {
        var blow = ticks[at];
        if (blow.Orc is not { } struck) return (false, null, null);
        var before = at > 0 && ticks[at - 1].Orc is { } prior && prior.Id == struck.Id ? prior : struck;
        static bool Reaction(string s) => Calibration.ReactionCalibration.IsReaction(s)
            && !s.StartsWith("hit_stunned", StringComparison.Ordinal) && !s.StartsWith("StandUp", StringComparison.Ordinal);
        int? start = null;
        for (int k = at; k < ticks.Count && ticks[k].Tick <= blow.Tick + Calibration.ReactionCalibration.Within; k++)
        {
            if (ticks[k].Orc is not { } o || o.Id != struck.Id) break;
            var previous = k == at ? before : ticks[k - 1].Orc!;
            if (Reaction(o.Sequence) && (o.Sequence != before.Sequence || o.Sequence == previous.Sequence && o.Cycle < previous.Cycle - .05f)) { start = k; break; }
        }
        if (start is not int s0) return (false, null, null);
        string reaction = ticks[s0].Orc!.Sequence;
        for (int k = s0 + 1; k < ticks.Count && ticks[k].Tick <= blow.Tick + Calibration.ReactionCalibration.Follow; k++)
        {
            if (ticks[k].Orc is not { } o || o.Id != struck.Id) return (true, s0, null);
            if (o.Sequence != reaction || o.Cycle < ticks[k - 1].Orc!.Cycle - .05f) return (true, s0, k);
        }
        return (true, s0, null);
    }

    internal static ReplayReport Run(LabCombat combat, string path, IReplayBody? body = null)
    {
        var clips = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning);
        return Run(combat, InputTimeline.Read(path, clips.Moves.Select(m => m.Clip).ToArray(), clips.Kick.Clip), body);
    }

    internal static ReplayReport Run(LabCombat combat, InputTimeline timeline, IReplayBody? body = null)
    {
        var log = new List<CombatEvent>();
        var labSwings = new List<DonorSwing>();
        var followed = new Dictionary<int, string>();
        var times = new Dictionary<int, double>();
        var requests = new List<double>();
        // The body: lab and donor planar speeds over ticks with movement input, and jumps.
        bool driveBody = body is not null && timeline.HasCommands;
        var donorSpeeds = new List<float>();
        var labSpeeds = new List<float>();
        var donorTrace = new List<Calibration.MovementSample>();
        int staminaTicks = 0, staminaNear = 0, cameraTicks = 0, cameraNear = 0, healthTicks = 0, healthNear = 0, maxAgree = 0;
        float healthWorst = 0;
        float? lastDonorHealth = null, lastDonorMana = null;
        int manaTicks = 0, manaNear = 0;
        float manaWorst = 0;
        bool manaSet = false, manaSynced = false;
        bool healthSet = false, regenSynced = false, staminaSynced = false;
        float? lastDonorStamina = null;
        int hurtAt = 0, drunkAt = 0;
        BeltItem life = BeltItem.Of(combat.Donor, PotionModels.Life), full = BeltItem.Of(combat.Donor, PotionModels.FullLife);
        var cameraOffs = new List<float>();
        var placeOffs = new List<float>();
        float staminaWorst = 0;
        var labTrace = new List<Calibration.MovementSample>();
        // A lab-space point (metres, Y up) as Source units (Z up), for the movement calibration.
        static Calibration.MovementSample Sample(double time, Vector3 at, ReplayTick t) =>
            new(time, at.X / SourceSpace.MetresPerUnit, at.Z / SourceSpace.MetresPerUnit, at.Y / SourceSpace.MetresPerUnit, t.Command, t.Jump,
                t.KickHeld || t.Held.HasFlag(InputButtons.Attack) || t.Held.HasFlag(InputButtons.Attack2), MathF.Atan2(t.Forward.X, t.Forward.Z) * 180 / MathF.PI);
        int jumps = 0, labJumps = 0, uses = 0;
        Vector3? lastDonor = null, lastLab = null;
        bool god = combat.God;
        // From v15 the replay plays the donor player's build; the lab's own comes back after.
        var ownBuild = combat.Build;
        if (timeline.Build is { } recorded) combat.Rebuild(recorded, draw: false);
        combat.Reset();
        // The moves of the weapon the replay holds: the recording's, from v15 (#10148).
        var clips = combat.Sword.Clips;
        string[] moves = clips.Moves.Select(m => m.Clip).ToArray();
        // A lab blow is a critical where the donor's blow within a few ticks was: replays take the recorded roll.
        var recordedCriticals = timeline.Fight.Damage.Where(d => d.Attacker.IsPlayer && d.AttackerCritical).Select(d => d.Tick).ToArray();
        combat.RecordedCritical = tick => recordedCriticals.Any(c => Math.Abs(c - (long)tick) <= CriticalTicks);
        // And the victim's alert level where the donor's blow within as many ticks streamed it (v11; #10198).
        var recordedAlerts = timeline.Fight.Damage.Where(d => d.Attacker.IsPlayer && d.Victim.IsNpc && d.VictimAlert is not null)
            .Select(d => (d.Tick, Level: d.VictimAlert!.Value, d.VictimNpcState)).ToArray();
        combat.RecordedAlert = tick => recordedAlerts.Where(a => Math.Abs(a.Tick - (long)tick) <= CriticalTicks)
            .OrderBy(a => Math.Abs(a.Tick - (long)tick)).Select(a => ((int, int)?)(a.Level, a.VictimNpcState)).FirstOrDefault();
        combat.God = true;
        // The view's kicks follow the donor's blows on the player, not the lab's: how the lab's orc lands them is the
        // fight's comparison, the camera's is how each kick moves the view (#10161).
        combat.BlowsPunch = false;
        var struck = timeline.Struck.ToLookup(s => s.Tick, s => s.Damage);
        combat.Log = log;
        bool grounded = true;
        double donorJumped = double.NegativeInfinity;
        int switched = 0, rebuilt = 0;
        // The donor's hands taken (its view model empty) and given back with the quick draw (R10148-1).
        var quickDraws = timeline.Swings.Where(s => InputTimeline.Family(s.Clip) == "quick draw").Select(s => s.Tick).ToHashSet();
        // The hands are taken where the empty stretch that ends in a quick draw begins (an empty view model with no
        // quick draw after it is something else, and the lab's sword stays).
        var takes = new HashSet<int>();
        for (int i = 0; i < timeline.Ticks.Count; i++)
            if (quickDraws.Contains(timeline.Ticks[i].Tick))
            {
                int j = i - 1;
                while (j > 0 && !timeline.Ticks[j - 1].Armed) j--;
                if (j >= 0 && !timeline.Ticks[j].Armed) takes.Add(timeline.Ticks[j].Tick);
            }
        var donorHeights = new Queue<float>();
        // Each recorded build change as the lab applied it: its tick, the modules and the stamina maximum it gave.
        var applied = new List<(int, int, float)>();
        // A tick in a stretch the donor held something the lab doesn't play: nothing there is compared (#10140).
        bool Unplayable(int tick) => timeline.Unplayable.Any(u => tick >= u.From && tick <= u.To);
        SwordMove lastMove = combat.Sword.Move;
        PlayerAdrenaline? shadow = null;
        int shadowTicks = 0, shadowNear = 0;
        if (driveBody) body!.Begin();
        // The procedural reaction's outcomes on the followed orc by the timeline's index, each takeover's end, the orc's
        // blows meanwhile, and the lean's peak (R10382-1).
        var flinches = new List<(int Index, FlinchOutcome Outcome, (Vector3 Feet, Vector3 Facing)? Taken)>();
        var released = new Dictionary<int, (int Index, (Vector3 Feet, Vector3 Facing) At, int OrcBlows, string Why)>();
        int takenFlinch = -1, orcBlowsTaken = 0, lastOrcLogged = 0;
        float peakLean = 0;
        int index = -1;
        try
        {
            foreach (var t in timeline.Ticks)
            {
                index++;
                times[t.Tick] = t.Time;
                if (t.Orc is { } o) followed[t.Tick] = o.Id;
                // A request while the donor holds gear the lab doesn't play isn't compared, like its moves (R10140-3).
                if (t.Kick && timeline.HasCommands && !Unplayable(t.Tick)) requests.Add(t.Time);
                if (t.Adrenaline is float adrenaline)
                {
                    combat.Adrenaline.Set(adrenaline, t.AdrenalineMax);
                    if (shadow is null) { shadow = new PlayerAdrenaline(); shadow.Set(adrenaline, t.AdrenalineMax); }
                }
                // The donor's build changed (a skill learned, a shield taken): the lab plays it from this tick (#10140).
                for (; rebuilt < timeline.Builds.Count && timeline.Builds[rebuilt].Tick <= t.Tick; rebuilt++)
                {
                    combat.Rebuild(timeline.Builds[rebuilt].Build, draw: false, arm: false);
                    applied.Add((t.Tick, timeline.Builds[rebuilt].Build.Modules.Count, combat.Stamina.Rules.Max));
                }
                // The donor's hands taken (an orc's grab empties its view model), then given back: the lab's sword follows,
                // its quick draw beginning as the donor's does.
                if (takes.Contains(t.Tick)) combat.Sword.TakeHands();
                if (quickDraws.Contains(t.Tick)) combat.Sword.QuickDraw();
                // The donor took another weapon: the lab's sword puts its own away and draws that one (#10148).
                for (; switched < timeline.WeaponChanges.Count && timeline.WeaponChanges[switched].Tick <= t.Tick; switched++)
                    if (LabWeapon.ClassOf(timeline.WeaponChanges[switched].Model) is not null) combat.Arm(timeline.WeaponChanges[switched].Model);
                // The donor in the air over ground the lab's arena lacks is in the air for the sword, though the lab's body
                // has landed (R10144-1): falling from a ledge (its feet above the lab body's and coming down faster than
                // 1 cm a tick), or holding the jump attack until its feet come to rest (still for 4 ticks; the top of an
                // arc isn't rest, and the donor's ground can sit below the lab's floor).
                float droppedBy = lastDonor is Vector3 ld ? ld.Y - t.Feet.Y : 0;
                donorHeights.Enqueue(t.Feet.Y);
                if (donorHeights.Count > 4) donorHeights.Dequeue();
                bool donorResting = donorHeights.Count == 4 && donorHeights.Max() - donorHeights.Min() < .005f;
                // Or taking off before the lab's body has (R10159-2): the donor's body leaves the ground 2 to 5 ticks after
                // jump goes down, the lab's after its jumpDelay, so for 0.15 s after a press its feet rising (over 0.5 cm
                // a tick) count as in the air.
                if (t.Jump) donorJumped = t.Time;
                bool donorTakingOff = t.Time - donorJumped < .15 && droppedBy < -.005f;
                bool donorFalling = lastLab is Vector3 lf && t.Feet.Y - lf.Y > DonorAbove && droppedBy > .01f
                    || combat.Sword.Move == SwordMove.Leap && !donorResting || donorTakingOff;
                // The orc's blow on the donor's player, by its attack (the followed orc's sequence) and HitPow (the orc's
                // 8 damage a HitPow of 1).
                foreach (var damage in struck[t.Tick])
                    combat.FirstPerson.Head.Punch(HeadMotion.PunchOf(t.Orc?.Sequence ?? "", damage / 8));
                // With no weapon in hand the donor's player ignores attack, block and kick: so does the lab's sword.
                combat.Step(t.Seconds, (ulong)t.Tick, t.Armed && t.Held.HasFlag(InputButtons.Attack), t.Armed && t.Held.HasFlag(InputButtons.Attack2),
                    t.Armed && (t.KickHeld || t.Kick), t.Eye, t.Forward, t.Feet, t.Orc ?? Absent, t.Move, grounded && !donorFalling, t.Sprint, t.Held.HasFlag(InputButtons.Duck));
                if (combat.Sword.SwingStarted || combat.Sword.Drawing && combat.Sword.Move != lastMove) labSwings.Add(new DonorSwing(t.Tick, t.Time, combat.Sword.Clip.Clip));
                int orcLogged = log.Count(e => !e.ByPlayer);
                if (takenFlinch >= 0 && !combat.Orc.Staggered)
                {
                    released[takenFlinch] = (index, combat.Orc.ReleasedAt, orcBlowsTaken, combat.Orc.ReleasedFor);
                    takenFlinch = -1;
                }
                for (int f = flinches.Count; f < combat.Orc.FollowedFlinches.Count; f++)
                {
                    var outcome = combat.Orc.FollowedFlinches[f];
                    bool taken = outcome.Interrupted && combat.Orc.Staggered;
                    flinches.Add((index, outcome, taken ? combat.Orc.TakenAt : null));
                    if (taken) { takenFlinch = flinches.Count - 1; orcBlowsTaken = 0; lastOrcLogged = orcLogged; }
                }
                if (takenFlinch >= 0) { orcBlowsTaken += orcLogged - lastOrcLogged; }
                lastOrcLogged = orcLogged;
                peakLean = MathF.Max(peakLean, combat.Orc.Flinching.Lean);
                // The lab's camera in place against the donor's drawn one, every tick (R10161-4): the eyes attachment of
                // the arms posed at the recorded eye.
                if (t.Camera is { } drawnCamera)
                    placeOffs.Add(Vector3.Distance(combat.FirstPerson.Camera.Position, drawnCamera.Position) / SourceSpace.MetresPerUnit);
                if (t.Camera is { } traced && Environment.GetEnvironmentVariable("RB_PLACE_TRACE") == "1" && placeOffs[^1] > 5)
                    Console.WriteLine(FormattableString.Invariant($"PLACE {t.Tick} off {placeOffs[^1]:0.0} {combat.Sword.Move} eye {t.Eye / SourceSpace.MetresPerUnit:0} donor camera {traced.Position / SourceSpace.MetresPerUnit:0}"));
                // The lab's camera against the donor's drawn one (Wire v9; #10161), where the donor's head turned it.
                if (t.Camera is { Turned: true } donorCamera)
                {
                    var (_, labForward, labUp) = combat.FirstPerson.Camera;
                    float Degrees(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;
                    float off = MathF.Max(Degrees(labForward, donorCamera.Forward), Degrees(labUp, donorCamera.Up));
                    cameraTicks++;
                    if (off <= CameraTolerance) cameraNear++;
                    cameraOffs.Add(off);
                    if (Environment.GetEnvironmentVariable("RB_CAMERA_TRACE") == "1")
                        Console.WriteLine(FormattableString.Invariant($"CAMT {t.Tick} off {off:0.0} donor {donorCamera.Turn.Pitch:0.0},{donorCamera.Turn.Yaw:0.0},{donorCamera.Turn.Roll:0.0} lab {combat.FirstPerson.Head.Turn.Pitch:0.0},{combat.FirstPerson.Head.Turn.Yaw:0.0},{combat.FirstPerson.Head.Turn.Roll:0.0} {combat.FirstPerson.Head.Playing ?? "-"} sword {combat.Sword.Clip.Clip}@{combat.Sword.Cycle:0.00} {combat.Sword.Move} orc {t.Orc?.Sequence}@{t.Orc?.Cycle:0.00} side {(lastDonor is Vector3 pd && t.Seconds > 0 ? Vector3.Dot((t.Feet - pd) / t.Seconds, Vector3.Normalize(Vector3.Cross(t.Forward, Vector3.UnitY))) / SourceSpace.MetresPerUnit : 0):0}"));
                }
                // The lab's health against the donor's (#10162): it starts from the donor's, takes what the donor's blows took
                // (the lab's own orc is held off by god), and drinks where the donor's rose beyond its regeneration: the full
                // life potion where it reached the maximum past a life potion's 10, else a life potion. Its regeneration
                // takes the donor's phase from its first 0.25.
                if (t.Health is var (donorHealth, donorMax))
                {
                    if (!healthSet) { combat.Health.Set(donorHealth); healthSet = true; }
                    // Each up to this tick, a frame the replay skips included.
                    for (; hurtAt < timeline.Hurt.Count && timeline.Hurt[hurtAt].Tick <= t.Tick; hurtAt++) combat.Health.Take(timeline.Hurt[hurtAt].Taken);
                    for (; drunkAt < timeline.Drinks.Count && timeline.Drinks[drunkAt].Tick <= t.Tick; drunkAt++)
                        combat.Health.Heal((timeline.Drinks[drunkAt].ToMax && timeline.Drinks[drunkAt].Rise > life.GiveHealth + .5f ? full : life).GiveHealth);
                    if (!regenSynced && lastDonorHealth is float was && MathF.Abs(donorHealth - was - combat.Health.Rules.ModuleRegeneration) < .01f)
                    {
                        combat.Health.Sync();
                        combat.Health.Set(donorHealth);
                        regenSynced = true;
                    }
                    lastDonorHealth = donorHealth;
                    float off = MathF.Abs(combat.Health.Value - donorHealth);
                    healthTicks++;
                    if (off <= HealthTolerance) healthNear++;
                    healthWorst = MathF.Max(healthWorst, off);
                    if (combat.Health.Max == donorMax) maxAgree++;
                }
                // The lab's stamina against the donor's streamed one (Wire v15; #10160). Its regeneration takes the donor's
                // phase from the donor's first rise of one step (R10160-1): where the replay starts sets the lab's clock,
                // and a step's phase decides the tick a sprint may start at 3.
                if (t.Stamina is float donorStamina)
                {
                    if (!staminaSynced && lastDonorStamina is float before && MathF.Abs(donorStamina - before - combat.Stamina.Rules.AddValue) < .005f)
                    {
                        combat.Stamina.Sync();
                        staminaSynced = true;
                    }
                    lastDonorStamina = donorStamina;
                    float off = MathF.Abs(combat.Stamina.Value - donorStamina);
                    staminaTicks++;
                    if (off <= .2f) staminaNear++;
                    staminaWorst = MathF.Max(staminaWorst, off);
                    if (Environment.GetEnvironmentVariable("RB_STAMINA_TRACE") is { Length: > 0 } span && span.Split('-') is [var traceFrom, var traceTo]
                        && t.Tick >= int.Parse(traceFrom!) && t.Tick <= int.Parse(traceTo!))
                        Console.WriteLine(FormattableString.Invariant($"STAM {t.Tick} donor {donorStamina:0.000} lab {combat.Stamina.Value:0.000} sprinting {combat.Stamina.Sprinting} asked {t.Sprint} move {t.Move.Length():0.00}"));
                }
                // The lab's mana against the donor's streamed one (Wire v15; #10165): from the donor's at the start, its
                // regeneration on the lab's clock (the donor's phase from its first rise of one step), the donor's casts
                // (a fall) and mana potions (a rise past a step) taken as they come.
                if (t.Mana is float donorMana)
                {
                    if (!manaSet) { combat.Mana.Set(donorMana); manaSet = true; }
                    else if (lastDonorMana is float was)
                    {
                        float change = donorMana - was;
                        if (!manaSynced && MathF.Abs(change - combat.Mana.Rules.Recovery) < .01f) { combat.Mana.Sync(); combat.Mana.Set(donorMana); manaSynced = true; }
                        else if (change < -.5f) combat.Mana.Spend(MathF.Min(-change, combat.Mana.Value));
                        else if (change > combat.Mana.Rules.Recovery + .05f) combat.Mana.Restore(change);
                    }
                    lastDonorMana = donorMana;
                    float off = MathF.Abs(combat.Mana.Value - donorMana);
                    manaTicks++;
                    if (off <= ManaTolerance) manaNear++;
                    manaWorst = MathF.Max(manaWorst, off);
                }
                if (Trace is var (from, to) && t.Tick >= from && t.Tick <= to)
                    Console.WriteLine($"  trace {t.Tick} held {t.Held} grounded {grounded} move {combat.Sword.Move} {combat.Sword.Clip.Clip}@{combat.Sword.Cycle:0.00} donor feet y {t.Feet.Y:0.00}");
                lastMove = combat.Sword.Move;
                // The gauge the lab's own blows would keep (the fight's is held to the donor's), against the donor's.
                if (shadow is not null && t.Adrenaline is float donorGauge)
                {
                    shadow.Gain(combat.AdrenalineGained);
                    if (combat.FatalityLanded) shadow.Spend();
                    shadowTicks++;
                    if (MathF.Abs(shadow.Value - donorGauge) <= 1) shadowNear++;
                }
                if (driveBody)
                {
                    // A recorded teleport (a scenario's setpos: over a metre in a tick) that raises the donor raises the
                    // lab's body as far, to fall as the donor does; the lab's arena has no ground but its floor.
                    if (lastDonor is Vector3 was && Vector3.Distance(t.Feet, was) > 1)
                    {
                        // A teleport takes the lab's body back to the middle of its arena, raised as far as the donor was.
                        body!.Return();
                        if (t.Feet.Y - was.Y > .1f) body.Lift(t.Feet.Y - was.Y);
                    }
                    var (feet, jumped, onGround, blocked) = body!.Step(t.Move, t.Forward, t.Jump, t.Held.HasFlag(InputButtons.Jump), t.Held.HasFlag(InputButtons.Duck), t.Use, t.Seconds,
                        combat.Stamina.Sprinting, t.Held.HasFlag(InputButtons.BullRush));
                    if (t.Move != Vector2.Zero && t.Seconds > 0 && lastDonor is Vector3 d0 && lastLab is Vector3 l0)
                    {
                        donorSpeeds.Add(new Vector2(t.Feet.X - d0.X, t.Feet.Z - d0.Z).Length() / t.Seconds);
                        labSpeeds.Add(new Vector2(feet.X - l0.X, feet.Z - l0.Z).Length() / t.Seconds);
                    }
                    lastDonor = t.Feet;
                    lastLab = feet;
                    donorTrace.Add(Sample(t.Time, t.Feet, t));
                    labTrace.Add(Sample(t.Time, feet, t) with { Blocked = blocked, Sprinting = combat.Stamina.Sprinting });
                    jumps += t.Jump ? 1 : 0;
                    uses += t.Use ? 1 : 0;
                    labJumps += jumped ? 1 : 0;
                    grounded = onGround;
                }
            }
        }
        finally
        {
            if (driveBody) body!.End();
            combat.Log = null;
            combat.Orc.StopFollowing();
            combat.RecordedCritical = null;
            combat.RecordedAlert = null;
            if (timeline.Build is not null && ownBuild is not null) combat.Rebuild(ownBuild, draw: false);
            combat.Reset();
            combat.God = god;
            combat.BlowsPunch = true;
        }

        FlinchReplay? flinchReplay = null;
        if (flinches.Count > 0)
        {
            static float Turn(Vector3 was, Vector3 now) =>
                MathF.Atan2(Vector3.Dot(Vector3.Cross(was with { Y = 0 }, now with { Y = 0 }), Vector3.UnitY), Vector3.Dot(was with { Y = 0 }, now with { Y = 0 })) * 180 / MathF.PI;
            var takeovers = new List<FlinchTakeover>();
            for (int f = 0; f < flinches.Count; f++)
            {
                var (i, outcome, taken) = flinches[f];
                var donor = DonorReaction(timeline.Ticks, i);
                float labBusy = float.NaN, labMoved = float.NaN, labTurned = float.NaN;
                int labOrcBlows = 0;
                string why = taken is null ? "" : "the replay ended";
                if (taken is { } at && released.TryGetValue(f, out var end))
                {
                    labBusy = (float)(timeline.Ticks[end.Index].Time - timeline.Ticks[i].Time);
                    labMoved = Vector2.Distance(new(at.Feet.X, at.Feet.Z), new(end.At.Feet.X, end.At.Feet.Z));
                    labTurned = Turn(at.Facing, end.At.Facing);
                    labOrcBlows = end.OrcBlows;
                    why = end.Why;
                }
                float donorBusy = float.NaN, donorMoved = float.NaN, donorTurned = float.NaN;
                if (donor.Cut && donor.End is int e && timeline.Ticks[i].Orc is { } before && timeline.Ticks[e].Orc is { } after)
                {
                    donorBusy = (float)(timeline.Ticks[e].Time - timeline.Ticks[i].Time);
                    donorMoved = Vector2.Distance(new(before.Feet.X, before.Feet.Z), new(after.Feet.X, after.Feet.Z));
                    donorTurned = Turn(before.Facing, after.Facing);
                }
                takeovers.Add(new FlinchTakeover(timeline.Ticks[i].Tick, outcome, taken is not null, labBusy, labMoved, labTurned, labOrcBlows,
                    donor.Cut, donorBusy, donorMoved, donorTurned, why));
            }
            flinchReplay = new FlinchReplay(takeovers, peakLean);
        }

        double TimeOf(int tick) => times.TryGetValue(tick, out var s) ? s : double.NaN;
        string? Followed(int tick) => followed.GetValueOrDefault(tick);
        var notCompared = new List<string>();
        var fight = timeline.Fight;

        // The moves each side began, paired within the window: by family (fast attack, power attack, kick), and by clip
        // (A or B, each power attack's direction), where the lab must play the donor's own variant.
        var lags = new List<double>();
        (int, int, int) Pair(Func<string, bool> of, bool lag)
        {
            var donor = timeline.Swings.Where(s => of(s.Clip) && !Unplayable(s.Tick)).ToList();
            var lab = labSwings.Where(s => of(s.Clip) && !Unplayable(s.Tick)).ToList();
            var used = new HashSet<DonorSwing>();
            int together = 0;
            foreach (var d in donor)
                if (lab.Where(l => !used.Contains(l) && Math.Abs(l.Time - d.Time) <= Window).OrderBy(l => Math.Abs(l.Time - d.Time)).FirstOrDefault() is { } l)
                {
                    used.Add(l);
                    together++;
                    if (lag) lags.Add(l.Time - d.Time);
                }
            return (donor.Count, lab.Count, together);
        }
        // With switches, the put-aways and draws too (#10148).
        var moveCounts = moves.Select(InputTimeline.Family).Concat(timeline.WeaponChanges.Count > 0 ? ["put away", "draw"] : [])
            .Concat(quickDraws.Count > 0 ? ["quick draw"] : []).OfType<string>().Distinct()
            .ToDictionary(f => f, f => Pair(c => InputTimeline.Family(c) == f, lag: true));
        var byClip = timeline.Swings.Select(s => s.Clip).Concat(labSwings.Select(s => s.Clip)).Distinct()
            .ToDictionary(c => c, c => Pair(x => x == c, lag: false));

        // The player's blows on NPCs: the first contact of each swing on each victim, with its verdict and the damage it
        // asked. A swing is a move starting on the view model (DonorSwing): the player's body animation doesn't show it.
        var donorPlayer = new List<ReplayBlow>();
        var otherVictims = new Dictionary<string, int>();
        // Every donor blow left out, by tick and side: what "not compared" counts.
        var excluded = new List<(int Tick, bool ByPlayer, string Class)>();
        void Exclude(int tick, bool byPlayer, string cls)
        {
            otherVictims[cls] = otherVictims.GetValueOrDefault(cls) + 1;
            excluded.Add((tick, byPlayer, cls));
        }
        // The newest move layer: an earlier move's can still be fading out under it.
        string Move(IReadOnlyList<string> layers) => layers.LastOrDefault(l => InputTimeline.Family(l) is not null) ?? layers.LastOrDefault() ?? "?";
        // Every donor blow, damage records, defense-only verdicts and kicks alike, deduplicated in tick order (R10198-1):
        // a blocked first window and a damaging second one are both kept. At one tick a damage record comes before a
        // verdict and a verdict before a kick, as each pass had them.
        var candidates = new List<(int Tick, int Order, string Victim, string Class, Func<ReplayBlow> Blow)>();
        foreach (var d in fight.Damage.Where(d => d.Attacker.IsPlayer && d.Victim.IsNpc && !Unplayable(d.Tick)))
            candidates.Add((d.Tick, 0, d.Victim.Id, d.Victim.Class, () =>
            {
                var verdict = fight.Defense.FirstOrDefault(v => v.Tick == d.Tick && v.Victim.Id == d.Victim.Id && v.Attacker.IsPlayer)?.Donor ?? DefenseResult.Hit;
                // The jump and rush attacks' and the bash's blows carry the kick's bit too (they push): a kick is one the newest move doesn't say is a swing.
                bool kick = (d.DamageType & 0x40000) != 0 && InputTimeline.Family(Move(d.Attacker.Layers)) is null or "kick";
                return new ReplayBlow(d.Tick, TimeOf(d.Tick), kick ? clips.Kick.Clip : Move(d.Attacker.Layers),
                    verdict is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.NoEffect ? verdict : DefenseResult.Hit,
                    d.Defended ? 0 : d.Requested, kick ? null : d.HitGroup);
            }));
        // A parried or blocked swing makes no damage call: its verdict is its only record.
        foreach (var v in fight.Defense.Where(v => v.Attacker.IsPlayer && v.Victim.IsNpc && v.Donor is DefenseResult.Parry or DefenseResult.Shield && !Unplayable(v.Tick)))
            candidates.Add((v.Tick, 1, v.Victim.Id, v.Victim.Class, () => new ReplayBlow(v.Tick, TimeOf(v.Tick), Move(v.Attacker.Layers), v.Donor, 0, null)));
        foreach (var k in timeline.Kicks.Where(k => !Unplayable(k.Tick)))
            candidates.Add((k.Tick, 2, k.Victim, k.Class, () => new ReplayBlow(k.Tick, TimeOf(k.Tick), clips.Kick.Clip, DefenseResult.Hit, k.Damage, null)));
        foreach (int i in FirstPerWindow(timeline.Swings, [.. candidates.Select(c => (c.Tick, c.Order, c.Victim))]))
        {
            var c = candidates[i];
            if (Followed(c.Tick) != c.Victim) { Exclude(c.Tick, true, c.Class); continue; }
            donorPlayer.Add(c.Blow());
        }
        foreach (var (cls, n) in otherVictims) notCompared.Add($"{n} player blows on {cls} the lab wasn't following");

        // The followed orc's blows that reached the player: parried, breaking the parry, or landing.
        var donorOrc = new List<ReplayBlow>();
        int otherAttackers = 0;
        foreach (var p in fight.Parries.Where(p => p.Attacker.Class == InputTimeline.OrcClass && !Unplayable(p.Tick)))
        {
            if (Followed(p.Tick) != p.Attacker.Id) { otherAttackers++; excluded.Add((p.Tick, false, p.Attacker.Class)); continue; }
            // A broken parry lets the blow's damage through; a shield's block puts it on the shield (#10149).
            float dealt = p.Donor == DefenseResult.PlayerParryBroken
                ? fight.Damage.Where(d => d.Tick == p.Tick && d.Attacker.Id == p.Attacker.Id && d.Victim.IsPlayer).Sum(d => d.Requested)
                : timeline.ShieldWear.Where(w => w.Tick == p.Tick).Sum(w => w.Damage);
            donorOrc.Add(new ReplayBlow(p.Tick, TimeOf(p.Tick), p.Attacker.Sequence, p.Donor, dealt, null));
        }
        foreach (var d in fight.Damage.Where(d => d.Attacker.Class == InputTimeline.OrcClass && d.Victim.IsPlayer && !Unplayable(d.Tick)))
        {
            if (fight.Parries.Any(p => p.Tick == d.Tick && p.Attacker.Id == d.Attacker.Id)) continue;
            if (Followed(d.Tick) != d.Attacker.Id) { otherAttackers++; excluded.Add((d.Tick, false, d.Attacker.Class)); continue; }
            donorOrc.Add(new ReplayBlow(d.Tick, TimeOf(d.Tick), d.Attacker.Sequence, DefenseResult.Hit, d.Requested, null));
        }
        if (otherAttackers > 0) notCompared.Add($"{otherAttackers} blows on the player by an orc the lab wasn't following");
        int otherNpcs = fight.Damage.Count(d => d.Victim.IsPlayer && d.Attacker.IsNpc && d.Attacker.Class != InputTimeline.OrcClass);
        if (otherNpcs > 0) notCompared.Add($"{otherNpcs} blows on the player by other NPCs");
        foreach (var group in timeline.Unplayable.GroupBy(u => u.What))
            notCompared.Add(FormattableString.Invariant(
                $"{group.Sum(u => (TimeOf(u.To) - TimeOf(u.From)) is double s && double.IsFinite(s) ? s : 0):0.#} s holding {group.Key}: no move or blow there is compared"));

        ReplayBlow Lab(CombatEvent e) => new((int)e.Tick, TimeOf((int)e.Tick), e.Move, e.Verdict, e.Damage, e.Group);
        var labLog = log.Where(e => !Unplayable((int)e.Tick)).ToList();
        // Each kick request against the kicks each side began after it, within the window.
        KickRequests? kicks = null;
        if (timeline.HasCommands)
        {
            bool Began(IEnumerable<DonorSwing> swings, double at) => swings.Any(s => s.Time >= at - .05 && s.Time <= at + Window);
            var donorKicks = timeline.Swings.Where(s => InputTimeline.Family(s.Clip) == "kick" && !Unplayable(s.Tick)).ToList();
            var labKicks = labSwings.Where(s => InputTimeline.Family(s.Clip) == "kick" && !Unplayable(s.Tick)).ToList();
            kicks = new KickRequests(requests.Count, requests.Count(r => Began(donorKicks, r)), requests.Count(r => Began(labKicks, r)),
                requests.Count(r => Began(donorKicks, r) && Began(labKicks, r)));
        }
        ShieldWear? shieldWear = null;
        if (timeline.ShieldWear.Count > 0 || labLog.Any(e => !e.ByPlayer && e.Verdict == DefenseResult.PlayerParried && e.Damage > 0))
        {
            var donorWear = timeline.ShieldWear.Where(w => !Unplayable(w.Tick)).ToList();
            var labWear = labLog.Where(e => !e.ByPlayer && e.Verdict == DefenseResult.PlayerParried && e.Damage > 0).ToList();
            var free = labWear.ToList();
            int paired = 0, alike = 0;
            foreach (var (tick, worn) in donorWear)
            {
                var near = free.Where(e => Math.Abs(TimeOf((int)e.Tick) - TimeOf(tick)) <= Window).OrderBy(e => Math.Abs((long)e.Tick - tick)).FirstOrDefault();
                if (near is null) continue;
                free.Remove(near);
                paired++;
                if (MathF.Abs(near.Damage - worn) <= .05f) alike++;
            }
            shieldWear = new ShieldWear(donorWear.Count, donorWear.Sum(w => w.Damage), labWear.Count, labWear.Sum(e => e.Damage), paired, alike);
        }
        BodyReplay? bodyReplay = driveBody
            ? new BodyReplay(donorSpeeds.Count, donorSpeeds.Count > 0 ? donorSpeeds.Average() : 0, labSpeeds.Count > 0 ? labSpeeds.Average() : 0, jumps, labJumps, uses, donorTrace, labTrace)
            : null;
        return new ReplayReport(timeline.Name, timeline.Ticks.Count, timeline.Ticks.Sum(t => t.Seconds), timeline.Skipped,
            moveCounts, lags.Count > 0 ? lags.Average() : double.NaN,
            Compare("the player's blows on the followed orc", donorPlayer, labLog.Where(e => e.ByPlayer).Select(Lab).ToList()),
            Compare("the followed orc's blows on the player", donorOrc, labLog.Where(e => !e.ByPlayer).Select(Lab).ToList()),
            notCompared, kicks, bodyReplay, byClip, excluded,
            timeline.Build is { } b ? $"{b}{(timeline.BuildChanges > 0 ? $"; it changed {timeline.BuildChanges} times later, each played from its tick" : "")}"
                + (timeline.Unplayable.Count > 0 ? $"; not played: {string.Join(", ", timeline.Unplayable.Select(u => u.What).Distinct())} (not compared)" : "") : null,
            shadow is null ? null : (shadowNear, shadowTicks), applied, shieldWear, staminaTicks > 0 ? (staminaNear, staminaTicks, staminaWorst) : null,
            cameraTicks > 0 ? (cameraNear, cameraTicks, cameraOffs.Order().ElementAt(cameraOffs.Count / 2)) : null,
            healthTicks > 0 ? (healthNear, healthTicks, healthWorst, maxAgree) : null,
            placeOffs.Count > 0 ? (placeOffs.Count(o => o <= CameraPlaceTolerance), placeOffs.Count, placeOffs.Order().ElementAt(placeOffs.Count / 2), placeOffs.Max()) : null,
            manaTicks > 0 ? (manaNear, manaTicks, manaWorst) : null, flinchReplay);
    }

    /// <summary>
    /// Pairs each donor blow with the nearest unpaired lab blow within <see cref="Window"/>, preferring one of the
    /// same verdict. A pair agrees on the verdict and, for blows that land, the damage asked and the hit group.
    /// </summary>
    static BlowComparison Compare(string side, List<ReplayBlow> donor, List<ReplayBlow> lab)
    {
        var matches = new List<BlowMatch>();
        var used = new HashSet<ReplayBlow>();
        foreach (var d in donor.OrderBy(d => d.Tick))
        {
            var l = lab.Where(l => !used.Contains(l) && Math.Abs(l.Time - d.Time) <= Window)
                .OrderBy(l => l.Verdict == d.Verdict ? 0 : 1).ThenBy(l => Math.Abs(l.Time - d.Time)).FirstOrDefault();
            if (l is null) { matches.Add(new BlowMatch("donor only", d, null)); continue; }
            used.Add(l);
            bool agree = l.Verdict == d.Verdict && MathF.Abs(l.Damage - d.Damage) < 1e-3f && (l.Group is null || d.Group is null || l.Group == d.Group);
            matches.Add(new BlowMatch(agree ? "agree" : "differ", d, l));
        }
        matches.AddRange(lab.Where(l => !used.Contains(l)).Select(l => new BlowMatch("lab only", null, l)));
        return new BlowComparison(side, matches.OrderBy(m => (m.Donor ?? m.Lab)!.Tick).ToList());
    }
}
