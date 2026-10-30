namespace DarkMessiah.Lab.Npc;

/// <summary>
/// What a lab NPC is (#10357): its class, its model (whose <c>.qct</c> gives its numbers: health, zones, attacks,
/// defence, perception), and the weapon and shield it holds. The orc swordsman, or the necromancer, a caster
/// (<see cref="CastingData"/>; <c>combat.json</c> <c>orc.necromancer</c>), as <c>mm_npc_create npc_necromancer
/// models/NPC/Necromancer/Npc_necromancer.mdl weapon_mm_hook</c> makes it.
/// </summary>
internal sealed record NpcKind(string Class, string Model, string Weapon, string WeaponClass, string? Shield)
{
    internal const string NecromancerModel = "models/NPC/Necromancer/Npc_necromancer.mdl", Hook = "models/Items/Weapons/Hook/Hook.mdl";

    internal static readonly NpcKind OrcSword = new("npc_orc_sword", Orc.Model, Orc.Cleaver, "weapon_arxorccleaver", Orc.Shield);
    internal static readonly NpcKind Necromancer = new("npc_necromancer", NecromancerModel, Hook, "weapon_mm_hook", null);

    /// <summary>The kind the tuning asks for its first NPC.</summary>
    internal static NpcKind For(Sword.OrcTuning tuning) => tuning.Necromancer ? Necromancer : OrcSword;

    internal bool Caster => this == Necromancer;
}
