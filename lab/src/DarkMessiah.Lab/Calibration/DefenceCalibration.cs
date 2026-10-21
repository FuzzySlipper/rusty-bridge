using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>The kind of player swing an orc answered, as <see cref="DefenceCalibration"/> sorts them.</summary>
internal enum SwingKind { Fast, Power, Kick }

/// <summary>
/// What the orc was doing as a swing started (R10151-1): standing ready (idle, walking, strafing), attacking, recovering
/// (hit, pushed, stunned, down, dodging) or wounded (below its <c>life_tohurt</c> share of health, or playing its wounded
/// sequences, whatever else it does).
/// </summary>
internal enum OrcSituation { Ready, Attacking, Recovering, Wounded }

/// <summary>
/// One player swing an orc saw start, and what it did about it within <see cref="DefenceCalibration.Within"/> ticks.
/// </summary>
/// <param name="Direction">A power attack's direction (<c>T</c>, <c>B</c>, <c>L</c>, <c>R</c>, or <c>N</c> for the
/// neutral one); empty for the others.</param>
/// <param name="Nth">How many swings this orc had seen before (from 0).</param>
/// <param name="NthKick">How many kicks this orc had seen before (from 0).</param>
internal sealed record DefenceCase(SwingKind Kind, string Direction, OrcSituation Situation, int Nth, int NthKick, Defence Answer);

/// <summary>How often an orc parried or dodged the swings it saw start (#10151, R10151-1), case by case.</summary>
internal sealed record DefenceRates(List<DefenceCase> Cases)
{
    internal (int Swings, int Parries, int Dodges) Count(Func<DefenceCase, bool> where)
    {
        var picked = Cases.Where(where).ToList();
        return (picked.Count, picked.Count(c => c.Answer == Defence.Parry), picked.Count(c => c.Answer == Defence.Dodge));
    }

    /// <summary>The swings of a kind on an orc standing ready (or not).</summary>
    internal (int Swings, int Parries, int Dodges) this[SwingKind kind, bool ready] =>
        Count(c => c.Kind == kind && (c.Situation == OrcSituation.Ready) == ready);

    internal static string Direction(string clip)
    {
        foreach (var d in new[] { "T", "B", "L", "R" })
            if (clip.Contains($"_{d}_", StringComparison.Ordinal)) return d;
        return "N";
    }
}

/// <summary>
/// The orc's defence against the donor's (#10151): every player swing that started with a living orc within 150 units,
/// and what that orc began within 40 ticks or before the next swing (a parry layer or sequence, a dodge); the lab's
/// over a scripted fight.
/// </summary>
internal static class DefenceCalibration
{
    /// <summary>An orc within this many units of the player as the swing starts sees it.</summary>
    internal const float Near = 150;
    /// <summary>An answer begun this many ticks after the swing's start is the swing's.</summary>
    internal const int Within = 40;

    /// <summary>The orc's situation by its sequence, wounded first.</summary>
    internal static OrcSituation Situation(string sequence, bool wounded) =>
        wounded || sequence.Contains("ounded", StringComparison.Ordinal) || sequence.StartsWith("Hurt", StringComparison.OrdinalIgnoreCase) ? OrcSituation.Wounded
        : sequence.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("Turn_strike", StringComparison.OrdinalIgnoreCase)
            || sequence.StartsWith("Dodgestrike", StringComparison.OrdinalIgnoreCase) ? OrcSituation.Attacking
        : Busy(sequence) ? OrcSituation.Recovering
        : OrcSituation.Ready;

    internal static SwingKind? KindOf(string clip) =>
        clip.Contains("kick", StringComparison.Ordinal) ? SwingKind.Kick
        : clip.Contains("fastattack", StringComparison.Ordinal) && !clip.Contains("_end", StringComparison.Ordinal) ? SwingKind.Fast
        : clip.Contains("bigslash", StringComparison.Ordinal) && clip.EndsWith("_out", StringComparison.Ordinal) ? SwingKind.Power
        : null;

    static bool Busy(string sequence) =>
        sequence.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("Turn_strike", StringComparison.OrdinalIgnoreCase)
        || sequence.StartsWith("Dodge", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("Hit", StringComparison.OrdinalIgnoreCase)
        || sequence.StartsWith("Push", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("hit_", StringComparison.OrdinalIgnoreCase)
        || sequence.Contains("Stunned", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("StandUp", StringComparison.OrdinalIgnoreCase)
        || sequence.Contains("ounded", StringComparison.Ordinal) || sequence.StartsWith("Hurt", StringComparison.OrdinalIgnoreCase);

    internal static DefenceRates Donor(IEnumerable<string> recordings)
    {
        var rates = new DefenceRates([]);
        foreach (var path in recordings)
        {
            // The swings by tick (a first-person clip starting), and each orc's sequence, layers, place and health by tick.
            var swings = new List<(int Tick, SwingKind Kind, string Clip, float X, float Y)>();
            var orcs = new Dictionary<string, List<(int Tick, string Sequence, bool Parrying, float X, float Y, bool Alive, float Health)>>();
            var seen = new Dictionary<string, (int Swings, int Kicks, float MaxHealth)>();
            var playing = new Dictionary<string, float>();
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var f in RecordingReader.Frames(stream))
                {
                    var now = DamageCalibration.MoveLayers(f);
                    var player = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "player");
                    foreach (var (clip, cycle) in now)
                        if (KindOf(clip) is { } kind && (!playing.TryGetValue(clip, out float was) || cycle < was - .05f) && f.Classes.GetValueOrDefault(player.ClassId) == "player")
                            swings.Add((f.Tick, kind, clip, player.OriginX, player.OriginY));
                    playing = now;
                    foreach (var (index, e) in f.Entities)
                    {
                        if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass) continue;
                        var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                        string Name(int s) => s >= 0 && s < names.Length ? names[s] : "";
                        bool parrying = f.Layers.Any(l => l.Index == index && l.Weight > 0 && Name(l.Sequence).Contains("parry", StringComparison.OrdinalIgnoreCase));
                        string id = $"#{index}.{e.Serial:x}";
                        if (!orcs.TryGetValue(id, out var list)) orcs[id] = list = [];
                        bool health = e.Flags.HasFlag(WireEntityFlags.HasHealth);
                        list.Add((f.Tick, Name(e.Sequence), parrying, e.OriginX, e.OriginY, !(health && (e.LifeState != 0 || e.Health <= 0)), health ? e.Health : float.NaN));
                    }
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            for (int w = 0; w < swings.Count; w++)
            {
                var (tick, kind, clip, px, py) = swings[w];
                // An answer is the latest swing's: this one's window ends where the next swing starts.
                int until = Math.Min(tick + Within, w + 1 < swings.Count ? swings[w + 1].Tick : int.MaxValue);
                // The nearest living orc at the swing's start, within reach.
                (string Id, int At, float Distance)? nearest = null;
                foreach (var (id, list) in orcs)
                {
                    int at = list.FindLastIndex(s => s.Tick <= tick);
                    if (at < 0 || !list[at].Alive) continue;
                    float d = MathF.Sqrt((list[at].X - px) * (list[at].X - px) + (list[at].Y - py) * (list[at].Y - py));
                    if (d <= Near && (nearest is null || d < nearest.Value.Distance)) nearest = (id, at, d);
                }
                if (nearest is not { } n) continue;
                var states = orcs[n.Id];
                string before = states[n.At].Sequence;
                var answer = Defence.None;
                for (int i = n.At + 1; i < states.Count && states[i].Tick <= until; i++)
                {
                    if (states[i].Sequence.StartsWith("Dodge_", StringComparison.Ordinal) && !before.StartsWith("Dodge_", StringComparison.Ordinal)) { answer = Defence.Dodge; break; }
                    if (states[i].Parrying || states[i].Sequence.Contains("parry", StringComparison.OrdinalIgnoreCase)) { answer = Defence.Parry; break; }
                }
                // Wounded below its life_tohurt share (0.2) of the most health it was seen with.
                var had = seen.GetValueOrDefault(n.Id);
                float most = MathF.Max(had.MaxHealth, states.Take(n.At + 1).Select(s => float.IsNaN(s.Health) ? 0 : s.Health).DefaultIfEmpty(0).Max());
                float now = states[n.At].Health;
                bool wounded = !float.IsNaN(now) && most > 0 && now <= HurtFraction * most;
                rates.Cases.Add(new DefenceCase(kind, kind == SwingKind.Power ? DefenceRates.Direction(clip) : "", Situation(before, wounded), had.Swings, had.Kicks, answer));
                seen[n.Id] = (had.Swings + 1, had.Kicks + (kind == SwingKind.Kick ? 1 : 0), most);
            }
        }
        return rates;
    }

    /// <summary>The orc's <c>life_tohurt</c> (its <c>.qct</c>'s 0.2), below which the donor's measure counts it wounded.</summary>
    const float HurtFraction = .2f;

    /// <summary>
    /// The lab's orc fighting a scripted player through the lab's combat (R10151-1): the player stands at the origin
    /// facing the orc, which fights as it would (it attacks, its blows land, it is hit, pushed, stunned, knocked down
    /// and wounded, and respawns when it dies), and while it is within <see cref="Near"/> units the player
    /// swings (in the orc's idle moments, or after 1.8 s): two fast attacks, a power attack (neutral, then forward, back, left and right in turn), a kick. Each
    /// swing's start is a case, with the orc's situation and what it began within <see cref="Within"/> ticks or before
    /// the next swing. In the second half the orc makes no attacks, standing ready for more of them.
    /// </summary>
    internal static DefenceRates Lab(LabCombat combat, float eyeHeight, float seconds, float step = 1 / 66f)
    {
        var rates = new DefenceRates([]);
        var orc = combat.Orc;
        SwingKind[] order = [SwingKind.Fast, SwingKind.Fast, SwingKind.Power, SwingKind.Kick];
        System.Numerics.Vector2[] directions = [default, new(0, 1), new(0, -1), new(-1, 0), new(1, 0)];
        var feet = System.Numerics.Vector3.Zero;
        var eye = feet + System.Numerics.Vector3.UnitY * eyeHeight;
        int next = 0, powers = 0, swingsSeen = 0, kicksSeen = 0;
        float wait = 1.5f, hold = 0;
        SwingKind? pressing = null;
        var move = System.Numerics.Vector2.Zero;
        (DefenceCase Case, int Left)? watching = null;
        ulong tick = 0;
        bool alive = orc.Alive;
        bool held = orc.Hold;
        for (float t = 0; t < seconds; t += step, tick++)
        {
            // The second half the orc holds its attacks: it stands ready for the power attacks and kicks, which its
            // back-to-back attacks leave few idle moments for.
            orc.Hold = held || t >= seconds / 2;
            var flat = (orc.Feet - feet) with { Y = 0 };
            var forward = flat.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(flat) : System.Numerics.Vector3.UnitZ;
            bool near = flat.Length() / SourceSpace.MetresPerUnit <= Near;
            wait -= step;
            // The player swings in the orc's idle moments (out of its attack with its wait not yet over; else it attacks
            // in the very step), as the recorded players mostly swung at a ready orc, at least half a second after its
            // last; and anyway after 1.8 s, since a player holding off 2 s makes it berserk, attacking back to back.
            bool idle = orc.State is OrcState.Ready or OrcState.Approach && !orc.MayAttack;
            if (pressing is null && near && orc.Alive && combat.Sword.Move == Sword.SwordMove.Ready && (idle && wait <= 1.3f || wait <= 0))
            {
                pressing = order[next++ % order.Length];
                hold = pressing == SwingKind.Fast ? 3 * step : pressing == SwingKind.Power ? 40 * step : step;
                move = pressing == SwingKind.Power ? directions[powers++ % directions.Length] : default;
                wait = 1.8f;
            }
            bool attack = pressing is SwingKind.Fast or SwingKind.Power && hold > 0, kick = pressing == SwingKind.Kick && hold > 0;
            var before = orc.State;
            string clip = orc.Clip;
            bool wounded = orc.Wounded;
            combat.Step(step, tick, attack, false, kick, eye, forward, feet, move: attack ? move : default);

            if (pressing is not null && (hold -= step) <= -step) pressing = null;
            // A new orc after a death: its count starts again.
            if (orc.Alive && !alive) swingsSeen = kicksSeen = 0;
            alive = orc.Alive;
            if (watching is { } w)
            {
                var answer = w.Case.Answer != Defence.None ? w.Case.Answer
                    : orc.State == OrcState.Parry && before != OrcState.Parry ? Defence.Parry
                    : orc.State == OrcState.Dodging && before != OrcState.Dodging ? Defence.Dodge : Defence.None;
                watching = (w.Case with { Answer = answer }, w.Left - 1);
                if (w.Left - 1 <= 0) { rates.Cases.Add(watching.Value.Case); watching = null; }
            }
            if (combat.Sword.SwingStarted && watching is { } open)
            {
                rates.Cases.Add(open.Case);
                watching = null;
            }
            if (combat.Sword.SwingStarted && near && orc.Alive && watching is null
                && combat.Sword.Move is Sword.SwordMove.Fast or Sword.SwordMove.Power or Sword.SwordMove.Kick)
            {
                var kind = combat.Sword.Move == Sword.SwordMove.Kick ? SwingKind.Kick : combat.Sword.Move == Sword.SwordMove.Power ? SwingKind.Power : SwingKind.Fast;
                // The situation as the swing began, before the orc answered it this step.
                var situation = wounded ? OrcSituation.Wounded
                    : before == OrcState.Attack ? OrcSituation.Attacking
                    : before is OrcState.Ready or OrcState.Approach or OrcState.Parry ? OrcSituation.Ready
                    : OrcSituation.Recovering;
                var answer = orc.State == OrcState.Parry && before != OrcState.Parry ? Defence.Parry
                    : orc.State == OrcState.Dodging && before != OrcState.Dodging ? Defence.Dodge : Defence.None;
                watching = (new DefenceCase(kind, kind == SwingKind.Power ? DefenceRates.Direction(combat.Sword.Clip.Clip) : "", situation,
                    swingsSeen, kicksSeen, answer), Within);
                swingsSeen++;
                if (kind == SwingKind.Kick) kicksSeen++;
            }
        }
        orc.Hold = held;
        combat.Reset();
        return rates;
    }
}
