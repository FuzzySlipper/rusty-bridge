using System.Numerics;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using Rusty.Engine;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The knocked-down orc as an Engine ragdoll (#10216; docs/lab.md, "Stuns and knockdowns"), where the donor drops a
/// <c>prop_ragdoll</c> and its NPC waits on the get-up's first frame. Eleven bodies over the orc's skeleton, each from
/// a joint to the next (the skeleton's joint axes don't follow its bones, so every body spans joint to end joint):
/// the pelvis and chest as boxes, the head and limbs as capsules, sized to the orc's hit boxes; cone joints, each
/// twisting about its bone. It spawns at the pose the orc was drawn in, moving as it moved, pushed along the blow.
/// </summary>
internal sealed class OrcRagdoll : IDisposable
{
    sealed record Bone(string Joint, string End, DynamicsRagdollShape Shape, float Radius, float HalfDepth, float Mass);
    sealed record Link(int Parent, int Child, float Swing, float Twist);

    static readonly Bone[] Bones =
    [
        new("SK_Root", "SK_Spine_2", DynamicsRagdollShape.Box, .19f, .14f, 14),
        new("SK_Spine_2", "SK_Neck", DynamicsRagdollShape.Box, .23f, .16f, 26),
        new("SK_Neck", "SK_Head", DynamicsRagdollShape.Capsule, .14f, 0, 6),
        new("SK_L_Shoulder", "SK_L_Elbow", DynamicsRagdollShape.Capsule, .08f, 0, 3),
        new("SK_L_Elbow", "SK_L_Wrist", DynamicsRagdollShape.Capsule, .07f, 0, 2.5f),
        new("SK_R_Shoulder", "SK_R_Elbow", DynamicsRagdollShape.Capsule, .08f, 0, 3),
        new("SK_R_Elbow", "SK_R_Wrist", DynamicsRagdollShape.Capsule, .07f, 0, 2.5f),
        new("SK_L_Leg", "SK_L_Knee", DynamicsRagdollShape.Capsule, .11f, 0, 9),
        new("SK_L_Knee", "SK_L_Ankle", DynamicsRagdollShape.Capsule, .09f, 0, 5),
        new("SK_R_Leg", "SK_R_Knee", DynamicsRagdollShape.Capsule, .11f, 0, 9),
        new("SK_R_Knee", "SK_R_Ankle", DynamicsRagdollShape.Capsule, .09f, 0, 5),
    ];

    static readonly Link[] Links =
    [
        new(0, 1, .5f, .3f), new(1, 2, .6f, .6f),
        new(1, 3, 1.4f, .8f), new(3, 4, 1.3f, .5f), new(1, 5, 1.4f, .8f), new(5, 6, 1.3f, .5f),
        new(0, 7, 1.2f, .4f), new(7, 8, 1.3f, .3f), new(0, 9, 1.2f, .4f), new(9, 10, 1.3f, .3f),
    ];

    const float JointDamping = 2;
    const uint Group = 1 << 8;
    internal const int Pelvis = 0, Chest = 1, Head = 2;

    /// <summary>
    /// How hard a felling blow pushes the chest, N·s: the Engine fixture's fall (110 on an 80 kg body), with a little lift.
    /// The bodies here weigh 85 kg.
    /// </summary>
    internal const float Impulse = 120;
    /// <summary>The share of <see cref="Impulse"/> a felling blow lifts the chest by.</summary>
    internal const float Rise = .15f;

    readonly IEngineContext engine;
    readonly DynamicsRagdoll ragdoll;
    // The chest's front in its own frame at the spawn: the way the orc faced.
    readonly Vector3 chestFront;

    OrcRagdoll(IEngineContext engine, DynamicsRagdoll ragdoll, Vector3 chestFront)
    {
        this.engine = engine;
        this.ragdoll = ragdoll;
        this.chestFront = chestFront;
    }

    internal float Blend { get; private set; } = 1;

    /// <summary>
    /// A ragdoll of <paramref name="body"/>'s drawn pose in <paramref name="world"/>, the orc facing
    /// <paramref name="facing"/>, pushed along <paramref name="push"/> (horizontal, from the blow). Null until the
    /// body's pose has been drawn and reported.
    /// </summary>
    internal static OrcRagdoll? Spawn(IEngineContext engine, DynamicsWorld world, Puppet body, Vector3 facing, Vector3 push, float up = Rise)
    {
        var drawn = engine.Animation.ReadJointPose(body.Instance);
        if (!drawn.Reported) return null;
        var joints = body.Model.Joints;
        uint Index(string name) => (uint)joints[name];
        var pose = drawn.Joints.ToArray();
        var bones = Bones.Select(b => new DynamicsRagdollBone(Index(b.Joint), Index(b.End), 0, b.Shape, b.Radius, b.HalfDepth, b.Mass)).ToArray();
        var links = Links.Select(l =>
        {
            // The twist axis: the child's bone, in the child joint's frame as drawn.
            var child = Bones[l.Child];
            var at = pose[joints[child.Joint]].World;
            var along = Vector3.Normalize(pose[joints[child.End]].World.Translation - at.Translation);
            var axis = Vector3.Normalize(Vector3.Transform(along, Quaternion.Inverse(at.Rotation)));
            return new DynamicsRagdollLink((uint)l.Parent, (uint)l.Child, axis, DynamicsJointLimits.Cone(l.Swing, -l.Twist, l.Twist, JointDamping));
        }).ToArray();
        var created = engine.Dynamics.CreateRagdoll(new DynamicsRagdollRequest(world, body.Instance, bones, links, Group, uint.MaxValue, .8f, 0, .05f, .3f, 1));
        var spawned = engine.Dynamics.ReadRagdoll(created).Bones.Span;
        var chest = spawned[Chest];
        var front = Vector3.Normalize(Vector3.Transform(facing with { Y = 0 }, Quaternion.Inverse(chest.Rotation)));
        if (push.LengthSquared() > 1e-6f)
        {
            // At the chest, a little above its joint (where a swing lands), so the push turns the body as well.
            var impulse = Vector3.Normalize(push with { Y = 0 }) * Impulse + Vector3.UnitY * Impulse * up;
            engine.Dynamics.ApplyRagdollImpulse(new DynamicsRagdollImpulseRequest(created, Chest, chest.Translation + Vector3.UnitY * .3f, impulse));
        }
        return new OrcRagdoll(engine, created, front);
    }

    /// <summary>Each body's joint placement now, and whether every body sleeps.</summary>
    internal (Transform[] Bones, bool Resting) Read()
    {
        var read = engine.Dynamics.ReadRagdoll(ragdoll);
        return (read.Bones.ToArray(), read.Resting);
    }

    /// <summary>
    /// The side it lies on: on its back when its chest's front (the way it faced as it fell) points up, else on its
    /// front; where its pelvis lies; and the way its get-up should face, from its head to its pelvis (the get-up's
    /// first frame lies with the head behind the root).
    /// </summary>
    internal (string Side, Vector3 Pelvis, Vector3 Facing) Lie()
    {
        var bones = Read().Bones;
        var front = Vector3.Transform(chestFront, bones[Chest].Rotation);
        var pelvis = bones[Pelvis].Translation;
        var toPelvis = (pelvis - bones[Head].Translation) with { Y = 0 };
        return (front.Y >= 0 ? "back" : "front", pelvis, toPelvis.LengthSquared() > 1e-6f ? Vector3.Normalize(toPelvis) : Vector3.UnitZ);
    }

    /// <summary>Its chest's front now, its head's and pelvis's heights: a developer readout.</summary>
    internal string Describe()
    {
        var bones = Read().Bones;
        var front = Vector3.Transform(chestFront, bones[Chest].Rotation);
        return FormattableString.Invariant($"chest front {front.X:0.00} {front.Y:0.00} {front.Z:0.00}, head {bones[Head].Translation.Y:0.00}, pelvis {bones[Pelvis].Translation.Y:0.00}, chest {bones[Chest].Translation.Y:0.00}");
    }

    /// <summary>Mixes the bodies over the clips: 1 all ragdoll, 0 all clips.</summary>
    internal void SetBlend(float blend)
    {
        Blend = Math.Clamp(blend, 0, 1);
        engine.Dynamics.SetRagdollBlend(new DynamicsRagdollBlendRequest(ragdoll, Blend));
    }

    public void Dispose() => ragdoll.Dispose();
}
