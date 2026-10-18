using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Mirror.Replay;

/// <summary>What the mirror does with an entity of a Dark Messiah class.</summary>
internal enum EntityRole
{
    /// <summary>Drawn at its streamed transform.</summary>
    Prop,
    /// <summary>An NPC or the player: other entities may hang from its joints.</summary>
    Character,
    /// <summary>A weapon, shield or helmet: hung from its holder's attachment while held.</summary>
    Held,
    /// <summary>Never drawn: the world itself, physics helpers, the first-person view model.</summary>
    Hidden,
}

/// <summary>
/// Dark Messiah's entity classes as the mirror needs them. This is donor
/// knowledge; it stays here until a second donor needs the same shape.
/// </summary>
internal static class DonorClasses
{
    public static EntityRole Role(string className) => className switch
    {
        // phys_bone_follower carries its owner's model path; the view model is the first-person arms;
        // playerheadmvt is a 15-triangle helper for the player's head motion.
        "worldspawn" or "phys_bone_follower" or "viewmodel" or "playerheadmvt" => EntityRole.Hidden,
        "player" => EntityRole.Character,
        _ when className.StartsWith("npc_", StringComparison.Ordinal) => EntityRole.Character,
        _ when className.StartsWith("weapon_", StringComparison.Ordinal) => EntityRole.Held,
        _ => EntityRole.Prop,
    };

    /// <summary>The holder's attachment a held item hangs from (studio model attachments, e.g. the orc's).</summary>
    public static string Attachment(string className) =>
        className.Contains("shield", StringComparison.Ordinal) ? "LeftShield"
        : className.Contains("helmet", StringComparison.Ordinal) ? "Helmet"
        : "RightHand";
}

/// <summary>A held item's holder at a moment, and the holder's attachment it hangs from.</summary>
/// <param name="Attachment">The holder model's attachment, numbered from 1 as the Wire streams it; 0 when inferred (the class then names it).</param>
internal readonly record struct Holding(EntityTrack Holder, int Attachment, bool Streamed);

/// <summary>
/// Who holds each weapon, shield and helmet.
/// - From Wire v10 the recording says: an item's move parent is its holder,
///   and m_iParentAttachment the attachment it hangs from.
/// - Before v10 it is inferred: a weapon the damage hook named with its
///   attacker belongs to that attacker; otherwise to the character it stays
///   closest to. An inferred holder holds the item while it is live and the
///   streamed item is near it; the item is drawn where it was streamed
///   otherwise (dropped, thrown, or its holder gone).
/// </summary>
internal sealed class HeldItems
{
    /// <summary>A hand is about this far above a character's origin, in Source units.</summary>
    const float HandHeight = 40;
    /// <summary>Within this of a character's hand height, an item counts towards that character holding it.</summary>
    const float Reach = 64;
    /// <summary>
    /// A known holder's item further than this from the holder's origin has been dropped or thrown. An
    /// orc's swing carries a blade well past <see cref="Reach"/>, so this only catches an item left behind.
    /// </summary>
    const float Dropped = 160;

    readonly Dictionary<EntityTrack, EntityTrack> holderOf = [];
    readonly ILookup<EntityKey, EntityTrack> byKey;
    readonly bool streamed;

    public HeldItems(RecordedFight fight)
    {
        // A key can come back (an index and serial reused after the entity left), so it names a track only with a frame.
        byKey = fight.Tracks.ToLookup(t => t.Key);
        streamed = fight.HasParents;
        var items = fight.Tracks.Where(t => DonorClasses.Role(t.ClassName) == EntityRole.Held).ToList();
        Items = items.Count;
        if (streamed)
        {
            // Every item that ever hangs from something counts as held, for the status line.
            foreach (var item in items)
                if (item.Updates.Select(u => u.State).FirstOrDefault(s => s.Parent != WireLayout.NoEntity && s.Parent != 0) is { Parent: not 0 } s
                    && At(new EntityKey(s.Parent, s.ParentSerial), item.FirstFrame) is EntityTrack parent)
                    holderOf[item] = parent;
            return;
        }
        var characters = fight.Tracks.Where(t => DonorClasses.Role(t.ClassName) == EntityRole.Character).ToList();
        var evidence = fight.Wielding
            .Select(w => (Weapon: At(w.Weapon, w.Frame), Holder: At(w.Holder, w.Frame)))
            .Where(w => w.Weapon is not null && w.Holder is not null)
            .GroupBy(w => w.Weapon!)
            .ToDictionary(g => g.Key, g => g.GroupBy(w => w.Holder!).MaxBy(h => h.Count())!.Key);

        foreach (var item in items)
        {
            if (evidence.TryGetValue(item, out var named))
            {
                holderOf[item] = named;
                continue;
            }
            // Count, over the item's updates, which character it was within reach of.
            var near = new Dictionary<EntityTrack, int>();
            foreach (var u in item.Updates)
            {
                var at = new Vector3(u.State.OriginX, u.State.OriginY, u.State.OriginZ);
                EntityTrack? best = null;
                float bestDistance = Reach;
                foreach (var c in characters)
                {
                    if (!c.LiveAt(u.Frame)) continue;
                    float d = Vector3.Distance(at, Hand(c.Sample(u.Time).Origin));
                    if (d < bestDistance) { bestDistance = d; best = c; }
                }
                if (best is not null) near[best] = near.GetValueOrDefault(best) + 1;
            }
            if (near.Count > 0 && near.MaxBy(kv => kv.Value) is var (holder, count) && count * 2 >= item.Updates.Count)
                holderOf[item] = holder;
        }
    }

    EntityTrack? At(EntityKey key, int frame) => byKey[key].FirstOrDefault(t => t.LiveAt(frame));

    static Vector3 Hand(Vector3 origin) => origin + new Vector3(0, 0, HandHeight);

    /// <summary>The recording streams parents (v10), so nothing here is inferred.</summary>
    public bool Streamed => streamed;

    /// <summary>Items with a holder, and items the recording has at all.</summary>
    public int Count => holderOf.Count;
    public int Items { get; }

    /// <summary>The holder inferred for an item over the whole recording, whether or not it holds it now.</summary>
    public EntityTrack? InferredHolder(EntityTrack item) => holderOf.GetValueOrDefault(item);

    /// <summary>
    /// What holds <paramref name="item"/> at a frame and time, and at which
    /// attachment; null when it's loose. From v10 that is the item's streamed
    /// move parent (any entity: a rider hangs from its horse too).
    /// </summary>
    public Holding? Holder(EntityTrack item, int frame, double time)
    {
        if (streamed)
        {
            var sample = item.Sample(time);
            return sample.Parent is EntityKey key && At(key, frame) is EntityTrack parent ? new Holding(parent, sample.ParentAttachment, true) : null;
        }
        if (!holderOf.TryGetValue(item, out var holder) || !holder.LiveAt(frame)) return null;
        return Vector3.Distance(item.Sample(time).Origin, holder.Sample(time).Origin) <= Dropped ? new Holding(holder, 0, false) : null;
    }
}
