using System.Numerics;
using Rusty.Engine;

namespace DarkMessiah.Shared;

/// <summary>
/// One entity to sweep against, posed: its model, its joints by name (as the Engine samples them) and their poses in
/// the scene. A character answers by its hit boxes, an item by its collision model's convex pieces.
/// <paramref name="Holder"/> is the index in the same list of what holds it (a weapon's character), or -1.
/// </summary>
public sealed record PosedTarget(ModelInfo Model, IReadOnlyDictionary<string, int> Joints, JointPose[] Pose, int Holder = -1);

/// <summary>
/// What a sweep met: the target's index in the list given, the hit box (by its index in the model's default set) or
/// the item's hull, how far along the sweep in metres, and where.
/// </summary>
public readonly record struct HitContact(int Target, int Hitbox, bool Hull, float Along, Vector3 Point);

/// <summary>
/// The Engine's swept contact query over posed hit shapes, Dark Messiah's way (docs/combat.md, "Contacts"): a
/// character's hit boxes are parts of its entity on their joints, and an item's collision model's convex pieces are
/// static meshes of the session, uploaded once per model and moved to where each sweep finds them. The level isn't
/// part of it: the session holds only these hulls. Used by the mirror's Shadow contacts and the combat lab, so both
/// measure a blow the same way.
/// </summary>
public sealed class HitSweep(IEngineContext engine, SpatialSession session)
{
    readonly Dictionary<(ModelInfo, int), ulong> hullAssets = [];
    readonly HashSet<ulong> placed = [];
    ulong nextAsset = 1;

    /// <summary>
    /// Every target part a sphere of <paramref name="radius"/> meets moving from <paramref name="start"/> to
    /// <paramref name="end"/> (metres), in order along the way.
    /// </summary>
    public List<HitContact> Sweep(Vector3 start, Vector3 end, float radius, IReadOnlyList<PosedTarget> targets, uint maxContacts = 64)
    {
        var parts = new List<SpatialEntityPart>();
        var instances = new List<StaticMeshInstance>();
        var owners = new Dictionary<ulong, int>();
        var identity = new SpatialPose(Vector3.Zero, Quaternion.Identity);
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t.Model.Collision.Count > 0)
            {
                if (!t.Joints.TryGetValue(t.Model.Collision[0].Joint, out int root)) continue;
                for (int k = 0; k < t.Model.Collision.Count; k++)
                {
                    ulong id = ((ulong)i + 1) << 8 | (uint)k;
                    instances.Add(new StaticMeshInstance(id, Asset(t.Model, k), t.Pose[root].World));
                    owners[id] = i;
                }
                continue;
            }
            ulong holder = t.Holder >= 0 ? (ulong)t.Holder + 1 : 0;
            for (int k = 0; k < t.Model.Hitboxes.Count; k++)
            {
                var box = t.Model.Hitboxes[k];
                if (!t.Joints.TryGetValue(box.Joint, out int joint)) continue;
                parts.Add(SpatialEntityPart.OnJoint((ulong)i + 1, (uint)k, t.Pose[joint], SpatialShape.Box(box.HalfExtents, box.Center), identity, holder));
            }
        }
        Place(instances);
        var result = engine.Spatial.SweepContacts(SpatialContactQueryRequest.Sweep(session, SpatialShape.Sphere(radius, Vector3.Zero),
            new SpatialPose(start, Quaternion.Identity), new SpatialPose(end, Quaternion.Identity),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, parts.ToArray(), ReadOnlyMemory<ulong>.Empty, default, Vector3.Zero, false, maxContacts));
        float span = Vector3.Distance(start, end);
        var contacts = new List<HitContact>();
        foreach (var c in result.Contacts.Span)
        {
            if (c.Kind == SpatialHitKind.StaticMesh && owners.TryGetValue(c.Instance, out int item))
                contacts.Add(new HitContact(item, 0, true, (float)c.Fraction * span, c.Point));
            else if (c.Kind == SpatialHitKind.Entity && c.Entity >= 1 && c.Entity <= (ulong)targets.Count)
                contacts.Add(new HitContact((int)c.Entity - 1, (int)c.Part, false, (float)c.Fraction * span, c.Point));
        }
        return contacts;
    }

    /// <summary>A model's convex piece as a static mesh asset of the session, uploaded on first use.</summary>
    ulong Asset(ModelInfo model, int piece)
    {
        if (hullAssets.TryGetValue((model, piece), out ulong id)) return id;
        var hull = model.Collision[piece];
        id = nextAsset++;
        engine.Spatial.ApplyCollisionResidency(new CollisionResidencyRequest(session,
            new[] { new StaticMeshAsset(id, 0, (uint)hull.Points.Length, 0, (uint)hull.Triangles.Length) },
            hull.Points, hull.Triangles, ReadOnlyMemory<StaticMeshInstance>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<ulong>.Empty));
        return hullAssets[(model, piece)] = id;
    }

    /// <summary>Makes <paramref name="instances"/> the session's item hulls: moved, added, and the rest removed.</summary>
    void Place(List<StaticMeshInstance> instances)
    {
        var now = instances.Select(i => i.Id).ToHashSet();
        var gone = placed.Where(id => !now.Contains(id)).ToArray();
        if (instances.Count == 0 && gone.Length == 0) return;
        engine.Spatial.ApplyCollisionResidency(new CollisionResidencyRequest(session, ReadOnlyMemory<StaticMeshAsset>.Empty,
            ReadOnlyMemory<Vector3>.Empty, ReadOnlyMemory<Triangle>.Empty, instances.ToArray(), ReadOnlyMemory<ulong>.Empty, gone));
        placed.Clear();
        placed.UnionWith(now);
    }

    /// <summary>How far a point lies outside a hit box on a joint placed at <paramref name="joint"/>; 0 inside. Metres.</summary>
    public static float Outside(Vector3 point, Transform joint, ModelHitbox box)
    {
        var local = Vector3.Transform(point - joint.Translation, Quaternion.Inverse(joint.Rotation)) - box.Center;
        return Vector3.Max(Vector3.Abs(local) - box.HalfExtents, Vector3.Zero).Length();
    }

    /// <summary>How far a point lies outside a convex hull on a joint placed at <paramref name="world"/>: its furthest distance in front of a face; 0 inside. Metres.</summary>
    public static float OutsideHull(Vector3 point, Transform world, ConvexHull hull)
    {
        var local = Vector3.Transform(point - world.Translation, Quaternion.Inverse(world.Rotation));
        var used = hull.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Select(i => hull.Points[i]).ToList();
        var centre = used.Aggregate(Vector3.Zero, (a, b) => a + b) / used.Count;
        float outside = 0;
        foreach (var t in hull.Triangles)
        {
            Vector3 a = hull.Points[t.A], b = hull.Points[t.B], c = hull.Points[t.C];
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, centre - a) > 0) n = -n;
            outside = MathF.Max(outside, Vector3.Dot(n, local - a));
        }
        return outside;
    }

    /// <summary>A model's joints by name, in the order the Engine samples them.</summary>
    public static Dictionary<string, int> Joints(IEngineContext engine, RenderResource mesh)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var joints = engine.Animation.ReadJoints(mesh).Span;
        for (int i = 0; i < joints.Length; i++) map.TryAdd(joints[i].Id, i);
        return map;
    }
}
