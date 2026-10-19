using System.Numerics;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Mirror.Replay;

/// <summary>An entity's identity on the Wire: an edict index reused by a new entity gets a new serial.</summary>
internal readonly record struct EntityKey(ushort Index, uint Serial)
{
    public override string ToString() => $"#{Index}.{Serial}";
}

/// <summary>A state the entity changed to, at a donor frame.</summary>
internal readonly record struct EntityUpdate(int Frame, double Time, WireEntity State);

/// <summary>
/// Where an entity is drawn at a moment: Source space, ready for <see cref="SourceSpace"/>.
/// <paramref name="Shown"/> is the donor time the drawn state stands for: the entity is drawn
/// one of its own update intervals behind, so its overlay layers are read at that time.
/// </summary>
internal readonly record struct EntitySample(Vector3 Origin, Quaternion Rotation, int ModelIndex, short Sequence, float Cycle, bool Animated, double Shown = 0)
{
    /// <summary>v10: what the entity hangs from, as of its last update; null when nothing or before v10.</summary>
    public EntityKey? Parent { get; init; }
    /// <summary>v10: the parent model's attachment, numbered from 1; 0 for none.</summary>
    public byte ParentAttachment { get; init; }
}

/// <summary>One active overlay layer (Wire v6): a sequence playing over the main one at its own cycle and weight.</summary>
internal readonly record struct LayerState(byte Slot, short Sequence, short Order, float Cycle, float Weight);

/// <summary>
/// One entity's life in a recording, kept as the states it changed to. The
/// donor updates each entity at its own cadence (NPCs every 8 ticks, the
/// player every 2, props as they move), so a track is sampled across its own
/// last two updates, not across ticks.
/// </summary>
internal sealed class EntityTrack(EntityKey key, string className, int firstFrame)
{
    /// <summary>
    /// The longest a step between two updates is spread over. An entity that
    /// sat still for ten seconds and then moved is drawn moving over this, not
    /// over the ten seconds.
    /// </summary>
    public const double MaxSpanSeconds = 0.25;

    readonly List<EntityUpdate> updates = [];
    readonly List<(double Time, LayerState[] Layers)> layers = [];

    public EntityKey Key { get; } = key;
    public string ClassName { get; } = className;
    public int FirstFrame { get; } = firstFrame;
    public int LastFrame { get; private set; } = int.MaxValue;
    public IReadOnlyList<EntityUpdate> Updates => updates;

    public bool LiveAt(int frame) => frame >= FirstFrame && frame <= LastFrame;

    internal void Observe(int frame, double time, in WireEntity state)
    {
        if (updates.Count > 0 && Same(updates[^1].State, state)) return;
        updates.Add(new EntityUpdate(frame, time, state));
    }

    internal void End(int frame) => LastFrame = frame;

    /// <summary>The entity's layers from donor time <paramref name="time"/> on; kept only when they change.</summary>
    internal void ObserveLayers(double time, LayerState[] state)
    {
        if (layers.Count == 0 ? state.Length == 0 : layers[^1].Layers.AsSpan().SequenceEqual(state)) return;
        layers.Add((time, state));
    }

    /// <summary>The overlay layers playing at donor time <paramref name="time"/>, in no particular order; empty before v6.</summary>
    public IReadOnlyList<LayerState> LayersAt(double time)
    {
        int lo = 0, hi = layers.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (layers[mid].Time <= time) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found < 0 ? [] : layers[found].Layers;
    }

    static bool Same(in WireEntity a, in WireEntity b) =>
        a.OriginX == b.OriginX && a.OriginY == b.OriginY && a.OriginZ == b.OriginZ
        && a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Roll == b.Roll
        && a.ModelIndex == b.ModelIndex && a.Sequence == b.Sequence && a.Cycle == b.Cycle && a.Flags == b.Flags
        && a.Parent == b.Parent && a.ParentSerial == b.ParentSerial && a.ParentAttachment == b.ParentAttachment;

    /// <summary>The latest update at or before <paramref name="time"/>, or -1.</summary>
    public int UpdateAt(double time)
    {
        int lo = 0, hi = updates.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (updates[mid].Time <= time) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    /// <summary>
    /// The entity at donor time <paramref name="time"/>, moving from its
    /// second-to-last update to its last one over the gap between them. That
    /// uses only updates already received, as a live Wire allows, and draws
    /// each entity one of its own update intervals behind the donor.
    /// </summary>
    public EntitySample Sample(double time)
    {
        int k = Math.Max(0, UpdateAt(time));
        var b = updates[k];
        var a = k > 0 ? updates[k - 1] : b;
        double span = Math.Min(b.Time - a.Time, MaxSpanSeconds);
        float t = span > 0 ? (float)Math.Clamp((time - b.Time) / span, 0, 1) : 1;
        return Blend(a.State, b.State, t) with { Shown = time - span };
    }

    /// <summary>
    /// The entity as the donor had it at donor time <paramref name="time"/>:
    /// its latest update at or before then, unblended. A server-side test (a
    /// trace against hit boxes) sees this state, not the drawn one.
    /// </summary>
    public EntitySample At(double time)
    {
        var b = updates[Math.Max(0, UpdateAt(time))];
        return Blend(b.State, b.State, 1) with { Shown = time };
    }

    internal static EntitySample Blend(in WireEntity a, in WireEntity b, float t)
    {
        var origin = Vector3.Lerp(new(a.OriginX, a.OriginY, a.OriginZ), new(b.OriginX, b.OriginY, b.OriginZ), t);
        var rotation = Quaternion.Slerp(SourceSpace.FromAngles(a.Pitch, a.Yaw, a.Roll), SourceSpace.FromAngles(b.Pitch, b.Yaw, b.Roll), t);
        bool animated = (b.Flags & WireEntityFlags.HasAnimation) != 0 && b.Sequence >= 0;
        float cycle;
        if (a.Sequence == b.Sequence)
        {
            // The cycle runs forward; a smaller one means it wrapped (a looping clip) in between.
            float to = b.Cycle < a.Cycle ? b.Cycle + 1 : b.Cycle;
            cycle = a.Cycle + (to - a.Cycle) * t;
            if (cycle >= 1) cycle -= 1;
        }
        else
        {
            // A new sequence started somewhere between the updates; take it as starting at the first.
            cycle = b.Cycle * t;
        }
        return new EntitySample(origin, rotation, b.ModelIndex, b.Sequence, Math.Clamp(cycle, 0, 1), animated)
        {
            Parent = b.Parent != WireLayout.NoEntity && b.Parent != 0 ? new EntityKey(b.Parent, b.ParentSerial) : null,
            ParentAttachment = b.ParentAttachment,
        };
    }
}
