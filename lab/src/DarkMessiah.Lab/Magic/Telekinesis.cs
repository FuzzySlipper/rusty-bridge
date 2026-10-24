using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>How telekinesis sorts a body by its mass (<c>spells.txt</c>'s <c>air_1</c>).</summary>
internal enum MassClass { Small, Big, Huge }

/// <summary>How a thrown body hurts an NPC, by its mass (the game settings' <c>PhysicsWeightToDamageNPCs*</c>).</summary>
internal enum ImpactClass { Small, Medium, Big }

/// <summary>
/// Dark Messiah's telekinesis (#10164; docs/lab.md, "Telekinesis"), from the donor's data:
/// <list type="bullet">
/// <item><c>spells.txt</c>'s <c>air_1</c>: a body under <c>Small_Object_Mass</c> (20 kg) is small and costs
/// <c>ManaCost_Small_Object</c> (4) to grip, under <c>Big_Object_Mass</c> (120) big and costs <c>ManaCost_Big_Object</c>
/// (15), past it huge (<c>ManaCost_Huge_Object</c> 9999: never, the skill's "massive objects" taking adrenaline);</item>
/// <item>the game settings: <c>PlayerTelekinesisDist</c> (850 units) is its reach; a thrown body hurts an NPC from
/// <c>PhysicsSpeedToDamageNPCs</c> (70 units/s), medium from <c>PhysicsWeightToDamageNPCsMedium</c> (10 kg) and big from
/// <c>PhysicsWeightToDamageNPCsBig</c> (50), for <c>Physics{Small,Medium,Big}DamagesTelekinesis</c> (8, 15, 50).</item>
/// </list>
/// The rest is measured (<c>l01_a-v18-telekinesis-mass</c>): where a gripped body is held, <see cref="HoldSmallUnits"/>
/// (54) or <see cref="HoldBigUnits"/> (74) from the eye (the medians of its origin's distance: the jug, bread and logs 33
/// to 55, the stool, crate and barrels 64 to 77; the table, held by its foot, 98), and how fast it's thrown,
/// <see cref="ThrowUnits"/> (1447 units/s: the median of the stools' 1461 and 1475, a log's 1433 and the 40 kg crate's
/// 1159; a barrel, still rising 1.1 s after the grip, left at 365).
/// </summary>
internal sealed record TelekinesisRules(float SmallMass, float BigMass, float SmallCost, float BigCost, float HugeCost, float ReachUnits,
    float DamageSpeed, float MediumWeight, float BigWeight, float SmallDamage, float MediumDamage, float BigDamage,
    float HoldSmallUnits = 54, float HoldBigUnits = 74, float ThrowUnits = 1447)
{
    internal const string Spell = "air_1";

    internal static readonly TelekinesisRules Shipped = new(20, 120, 4, 15, 9999, 850, 70, 10, 50, 8, 15, 50);

    internal static TelekinesisRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(TelekinesisRules.Spell, key);
        float? Setting(string key) => donor.Combat.Setting(key);
        return new(
            Spell("Small_Object_Mass") ?? Shipped.SmallMass,
            Spell("Big_Object_Mass") ?? Shipped.BigMass,
            Spell("ManaCost_Small_Object") ?? Shipped.SmallCost,
            Spell("ManaCost_Big_Object") ?? Shipped.BigCost,
            Spell("ManaCost_Huge_Object") ?? Shipped.HugeCost,
            Setting("PlayerTelekinesisDist") ?? Shipped.ReachUnits,
            Setting("PhysicsSpeedToDamageNPCs") ?? Shipped.DamageSpeed,
            Setting("PhysicsWeightToDamageNPCsMedium") ?? Shipped.MediumWeight,
            Setting("PhysicsWeightToDamageNPCsBig") ?? Shipped.BigWeight,
            Setting("PhysicsSmallDamagesTelekinesis") ?? Shipped.SmallDamage,
            Setting("PhysicsMediumDamagesTelekinesis") ?? Shipped.MediumDamage,
            Setting("PhysicsBigDamagesTelekinesis") ?? Shipped.BigDamage);
    }

    internal float ReachMetres => ReachUnits * SourceSpace.MetresPerUnit;
    internal float ThrowMetres => ThrowUnits * SourceSpace.MetresPerUnit;

    /// <summary>How far from the eye a gripped body of <paramref name="kg"/> is held, metres.</summary>
    internal float HoldMetres(float kg) => (Class(kg) == MassClass.Small ? HoldSmallUnits : HoldBigUnits) * SourceSpace.MetresPerUnit;

    internal MassClass Class(float kg) => kg < SmallMass ? MassClass.Small : kg < BigMass ? MassClass.Big : MassClass.Huge;

    /// <summary>The mana a grip on a body of <paramref name="kg"/> costs.</summary>
    internal float Cost(float kg) => Class(kg) switch { MassClass.Small => SmallCost, MassClass.Big => BigCost, _ => HugeCost };

    internal ImpactClass Impact(float kg) => kg >= BigWeight ? ImpactClass.Big : kg >= MediumWeight ? ImpactClass.Medium : ImpactClass.Small;

    /// <summary>What a thrown body of <paramref name="kg"/> takes from an NPC it meets at <paramref name="unitsPerSecond"/>.</summary>
    internal float Damage(float kg, float unitsPerSecond) => unitsPerSecond < DamageSpeed ? 0 : Impact(kg) switch
    {
        ImpactClass.Big => BigDamage,
        ImpactClass.Medium => MediumDamage,
        _ => SmallDamage,
    };
}
