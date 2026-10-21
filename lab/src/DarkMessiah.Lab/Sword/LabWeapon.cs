using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The move sets <c>first_basic</c> has for a melee weapon in the main hand (docs/lab.md, "Weapons"): the swords and
/// the club share <c>sword_noshield</c>'s, the daggers and the staffs have their own. The bow's isn't played.
/// </summary>
internal enum WeaponClass { Sword, Daggers, Staff }

/// <summary>
/// The weapon the player holds, as the build names it by model path: its class's move set, and its own numbers from
/// its <c>.qct</c>'s <c>entity_data</c> (its <c>WDmg</c> and <c>WCritHit</c> the combat model reads by the same path).
/// </summary>
/// <param name="Range">The swing's reach, metres: <c>rangeattack</c> (100 units for a sword, 80 for daggers, 110 for the combat staff).</param>
/// <param name="AttackSpeed"><c>speed_attack</c>, the attack clips' playback rate (1.10 for the short sword, daggers and staff; 1 when absent).</param>
/// <param name="Simple"><c>simple_weapon</c> (the club): its fast attacks chain A and B only, with or without
/// <c>combat_chained_slash</c>, and it comes to hand without a draw.</param>
/// <param name="StunMultiplier"><c>stunmultiplicator</c>: what its blows' <c>chancetostun</c> is multiplied by (1 for
/// every weapon the lab plays, 2 for the axes; #10152).</param>
internal sealed record LabWeapon(string Model, WeaponClass Class, float Range, float AttackSpeed, bool Simple, float StunMultiplier = 1)
{
    internal const string ShortSword = "models/items/weapons/sword_short/sword_short.mdl";

    /// <summary>
    /// The class a weapon's model plays, by its folder under <c>models/items/weapons</c>: <c>sword_*</c> and <c>club</c> the
    /// sword's, <c>daggers*</c> the daggers', <c>staff_*</c> the staff's; null for any other (bows, shields, tools). The game
    /// picks by the weapon's entity class (<c>weapon_arx_*_sword</c>, <c>weapon_arxclub</c>, <c>weapon_mm_daggers_*</c>,
    /// <c>weapon_mm_staff_*</c>), which follows the same folders for every weapon the recordings drew.
    /// </summary>
    internal static WeaponClass? ClassOf(string model)
    {
        var parts = VpkIndex.Normalize(model).Split('/');
        if (parts.Length < 5 || parts[^5] != "models" || parts[^4] != "items" || parts[^3] != "weapons") return null;
        string folder = parts[^2];
        return folder.StartsWith("sword_", StringComparison.Ordinal) || folder == "club" ? WeaponClass.Sword
            : folder.StartsWith("daggers", StringComparison.Ordinal) ? WeaponClass.Daggers
            : folder.StartsWith("staff_", StringComparison.Ordinal) ? WeaponClass.Staff
            : null;
    }

    /// <summary>The weapon a model path names, read from the donor; throws for one no class plays.</summary>
    internal static LabWeapon Of(DonorContent donor, string model)
    {
        var weaponClass = ClassOf(model) ?? throw new ArgumentException($"{model} isn't a sword, club, daggers or staff the lab plays.", nameof(model));
        return new LabWeapon(VpkIndex.Normalize(model), weaponClass,
            (donor.Number(model, "entity_data", "rangeattack") ?? 100) * SourceSpace.MetresPerUnit,
            donor.Number(model, "entity_data", "speed_attack") ?? 1,
            donor.Number(model, "entity_data", "simple_weapon") is > 0,
            donor.Number(model, "entity_data", "stunmultiplicator") ?? 1);
    }
}
