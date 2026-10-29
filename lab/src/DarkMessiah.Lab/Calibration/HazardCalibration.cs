using System.Numerics;
using DarkMessiah.Shared;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A recorded kick's push (#10157): the clip it played, how far it moved the NPC, and whether the clip played out.</summary>
internal sealed record RecordedPush(string Recording, int Tick, string Clip, float Units, bool PlayedOut, float Cycle);

/// <summary>
/// A recorded NPC on fire (#10157): whether a kick sent it in, whether it was thrown down first, its health when
/// <c>onfire</c> began, how long that played, and whether it was dead after.
/// </summary>
internal sealed record RecordedBurn(string Recording, int Tick, bool Kicked, bool ThrownDown, float Health, float OnFireSeconds, bool Died);

/// <summary>A recorded NPC dropped from a height (#10157): the drop, Source units, and whether it died of it.</summary>
internal sealed record RecordedDrop(string Recording, int Tick, float Units, bool Died);

/// <summary>
/// What the recordings say of kicks and the hazards they send NPCs into (#10157; docs/lab.md, "Kicks into hazards"):
/// every kick's push against its clip's root motion, every NPC that caught fire, and every NPC that fell. NPCs are
/// followed by index and serial; a recording ends what it can't read.
/// </summary>
internal static class HazardCalibration
{
    const int KickFlag = 0x400000;

    static IEnumerable<RecordedFrame> Frames(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = RecordingReader.Frames(stream).GetEnumerator();
        while (true)
        {
            try { if (!frames.MoveNext()) yield break; }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { yield break; }
            yield return frames.Current;
        }
    }

    static string SequenceOf(RecordedFrame f, WireEntity e) =>
        f.Sequences.GetValueOrDefault(e.ModelIndex) is { } names && e.Sequence >= 0 && e.Sequence < names.Length ? names[e.Sequence] : "";

    /// <summary>
    /// Every kick of the player's on an <c>npc_orc_sword</c> that played a push clip: how far the orc moved from the kick
    /// to the clip's end or the next sequence, and whether the clip played out (to cycle 0.85: the donor leaves a push at
    /// 0.90, as it does most) rather than being cut short.
    /// </summary>
    internal static List<RecordedPush> Pushes(IEnumerable<string> paths)
    {
        var pushes = new List<RecordedPush>();
        foreach (var path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var open = new Dictionary<(ushort, uint), (int Tick, Vector2 Start, string Clip, float Cycle, Vector2 At)>();
            foreach (var f in Frames(path))
            {
                foreach (var (key, push) in open.ToList())
                {
                    if (!f.Entities.TryGetValue(key.Item1, out var e) || e.Serial != key.Item2) { open.Remove(key); continue; }
                    string seq = SequenceOf(f, e);
                    var at = new Vector2(e.OriginX, e.OriginY);
                    if (push.Clip.Length == 0)
                    {
                        if (seq.StartsWith("Push_", StringComparison.Ordinal)) open[key] = push with { Clip = seq, Cycle = e.Cycle, At = at };
                        else if (f.Tick - push.Tick > 20) open.Remove(key);
                        continue;
                    }
                    if (seq == push.Clip && e.Cycle >= push.Cycle) { open[key] = push with { Cycle = e.Cycle, At = at }; continue; }
                    pushes.Add(new RecordedPush(name, push.Tick, push.Clip, Vector2.Distance(push.Start, push.At), push.Cycle >= .85f, push.Cycle));
                    open.Remove(key);
                }
                foreach (var d in f.Damage)
                {
                    if ((d.MMDamageFlag & KickFlag) == 0 || d.Damage <= 0 || !f.Entities.TryGetValue(d.Attacker, out var a) || f.Classes.GetValueOrDefault(a.ClassId) != "player") continue;
                    if (!f.Entities.TryGetValue(d.Victim, out var v) || f.Classes.GetValueOrDefault(v.ClassId) != "npc_orc_sword") continue;
                    open[(d.Victim, v.Serial)] = (f.Tick, new Vector2(v.OriginX, v.OriginY), "", 0, new Vector2(v.OriginX, v.OriginY));
                }
            }
        }
        return pushes;
    }

    /// <summary>
    /// Every <c>npc_orc_sword</c> that played <c>onfire</c>: kicked (a kick on it in the 9 s before), thrown down (it played a
    /// <c>StandUp_</c> clip between), its health as it began, how long it played, and whether the NPC was gone or dead
    /// within a second after.
    /// </summary>
    internal static List<RecordedBurn> Burns(string path)
    {
        var burns = new List<RecordedBurn>();
        string name = Path.GetFileNameWithoutExtension(path);
        var kicked = new Dictionary<(ushort, uint), int>();
        var thrown = new HashSet<(ushort, uint)>();
        var burning = new Dictionary<(ushort, uint), (int Tick, float Health)>();
        var ended = new Dictionary<(ushort, uint), (int Tick, float Health, int End)>();
        int last = 0;
        foreach (var f in Frames(path))
        {
            last = f.Tick;
            foreach (var d in f.Damage)
                if ((d.MMDamageFlag & KickFlag) != 0 && d.Damage > 0 && f.Entities.TryGetValue(d.Victim, out var kv)) kicked[(d.Victim, kv.Serial)] = f.Tick;
            foreach (var (index, e) in f.Entities)
            {
                if (f.Classes.GetValueOrDefault(e.ClassId) != "npc_orc_sword") continue;
                var key = (index, e.Serial);
                string seq = SequenceOf(f, e);
                if (seq.StartsWith("StandUp_", StringComparison.Ordinal)) thrown.Add(key);
                if (seq == "onfire" && !burning.ContainsKey(key) && !ended.ContainsKey(key)) burning[key] = (f.Tick, e.Health);
                if (seq != "onfire" && burning.Remove(key, out var b)) ended[key] = (b.Tick, b.Health, f.Tick);
            }
            // Gone mid-burn: it died in it.
            foreach (var key in burning.Keys.ToList())
                if (!f.Entities.TryGetValue(key.Item1, out var e) || e.Serial != key.Item2 || e.LifeState != 0)
                {
                    var b = burning[key];
                    burning.Remove(key);
                    burns.Add(new RecordedBurn(name, b.Tick, kicked.TryGetValue(key, out int k) && b.Tick - k < 600, thrown.Contains(key), b.Health, (f.Tick - b.Tick) / 66.67f, true));
                }
        }
        foreach (var (key, b) in ended)
            burns.Add(new RecordedBurn(name, b.Tick, kicked.TryGetValue(key, out int k) && b.Tick - k < 600, thrown.Contains(key), b.Health, (b.End - b.Tick) / 66.67f, false));
        return burns.OrderBy(b => b.Tick).ToList();
    }

    /// <summary>
    /// Every <c>npc_orc_sword</c> that fell more than 100 units in the air (a setpos drop): from the highest it was to where
    /// it came down, and whether it died (gone, a ragdoll in its place) within 3 s.
    /// </summary>
    internal static List<RecordedDrop> Drops(string path)
    {
        var drops = new List<RecordedDrop>();
        string name = Path.GetFileNameWithoutExtension(path);
        var air = new Dictionary<(ushort, uint), (int Tick, float Top, float Low, int Seen)>();
        foreach (var f in Frames(path))
        {
            foreach (var (index, e) in f.Entities)
            {
                if (f.Classes.GetValueOrDefault(e.ClassId) != "npc_orc_sword") continue;
                var key = (index, e.Serial);
                if (!air.TryGetValue(key, out var a)) { air[key] = (f.Tick, e.OriginZ, e.OriginZ, f.Tick); continue; }
                // A placement up (an addoutput origin) starts a drop from there.
                if (e.OriginZ > a.Low + 50) a = (f.Tick, e.OriginZ, e.OriginZ, f.Tick);
                air[key] = (a.Tick, MathF.Max(a.Top, e.OriginZ), MathF.Min(a.Low, e.OriginZ), f.Tick);
            }
            foreach (var (key, a) in air.ToList())
            {
                bool gone = !f.Entities.TryGetValue(key.Item1, out var e) || e.Serial != key.Item2;
                if (f.Tick - a.Tick < 200 && !gone) continue;
                // Standing where it came down: the drop is from its top to its lowest.
                float drop = a.Top - (gone ? GroundUnder(f, a) : a.Low);
                if (drop > 100) drops.Add(new RecordedDrop(name, a.Tick, drop, gone));
                air.Remove(key);
            }
        }
        return drops;
    }

    // Where a ragdoll came to rest stands for the ground: the lowest prop_ragdoll's origin, near enough.
    static float GroundUnder(RecordedFrame f, (int Tick, float Top, float Low, int Seen) a) =>
        f.Entities.Values.Where(e => f.Classes.GetValueOrDefault(e.ClassId) == "prop_ragdoll").Select(e => e.OriginZ).DefaultIfEmpty(a.Low).Min();
}
