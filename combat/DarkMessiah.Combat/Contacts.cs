namespace DarkMessiah.Combat;

/// <summary>What a blow met, as the contact decision compares it: the victim's body (a hit group and hit box on it) or an item.</summary>
public sealed record ContactPart(Struck Struck, int Group, int Hitbox)
{
    public override string ToString() => Struck == Struck.Body ? $"body group {Group} (hit box {Hitbox})" : Struck.ToString();
    /// <summary>The same decision: the same kind of thing and, on the body, the same hit group (what damage reads).</summary>
    public bool SameAs(ContactPart other) => Struck == other.Struck && (Struck != Struck.Body || Group == other.Group);
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
    /// The sweep found what the donor did: the same part (the same kind, and on
    /// the body the same hit group), or for an untraced blow, the victim's body
    /// at all.
    /// </summary>
    public bool Agrees => Before is ContactPart found && (Traced ? found.SameAs(Donor) : found.Struck == Struck.Body);
}
