using System.Numerics;
using DarkMessiah.Mirror.Replay;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Mirror.Scene;

/// <summary>
/// Draws the recording's entities at a donor time: each live entity whose
/// model was imported gets an appearance at its interpolated transform, an
/// animated one gets its streamed sequence sampled at its streamed cycle, and
/// a held item hangs from its holder's attachment joint. Only what changed
/// since the last call is published.
///
/// From Wire v6 an animated entity's overlay layers play over its main
/// sequence as the Engine's clip layers: one override per layer, in Source's
/// layer order, at the layer's cycle and weight. They are read at the donor
/// time the drawn state stands for, so they keep step with the main sequence.
/// Dark Messiah's layer clips (parries, blocks, attack gestures, hit
/// reactions) have channels only for the bones they drive, and the
/// full-body ones (a sequence fading out under the next) blend the whole pose,
/// as Source does.
/// </summary>
internal sealed class EntityViews : IDisposable
{
    const ulong WorldObject = 1;

    sealed class View(EntityTrack track, ulong id, LevelModel model, Appearance appearance)
    {
        public EntityTrack Track { get; } = track;
        public ulong Id { get; } = id;
        public LevelModel Model { get; } = model;
        public Appearance Appearance { get; } = appearance;
        public AnimationInstance? Instance;
        public bool Published;
        public Transform Placed;
        public ulong Parent;
        public string? Clip;
        public float Cycle = -1;
        public (uint Clip, float Cycle, float Weight)[] Layers = [];
        public string[] LayerNames = [];
    }

    readonly IEngineContext engine;
    readonly RecordedFight fight;
    readonly LevelAssets assets;
    readonly HeldItems held;
    readonly Dictionary<EntityKey, View> views = [];
    readonly HashSet<string> missingClips = new(StringComparer.Ordinal);
    readonly Dictionary<LevelModel, Dictionary<string, uint>> clipIndices = [];
    readonly AnimationLayerSet layerSet = new();
    readonly List<(uint Clip, float Cycle, float Weight)> wanted = [];
    readonly List<string> wantedNames = [];
    readonly List<View> leaving = [];
    readonly Appearance? world;
    bool worldPublished;
    ulong nextId = WorldObject + 1;

    public int Drawn => views.Count;
    public int Attached { get; private set; }
    public int Sampled { get; private set; }
    /// <summary>Animated entities drawing at least one overlay layer in the last update.</summary>
    public int Layered { get; private set; }
    /// <summary>Overlay layers laid in the last update, over all entities.</summary>
    public int LayersDrawn { get; private set; }
    /// <summary>Whether overlay layers are drawn; off shows the main sequences alone, for comparison.</summary>
    public bool LayersOn { get; set; } = true;
    public IReadOnlyCollection<string> MissingClips => missingClips;
    /// <summary>The last scene change the Engine refused, if any.</summary>
    public string? LastError { get; private set; }

    public EntityViews(IEngineContext engine, RecordedFight fight, LevelAssets assets, HeldItems held)
    {
        this.engine = engine;
        this.fight = fight;
        this.assets = assets;
        this.held = held;
        if (assets.Model(assets.WorldPath) is LevelModel map)
            world = engine.Animation.CreateAnimatedMeshAppearance(new(map.Mesh));
    }

    /// <summary>The model a track is drawn with, if it is drawn.</summary>
    public LevelModel? ModelOf(EntityTrack track) => views.TryGetValue(track.Key, out var view) && view.Track == track ? view.Model : null;

    /// <summary>Whether a track hangs from another entity's attachment now.</summary>
    public bool IsHung(EntityTrack track) => views.TryGetValue(track.Key, out var view) && view.Track == track && view.Published && view.Parent != 0;

    /// <summary>Where a track is drawn now, in glTF space; null when it isn't drawn.</summary>
    public Transform? Placement(EntityKey key) => views.TryGetValue(key, out var view) && view.Published ? view.Placed : null;

    public void Present(int frame, double time)
    {
        var upserts = new List<AppearanceFact>();
        var removals = new List<ulong>();
        var attachments = new List<MeshJointAttachment>();
        var retired = new List<View>();
        if (!worldPublished && world is not null)
        {
            upserts.Add(new(WorldObject, false, 0, new(Vector3.Zero, Quaternion.Identity, Vector3.One), world, true, RenderLayer.Scene));
            worldPublished = true;
        }

        // Views a refused batch failed to remove go now.
        foreach (var view in leaving) if (view.Published) removals.Add(view.Id);
        retired.AddRange(leaving);
        leaving.Clear();

        // Entities that have gone, or changed model, leave first.
        foreach (var (key, view) in views.ToList())
        {
            if (view.Track.LiveAt(frame) && fight.ModelPath(view.Track.Sample(time).ModelIndex) == view.Model.DonorPath) continue;
            if (view.Published) removals.Add(view.Id);
            retired.Add(view);
            views.Remove(key);
        }

        var samples = new Dictionary<EntityKey, EntitySample>();
        foreach (var track in fight.Tracks)
        {
            if (!track.LiveAt(frame) || track.UpdateAt(time) < 0) continue;
            var role = DonorClasses.Role(track.ClassName);
            if (role == EntityRole.Hidden) continue;
            var sample = track.Sample(time);
            string path = fight.ModelPath(sample.ModelIndex);
            if (path.Length == 0 || path == "*0" || path == assets.WorldPath) continue;
            if (!views.TryGetValue(track.Key, out var view))
            {
                if (assets.Model(path) is not LevelModel model) continue;
                view = new View(track, nextId++, model, engine.Animation.CreateAnimatedMeshAppearance(new(model.Mesh)));
                views[track.Key] = view;
            }
            samples[track.Key] = sample;
        }

        Attached = 0;
        // The Engine refuses a batch that removes a parent and leaves its child hanging from it, so a
        // child of a leaving holder is published again in the same batch, loose (its parent changes).
        var removed = removals.ToHashSet();
        // Only a holder already in the scene, and staying, can take a child in this batch.
        var standing = views.Values.Where(v => v.Published && !removed.Contains(v.Id)).Select(v => v.Id).ToHashSet();
        var published = new List<(View View, Transform Placed, ulong Parent)>();
        foreach (var (key, sample) in samples)
        {
            var view = views[key];
            var placed = new Transform(SourceSpace.Position(sample.Origin), SourceSpace.Rotation(sample.Rotation), Vector3.One);
            ulong parent = 0;
            string? joint = null;
            // From v10 anything with a streamed move parent hangs from the attachment the donor named (a
            // weapon from its holder's hand, a rider from its horse); before v10 only held items, by inference.
            if ((held.Streamed || DonorClasses.Role(view.Track.ClassName) == EntityRole.Held)
                && held.Holder(view.Track, frame, time) is Holding holding
                && views.TryGetValue(holding.Holder.Key, out var holderView) && holderView.Track == holding.Holder && standing.Contains(holderView.Id)
                && (holding.Streamed ? holderView.Model.AttachmentAt(holding.Attachment)
                    : holderView.Model.Attachments.TryGetValue(DonorClasses.Attachment(view.Track.ClassName), out var named) ? named : null) is ModelAttachment attachment)
            {
                parent = holderView.Id;
                joint = attachment.Joint;
                placed = attachment.Local;
                Attached++;
            }
            if (view.Published && parent == view.Parent && placed == view.Placed) continue;
            upserts.Add(new(view.Id, parent != 0, parent, placed, view.Appearance, true, RenderLayer.Scene));
            // An upsert without its attachment returns a child to its parent's root, so it goes with every one.
            if (joint is not null) attachments.Add(new(view.Id, joint));
            published.Add((view, placed, parent));
        }

        if (upserts.Count > 0 || removals.Count > 0)
        {
            try
            {
                engine.Graphics.PublishChanges(new(upserts.ToArray(), removals.ToArray(), attachments.ToArray()));
            }
            catch (EngineCallException error)
            {
                // Nothing in a refused batch applied: the scene is as it was, so leaving views stay
                // published and leave with the next batch.
                LastError = error.Message;
                leaving.AddRange(retired);
                return;
            }
        }
        foreach (var (view, placed, parent) in published)
        {
            view.Placed = placed;
            view.Parent = parent;
            view.Published = true;
        }
        // Appearances leave the scene before they're disposed.
        foreach (var view in retired) Release(view);

        Sampled = Layered = LayersDrawn = 0;
        foreach (var (key, sample) in samples)
            if (sample.Animated && views[key].Published) Animate(views[key], sample);
    }

    /// <summary>Holds the entity's streamed sequence at its streamed cycle (the Engine's Sample playback).</summary>
    void Animate(View view, EntitySample sample)
    {
        if (!fight.Sequences.TryGetValue(view.Model.DonorPath, out var names) || sample.Sequence >= names.Length) return;
        string clip = names[sample.Sequence];
        if (missingClips.Contains($"{view.Model.DonorPath}:{clip}")) return;
        Sampled++;
        view.Instance ??= engine.Animation.CreateInstance(new(view.Appearance, view.Id));
        if (clip != view.Clip || sample.Cycle != view.Cycle)
        {
            try
            {
                engine.Animation.SetPlayback(new(view.Instance, AnimationPlaybackKind.Sample, clip, AnimationLoopMode.Once, 1, 1, false, 0, false, sample.Cycle));
                view.Clip = clip;
                view.Cycle = sample.Cycle;
            }
            catch (EngineCallException)
            {
                missingClips.Add($"{view.Model.DonorPath}:{clip}");
                return;
            }
        }
        Overlay(view, names, view.Track.LayersAt(sample.Shown));
    }

    /// <summary>Lays the entity's overlay layers over its main sequence; nothing is sent when they haven't changed.</summary>
    void Overlay(View view, string[] names, IReadOnlyList<LayerState> layers)
    {
        wanted.Clear();
        wantedNames.Clear();
        var clips = Clips(view.Model);
        foreach (var layer in (LayersOn ? layers : []).OrderBy(l => l.Order).ThenBy(l => l.Slot))
        {
            if (layer.Sequence < 0 || layer.Sequence >= names.Length || layer.Weight <= 0) continue;
            if (!clips.TryGetValue(names[layer.Sequence], out uint index)) { missingClips.Add($"{view.Model.DonorPath}:{names[layer.Sequence]}"); continue; }
            wanted.Add((index, Math.Clamp(layer.Cycle, 0, 1), Math.Min(layer.Weight, 1)));
            wantedNames.Add(names[layer.Sequence]);
        }
        if (wanted.Count > 0) { Layered++; LayersDrawn += wanted.Count; }
        if (wanted.SequenceEqual(view.Layers)) return;
        layerSet.Clear();
        foreach (var (clip, cycle, weight) in wanted) layerSet.Override(clip, cycle, weight);
        engine.Animation.SetLayers(layerSet.ToRequest(view.Instance!));
        view.Layers = [.. wanted];
        view.LayerNames = [.. wantedNames];
    }

    /// <summary>The overlay clips laid on the drawn entity at an edict index, in order; empty when none.</summary>
    internal string[] LayersOf(ushort index) => views.Values.FirstOrDefault(v => v.Track.Key.Index == index)?.LayerNames ?? [];

    /// <summary>
    /// The pose the drawn entity at an edict index shows now, layers included
    /// (its current playback), at the origin; null when it isn't animated.
    /// </summary>
    internal JointPose[]? PoseOf(ushort index)
    {
        var view = views.Values.FirstOrDefault(v => v.Track.Key.Index == index);
        if (view?.Instance is not AnimationInstance instance) return null;
        return engine.Animation.SampleJointPose(AnimationPoseSampleRequest.OfCurrentPlayback(instance, new(Vector3.Zero, Quaternion.Identity, Vector3.One), applyPoseControls: false)).ToArray();
    }

    /// <summary>A model's clips by name, as clip layers index them (its <c>ReadClips</c> order).</summary>
    Dictionary<string, uint> Clips(LevelModel model)
    {
        if (clipIndices.TryGetValue(model, out var map)) return map;
        map = new(StringComparer.Ordinal);
        var clips = engine.Animation.ReadClips(model.Mesh).Span;
        for (int i = 0; i < clips.Length; i++)
        {
            map.TryAdd(clips[i].Name, (uint)i);
            map.TryAdd(clips[i].Id, (uint)i);
        }
        return clipIndices[model] = map;
    }

    static void Release(View view)
    {
        view.Instance?.Dispose();
        view.Appearance.Dispose();
    }

    public void Dispose()
    {
        var ids = views.Values.Where(v => v.Published).Select(v => v.Id).ToList();
        if (worldPublished) ids.Add(WorldObject);
        if (ids.Count > 0) engine.Graphics.PublishChanges(new(Array.Empty<AppearanceFact>(), ids.ToArray(), Array.Empty<MeshJointAttachment>()));
        foreach (var view in views.Values) Release(view);
        views.Clear();
        world?.Dispose();
    }
}
