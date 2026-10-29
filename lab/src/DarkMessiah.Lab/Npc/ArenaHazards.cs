using System.Numerics;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>A spike rack (#10157): its box, and the way its spikes point.</summary>
internal sealed record SpikeRack(string Label, Vector3 Min, Vector3 Max, Vector3 Facing);

/// <summary>A fire on the ground (#10157): its middle, at the ground, and how far from it an orc burns.</summary>
internal sealed record FirePatch(string Label, Vector3 Center, float Radius);

/// <summary>What a pushed orc's step met first (#10157).</summary>
internal enum HazardKind { Wall, Spikes, FireEdge }

/// <summary>
/// The arena's hazards as an orc meets them (#10157; docs/lab.md, "Kicks into hazards"), by the donor's rules as
/// server.dll and the recordings have them:
/// <list type="bullet">
/// <item><b>Spikes</b> (<c>prop_trap</c>, server.dll's <c>CTrapProp::TrapTouch</c>, +0x35FF40): an NPC that touches a
/// live trap, with a clear trace to it, takes <see cref="SpikeDamage"/> slash damage and dies impaled, whatever it
/// was doing.</item>
/// <item><b>Fire</b> (<c>env_fire</c>): within <see cref="FirePatch.Radius"/> it burns, <see cref="FireDamage"/> every
/// <see cref="FireEvery"/> s; on fire it plays <c>onfire</c> for <see cref="OnFireSeconds"/> as soon as it can act and
/// dies, whatever its health (l01_a-v21-kick-fire: 991 and 997 of 1000 died as 26 of 35 did). The fire's edge stops an
/// NPC as a wall does (its controller box).</item>
/// <item><b>Something in a push's way</b> (a wall, a fire's edge): the NPC is thrown down, a ragdoll, server.dll's
/// <c>mm_ai_force_fall_wall</c> across and <c>mm_ai_force_fall_wallz</c> up (5000 and 2000; the lab keeps its
/// knockdown's impulse and their ratio, <see cref="WallFallUp"/>).</item>
/// <item><b>Falls</b>: a drop under <see cref="LethalDrop"/> it walks or slides off and lands on its feet; a deeper one it
/// falls as a ragdoll and dies where it lands (l01_a-v21-npc-fall: 165 units unhurt, 311, 511 and 811 dead).</item>
/// </list>
/// </summary>
internal sealed class ArenaHazards(IReadOnlyList<SpikeRack> spikes, IReadOnlyList<FirePatch> fires, Func<Vector3, Vector3, float, float?> ray)
{
    /// <summary>A trap's damage: 10000 (<c>push 0x461C3C00</c> at server.dll+0x3601C7), slash.</summary>
    internal const float SpikeDamage = 10000;
    /// <summary>An <c>env_fire</c>'s burn on an NPC within it: 3 every 72 ticks (1.1 s) in the recordings, damage type 8.</summary>
    internal const float FireDamage = 3, FireEvery = 1.1f;
    /// <summary>How long an NPC on fire plays <c>onfire</c> before it dies: 128 ticks, every time (1.92 s; the clip runs 2.08).</summary>
    internal const float OnFireSeconds = 1.92f;
    /// <summary>
    /// The drop, Source units, from which an NPC falls as a ragdoll and dies: between the recorded 165 (it landed) and
    /// 311 (it fell and died); the lab takes their middle.
    /// </summary>
    internal const float LethalDrop = 238;
    /// <summary>How much of a wall fall's push goes up: <c>mm_ai_force_fall_wallz</c> over <c>mm_ai_force_fall_wall</c>.</summary>
    internal const float WallFallUp = 2000f / 5000;
    /// <summary>A step down it takes in its stride, metres: anything deeper it drops.</summary>
    internal const float StepDown = .3f;
    /// <summary>How far round its feet an orc's body reaches, metres (its hull's half width).</summary>
    internal const float Radius = .4f;

    internal IReadOnlyList<SpikeRack> Spikes => spikes;
    internal IReadOnlyList<FirePatch> Fires => fires;

    /// <summary>
    /// What a step of its feet from <paramref name="from"/> to <paramref name="to"/> meets first: a spike rack it touches,
    /// a fire it would enter (its edge, from outside), or the arena's walls at its knee or chest; null when clear.
    /// </summary>
    internal HazardKind? Ahead(Vector3 from, Vector3 to)
    {
        var step = (to - from) with { Y = 0 };
        float length = step.Length();
        if (length < 1e-5f) return null;
        if (Touching(to) is not null) return HazardKind.Spikes;
        foreach (var fire in fires)
            if (Flat(from - fire.Center) > fire.Radius + Radius && Flat(to - fire.Center) <= fire.Radius + Radius) return HazardKind.FireEdge;
        var direction = step / length;
        foreach (float height in (ReadOnlySpan<float>)[.5f, 1.2f])
            if (ray(from + Vector3.UnitY * height, direction, length + Radius) is not null) return HazardKind.Wall;
        return null;
    }

    /// <summary>The spike rack its body touches at <paramref name="feet"/>, if any.</summary>
    internal SpikeRack? Touching(Vector3 feet)
    {
        foreach (var rack in spikes)
            if (feet.X > rack.Min.X - Radius && feet.X < rack.Max.X + Radius && feet.Z > rack.Min.Z - Radius && feet.Z < rack.Max.Z + Radius
                && feet.Y < rack.Max.Y && feet.Y + 1.8f > rack.Min.Y)
                return rack;
        return null;
    }

    /// <summary>The fire it stands in at <paramref name="feet"/>, if any.</summary>
    internal FirePatch? FireAt(Vector3 feet) =>
        fires.FirstOrDefault(fire => Flat(feet - fire.Center) <= fire.Radius + Radius && MathF.Abs(feet.Y - fire.Center.Y) < 1);

    /// <summary>How far below <paramref name="feet"/> the ground is, metres; null when there's none within 50 m.</summary>
    internal float? Drop(Vector3 feet) => ray(feet + Vector3.UnitY * StepDown, -Vector3.UnitY, 50) is float d ? d - StepDown : null;

    /// <summary>Whether a drop of <paramref name="metres"/> kills: <see cref="LethalDrop"/>.</summary>
    internal static bool Lethal(float? metres) => metres is not float m || m / SourceSpace.MetresPerUnit >= LethalDrop;

    static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
