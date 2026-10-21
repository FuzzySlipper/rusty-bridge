using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>The kind of player swing an orc answered, as <see cref="DefenceCalibration"/> sorts them.</summary>
internal enum SwingKind { Fast, Power, Kick }

/// <summary>
/// How often an orc parried or dodged a player's swing it saw start (#10151), by the swing's kind and whether the orc
/// stood ready (not attacking, hurt, down or dodging): counts of swings, parries and dodges.
/// </summary>
internal sealed record DefenceRates(Dictionary<(SwingKind Kind, bool Ready), (int Swings, int Parries, int Dodges)> Counts)
{
    internal (int Swings, int Parries, int Dodges) this[SwingKind kind, bool ready] => Counts.GetValueOrDefault((kind, ready));

    internal void Add(SwingKind kind, bool ready, Defence answer)
    {
        var c = Counts.GetValueOrDefault((kind, ready));
        Counts[(kind, ready)] = (c.Swings + 1, c.Parries + (answer == Defence.Parry ? 1 : 0), c.Dodges + (answer == Defence.Dodge ? 1 : 0));
    }
}

/// <summary>
/// The orc's defence against the donor's (#10151): every player swing that started with a living orc within 150 units,
/// and what that orc began within 40 ticks (a parry layer or sequence, a dodge); the lab's over a scripted fight.
/// </summary>
internal static class DefenceCalibration
{
    const float Near = 150;
    const int Within = 40;

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
            // The swings by tick (a first-person clip starting), and each orc's sequence, layers and place by tick.
            var swings = new List<(int Tick, SwingKind Kind, float X, float Y)>();
            var orcs = new Dictionary<string, List<(int Tick, string Sequence, bool Parrying, float X, float Y, bool Alive)>>();
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
                            swings.Add((f.Tick, kind, player.OriginX, player.OriginY));
                    playing = now;
                    foreach (var (index, e) in f.Entities)
                    {
                        if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass) continue;
                        var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                        string Name(int s) => s >= 0 && s < names.Length ? names[s] : "";
                        bool parrying = f.Layers.Any(l => l.Index == index && l.Weight > 0 && Name(l.Sequence).Contains("parry", StringComparison.OrdinalIgnoreCase));
                        string id = $"#{index}.{e.Serial:x}";
                        if (!orcs.TryGetValue(id, out var list)) orcs[id] = list = [];
                        list.Add((f.Tick, Name(e.Sequence), parrying, e.OriginX, e.OriginY, !(e.Flags.HasFlag(WireEntityFlags.HasHealth) && (e.LifeState != 0 || e.Health <= 0))));
                    }
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            foreach (var (tick, kind, px, py) in swings)
            {
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
                for (int i = n.At + 1; i < states.Count && states[i].Tick <= tick + Within; i++)
                {
                    if (states[i].Sequence.StartsWith("Dodge_", StringComparison.Ordinal) && !before.StartsWith("Dodge_", StringComparison.Ordinal)) { answer = Defence.Dodge; break; }
                    if (states[i].Parrying || states[i].Sequence.Contains("parry", StringComparison.OrdinalIgnoreCase)) { answer = Defence.Parry; break; }
                }
                rates.Add(kind, !Busy(before), answer);
            }
        }
        return rates;
    }

    /// <summary>
    /// The lab's orc against a scripted player standing 1.5 m off: every 1.6 s a swing, fast twice, then a power attack,
    /// then a kick, in turn, and what it did about each within 40 ticks.
    /// </summary>
    internal static DefenceRates Lab(Orc orc, float seconds, float step = 1 / 66f)
    {
        var rates = new DefenceRates([]);
        SwingKind[] order = [SwingKind.Fast, SwingKind.Fast, SwingKind.Power, SwingKind.Kick];
        orc.Spawn();
        var player = System.Numerics.Vector3.Zero;
        orc.PlaceAt(new System.Numerics.Vector3(0, 0, 1.5f));
        int next = 0;
        float sinceSwing = 0;
        (SwingKind Kind, bool Ready, float Left)? watching = null;
        var answered = Defence.None;
        for (float t = 0; t < seconds; t += step)
        {
            sinceSwing += step;
            var incoming = IncomingSwing.None;
            if (sinceSwing >= 1.6f && watching is null)
            {
                sinceSwing = 0;
                var kind = order[next++ % order.Length];
                incoming = kind switch { SwingKind.Kick => IncomingSwing.Kick, SwingKind.Power => IncomingSwing.BreaksParry, _ => IncomingSwing.Parryable };
                watching = (kind, orc.State is OrcState.Ready or OrcState.Approach, Within * step);
                answered = Defence.None;
            }
            orc.Step(step, player, incoming);
            if (!orc.Alive) orc.Spawn();
            if (watching is { } w)
            {
                if (answered == Defence.None && orc.State == OrcState.Parry) answered = Defence.Parry;
                if (answered == Defence.None && orc.State == OrcState.Dodging) answered = Defence.Dodge;
                if (w.Left - step <= 0) { rates.Add(w.Kind, w.Ready, answered); watching = null; }
                else watching = w with { Left = w.Left - step };
            }
        }
        orc.Spawn();
        return rates;
    }
}
