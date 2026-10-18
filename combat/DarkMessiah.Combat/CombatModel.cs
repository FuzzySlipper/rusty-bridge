using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>A base damage for an attacker, and where the number came from.</summary>
public sealed record AttackerDamage(string Attacker, string Weapon, float Base, string Source);

/// <summary>An attack's scale on its attacker's base from a cycle on, and the blows that show it.</summary>
public sealed record AttackPhase(string Attacker, string Sequence, float FromCycle, float Scale, string Source);

/// <summary>A hit group's scale for victims whose class starts with <see cref="Victim"/>.</summary>
public sealed record VictimScale(string Victim, int HitGroup, float Scale, string Source);

/// <summary>
/// Dark Messiah's melee decisions as far as the Wire has shown them, for Shadow
/// mode: given what the donor's code was given, decide what it decided. Each
/// rule names the evidence behind it (docs/wire.md "The defense hook" and
/// "The player's parry", docs/combat.md for the damage table).
/// </summary>
public static class CombatModel
{
    /// <summary>How far in front, as the cosine of <see cref="DefenseCase.Front"/>, a raised shield still blocks. A guess: no recorded blow tests it yet.</summary>
    public const float ShieldFrontCosine = 0.5f;

    /// <summary>
    /// An NPC's verdict on a blow (CNPC_BaseArx's defense virtual). The blow
    /// striking its weapon is a parry and its off-hand item a shield block,
    /// whatever its state. On the body, its defense state decides: parrying
    /// (state bit 0x4) parries, a raised shield (0x10) blocks blows from in
    /// front. The state bits aren't on the Wire; the stance the defender is
    /// playing stands in for them.
    /// </summary>
    public static DefenseResult Verdict(DefenseCase c) => c.Struck switch
    {
        Struck.Weapon => DefenseResult.Parry,
        Struck.OffHand => DefenseResult.Shield,
        _ when Stance(c.Victim) == DefenseStance.Parry => DefenseResult.Parry,
        _ when Stance(c.Victim) == DefenseStance.Shield && c.Front >= ShieldFrontCosine => DefenseResult.Shield,
        _ => DefenseResult.Hit,
    };

    public enum DefenseStance { None, Parry, Shield }

    /// <summary>
    /// The defense a stance shows: a parry pose (`Parry_*`, `layer_parry_*`, not the
    /// `Parry_Break` recoil) or a raised shield (`Block_*`, `layer_block`, not the
    /// `Block_attack` counter).
    /// </summary>
    public static DefenseStance Stance(Party p)
    {
        foreach (var name in p.Layers.Prepend(p.Sequence))
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("break", StringComparison.Ordinal) || n.Contains("attack", StringComparison.Ordinal)) continue;
            if (n.StartsWith("parry_", StringComparison.Ordinal) || n.StartsWith("layer_parry", StringComparison.Ordinal)) return DefenseStance.Parry;
            if (n.StartsWith("block", StringComparison.Ordinal) || n.StartsWith("layer_block", StringComparison.Ordinal)) return DefenseStance.Shield;
        }
        return DefenseStance.None;
    }

    /// <summary>
    /// The player's parry against an attack (the attack resolver at
    /// server.dll+0x3B1A90). An attacker whose weapon flags carry 0x10 breaks
    /// the parry; any other attack is parried. The flag isn't on the Wire;
    /// the NPCs that set it are the ones bashing, or kicking in the first half
    /// of a kick-and-swing (l01_a-layers: attack_KickSwing broke the parry at
    /// cycle 0.37, the kick, and was parried twice at 0.61, the blade).
    /// </summary>
    public static DefenseResult PlayerParry(ParryCase c) =>
        BreaksParry(c.Attacker) ? DefenseResult.PlayerParryBroken : DefenseResult.PlayerParried;

    static bool BreaksParry(Party attacker) =>
        attacker.Sequence.Contains("bash", StringComparison.OrdinalIgnoreCase)
        || attacker.Sequence.Contains("kick", StringComparison.OrdinalIgnoreCase) && attacker.Cycle < 0.5f;

    /// <summary>Base damage per attacker and weapon, measured from recorded blows (docs/combat.md).</summary>
    public static readonly IReadOnlyList<AttackerDamage> Bases =
    [
        new("player", "weapon_arx_short_sword", 1.4f, "l01_a-parrybot: 1.4 on every limb, chest and stomach hit, 4.2 on the head"),
        new("npc_orc_sword", "weapon_arxorccleaver", 8f, "l01_a-parrybot, -orcs-vs-guards: attack_right/left/top/bottom/Uppercut and most turn strikes"),
        new("npc_human_guard", "weapon_arx_short_sword", 9f, "l01_a-orcs-vs-guards: one head hit of 27"),
        new("npc_ghoul", "-", 12f, "l01_a-parrybot, -orcs-vs-guards: every ghoul blow that landed"),
        new("npc_cyclope_undead", "-", 17f, "l01_a-parrybot, -orcs-vs-guards: every blow; attack_dual strikes with both arms"),
    ];

    /// <summary>
    /// How an attack scales its attacker's base, from a point in the swing on:
    /// the last entry for the attacker and sequence at or before the blow's
    /// cycle applies, and no entry means ×1. Several attacks contact twice.
    /// </summary>
    public static readonly IReadOnlyList<AttackPhase> AttackScales =
    [
        // The orc's heavy swings: 12 against a base of 8.
        new("npc_orc_sword", "attack_Bash", 0, 1.5f, "12 on the player, 3 blows"),
        new("npc_orc_sword", "attack_Forward", 0, 1.5f, "12 on a ghoul, 36 on a goblin's head"),
        new("npc_orc_sword", "attack_Jump", 0, 1.5f, "12 on a ghoul"),
        // A kick, then the blade.
        new("npc_orc_sword", "attack_KickSwing", 0, 1f, "8 on a ghoul and the player at cycle 0.37"),
        new("npc_orc_sword", "attack_KickSwing", 0.5f, 1.5f, "12 on a ghoul and the player at cycle 0.61"),
        new("npc_orc_sword", "attack_KickBash", 0, 0.5f, "4 on a ghoul and the player at cycle 0.23"),
        new("npc_orc_sword", "attack_KickBash", 0.5f, 1.5f, "12 on a ghoul and an orc at cycle 0.69"),
        // Two light contacts (animation events at cycles 0.34 and 0.54).
        new("npc_orc_sword", "attack_Fast", 0, 0.4f, "3.2 on the player at cycle 0.35; a ghoul took 4.8 there"),
        new("npc_orc_sword", "attack_Fast", 0.45f, 0.8f, "6.4 on the player at cycle 0.56"),
        // Turning to the left strikes weaker than turning to the right (8).
        new("npc_orc_sword", "Turn_strike_90_left", 0, 0.7f, "5.6 on a ghoul"),
        new("npc_orc_sword", "Turn_strike_180_left", 0, 0.7f, "5.6 on a ghoul"),
        // The ghoul's lunges strike, then shove: the second contact asks for nothing.
        new("npc_ghoul", "attack_forward", 0.5f, 0f, "0 on the player and a villager at cycle 0.59, 12 at 0.27"),
        new("npc_ghoul", "attack_run_strike", 0.5f, 0f, "0 on a villager at cycle 0.58"),
        // The cyclops's two-armed blow: 34 against 17.
        new("npc_cyclope_undead", "attack_dual", 0, 2f, "34 on a guard"),
    ];

    /// <summary>A head hit (Source hit group 1) does three times the damage, against every victim.</summary>
    public const float HeadScale = 3f;

    /// <summary>
    /// Hit-group scales that depend on the victim: villagers, guards and
    /// goblins take half on the chest, and villagers and goblins more on the
    /// limbs (no guard limb hit is recorded yet). Orcs, ghouls and the player
    /// take ×1 on every part but the head.
    /// </summary>
    public static readonly IReadOnlyList<VictimScale> VictimScales =
    [
        new("npc_villager_", 2, 0.5f, "ghoul blows: 6 on the chest against 12 unplaced"),
        new("npc_villager_", 5, 1.25f, "ghoul blows: 15 on the right arm"),
        new("npc_villager_", 6, 1.25f, "ghoul blows: 15 on the left leg"),
        new("npc_human_guard", 2, 0.5f, "ghoul blows: 6 on the chest, 74 times in l01_a-parry-attempt3 and -layers"),
        new("npc_goblin", 2, 0.5f, "an orc turn strike: 4 on the chest against 8"),
        new("npc_goblin", 6, 1.25f, "an orc swing: 10 on the left leg against 8"),
    ];

    /// <summary>A blow between two NPCs of the same class applies a tenth of what it asks: 1.2 of 12, ghoul on ghoul and orc on orc.</summary>
    public const float SameClassScale = 0.1f;

    /// <summary>
    /// The victim's health after a blow: what was asked, a tenth of it between
    /// NPCs of the same class. Whether the victim takes damage at all, and any
    /// floor on its health, belong to the victim and aren't on the Wire.
    /// </summary>
    public static float HealthAfter(DamageCase c) =>
        c.HealthBefore - c.Requested * (c.Attacker.IsNpc && c.Attacker.Class == c.Victim.Class ? SameClassScale : 1f);

    /// <summary>
    /// The damage the donor asks TakeDamage to apply, or null when the model
    /// has no base for the attacker. A blow the defender parried or blocked
    /// arrives zeroed: the melee routine sets it to 0 before TakeDamage.
    /// </summary>
    public static float? Damage(DamageCase c)
    {
        if (c.Defended) return 0;
        var entry = Bases.FirstOrDefault(b => b.Attacker == c.Attacker.Class && (b.Weapon == c.Weapon || b.Weapon == "-"));
        if (entry is null) return null;
        float scale = AttackScales.LastOrDefault(a => a.Attacker == c.Attacker.Class
            && a.Sequence.Equals(c.Attacker.Sequence, StringComparison.OrdinalIgnoreCase) && a.FromCycle <= c.Attacker.Cycle)?.Scale ?? 1f;
        if (c.HitGroup == 1) scale *= HeadScale;
        else if (c.HitGroup is int group)
            scale *= VictimScales.FirstOrDefault(v => c.Victim.Class.StartsWith(v.Victim, StringComparison.Ordinal) && v.HitGroup == group)?.Scale ?? 1f;
        return entry.Base * scale;
    }
}
