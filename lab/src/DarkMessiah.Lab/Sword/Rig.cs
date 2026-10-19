using System.Numerics;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Shared;
using Rusty.Engine;

namespace DarkMessiah.Lab.Sword;

/// <summary>Placement helpers for imported Dark Messiah models, which face +Z in glTF space (Source's +X).</summary>
internal static class Rig
{
    /// <summary>The rotation that turns a model's +Z to <paramref name="forward"/> and its +Y toward <paramref name="up"/>.</summary>
    internal static Quaternion Facing(Vector3 forward, Vector3 up)
    {
        // CreateWorld puts -forward on its Z row; passing -forward puts forward there, so the model's +Z follows it.
        var world = Matrix4x4.CreateWorld(Vector3.Zero, -Vector3.Normalize(forward), up);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
    }

    /// <summary>A transform given in a parent's frame, placed in the scene.</summary>
    internal static Transform Compose(Transform parent, Transform local) =>
        new(parent.Translation + Vector3.Transform(local.Translation * parent.Scale, parent.Rotation),
            Quaternion.Normalize(parent.Rotation * local.Rotation), parent.Scale * local.Scale);

    /// <summary>Where an item hangs: the holder's attachment joint as posed, then the attachment's local transform.</summary>
    internal static Transform? Attached(DonorModel holder, JointPose[] pose, string attachment) =>
        holder.Info.Attachments.TryGetValue(attachment, out var a) && holder.Joints.TryGetValue(a.Joint, out int j)
            ? Compose(pose[j].World, a.Local) : null;

    /// <summary>
    /// The far end of a one-bone weapon's blade in its root joint's frame: the end of its hit box's longest axis further
    /// from the joint (the grip).
    /// </summary>
    internal static (string Joint, Vector3 Tip) BladeTip(ModelInfo weapon)
    {
        var box = weapon.Hitboxes[0];
        Vector3 h = box.HalfExtents, c = box.Center;
        Vector3 axis = h.X >= h.Y && h.X >= h.Z ? Vector3.UnitX : h.Y >= h.Z ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 a = c + axis * h, b = c - axis * h;
        return (box.Joint, a.Length() >= b.Length() ? a : b);
    }

    /// <summary>
    /// A puppet's joints posed now, placed at <paramref name="placement"/>: its current playback, layers included, once
    /// it has been drawn (the Engine samples playback only for instances in the published scene); before that, its
    /// main clip alone.
    /// </summary>
    internal static JointPose[] Pose(IEngineContext engine, Puppet puppet, Transform placement) =>
        engine.Animation.SampleJointPose(puppet.Drawn
            ? AnimationPoseSampleRequest.OfCurrentPlayback(puppet.Instance, placement, applyPoseControls: false)
            : AnimationPoseSampleRequest.OfClip(puppet.Instance, puppet.Playing.Clip, Math.Clamp(puppet.Playing.Cycle, 0, 1), placement, normalized: true, applyPoseControls: false)).ToArray();

    /// <summary>A model at rest, placed: an item's joints, which no clip moves.</summary>
    internal static JointPose[] Rest(IEngineContext engine, AnimationInstance instance, Transform placement) =>
        engine.Animation.SampleJointPose(AnimationPoseSampleRequest.OfClip(instance, "", 0, placement, normalized: true, applyPoseControls: false)).ToArray();
}

/// <summary>An imported model drawn by the lab: its appearance and animation instance under one object id.</summary>
internal sealed class Puppet : IDisposable
{
    readonly IEngineContext engine;
    readonly AnimationLayerSet layers = new();
    (string Clip, float Cycle) playing = ("", -1);
    (uint Clip, float Cycle)[] laid = [];

    internal Puppet(IEngineContext engine, DonorModel model, ulong id)
    {
        this.engine = engine;
        Model = model;
        Id = id;
        Appearance = engine.Animation.CreateAnimatedMeshAppearance(new(model.Mesh));
        Instance = engine.Animation.CreateInstance(new(Appearance, id));
    }

    internal DonorModel Model { get; }
    internal ulong Id { get; }
    internal Appearance Appearance { get; }
    internal AnimationInstance Instance { get; }
    /// <summary>The main clip and cycle it holds.</summary>
    internal (string Clip, float Cycle) Playing => playing;
    /// <summary>Whether it has been handed to a publish: from then on its current playback can be sampled.</summary>
    internal bool Drawn { get; private set; }

    /// <summary>Holds <paramref name="clip"/> at <paramref name="cycle"/> (the Engine's Sample playback), with optional override layers over it.</summary>
    internal void Play(string clip, float cycle, params (string Clip, float Cycle, float Weight)[] over)
    {
        if (clip.Length > 0 && (clip, cycle) != playing)
        {
            engine.Animation.SetPlayback(new(Instance, AnimationPlaybackKind.Sample, clip, AnimationLoopMode.Once, 1, 1, false, 0, false, Math.Clamp(cycle, 0, 1)));
            playing = (clip, cycle);
        }
        var wanted = over.Where(o => o.Weight > 0 && Model.ClipIndices.ContainsKey(o.Clip))
            .Select(o => (Model.ClipIndices[o.Clip], Math.Clamp(o.Cycle, 0, 1))).ToArray();
        if (wanted.SequenceEqual(laid)) return;
        layers.Clear();
        foreach (var (index, c) in wanted) layers.Override(index, c, 1);
        engine.Animation.SetLayers(layers.ToRequest(Instance));
        laid = wanted;
    }

    internal AppearanceFact Fact(Transform placement, bool visible = true)
    {
        Drawn = true;
        return new(Id, false, 0, placement, Appearance, visible, RenderLayer.Scene);
    }

    public void Dispose()
    {
        Instance.Dispose();
        Appearance.Dispose();
    }
}
