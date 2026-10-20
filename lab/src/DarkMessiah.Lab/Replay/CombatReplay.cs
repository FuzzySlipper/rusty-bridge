using System.Numerics;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Sword;
using RustyBridge.Wire;

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
    IReadOnlyList<(int Tick, bool ByPlayer, string Class)>? Excluded = null, string? Build = null, (int Near, int Ticks)? Adrenaline = null)
{
    internal IEnumerable<string> Lines()
    {
        yield return FormattableString.Invariant($"{Name}: {Ticks} ticks replayed ({Seconds:0.0} s), {Skipped} recorded frames without input");
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
internal sealed record BodyReplay(int MoveTicks, float DonorSpeed, float LabSpeed, int JumpPresses, int LabJumps, int UsePresses)
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
    (Vector3 Feet, bool Jumped, bool Grounded) Step(Vector2 move, Vector3 forward, bool jump, bool jumpHeld, bool crouchHeld, bool use, float seconds);
    /// <summary>Raises the body <paramref name="metres"/> where it stands, at rest: it falls from there.</summary>
    void Lift(float metres);
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
    internal static int SwingAt(IReadOnlyList<DonorSwing> swings, int tick)
    {
        int at = -1;
        for (int i = 0; i < swings.Count && swings[i].Tick <= tick; i++) at = i;
        return at;
    }

    /// <summary>A donor blow and a lab blow this far apart in time pair up, seconds.</summary>
    internal const double Window = .3;

    static readonly OrcPose Absent = new("", new Vector3(0, -1000, 0), Vector3.UnitZ, 0, "combatidle", 0, 1, null, 0, true);

    /// <summary>Reads <paramref name="path"/> for replay with the lab's move clips, and replays it.</summary>
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
        combat.God = true;
        combat.Log = log;
        bool grounded = true;
        int switched = 0;
        SwordMove lastMove = combat.Sword.Move;
        PlayerAdrenaline? shadow = null;
        int shadowTicks = 0, shadowNear = 0;
        if (driveBody) body!.Begin();
        try
        {
            foreach (var t in timeline.Ticks)
            {
                times[t.Tick] = t.Time;
                if (t.Orc is { } o) followed[t.Tick] = o.Id;
                if (t.Kick && timeline.HasCommands) requests.Add(t.Time);
                if (t.Adrenaline is float adrenaline)
                {
                    combat.Adrenaline.Set(adrenaline, t.AdrenalineMax);
                    if (shadow is null) { shadow = new PlayerAdrenaline(); shadow.Set(adrenaline, t.AdrenalineMax); }
                }
                // The donor took another weapon: the lab's sword puts its own away and draws that one (#10148).
                for (; switched < timeline.WeaponChanges.Count && timeline.WeaponChanges[switched].Tick <= t.Tick; switched++)
                    if (LabWeapon.ClassOf(timeline.WeaponChanges[switched].Model) is not null) combat.Arm(timeline.WeaponChanges[switched].Model);
                // With no weapon in hand the donor's player ignores attack, block and kick: so does the lab's sword.
                combat.Step(t.Seconds, (ulong)t.Tick, t.Armed && t.Held.HasFlag(InputButtons.Attack), t.Armed && t.Held.HasFlag(InputButtons.Attack2),
                    t.Armed && (t.KickHeld || t.Kick), t.Eye, t.Forward, t.Feet, t.Orc ?? Absent, t.Move, grounded, t.Sprint, t.Held.HasFlag(InputButtons.Duck));
                if (combat.Sword.SwingStarted || combat.Sword.Drawing && combat.Sword.Move != lastMove) labSwings.Add(new DonorSwing(t.Tick, t.Time, combat.Sword.Clip.Clip));
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
                    if (lastDonor is Vector3 was && Vector3.Distance(t.Feet, was) > 1 && t.Feet.Y - was.Y > .1f) body!.Lift(t.Feet.Y - was.Y);
                    var (feet, jumped, onGround) = body!.Step(t.Move, t.Forward, t.Jump, t.Held.HasFlag(InputButtons.Jump), t.Held.HasFlag(InputButtons.Duck), t.Use, t.Seconds);
                    if (t.Move != Vector2.Zero && t.Seconds > 0 && lastDonor is Vector3 d0 && lastLab is Vector3 l0)
                    {
                        donorSpeeds.Add(new Vector2(t.Feet.X - d0.X, t.Feet.Z - d0.Z).Length() / t.Seconds);
                        labSpeeds.Add(new Vector2(feet.X - l0.X, feet.Z - l0.Z).Length() / t.Seconds);
                    }
                    lastDonor = t.Feet;
                    lastLab = feet;
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
            if (timeline.Build is not null && ownBuild is not null) combat.Rebuild(ownBuild, draw: false);
            combat.Reset();
            combat.God = god;
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
            var donor = timeline.Swings.Where(s => of(s.Clip)).ToList();
            var lab = labSwings.Where(s => of(s.Clip)).ToList();
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
        var moveCounts = moves.Select(InputTimeline.Family).Concat(timeline.WeaponChanges.Count > 0 ? ["put away", "draw"] : []).OfType<string>().Distinct()
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
        var taken = new HashSet<(int Swing, string Victim)>();
        bool First(int tick, string victim) => taken.Add((SwingAt(timeline.Swings, tick), victim));
        // The newest move layer: an earlier move's can still be fading out under it.
        string Move(IReadOnlyList<string> layers) => layers.LastOrDefault(l => InputTimeline.Family(l) is not null) ?? layers.LastOrDefault() ?? "?";
        foreach (var d in fight.Damage.Where(d => d.Attacker.IsPlayer && d.Victim.IsNpc).OrderBy(d => d.Tick))
        {
            if (!First(d.Tick, d.Victim.Id)) continue;
            if (Followed(d.Tick) != d.Victim.Id) { Exclude(d.Tick, true, d.Victim.Class); continue; }
            var verdict = fight.Defense.FirstOrDefault(v => v.Tick == d.Tick && v.Victim.Id == d.Victim.Id && v.Attacker.IsPlayer)?.Donor ?? DefenseResult.Hit;
            bool kick = (d.DamageType & 0x40000) != 0;
            donorPlayer.Add(new ReplayBlow(d.Tick, TimeOf(d.Tick), kick ? clips.Kick.Clip : Move(d.Attacker.Layers),
                verdict is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.NoEffect ? verdict : DefenseResult.Hit,
                d.Defended ? 0 : d.Requested, kick ? null : d.HitGroup));
        }
        // A parried or blocked swing makes no damage call: its verdict is its only record.
        foreach (var v in fight.Defense.Where(v => v.Attacker.IsPlayer && v.Victim.IsNpc && v.Donor is DefenseResult.Parry or DefenseResult.Shield).OrderBy(v => v.Tick))
        {
            if (!First(v.Tick, v.Victim.Id)) continue;
            if (Followed(v.Tick) != v.Victim.Id) { Exclude(v.Tick, true, v.Victim.Class); continue; }
            donorPlayer.Add(new ReplayBlow(v.Tick, TimeOf(v.Tick), Move(v.Attacker.Layers), v.Donor, 0, null));
        }
        foreach (var k in timeline.Kicks.Where(k => First(k.Tick, k.Victim)))
        {
            if (Followed(k.Tick) != k.Victim) { Exclude(k.Tick, true, k.Class); continue; }
            donorPlayer.Add(new ReplayBlow(k.Tick, TimeOf(k.Tick), clips.Kick.Clip, DefenseResult.Hit, k.Damage, null));
        }
        foreach (var (cls, n) in otherVictims) notCompared.Add($"{n} player blows on {cls} the lab wasn't following");

        // The followed orc's blows that reached the player: parried, breaking the parry, or landing.
        var donorOrc = new List<ReplayBlow>();
        int otherAttackers = 0;
        foreach (var p in fight.Parries.Where(p => p.Attacker.Class == InputTimeline.OrcClass))
        {
            if (Followed(p.Tick) != p.Attacker.Id) { otherAttackers++; excluded.Add((p.Tick, false, p.Attacker.Class)); continue; }
            float dealt = p.Donor == DefenseResult.PlayerParryBroken
                ? fight.Damage.Where(d => d.Tick == p.Tick && d.Attacker.Id == p.Attacker.Id && d.Victim.IsPlayer).Sum(d => d.Requested) : 0;
            donorOrc.Add(new ReplayBlow(p.Tick, TimeOf(p.Tick), p.Attacker.Sequence, p.Donor, dealt, null));
        }
        foreach (var d in fight.Damage.Where(d => d.Attacker.Class == InputTimeline.OrcClass && d.Victim.IsPlayer))
        {
            if (fight.Parries.Any(p => p.Tick == d.Tick && p.Attacker.Id == d.Attacker.Id)) continue;
            if (Followed(d.Tick) != d.Attacker.Id) { otherAttackers++; excluded.Add((d.Tick, false, d.Attacker.Class)); continue; }
            donorOrc.Add(new ReplayBlow(d.Tick, TimeOf(d.Tick), d.Attacker.Sequence, DefenseResult.Hit, d.Requested, null));
        }
        if (otherAttackers > 0) notCompared.Add($"{otherAttackers} blows on the player by an orc the lab wasn't following");
        int otherNpcs = fight.Damage.Count(d => d.Victim.IsPlayer && d.Attacker.IsNpc && d.Attacker.Class != InputTimeline.OrcClass);
        if (otherNpcs > 0) notCompared.Add($"{otherNpcs} blows on the player by other NPCs");

        ReplayBlow Lab(CombatEvent e) => new((int)e.Tick, TimeOf((int)e.Tick), e.Move, e.Verdict, e.Damage, e.Group);
        // Each kick request against the kicks each side began after it, within the window.
        KickRequests? kicks = null;
        if (timeline.HasCommands)
        {
            bool Began(IEnumerable<DonorSwing> swings, double at) => swings.Any(s => s.Time >= at - .05 && s.Time <= at + Window);
            var donorKicks = timeline.Swings.Where(s => InputTimeline.Family(s.Clip) == "kick").ToList();
            var labKicks = labSwings.Where(s => InputTimeline.Family(s.Clip) == "kick").ToList();
            kicks = new KickRequests(requests.Count, requests.Count(r => Began(donorKicks, r)), requests.Count(r => Began(labKicks, r)),
                requests.Count(r => Began(donorKicks, r) && Began(labKicks, r)));
        }
        BodyReplay? bodyReplay = driveBody
            ? new BodyReplay(donorSpeeds.Count, donorSpeeds.Count > 0 ? donorSpeeds.Average() : 0, labSpeeds.Count > 0 ? labSpeeds.Average() : 0, jumps, labJumps, uses)
            : null;
        return new ReplayReport(timeline.Name, timeline.Ticks.Count, timeline.Ticks.Sum(t => t.Seconds), timeline.Skipped,
            moveCounts, lags.Count > 0 ? lags.Average() : double.NaN,
            Compare("the player's blows on the followed orc", donorPlayer, log.Where(e => e.ByPlayer).Select(Lab).ToList()),
            Compare("the followed orc's blows on the player", donorOrc, log.Where(e => !e.ByPlayer).Select(Lab).ToList()),
            notCompared, kicks, bodyReplay, byClip, excluded,
            timeline.Build is { } b ? $"{b}{(timeline.BuildChanges > 0 ? $"; it changed {timeline.BuildChanges} times later, which the replay doesn't follow" : "")}" : null,
            shadow is null ? null : (shadowNear, shadowTicks));
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
