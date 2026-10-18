using RustyBridge.Wire;

namespace DarkMessiah.Mirror.Replay;

/// <summary>One recorded donor tick: its number and curtime.</summary>
internal readonly record struct FightFrame(int Tick, double Time);

/// <summary>
/// A recording reduced to what the mirror draws from: the frame clock, the
/// level's class and model tables, each model's sequence names, and one
/// track per entity (edict index and serial) holding only the states in
/// which it changed. Whole frames aren't kept: a two-minute fight has about
/// 8,000 frames of 1,300 entities, most of which never move.
/// </summary>
internal sealed class RecordedFight
{
    public string Map { get; private set; } = "";
    /// <summary>The recording streams each entity's move parent and attachment (Wire v10 and later), so held items need no inference.</summary>
    public bool HasParents { get; private set; }
    public double TickInterval { get; private set; }
    public IReadOnlyList<FightFrame> Frames => frames;
    public IReadOnlyList<EntityTrack> Tracks => tracks;
    public IReadOnlyDictionary<ushort, string> Classes => classes;
    public IReadOnlyDictionary<int, string> Models => models;
    /// <summary>Sequence names by model path, in <c>m_nSequence</c> order.</summary>
    public IReadOnlyDictionary<string, string[]> Sequences => sequences;
    /// <summary>Held weapon and attacker per damage record, as the damage hook saw them.</summary>
    public IReadOnlyList<(int Frame, EntityKey Weapon, EntityKey Holder)> Wielding => wielding;
    /// <summary>Every damage record: the frame, and the attacker and victim classes.</summary>
    public IReadOnlyList<(int Frame, string Attacker, string Victim)> Hits => hits;

    readonly List<FightFrame> frames = [];
    readonly List<EntityTrack> tracks = [];
    readonly Dictionary<ushort, string> classes = [];
    readonly Dictionary<int, string> models = [];
    readonly Dictionary<string, string[]> sequences = new(StringComparer.Ordinal);
    readonly List<(int, EntityKey, EntityKey)> wielding = [];
    readonly List<(int, string, string)> hits = [];

    public static RecordedFight Read(Stream stream)
    {
        var fight = new RecordedFight();
        var live = new Dictionary<EntityKey, EntityTrack>();
        var seen = new HashSet<EntityKey>();
        var layered = new HashSet<EntityKey>();
        var layersBy = new Dictionary<ushort, List<LayerState>>();
        foreach (var f in RecordingReader.Frames(stream))
        {
            int frame = fight.frames.Count;
            fight.frames.Add(new FightFrame(f.Tick, f.Curtime));
            fight.Map = f.Map;
            fight.HasParents |= f.Version >= 10;
            fight.TickInterval = f.TickInterval;
            foreach (var (id, name) in f.Classes) fight.classes[id] = name;
            foreach (var (index, path) in f.Models) fight.models[index] = path;
            foreach (var (index, names) in f.Sequences)
                if (f.Models.TryGetValue(index, out var path)) fight.sequences[path] = names;

            seen.Clear();
            foreach (var (index, e) in f.Entities)
            {
                var key = new EntityKey(index, e.Serial);
                seen.Add(key);
                if (!live.TryGetValue(key, out var track))
                {
                    track = new EntityTrack(key, fight.classes.GetValueOrDefault(e.ClassId, ""), frame);
                    live[key] = track;
                    fight.tracks.Add(track);
                }
                track.Observe(frame, f.Curtime, e);
            }
            // Overlay layers are stored whole every frame; an entity without any this frame has none.
            layersBy.Clear();
            foreach (var l in f.Layers)
            {
                if (!layersBy.TryGetValue(l.Index, out var list)) layersBy[l.Index] = list = [];
                list.Add(new LayerState(l.Slot, l.Sequence, l.Order, l.Cycle, l.Weight));
            }
            foreach (var (index, list) in layersBy)
                if (f.Entities.TryGetValue(index, out var e) && live.TryGetValue(new EntityKey(index, e.Serial), out var track))
                {
                    track.ObserveLayers(f.Curtime, [.. list]);
                    layered.Add(track.Key);
                }
            foreach (var key in layered.Where(k => !layersBy.ContainsKey(k.Index) || !live.ContainsKey(k)).ToList())
            {
                if (live.TryGetValue(key, out var track)) track.ObserveLayers(f.Curtime, []);
                layered.Remove(key);
            }
            foreach (var key in live.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                live[key].End(frame - 1);
                live.Remove(key);
            }
            string ClassOf(ushort index) => f.Entities.TryGetValue(index, out var e) ? fight.classes.GetValueOrDefault(e.ClassId, "?") : "-";
            foreach (var d in f.Damage) fight.hits.Add((frame, ClassOf(d.Attacker), ClassOf(d.Victim)));
            foreach (var d in f.Damage)
                if (d.Weapon != WireLayout.NoEntity && d.Attacker != WireLayout.NoEntity)
                    fight.wielding.Add((frame, new EntityKey(d.Weapon, d.WeaponSerial), new EntityKey(d.Attacker, d.AttackerSerial)));
        }
        foreach (var track in live.Values) track.End(fight.frames.Count - 1);
        return fight;
    }

    /// <summary>The last frame at or before donor time <paramref name="time"/>; 0 before the first.</summary>
    public int FrameAt(double time)
    {
        int lo = 0, hi = frames.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (frames[mid].Time <= time) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public string ModelPath(int modelIndex) => models.GetValueOrDefault(modelIndex, "");
}
