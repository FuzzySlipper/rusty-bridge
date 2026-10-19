namespace DarkMessiah.Combat;

/// <summary>
/// What a blow met, as the contact decision compares it: an entity, by its
/// recording identity (<c>#index.serial</c>) and class, and on the victim's body
/// a hit group and hit box; otherwise an item (weapon, shield) or another entity.
/// </summary>
public sealed record ContactPart(Struck Struck, int Group, int Hitbox, string Entity = "", string Class = "")
{
    public override string ToString() =>
        (Struck == Struck.Body ? $"body group {Group} (hit box {Hitbox})" : Struck.ToString()) + (Entity.Length > 0 ? $" of {Class} {Entity}" : "");
    /// <summary>The same decision: the same entity and kind of thing and, on the body, the same hit group (what damage reads).</summary>
    public bool SameAs(ContactPart other) => Entity == other.Entity && Struck == other.Struck && (Struck != Struck.Body || Group == other.Group);
    /// <summary>The same victim's body, whatever part of it: what an untraced blow has to meet.</summary>
    public bool SameBody(ContactPart other) => Entity == other.Entity && Struck == Struck.Body && other.Struck == Struck.Body;
}

/// <summary>
/// The contact decision for one melee blow (#10078): what the donor's trace
/// reported, and what sweeping that trace through the hit boxes posed at the
/// blow found (the mirror's <c>SwingContacts</c>).
/// </summary>
/// <param name="Before">The first contact, posed as the frame before the blow; null when the sweep met nothing.</param>
/// <param name="After">The same, posed as the blow's own frame.</param>
/// <param name="DonorAlong">Where along the trace (Source units) the sweep meets the donor's part, posed before; null when it doesn't.</param>
/// <param name="Miss">How far (Source units) the donor's hit point lies outside the donor's part as posed before; NaN when not posed.</param>
/// <param name="Stale">Ticks between the victim's last streamed update and the frame before the blow.</param>
/// <param name="Length">The donor trace's length, Source units.</param>
/// <param name="Traced">Whether the donor's trace met a shape; a blow on the player, or one naming no hit box, ends at a reach point instead.</param>
/// <param name="Reach">How far the donor's hit point lies from the victim's axis, Source units.</param>
public sealed record ContactCase(int Tick, string Attacker, string Victim, string Attack, ContactPart Donor, ContactPart? Before, ContactPart? After,
    float? DonorAlong, float Miss, int Stale, float Length, bool Traced = true, float Reach = 0, string VictimSequence = "")
{
    public override string ToString() =>
        $"tick {Tick}: {Attacker} ({Attack}) on {Victim} ({VictimSequence}, state {Stale} ticks old), trace {Length:F0}"
        + (Traced ? float.IsNaN(Miss) ? "" : $", its end {Miss:F1} off the donor's part" : $", reach {Reach:F0} from the victim's axis");

    /// <summary>
    /// The sweep found what the donor did: the same entity and part (on the body
    /// the same hit group), or for an untraced blow, the victim's body at all.
    /// </summary>
    public bool Agrees => Before is ContactPart found && Matches(found);

    /// <summary>Whether a contact found is what the donor struck, by <see cref="Agrees"/>'s rule.</summary>
    public bool Matches(ContactPart found) => Traced ? found.SameAs(Donor) : found.SameBody(Donor);
}
