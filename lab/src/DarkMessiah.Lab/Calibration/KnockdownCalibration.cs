using DarkMessiah.Combat;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>What followed a recorded player blow on an orc: a knockdown (<c>StandUp_*</c>), a stun (<c>hit_stunned_*</c>), or neither.</summary>
internal enum BlowOutcome { None, Stun, Fall }

/// <summary>
/// One recorded player blow on an orc (#10152) and what followed within <see cref="KnockdownCalibration.Within"/> ticks,
/// with the blow's <c>.qct</c> event (its <c>chancetostun</c> and <c>chancetofall</c>), or none for a kick.
/// </summary>
/// <param name="Drop">How far the player was below where they last stood on the ground, Source units (R10152-1).</param>
internal sealed record KnockdownCase(string Recording, int Tick, string Move, bool Kick, AttackEvent? Event, float Damage, string VictimBefore,
    BlowOutcome Outcome, string Then, int HitsBefore, string Victim = "", float Drop = 0);

/// <summary>A knockdown or stun from its first tick to the orc playing something else: its sequences and how long, seconds.</summary>
internal sealed record DownSpan(string Recording, int Tick, BlowOutcome Kind, string First, string Last, float Seconds);

/// <summary>
/// The donor's stuns and knockdowns (#10152): every player blow on a recorded orc that asked damage, with what the orc
/// played next, and every stretch it spent down or stunned.
/// </summary>
internal static class KnockdownCalibration
{
    /// <summary>A reaction this many ticks after a blow is the blow's.</summary>
    internal const int Within = 30;

    /// <summary>A wounded orc, as its sequences show it (<c>Wounded_*</c>, <c>Hurtwalk</c>, <c>attack_wounded_*</c>).</summary>
    internal static bool LooksWounded(string sequence) =>
        sequence.Contains("ounded", StringComparison.Ordinal) || sequence.StartsWith("Hurt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lab's knockdown rule (<c>Npc.OrcStagger</c>) against recorded blows on a standing orc: the falls it expects
    /// (each blow's <c>chancetofall</c>, plus <c>WoundedFallChance</c> on an orc that looks wounded; a jump attack's only
    /// after the player's 50-unit drop; none for a kick) against the falls recorded, with the spread a rule of chances
    /// allows (two standard deviations).
    /// </summary>
    internal static (int Blows, double Expected, int Observed, double Spread) Falls(IEnumerable<KnockdownCase> cases, Npc.OrcStaggerData data)
    {
        var standing = cases.Where(c => Kind(c.VictimBefore) == BlowOutcome.None && !c.VictimBefore.StartsWith("Push", StringComparison.Ordinal)).ToList();
        var chances = standing.Select(c => c.Kick || c.Event is null ? 0
            : Math.Min(1, Npc.OrcStagger.FallChance(c.Event, LooksWounded(c.VictimBefore), Npc.OrcStagger.IsJumpAttack(c.Move) ? c.Drop : null, data))).ToList();
        return (standing.Count, chances.Sum(), standing.Count(c => c.Outcome == BlowOutcome.Fall), 2 * Math.Sqrt(chances.Sum(p => p * (1 - p))));
    }

    internal static BlowOutcome Kind(string sequence) =>
        sequence.StartsWith("StandUp", StringComparison.OrdinalIgnoreCase) ? BlowOutcome.Fall
        : sequence.StartsWith("hit_stunned", StringComparison.OrdinalIgnoreCase) || sequence.Equals("Stunned", StringComparison.OrdinalIgnoreCase) ? BlowOutcome.Stun
        : BlowOutcome.None;

    internal static (List<KnockdownCase> Blows, List<DownSpan> Spans) Measure(IEnumerable<string> recordings, CombatModel model)
    {
        var cases = new List<KnockdownCase>();
        var spans = new List<DownSpan>();
        foreach (var path in recordings)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            // Each orc's sequence changes by tick, and the view model's move layers at each damage tick.
            var changes = new Dictionary<string, List<(int Tick, string Sequence)>>();
            var moveCycles = new Dictionary<int, Dictionary<string, float>>();
            // The player's kicks on orcs, which Blows leaves out: the traced call with damage (its push makes another).
            var kicks = new List<(int Tick, string Victim, float Damage)>();
            // The player's drop below where they took off, by tick: standing is a height unchanged from the last tick, and
            // a take-off height holds through the landing for half a second (server.dll keeps it until its ground trace
            // finds the ground again, which a blow struck as the player lands comes before).
            var drops = new Dictionary<int, float>();
            float lastZ = float.NaN, groundZ = float.NaN, takeoffZ = float.NaN;
            int airTick = int.MinValue;
            float interval = 1 / 66f;
            try
            {
                using var stream = File.OpenRead(path);
                var arrows = new Arrows();
                foreach (var f in arrows.Frames(stream))
                {
                    interval = f.TickInterval;
                    if (f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "player") is { } p
                        && f.Classes.GetValueOrDefault(p.ClassId) == "player")
                    {
                        bool standing = !float.IsNaN(lastZ) && MathF.Abs(p.OriginZ - lastZ) < .01f;
                        if (!standing && !float.IsNaN(lastZ))
                        {
                            if (f.Tick - airTick > 1) takeoffZ = groundZ;
                            airTick = f.Tick;
                        }
                        if (standing) groundZ = p.OriginZ;
                        lastZ = p.OriginZ;
                        bool sinceAir = (f.Tick - airTick) * f.TickInterval <= .5f;
                        drops[f.Tick] = sinceAir && !float.IsNaN(takeoffZ) ? MathF.Max(0, takeoffZ - p.OriginZ) : 0;
                    }
                    if (f.Damage.Count > 0) moveCycles.TryAdd(f.Tick, DamageCalibration.MoveLayers(f));
                    foreach (var d in f.Damage)
                        if ((d.DamageType & CombatModel.KickDamage) != 0 && d.TraceValid != 0 && d.Damage > 0 && !arrows.Is(f, d)
                            && f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == "player"
                            && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) == Replay.InputTimeline.OrcClass
                            && !DamageCalibration.MoveLayers(f).Keys.Any(k => Replay.InputTimeline.Family(k) is { } fam && fam != "kick"))
                            kicks.Add((d.Tick, $"#{d.Victim}.{d.VictimSerial:x}", d.Damage));
                    foreach (var (index, e) in f.Entities)
                    {
                        if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass) continue;
                        var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                        string sequence = e.Sequence >= 0 && e.Sequence < names.Length ? names[e.Sequence] : "";
                        if (e.Flags.HasFlag(WireEntityFlags.HasHealth) && (e.LifeState != 0 || e.Health <= 0)) sequence = "(dead)";
                        string id = $"#{index}.{e.Serial:x}";
                        if (!changes.TryGetValue(id, out var list)) changes[id] = list = [];
                        if (list.Count == 0 || list[^1].Sequence != sequence) list.Add((f.Tick, sequence));
                    }
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            FightCases fight;
            try { using var stream = File.OpenRead(path); fight = Blows.Read(name, stream); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { continue; }

            var hits = new Dictionary<string, int>();
            foreach (var d in fight.Damage.Where(d => d.Attacker.IsPlayer && d.Victim.Class == Replay.InputTimeline.OrcClass && !d.Defended && d.HealthBefore > 0)
                         .OrderBy(d => d.Tick))
            {
                if (!changes.TryGetValue(d.Victim.Id, out var seqs)) continue;
                string? family = d.Attacker.Layers.Select(Replay.InputTimeline.Family).LastOrDefault(f => f is not null);
                bool kick = (d.DamageType & CombatModel.KickDamage) != 0 && family is null or "kick";
                string move = kick ? "kick" : d.Attacker.Layers.LastOrDefault(l => Replay.InputTimeline.Family(l) is not null) ?? "?";
                AttackEvent? ev = null;
                if (!kick && move != "?")
                {
                    float cycle = moveCycles.GetValueOrDefault(d.Tick)?.GetValueOrDefault(move, float.NaN) is float c && !float.IsNaN(c) ? c : d.Attacker.Cycle;
                    ev = model.EventOf(d.Attacker with { Layers = [move], Cycle = cycle });
                }
                string before = seqs.LastOrDefault(s => s.Tick <= d.Tick).Sequence ?? "";
                var next = seqs.Where(s => s.Tick > d.Tick && s.Tick <= d.Tick + Within).ToList();
                var outcome = next.Select(s => Kind(s.Sequence)).FirstOrDefault(k => k != BlowOutcome.None);
                // Already down or stunned before the blow: not this blow's doing.
                if (Kind(before) != BlowOutcome.None) outcome = BlowOutcome.None;
                int count = hits.GetValueOrDefault(d.Victim.Id);
                hits[d.Victim.Id] = count + 1;
                cases.Add(new KnockdownCase(name, d.Tick, move, kick, ev, d.Requested, before, outcome, next.FirstOrDefault().Sequence ?? "", count, d.Victim.Id,
                    drops.GetValueOrDefault(d.Tick)));
            }
            foreach (var (tick, victim, damage) in kicks)
            {
                if (!changes.TryGetValue(victim, out var seqs)) continue;
                string before = seqs.LastOrDefault(s => s.Tick <= tick).Sequence ?? "";
                var next = seqs.Where(s => s.Tick > tick && s.Tick <= tick + Within * 3).ToList();
                var outcome = Kind(before) != BlowOutcome.None ? BlowOutcome.None : next.Select(s => Kind(s.Sequence)).FirstOrDefault(k => k != BlowOutcome.None);
                cases.Add(new KnockdownCase(name, tick, "kick", true, null, damage, before, outcome, next.FirstOrDefault().Sequence ?? "", 0, victim));
            }
            // Every stretch down or stunned: from its first such sequence to the first that isn't.
            foreach (var (id, seqs) in changes)
                for (int i = 0; i < seqs.Count; i++)
                {
                    var kind = Kind(seqs[i].Sequence);
                    if (kind == BlowOutcome.None || i > 0 && Kind(seqs[i - 1].Sequence) == kind) continue;
                    int j = i;
                    while (j + 1 < seqs.Count && Kind(seqs[j + 1].Sequence) == kind) j++;
                    if (j + 1 >= seqs.Count) continue;
                    spans.Add(new DownSpan(name, seqs[i].Tick, kind, seqs[i].Sequence, seqs[j].Sequence, (seqs[j + 1].Tick - seqs[i].Tick) * interval));
                }
        }
        return (cases, spans);
    }
}
