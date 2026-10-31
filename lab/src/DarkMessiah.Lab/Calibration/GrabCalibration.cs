using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// An attack a recorded <c>npc_orc_sword</c> began (#10185): its clip, how far off the player was (Source units, 2D),
/// whether its grab was armed (the newest 4 of the player's blows it parried or blocked since its last grab within 10 s),
/// whether the player had a shield in hand (the view model's), and whether it came straight out of a sword contest.
/// </summary>
internal sealed record RecordedAttack(string Recording, int Tick, string Clip, float Units, bool Armed, bool Shielded, bool AfterContest);

/// <summary>
/// A recorded grab (#10185): when the hands went empty after it began (null: never, the grab cut short), when the
/// quick draw came (null: none, as the club and daggers show none), and how far the player was carried, Source units.
/// </summary>
internal sealed record RecordedGrab(string Recording, int Tick, bool AfterContest, float? HandsGone, float? QuickDraw, float Carried);

/// <summary>
/// What the recordings say of the orc's grab (#10185; docs/lab.md, "The orc's grab"): every attack an orc began, armed
/// or not, and every grab's hands, quick draw and carry. Orcs are followed by index; a recording ends what it can't read.
/// </summary>
internal static class GrabCalibration
{
    const string OrcClass = "npc_orc_sword", Grab = "attack_grab";

    static IEnumerable<RecordedFrame> Frames(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = RecordingReader.Frames(stream).GetEnumerator();
        while (true)
        {
            try { if (!frames.MoveNext()) yield break; }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException or InvalidDataException) { yield break; }
            yield return frames.Current;
        }
    }

    /// <summary>The player's view-model layers this frame, by name (empty: the hands are gone).</summary>
    static List<string> ViewModel(RecordedFrame f)
    {
        var vm = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");
        if (!f.ClientView.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) || f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is not { } names) return [];
        return [.. f.ClientView.ViewmodelLayers.ToArray().Where(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length).Select(l => names[l.Sequence])];
    }

    /// <summary>Every attack the recording's orcs began, and every grab's hands, quick draw and carry.</summary>
    internal static (List<RecordedAttack> Attacks, List<RecordedGrab> Grabs) Read(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var attacks = new List<RecordedAttack>();
        var grabs = new List<RecordedGrab>();
        var parried = new Dictionary<ushort, List<double>>();
        var playing = new Dictionary<ushort, string>();
        var before = new Dictionary<ushort, string>();
        var open = new List<(int Tick, double T, bool Contest, Vector3 From, float? Gone, float? Drawn, float Carried, bool Seen)>();
        bool shielded = false;
        foreach (var f in Frames(path))
        {
            double t = f.Tick * (f.TickInterval > 0 ? f.TickInterval : .015);
            var me = f.Entities.GetValueOrDefault(f.Player.Index);
            var feet = new Vector3(me.OriginX, me.OriginY, me.OriginZ);
            var vm = ViewModel(f);
            if (vm.Count > 0) shielded = vm.Any(l => l.Contains("_shield", StringComparison.Ordinal) && !l.Contains("noshield", StringComparison.Ordinal));
            foreach (var d in f.Defense)
                if (d.Attacker == f.Player.Index && d.Result is DefenseResult.Parry or DefenseResult.Shield
                    && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) == OrcClass)
                {
                    if (!parried.TryGetValue(d.Victim, out var list)) parried[d.Victim] = list = [];
                    list.Insert(0, t);
                    if (list.Count > 4) list.RemoveAt(4);
                }
            foreach (var (index, e) in f.Entities)
            {
                if (f.Classes.GetValueOrDefault(e.ClassId) != OrcClass) continue;
                string seq = f.Sequences.GetValueOrDefault(e.ModelIndex) is { } n && e.Sequence >= 0 && e.Sequence < n.Length ? n[e.Sequence] : "";
                if (playing.GetValueOrDefault(index) == seq) continue;
                before[index] = playing.GetValueOrDefault(index) ?? "";
                playing[index] = seq;
                if (!seq.StartsWith("attack", StringComparison.OrdinalIgnoreCase)) continue;
                var list = parried.GetValueOrDefault(index) ?? [];
                bool contest = before[index].StartsWith("versus", StringComparison.Ordinal);
                float units = new Vector2(e.OriginX - me.OriginX, e.OriginY - me.OriginY).Length();
                attacks.Add(new RecordedAttack(name, f.Tick, seq, units, list.Count >= 4 && t - list[3] < 10, shielded, contest));
                if (seq != Grab) continue;
                parried.Remove(index);
                open.Add((f.Tick, t, contest, feet, null, null, 0, false));
            }
            // The grabs under way: the hands' going and coming back, and the carry, for 9 s.
            for (int i = 0; i < open.Count; i++)
            {
                var g = open[i];
                float since = (float)(t - g.T);
                if (g.Gone is null && vm.Count == 0 && f.ClientView.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && since < 1) g.Gone = since;
                if (g.Drawn is null && vm.Any(l => l.EndsWith("_quickdraw", StringComparison.Ordinal))) g.Drawn = since;
                if (since <= 4.5f) g.Carried = MathF.Max(g.Carried, Vector3.Distance(g.From, feet));
                open[i] = g;
                if (since > 9) { grabs.Add(new RecordedGrab(name, g.Tick, g.Contest, g.Gone, g.Drawn, g.Carried)); open.RemoveAt(i--); }
            }
        }
        grabs.AddRange(open.Select(g => new RecordedGrab(name, g.Tick, g.Contest, g.Gone, g.Drawn, g.Carried)));
        return (attacks, grabs);
    }
}
