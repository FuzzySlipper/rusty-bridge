using System.Numerics;
using DarkMessiah.Mirror.Replay;
using DarkMessiah.Mirror.Scene;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Mirror.Shadow;

/// <summary>
/// What a swing's trace met: an entity by its recording identity
/// (<c>#index.serial</c>) and class, whether that is the victim's own body
/// (otherwise an item, such as its weapon or shield, or another character), and
/// the hit box on it, by its index in the model's default set and its hit group.
/// <paramref name="Along"/> is how far along the trace, in Source units.
/// </summary>
public sealed record SwingTouch(string Entity, string Class, bool Body, int Hitbox, int Group, float Along);

/// <summary>
/// One melee blow's contact: what the donor's trace reported, and what
/// sweeping the same trace through the posed shapes meets.
/// </summary>
/// <param name="Before">The first contact, posed as the frame before the blow had everyone: the state the donor's trace was tested against, before the victim reacted. Null when the sweep met nothing.</param>
/// <param name="After">The same, posed as the blow's own frame.</param>
/// <param name="DonorAlong">Where along the trace the sweep, posed before, meets the part the donor reported; null when it never does.</param>
/// <param name="Miss">How far, in Source units, the donor's hit point (its trace's end) lies outside that part as posed before; 0 inside, NaN when it isn't posed.</param>
/// <param name="Stale">Ticks between the victim's last streamed update and the frame before the blow.</param>
/// <param name="Traced">
/// Whether the donor's trace met a shape. A blow on the player, or one that
/// names hit box 0 in hit group 0 on an NPC whose hit box 0 is in another
/// group, wasn't: its trace ends at a reach point (<paramref name="Reach"/>
/// units from the victim's axis), not on a surface.
/// </param>
public sealed record SwingContact(int Tick, string Attacker, string Victim, string Attack, SwingTouch Donor, SwingTouch? Before, SwingTouch? After,
    float? DonorAlong, float Miss, int Stale, float Length, bool Traced, float Reach)
{
    /// <summary>The victim's streamed sequence at the frame before the blow.</summary>
    public string VictimSequence { get; init; } = "";
}

/// <summary>
/// Recomputes what each recorded melee swing struck (Shadow mode, design
/// "Phase 2: Combat decisions"; docs/combat.md, "Contacts"). The donor
/// reports a trace with each damage record, from <c>m_vecTraceStartPos</c> to
/// the point it struck (<c>m_vecTraceEndPos</c>), with the entity and hit box
/// it met. Here every character and held item near the trace is posed at the
/// blow through the mirror's views, as the donor had it (its latest streamed
/// state, <see cref="EntityViews.Exact"/>):
/// <list type="bullet">
/// <item>a character's hit boxes (its model's default set) become parts of its entity for the Engine's contact queries;</item>
/// <item>an item (weapon, shield) stands where the Wire streamed it, and its collision model's convex pieces become static meshes of the session.</item>
/// </list>
/// The trace is then swept as a capsule (a sphere of <see cref="Radius"/>
/// moving along it) a little past its end, and the first contact is what it
/// struck. The attacker and what it holds are left out, as the donor's trace
/// skips them, and so is the level. Entities are named by their recording
/// identity, so the contact is compared with the donor's struck entity itself.
/// </summary>
public static class SwingContacts
{
    /// <summary>
    /// The swept sphere's radius, in Source units. Arkane's melee trace is a
    /// ray: its end is the struck point, on the surface. A 1-unit sphere already
    /// meets thighs that a ray toward the spine passes within an inch of.
    /// </summary>
    public const float Radius = 0.05f;
    /// <summary>How far past the donor's struck point the sweep runs, in Source units, to reach the surface the trace ended on.</summary>
    public const float Overshoot = 4;
    /// <summary>Entities standing further than this from the trace, in Source units, are left out.</summary>
    const float Reach = 120;

    /// <summary>Source DMG_* bits that mark a melee blow: slash and club.</summary>
    const uint MeleeBits = 0x4 | 0x80;

    static bool IsCharacter(string cls) => cls == "player" || cls.StartsWith("npc_", StringComparison.Ordinal);

    /// <summary>
    /// Every melee blow of a recording with a trace, scored contact by contact.
    /// <paramref name="descriptor"/> is dm-import's <c>&lt;map&gt;.assets.json</c>
    /// in the Engine's content, imported for any recording of the same map.
    /// </summary>
    public static IReadOnlyList<SwingContact> Recompute(IEngineContext engine, string descriptor, Stream recording)
    {
        var fight = RecordedFight.Read(recording);
        using var assets = new LevelAssets(engine, descriptor);
        if (assets.Map != fight.Map) throw new InvalidDataException($"the import is of {assets.Map}, the recording of {fight.Map}");
        var held = new HeldItems(fight);
        using var views = new EntityViews(engine, fight, assets, held) { Exact = true };
        using var session = engine.Spatial.CreateSession(new SpatialSessionConfig(1, 16, VoxelSurfaceMode.GreedyCubes));
        var byIndex = fight.Tracks.ToLookup(t => t.Key.Index);
        // An entity as last seen with that identity: a victim the blow killed is removed in the blow's
        // frame, so its track ends the frame before. Serials keep a reused index apart (from v5).
        EntityTrack? Track(ushort index, uint serial, int frame) =>
            index == WireLayout.NoEntity ? null
            : byIndex[index].Where(t => t.FirstFrame <= frame && t.LastFrame >= frame - 1 && (serial == 0 || t.Key.Serial == serial))
                .OrderByDescending(t => t.FirstFrame).FirstOrDefault();

        // One swing's contact can make several damage records (the shield, then its holder): one trace each.
        var blows = new List<(int Frame, WireDamage Damage, EntityTrack Attacker, EntityTrack Victim, EntityTrack Struck, EntityTrack? Weapon)>();
        var seen = new HashSet<(int, ushort, Vector3, Vector3)>();
        foreach (var (frame, d) in fight.Damage)
        {
            if ((d.DamageType & MeleeBits) == 0 || d.TraceValid == 0) continue;
            var attacker = Track(d.Attacker, d.AttackerSerial, frame);
            var weapon = Track(d.Weapon, d.WeaponSerial, frame);
            // Taps before 2026-10-17 17:10 named an npc_template_maker as the attacker and its NPC as the weapon.
            if (weapon is not null && !weapon.ClassName.StartsWith("weapon_", StringComparison.Ordinal)) { attacker = weapon; weapon = null; }
            var victim = Track(d.Victim, d.VictimSerial, frame);
            var struck = Track(d.TraceEntity, d.TraceEntitySerial, frame);
            if (attacker is null || victim is null || struck is null || !IsCharacter(attacker.ClassName) || !IsCharacter(victim.ClassName)) continue;
            if (!seen.Add((frame, d.Attacker, Start(d), End(d)))) continue;
            blows.Add((frame, d, attacker, victim, struck, weapon));
        }

        var sweeper = new Sweeper(engine, session, views, fight);
        var contacts = new List<SwingContact>();
        foreach (var (frame, d, attacker, victim, struck, weapon) in blows)
        {
            bool body = struck == victim;
            int donorHitbox = body ? d.HitBox : 0;
            var donor = new SwingTouch(struck.Key.ToString(), struck.ClassName, body, donorHitbox, body ? d.HitGroup : 0, Vector3.Distance(Start(d), End(d)));
            int before = Math.Max(0, frame - 1);
            var (first, donorAlong, miss) = sweeper.Sweep(before, d, attacker, weapon, victim, struck, donorHitbox);
            var (after, _, _) = sweeper.Sweep(frame, d, attacker, weapon, victim, struck, donorHitbox);
            var victimAt = victim.At(fight.Frames[before].Time);
            bool traced = !body || victim.ClassName != "player" && !(d.HitBox == 0 && d.HitGroup == 0
                && assets.Model(fight.ModelPath(victimAt.ModelIndex)) is { Hitboxes: [var first0, ..] } && first0.Group != 0);
            float reach = new Vector2(d.TraceEndX - victimAt.Origin.X, d.TraceEndY - victimAt.Origin.Y).Length();
            int update = victim.UpdateAt(fight.Frames[before].Time);
            int stale = update < 0 ? 0 : fight.Frames[before].Tick - fight.Frames[victim.Updates[update].Frame].Tick;
            contacts.Add(new SwingContact(d.Tick, attacker.ClassName, victim.ClassName, Attack(fight, attacker, before),
                donor, first, after, donorAlong, miss, stale, donor.Along, traced, reach) { VictimSequence = Attack(fight, victim, before) });
        }
        return contacts;
    }

    static Vector3 Start(in WireDamage d) => new(d.TraceStartX, d.TraceStartY, d.TraceStartZ);
    static Vector3 End(in WireDamage d) => new(d.TraceEndX, d.TraceEndY, d.TraceEndZ);

    /// <summary>An entity's streamed sequence at a frame, by name.</summary>
    static string Attack(RecordedFight fight, EntityTrack entity, int frame)
    {
        var s = entity.At(fight.Frames[frame].Time);
        return fight.Sequences.TryGetValue(fight.ModelPath(s.ModelIndex), out var names) && s.Sequence >= 0 && s.Sequence < names.Length ? names[s.Sequence] : "";
    }

    /// <summary>
    /// Sweeps traces through one recording's posed entities with the shared <see cref="HitSweep"/>: characters answer
    /// by hit boxes, items by their collision models.
    /// </summary>
    sealed class Sweeper(IEngineContext engine, SpatialSession session, EntityViews views, RecordedFight fight)
    {
        const float M = SourceSpace.MetresPerUnit;
        readonly HitSweep sweep = new(engine, session);

        /// <summary>Poses everyone near the trace as frame <paramref name="frame"/> had them and sweeps it: the first contact, where it meets the donor's part, and how far the donor's point lies outside that part.</summary>
        public (SwingTouch? First, float? DonorAlong, float Miss) Sweep(int frame, in WireDamage d, EntityTrack attacker, EntityTrack? weapon,
            EntityTrack victim, EntityTrack struck, int donorHitbox)
        {
            double time = fight.Frames[frame].Time;
            views.Present(frame, time);
            var start = SourceSpace.Position(Start(d));
            var end = SourceSpace.Position(End(d));
            var along = end - start;
            float length = along.Length();
            var through = length > 0 ? end + along / length * (Overshoot * M) : end;
            // Items stand where the Wire streamed them: the donor's own placement of what it traced.
            var posed = views.Pose(p => DistanceToSegment(p, start, end) < Reach * M, streamedItemsAt: time)
                .Where(e => e.Track != attacker && e.Holder != attacker && e.Track != weapon).ToList();
            var targets = posed.Select(e => new PosedTarget(e.Model.Info, e.Joints, e.Pose,
                e.Holder is EntityTrack h ? posed.FindIndex(p => p.Track == h) : -1)).ToList();

            float miss = float.NaN;
            if (posed.FindIndex(e => e.Track == struck) is int s and >= 0)
            {
                var e = posed[s];
                if (e.Model.Collision.Count > 0 && e.Joints.TryGetValue(e.Model.Collision[0].Joint, out int root))
                    miss = e.Model.Collision.Min(hull => HitSweep.OutsideHull(end, e.Pose[root].World, hull)) / M;
                else if (donorHitbox >= 0 && donorHitbox < e.Model.Hitboxes.Count && e.Joints.TryGetValue(e.Model.Hitboxes[donorHitbox].Joint, out int joint))
                    miss = HitSweep.Outside(end, e.Pose[joint].World, e.Model.Hitboxes[donorHitbox]) / M;
            }
            SwingTouch? first = null;
            float? donorAlong = null;
            foreach (var c in sweep.Sweep(start, through, Radius * M, targets))
            {
                var e = posed[c.Target];
                var touch = new SwingTouch(e.Track.Key.ToString(), e.Track.ClassName, e.Track == victim, c.Hitbox,
                    c.Hull ? 0 : e.Model.Hitboxes[c.Hitbox].Group, c.Along / M);
                first ??= touch;
                if (donorAlong is null && e.Track == struck && touch.Hitbox == donorHitbox) donorAlong = touch.Along;
            }
            return (first, donorAlong, miss);
        }
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + ab * t);
    }
}
