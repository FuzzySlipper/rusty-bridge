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
    }

    readonly IEngineContext engine;
    readonly RecordedFight fight;
    readonly LevelAssets assets;
    readonly HeldItems held;
    readonly Dictionary<EntityKey, View> views = [];
    readonly HashSet<string> missingClips = new(StringComparer.Ordinal);
    readonly List<View> leaving = [];
    readonly Appearance? world;
    bool worldPublished;
    ulong nextId = WorldObject + 1;

    public int Drawn => views.Count;
    public int Attached { get; private set; }
    public int Sampled { get; private set; }
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
            if (DonorClasses.Role(view.Track.ClassName) == EntityRole.Held
                && held.Holder(view.Track, frame, time) is EntityTrack holder
                && views.TryGetValue(holder.Key, out var holderView) && holderView.Track == holder && standing.Contains(holderView.Id)
                && holderView.Model.Attachments.TryGetValue(DonorClasses.Attachment(view.Track.ClassName), out var attachment))
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

        Sampled = 0;
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
        if (clip == view.Clip && sample.Cycle == view.Cycle) return;
        view.Instance ??= engine.Animation.CreateInstance(new(view.Appearance, view.Id));
        try
        {
            engine.Animation.SetPlayback(new(view.Instance, AnimationPlaybackKind.Sample, clip, AnimationLoopMode.Once, 1, 1, false, 0, false, sample.Cycle));
            view.Clip = clip;
            view.Cycle = sample.Cycle;
        }
        catch (EngineCallException)
        {
            missingClips.Add($"{view.Model.DonorPath}:{clip}");
        }
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
