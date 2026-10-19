using System.Numerics;
using Rusty.Engine;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// Where a body can stand when it is found deep inside collision, or asked to stand somewhere it does not fit. The
/// Engine controller pushes a body out of shallow overlaps itself (<see cref="CharacterRecoveryConfig"/>); deeper than
/// that it refuses the step with <see cref="PenetrationCode"/>, and a developer placement into a wall would do the
/// same on the next step. Both stand the body at the first nearby place an Engine capsule overlap finds clear.
/// Provenance: rusty-craftsurvive <c>PlayerRecovery</c> (refusal and the straight-up search) and rusty-hotel
/// <c>HotelDeveloper.ClearNear</c> (the ring search around a requested point), joined on one capsule query.
/// </summary>
internal static class PlayerRecovery
{
    /// <summary>The Engine's diagnostic for a step refused because the body is too deep in collision.</summary>
    internal const string PenetrationCode = "unresolved-character-controller-penetration";

    // How far apart candidate places are, and how far up or around they are tried, in metres.
    private const float SearchStep = .25f, SearchHeight = 3, SearchRadius = 2;

    internal static bool IsPenetration(EngineCallException refusal)
    {
        foreach (EngineDiagnostic diagnostic in refusal.Diagnostics.Span)
            if (diagnostic.Code == PenetrationCode) return true;
        return false;
    }

    /// <summary>After a refused step: where the body last stood clear, then straight above here, then above that.</summary>
    internal static IEnumerable<Vector3> Above(Vector3 current, Vector3 lastClear)
    {
        yield return lastClear;
        int steps = (int)MathF.Round(SearchHeight / SearchStep);
        for (int i = 1; i <= steps; i++) yield return current + Vector3.UnitY * (i * SearchStep);
        for (int i = 1; i <= steps; i++) yield return lastClear + Vector3.UnitY * (i * SearchStep);
    }

    /// <summary>For a placement: the asked-for centre, then rings around it at the same height.</summary>
    internal static IEnumerable<Vector3> Around(Vector3 centre)
    {
        yield return centre;
        for (float r = SearchStep; r <= SearchRadius + 1e-3f; r += SearchStep)
        {
            int count = Math.Max(8, (int)MathF.Ceiling(2 * MathF.PI * r / SearchStep));
            for (int i = 0; i < count; i++)
            {
                float a = i * 2 * MathF.PI / count;
                yield return centre + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r;
            }
        }
    }

    /// <summary>The first candidate centre where a capsule of the body's size overlaps nothing, if any.</summary>
    internal static Vector3? FirstClear(ISpatialService spatial, SpatialSession session, IEnumerable<Vector3> candidates,
        float height, CharacterShapeConfig shape, ReadOnlyMemory<SpatialEntityCollider> bodies, ulong self)
    {
        double halfSegment = Math.Max(0, height / 2 - shape.Radius);
        foreach (Vector3 centre in candidates)
        {
            SpatialHit hit = spatial.OverlapCapsule(new SpatialCapsuleQueryRequest(session, centre, halfSegment, shape.Radius,
                Vector3.Zero, shape.ContactSkin, new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), bodies, new[] { self }));
            if (!hit.Present) return centre;
        }
        return null;
    }
}
