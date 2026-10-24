using System.Numerics;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// The fireball (<c>spells.txt</c>'s <c>fire_3</c>; #10165, docs/lab.md "The fireball"): its <c>cost</c> (8) spent as
/// it's cast, its <c>effect</c> (12) on what it meets and less across its <c>explosion_radius</c> (120 units), and its
/// <c>timetolive</c> (5 s). Measured (<c>l01_a-v18-fireball</c>): it forms at the hand for <see cref="FormSeconds"/>
/// (0.57 s, every cast) and flies at <see cref="SpeedUnits"/> (725 units/s: 722 to 730).
/// </summary>
internal sealed record FireballRules(float Cost, float Effect, float RadiusUnits, float LifeSeconds, float AdrenalineAdd,
    float FormSeconds = .57f, float SpeedUnits = 725)
{
    internal const string Spell = "fire_3";
    /// <summary>The arms' clips (<c>first_basic.mdl</c>): ready in hand, and the cast (the donor's at every press).</summary>
    internal const string ReadyClip = "layer_spellcasting_ready_fire_3", CastClip = "layer_spellcasting_cast_in_fire_3";

    internal static readonly FireballRules Shipped = new(8, 12, 120, 5, 3);

    internal static FireballRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(FireballRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Effect = Spell("effect") ?? Shipped.Effect,
            RadiusUnits = Spell("explosion_radius") ?? Shipped.RadiusUnits,
            LifeSeconds = Spell("timetolive") ?? Shipped.LifeSeconds,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
        };
    }

    internal float SpeedMetres => SpeedUnits * SourceSpace.MetresPerUnit;

    /// <summary>
    /// What it takes from a body <paramref name="distanceUnits"/> from where it went off (to the body's nearest point; 0
    /// struck): its effect, falling off to none at the radius, as Source's radius damage does.
    /// </summary>
    internal float Damage(float distanceUnits) => Effect * MathF.Max(0, 1 - distanceUnits / RadiusUnits);
}

/// <summary>One fireball: forming at the hand, then flying until it meets something or its time runs out.</summary>
internal sealed class LabFireball(Vector3 at)
{
    internal Vector3 Position { get; private set; } = at;
    internal Vector3 Direction { get; private set; }
    internal float Age { get; private set; }
    internal bool Flying { get; private set; }
    /// <summary>Where it went off, once it has.</summary>
    internal Vector3? Exploded { get; private set; }

    /// <summary>
    /// One step: forming, it stays at <paramref name="hand"/>; then it leaves along <paramref name="aim"/> and flies,
    /// going off where <paramref name="meets"/> finds something on its way (the point) or when its time is up.
    /// </summary>
    internal void Step(float seconds, FireballRules rules, Vector3 hand, Vector3 aim, Func<Vector3, Vector3, Vector3?> meets)
    {
        if (Exploded is not null) return;
        Age += seconds;
        if (!Flying)
        {
            Position = hand;
            if (Age < rules.FormSeconds) return;
            Flying = true;
            Direction = Vector3.Normalize(aim);
        }
        var next = Position + Direction * rules.SpeedMetres * seconds;
        if (meets(Position, next) is Vector3 hit) { Position = hit; Exploded = hit; return; }
        Position = next;
        if (Age >= rules.LifeSeconds) Exploded = Position;
    }
}
