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
    BlowComparison PlayerBlows, BlowComparison OrcBlows, IReadOnlyList<string> NotCompared)
{
    internal IEnumerable<string> Lines()
    {
        yield return FormattableString.Invariant($"{Name}: {Ticks} ticks replayed ({Seconds:0.0} s), {Skipped} recorded frames without input");
        foreach (var (clip, m) in Moves)
            yield return $"  {clip}: donor {m.Donor}, lab {m.Lab}, {m.Together} begin together";
        if (double.IsFinite(MeanLagSeconds))
            yield return FormattableString.Invariant($"  the lab begins a move {MeanLagSeconds * 1000:0} ms after the donor on average");
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
/// Replays a recording's input in the lab and compares outcomes with the donor's. Each recorded tick is one lab
/// step of the tick's game time:
/// <list type="bullet">
/// <item>the player's held attack and attack2 buttons and its kick drive the lab's sword (<see cref="PlayerSword"/>),
/// from the recorded eye and view angles;</item>
/// <item>the lab's orc follows the donor orc the player faces (<see cref="Orc.Follow"/>), so the lab's rules decide
/// the same encounter: the lab's trace, verdict and damage for the player's blows, its reach and the player's parry
/// for the orc's.</item>
/// </list>
/// The donor's decisions come from the same recording (<see cref="DarkMessiah.Combat.Blows"/>). Blows by or on
/// NPCs the lab didn't follow are listed as not compared.
/// </summary>
internal static class CombatReplay
{
    /// <summary>A donor blow and a lab blow this far apart in time pair up, seconds.</summary>
    internal const double Window = .3;

    static readonly OrcPose Absent = new("", new Vector3(0, -1000, 0), Vector3.UnitZ, 0, "combatidle", 0, 1, null, 0, true);

    /// <summary>Reads <paramref name="path"/> for replay with the lab's move clips, and replays it.</summary>
    internal static ReplayReport Run(LabCombat combat, string path)
    {
        var clips = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning.KickWindow);
        return Run(combat, InputTimeline.Read(path, [clips.Fast.Clip, clips.Power.Clip, clips.Kick.Clip], clips.Kick.Clip));
    }

    internal static ReplayReport Run(LabCombat combat, InputTimeline timeline)
    {
        var clips = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning.KickWindow);
        string[] moves = [clips.Fast.Clip, clips.Power.Clip, clips.Kick.Clip];
        var log = new List<CombatEvent>();
        var labSwings = new List<DonorSwing>();
        var followed = new Dictionary<int, string>();
        var times = new Dictionary<int, double>();
        bool god = combat.God;
        combat.Reset();
        combat.God = true;
        combat.Log = log;
        try
        {
            foreach (var t in timeline.Ticks)
            {
                times[t.Tick] = t.Time;
                if (t.Orc is { } o) followed[t.Tick] = o.Id;
                combat.Step(t.Seconds, (ulong)t.Tick, t.Held.HasFlag(InputButtons.Attack), t.Held.HasFlag(InputButtons.Attack2), t.Kick,
                    t.Eye, t.Forward, t.Feet, t.Orc ?? Absent);
                if (combat.Sword.SwingStarted) labSwings.Add(new DonorSwing(t.Tick, t.Time, combat.Sword.Clip.Clip));
            }
        }
        finally
        {
            combat.Log = null;
            combat.Orc.StopFollowing();
            combat.Reset();
            combat.God = god;
        }

        double TimeOf(int tick) => times.TryGetValue(tick, out var s) ? s : double.NaN;
        string? Followed(int tick) => followed.GetValueOrDefault(tick);
        var notCompared = new List<string>();
        var fight = timeline.Fight;

        // The moves each side began, paired by clip within the window.
        var moveCounts = new Dictionary<string, (int, int, int)>();
        var lags = new List<double>();
        foreach (var clip in moves)
        {
            var donor = timeline.Swings.Where(s => s.Clip == clip).ToList();
            var lab = labSwings.Where(s => s.Clip == clip).ToList();
            var used = new HashSet<DonorSwing>();
            int together = 0;
            foreach (var d in donor)
                if (lab.Where(l => !used.Contains(l) && Math.Abs(l.Time - d.Time) <= Window).OrderBy(l => Math.Abs(l.Time - d.Time)).FirstOrDefault() is { } l)
                {
                    used.Add(l);
                    together++;
                    lags.Add(l.Time - d.Time);
                }
            moveCounts[clip] = (donor.Count, lab.Count, together);
        }

        // The player's blows on NPCs: the first contact of each swing, with its verdict and the damage it asked.
        var donorPlayer = new List<ReplayBlow>();
        var otherVictims = new Dictionary<string, int>();
        var taken = new HashSet<(int Tick, string Victim)>();
        string Move(IReadOnlyList<string> layers) => layers.FirstOrDefault(moves.Contains) ?? layers.FirstOrDefault() ?? "?";
        foreach (var d in fight.Damage.Where(d => d.Attacker.IsPlayer && d.Victim.IsNpc && d.Contact == 0).OrderBy(d => d.Tick))
        {
            if (Followed(d.Tick) != d.Victim.Id) { otherVictims[d.Victim.Class] = otherVictims.GetValueOrDefault(d.Victim.Class) + 1; continue; }
            var verdict = fight.Defense.FirstOrDefault(v => v.Tick == d.Tick && v.Victim.Id == d.Victim.Id && v.Attacker.IsPlayer)?.Donor ?? DefenseResult.Hit;
            bool kick = (d.DamageType & 0x40000) != 0;
            taken.Add((d.Tick, d.Victim.Id));
            donorPlayer.Add(new ReplayBlow(d.Tick, TimeOf(d.Tick), kick ? clips.Kick.Clip : Move(d.Attacker.Layers),
                verdict is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.NoEffect ? verdict : DefenseResult.Hit,
                d.Defended ? 0 : d.Requested, kick ? null : d.HitGroup));
        }
        // A parried or blocked swing makes no damage call: its verdict is its only record.
        foreach (var v in fight.Defense.Where(v => v.Attacker.IsPlayer && v.Victim.IsNpc && v.Donor is DefenseResult.Parry or DefenseResult.Shield))
        {
            if (taken.Contains((v.Tick, v.Victim.Id))) continue;
            if (Followed(v.Tick) != v.Victim.Id) { otherVictims[v.Victim.Class] = otherVictims.GetValueOrDefault(v.Victim.Class) + 1; continue; }
            taken.Add((v.Tick, v.Victim.Id));
            donorPlayer.Add(new ReplayBlow(v.Tick, TimeOf(v.Tick), Move(v.Attacker.Layers), v.Donor, 0, null));
        }
        foreach (var k in timeline.Kicks.Where(k => taken.Add((k.Tick, k.Victim))))
        {
            if (Followed(k.Tick) != k.Victim) { otherVictims[k.Class] = otherVictims.GetValueOrDefault(k.Class) + 1; continue; }
            donorPlayer.Add(new ReplayBlow(k.Tick, TimeOf(k.Tick), clips.Kick.Clip, DefenseResult.Hit, k.Damage, null));
        }
        foreach (var (cls, n) in otherVictims) notCompared.Add($"{n} player blows on {cls} the lab wasn't following");

        // The followed orc's blows that reached the player: parried, breaking the parry, or landing.
        var donorOrc = new List<ReplayBlow>();
        int otherAttackers = 0;
        foreach (var p in fight.Parries.Where(p => p.Attacker.Class == InputTimeline.OrcClass))
        {
            if (Followed(p.Tick) != p.Attacker.Id) { otherAttackers++; continue; }
            float dealt = p.Donor == DefenseResult.PlayerParryBroken
                ? fight.Damage.Where(d => d.Tick == p.Tick && d.Attacker.Id == p.Attacker.Id && d.Victim.IsPlayer).Sum(d => d.Requested) : 0;
            donorOrc.Add(new ReplayBlow(p.Tick, TimeOf(p.Tick), p.Attacker.Sequence, p.Donor, dealt, null));
        }
        foreach (var d in fight.Damage.Where(d => d.Attacker.Class == InputTimeline.OrcClass && d.Victim.IsPlayer))
        {
            if (fight.Parries.Any(p => p.Tick == d.Tick && p.Attacker.Id == d.Attacker.Id)) continue;
            if (Followed(d.Tick) != d.Attacker.Id) { otherAttackers++; continue; }
            donorOrc.Add(new ReplayBlow(d.Tick, TimeOf(d.Tick), d.Attacker.Sequence, DefenseResult.Hit, d.Requested, null));
        }
        if (otherAttackers > 0) notCompared.Add($"{otherAttackers} blows on the player by an orc the lab wasn't following");
        int otherNpcs = fight.Damage.Count(d => d.Victim.IsPlayer && d.Attacker.IsNpc && d.Attacker.Class != InputTimeline.OrcClass);
        if (otherNpcs > 0) notCompared.Add($"{otherNpcs} blows on the player by other NPCs");

        ReplayBlow Lab(CombatEvent e) => new((int)e.Tick, TimeOf((int)e.Tick), e.Move, e.Verdict, e.Damage, e.Group);
        return new ReplayReport(timeline.Name, timeline.Ticks.Count, timeline.Ticks.Sum(t => t.Seconds), timeline.Skipped,
            moveCounts, lags.Count > 0 ? lags.Average() : double.NaN,
            Compare("the player's blows on the followed orc", donorPlayer, log.Where(e => e.ByPlayer).Select(Lab).ToList()),
            Compare("the followed orc's blows on the player", donorOrc, log.Where(e => !e.ByPlayer).Select(Lab).ToList()),
            notCompared);
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
