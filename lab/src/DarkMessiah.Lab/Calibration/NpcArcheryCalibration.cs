using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One arrow an NPC shot in a recording (#10169): nocked (an <c>arrowshot</c> it owns appears at its hand) and let go
/// (the arrow's owner gone: a sample late, positions coming every other tick), its stance then (<c>bow_attack</c>
/// standing, else on the move), the 2D distance to the player and the player's speed, its speed between its first two
/// free samples (NaN when the second is at the player), how far below the player's middle (36 units up) its line passed, and its blow on the player.
/// </summary>
/// <param name="FromEye">The height its line passed the player at, from the player's eye (the client view's): negative below.</param>
/// <param name="NockedStanding">The archer stood (<c>bow_attack</c>) as it was nocked: its delay is the standing one, whatever it did after.</param>
internal sealed record NpcArrow(double Nocked, double Left, bool Standing, float Distance, float PlayerSpeed, float Speed, float Below, double Struck, float Asked, float FromEye = float.NaN, bool NockedStanding = false)
{
    internal bool Hit => !double.IsNaN(Struck);

    public override string ToString() =>
        FormattableString.Invariant($"nocked {Nocked:0.00} s, let go {Left - Nocked:0.000} s on {(Standing ? "standing" : "moving")} at {Distance:0} units (the player at {PlayerSpeed:0} units/s), {Speed:0} units/s, {FromEye:0.0} from the eye; ")
        + (Hit ? FormattableString.Invariant($"struck {Struck - Left:0.000} s on for {Asked:0.##}") : "no blow");
}

/// <summary>
/// An archer's fight in a recording (#10169): when the player's arrow woke it, its arrows nocked from then (those never
/// let go among them, <see cref="Nocks"/>), the distances it stood at to shoot (each <c>bow_attack</c> begun), and its
/// switch to the blade: the <c>bow_kick</c> and its blow, <c>drop_bow</c>, <c>unsheathe_sword</c>, and its blows after
/// (the clip and its cycle as each struck, the damage asked, the weapon).
/// </summary>
/// <param name="Stood">When it began to stand to shoot (each <c>bow_attack</c> begun).</param>
internal sealed record ArcherFight(int Archer, double Woken, IReadOnlyList<double> Nocks, IReadOnlyList<NpcArrow> Arrows, IReadOnlyList<float> StandOffs,
    double Kicked, float KickAsked, double Dropped, double Unsheathed, IReadOnlyList<(string Clip, float Cycle, float Asked, string Weapon)> Blows, IReadOnlyList<double> Stood)
{
    public override string ToString() =>
        FormattableString.Invariant($"archer #{Archer} woken {Woken:0.00} s: {Arrows.Count} arrows let go of {Nocks.Count} nocked, {Arrows.Count(a => a.Hit)} struck; stood at {string.Join(", ", StandOffs.Select(s => s.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))}; ")
        + (double.IsNaN(Kicked) ? "no kick" : FormattableString.Invariant($"kicked {Kicked - Woken:0.0} s on for {KickAsked:0.##} after {Arrows.Count(a => a.Left < Kicked)} arrows, dropped the bow {Dropped - Kicked:0.00} s after, the blade {Unsheathed - Kicked:0.00}; {Blows.Count} blows after"));
}

/// <summary>Reads the NPC archers from a recording (#10169; docs/lab.md, "NPC archery").</summary>
internal static class NpcArcheryCalibration
{
    internal const string ArcherClass = "npc_orc_bow";

    internal static List<ArcherFight> Measure(string path)
    {
        var names = new EventNames();
        double interval = .015;
        var arrows = new Dictionary<(int, uint), (double Nocked, double Left, int Owner, bool Standing, float Distance, float PlayerSpeed, List<(double T, Vector3 At)> Free, Vector3 Player, float Eye, bool NockedStanding)>();
        var clips = new Dictionary<int, List<(double T, string Clip, Vector3 At)>>();
        var woken = new List<(double T, int Archer)>();
        var damage = new List<(double T, int Attacker, string By, float Asked, string Clip, float Cycle)>();
        Vector3 player = default, lastPlayer = default;
        float eye = 64;
        double lastPlayerAt = double.NaN;
        float playerSpeed = 0;
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                names.Observe(f);
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double t = f.Tick * interval;
                if (f.Entities.TryGetValue(f.Player.Index, out var me))
                {
                    player = new Vector3(me.OriginX, me.OriginY, me.OriginZ);
                    eye = f.ClientView.OriginZ + f.ClientView.ViewOffsetZ;
                    if (double.IsNaN(lastPlayerAt) || t - lastPlayerAt >= .1)
                    {
                        if (!double.IsNaN(lastPlayerAt)) playerSpeed = new Vector2(player.X - lastPlayer.X, player.Y - lastPlayer.Y).Length() / (float)(t - lastPlayerAt);
                        (lastPlayer, lastPlayerAt) = (player, t);
                    }
                }
                foreach (var (i, e) in f.Entities)
                {
                    string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                    if (cls == ArcherClass && f.Sequences.GetValueOrDefault(e.ModelIndex) is { } seqs && e.Sequence >= 0 && e.Sequence < seqs.Length)
                    {
                        if (!clips.TryGetValue(i, out var list)) clips[i] = list = [];
                        if (list.Count == 0 || list[^1].Clip != seqs[e.Sequence]) list.Add((t, seqs[e.Sequence], new Vector3(e.OriginX, e.OriginY, e.OriginZ)));
                    }
                    if (cls != "arrowshot") continue;
                    var key = (i, e.Serial);
                    var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
                    bool archers = e.Owner != WireLayout.NoEntity && f.Entities.TryGetValue(e.Owner, out var owner) && f.Classes.GetValueOrDefault(owner.ClassId, "") == ArcherClass;
                    if (archers && !arrows.ContainsKey(key))
                        arrows[key] = (t, double.NaN, e.Owner, false, 0, 0, [], default, 0, f.Entities.TryGetValue(e.Owner, out var nocker) && f.Sequences.GetValueOrDefault(nocker.ModelIndex) is { } ns
                            && nocker.Sequence >= 0 && nocker.Sequence < ns.Length && ns[nocker.Sequence] == "bow_attack");
                    if (!arrows.TryGetValue(key, out var a)) continue;
                    if (!archers && double.IsNaN(a.Left) && f.Entities.TryGetValue((ushort)a.Owner, out var shooter) && f.Sequences.GetValueOrDefault(shooter.ModelIndex) is { } ss
                        && shooter.Sequence >= 0 && shooter.Sequence < ss.Length)
                        a = a with
                        {
                            Left = t, Standing = ss[shooter.Sequence] == "bow_attack", Player = player, PlayerSpeed = playerSpeed, Eye = eye,
                            Distance = new Vector2(player.X - shooter.OriginX, player.Y - shooter.OriginY).Length(),
                        };
                    if (!double.IsNaN(a.Left) && a.Free.Count < 3 && (a.Free.Count == 0 || a.Free[^1].At != at)) a.Free.Add((t, at));
                    arrows[key] = a;
                }
                foreach (var d in f.Damage)
                {
                    string by = names.Name(f, d.Attacker, d.AttackerSerial), victim = names.Name(f, d.Victim, d.VictimSerial), inflictor = names.Name(f, d.Inflictor, d.InflictorSerial);
                    if (by.StartsWith("player", StringComparison.Ordinal) && victim == ArcherClass) woken.Add((d.Tick * interval, d.Victim));
                    if (by != ArcherClass || !victim.StartsWith("player", StringComparison.Ordinal)) continue;
                    var (clip, cycle) = f.Entities.TryGetValue(d.Attacker, out var s) && f.Sequences.GetValueOrDefault(s.ModelIndex) is { } sq && s.Sequence >= 0 && s.Sequence < sq.Length
                        ? (sq[s.Sequence], s.Cycle) : ("?", 0f);
                    damage.Add((d.Tick * interval, d.Attacker, inflictor, d.Damage, clip, cycle));
                }
            }
        var shots = arrows.Values.Where(a => !double.IsNaN(a.Left) && a.Free.Count >= 2).Select(a =>
        {
            var dir = Vector3.Normalize(a.Free[^1].At - a.Free[0].At);
            // Between its first two free samples, the second still 40 units or more short of the player (else it had struck).
            float speed = new Vector2(a.Free[1].At.X - a.Player.X, a.Free[1].At.Y - a.Player.Y).Length() >= 40
                ? Vector3.Distance(a.Free[1].At, a.Free[0].At) / (float)Math.Max(interval, a.Free[1].T - a.Free[0].T) : float.NaN;
            var rel = a.Player + new Vector3(0, 0, 36) - a.Free[0].At;
            var off = rel - dir * Vector3.Dot(rel, dir);
            var blow = damage.FirstOrDefault(d => d.Attacker == a.Owner && d.By == "arrowshot" && d.T >= a.Left - interval && d.T <= a.Left + .5);
            // Where its line passed the player, from the eye: the middle less the offset to the line, against the eye.
            float fromEye = a.Player.Z + 36 - off.Z - a.Eye;
            return (a.Owner, Arrow: new NpcArrow(a.Nocked, a.Left, a.Standing, a.Distance, a.PlayerSpeed, speed, off.Z, blow.T > 0 ? blow.T : double.NaN, blow.Asked, fromEye, a.NockedStanding));
        }).ToList();
        // Each waking (the first of the player's blows on a new archer, or one 10 s after the last) starts a fight, to the next.
        var fights = new List<ArcherFight>();
        var wakes = woken.GroupBy(w => w.Archer).SelectMany(g => g.Aggregate(new List<double>(), (l, w) => { if (l.Count == 0 || w.T - l[^1] > 10) l.Add(w.T); return l; }).Select(t => (T: t, Archer: g.Key))).OrderBy(w => w.T).ToList();
        for (int k = 0; k < wakes.Count; k++)
        {
            var (from, archer) = wakes[k];
            double to = k + 1 < wakes.Count ? wakes[k + 1].T - 3 : double.PositiveInfinity;
            var list = clips.GetValueOrDefault(archer)?.Where(c => c.T >= from && c.T < to).ToList() ?? [];
            double First(string clip) => list.FirstOrDefault(c => c.Clip == clip) is { Clip: not null } c ? c.T : double.NaN;
            double kicked = First("bow_kick"), dropped = First("drop_bow"), unsheathed = First("unsheathe_sword");
            var kick = damage.FirstOrDefault(d => d.Attacker == archer && !double.IsNaN(kicked) && d.T >= kicked && d.T <= kicked + 1 && d.Clip == "bow_kick");
            // Where it stood to shoot: the distance its first arrow let go standing after each bow_attack began.
            fights.Add(new ArcherFight(archer, from, arrows.Values.Where(a => a.Owner == archer && a.Nocked >= from && a.Nocked < to).Select(a => a.Nocked).Order().ToList(),
                shots.Where(s => s.Owner == archer && s.Arrow.Nocked >= from && s.Arrow.Nocked < to).Select(s => s.Arrow).OrderBy(a => a.Nocked).ToList(),
                list.Where(c => c.Clip == "bow_attack").Select(c => shots.Where(s => s.Owner == archer && s.Arrow.Standing && s.Arrow.Left >= c.T).Select(s => s.Arrow.Distance).DefaultIfEmpty(float.NaN).First()).Where(d => !float.IsNaN(d)).ToList(),
                kicked, kick.Asked, dropped, unsheathed,
                damage.Where(d => d.Attacker == archer && !double.IsNaN(unsheathed) && d.T > unsheathed && d.T < to).Select(d => (d.Clip, d.Cycle, d.Asked, d.By)).ToList(),
                list.Where(c => c.Clip == "bow_attack").Select(c => c.T).ToList()));
        }
        return fights;
    }
}
