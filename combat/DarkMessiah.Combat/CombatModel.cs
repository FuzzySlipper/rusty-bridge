using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>
/// Dark Messiah's melee decisions, for Shadow mode: given what the donor's
/// code was given, decide what it decided. The numbers are the game's own,
/// from each model's .qct (<see cref="CombatData"/>); the rules that combine
/// them were found by scoring against recorded fights (docs/combat.md).
/// </summary>
public sealed class CombatModel(CombatData data)
{
    /// <summary>How far in front, as the cosine of <see cref="DefenseCase.Front"/>, a raised shield still blocks. A guess: no recorded blow tests it yet.</summary>
    public const float ShieldFrontCosine = 0.5f;

    /// <summary>
    /// The damage an unarmed attacker adds to its strength. No .qct sets it
    /// (the `NWDmg` lines are commented out); 1 is what the ghoul (strength
    /// 11, 12 a blow) and the cyclops (16, 17) show.
    /// </summary>
    public const float UnarmedDamage = 1f;

    /// <summary>A blow between two NPCs of the same class applies a tenth of what it asks: 1.2 of 12, ghoul on ghoul and orc on orc. Not in the .qct data.</summary>
    public const float SameClassScale = 0.1f;

    public CombatData Data => data;

    /// <summary>
    /// An NPC's verdict on a blow (CNPC_BaseArx's defense virtual). The blow
    /// striking its weapon is a parry and its off-hand item a shield block,
    /// whatever its state. On the body, its defense state decides: parrying
    /// (state bit 0x4) parries, a raised shield (0x10) blocks blows from in
    /// front. The state bits aren't on the Wire; the stance the defender is
    /// playing before the blow stands in for them.
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
    /// The attack a party is making, from its .qct table: the main sequence,
    /// an attack playing as a layer (an NPC striking on the move, the
    /// player's first-person swings), or the attack it played within the last
    /// second (its streamed state lags a blow by up to 8 ticks). Null when
    /// none is an attack there.
    /// </summary>
    public AttackData? Attack(Party attacker)
    {
        var table = data.Character(attacker.IsPlayer ? CombatData.PlayerAttacksModel : attacker.Model);
        if (table is null) return null;
        if (attacker.IsPlayer)
        {
            // A released power swing (`layer_bigslash_*_out`) outranks the fast attack that always
            // starts with it; the `_in` wind-up is no attack of its own. Before Wire v6 there are
            // no layers, and the player's swing is taken as the fast attack.
            var playing = attacker.Layers.Where(l => !l.EndsWith("_in", StringComparison.OrdinalIgnoreCase)).Select(table.Attack).OfType<AttackData>()
                .OrderByDescending(a => a.Events.Max(e => e.HitPow)).FirstOrDefault();
            return playing ?? (attacker.Layers.Count == 0 ? table.Attack(PlayerDefaultAttack) : null);
        }
        return table.Attack(attacker.Sequence) ?? attacker.Layers.Select(table.Attack).OfType<AttackData>().FirstOrDefault()
            ?? table.Attack(attacker.LastAttack)
            // Before Wire v6 a recording has no layers, and a blow struck on the move (an NPC in
            // Combatmove_* or combatidle) is one of the attacker's layer attacks; theirs all have
            // HitPow 1 (orc, guard).
            ?? (attacker.Layers.Count == 0 ? table.Attacks.Values.FirstOrDefault(a => a.Name.StartsWith("layer_attack", StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>The player's attack when a recording carries no view-model layers (before Wire v6): the first fast attack, as every recorded v6 hit shows.</summary>
    public const string PlayerDefaultAttack = "layer_fastattack_sword_noshield_A";

    /// <summary>
    /// The player's parry against an attack (the attack resolver at
    /// server.dll+0x3B1A90): broken when the attacking contact's event has
    /// `breakparade`/`forcebreakparade` (the donor's weapon flag 0x10),
    /// parried otherwise.
    /// </summary>
    public DefenseResult PlayerParry(ParryCase c) =>
        Event(c.Attacker, Attack(c.Attacker))?.BreaksParry == true ? DefenseResult.PlayerParryBroken : DefenseResult.PlayerParried;

    /// <summary>
    /// The part of an attack a blow is: the part whose animation event fired
    /// last (<see cref="CombatData.Part"/>), and that part's entry in the
    /// attack's .qct events. Null past the last entry: an animation can strike
    /// more often than its table has parts, and those strikes ask for nothing.
    /// An attack taken from the attacker's recent past (its stream has moved
    /// on) is its first part.
    /// </summary>
    AttackEvent? Event(Party attacker, AttackData? attack)
    {
        if (attack is null) return null;
        bool playing = attack.Name.Equals(attacker.Sequence, StringComparison.OrdinalIgnoreCase);
        int part = playing && !attacker.IsPlayer ? data.Part(attacker.Model, attacker.Sequence, attacker.Cycle) : 0;
        return part < attack.Events.Count ? attack.Events[part] : null;
    }

    /// <summary>
    /// The damage the donor asks TakeDamage to apply, or null when the
    /// attacker has no .qct table or its move is no attack in it.
    /// <list type="bullet">
    /// <item>Base: an NPC's strength plus its weapon's `WDmg` (or <see cref="UnarmedDamage"/>); the player's weapon `WDmg`.</item>
    /// <item>× the `HitPow` of the attack's part that struck (by animation event). A strike past the table's last part asks for nothing.</item>
    /// <item>× the victim's `hitboxes_modifier` for the hit group struck.</item>
    /// <item>A parried or blocked blow asks for 0: the melee routine zeroes it before TakeDamage.</item>
    /// </list>
    /// </summary>
    public float? Damage(DamageCase c)
    {
        if (c.Defended) return 0;
        var attack = Attack(c.Attacker);
        if (attack is null) return null;
        if (Event(c.Attacker, attack) is not AttackEvent hit) return 0;
        float weapon = data.Weapon(c.WeaponModel)?.Damage ?? (c.Weapon == "-" ? UnarmedDamage : float.NaN);
        if (float.IsNaN(weapon)) return null;
        float strength = c.Attacker.IsPlayer ? 0 : data.Character(c.Attacker.Model)?.Strength ?? 0;
        float group = c.HitGroup is int g && data.Character(c.Victim.Model) is CharacterData victim ? victim.HitGroup(g) : 1f;
        return (strength + weapon) * hit.HitPow * group;
    }

    /// <summary>
    /// The victim's health after a blow: what was asked, a tenth of it between
    /// NPCs of the same class. Whether the victim takes damage at all, and any
    /// floor on its health, belong to the victim and aren't on the Wire.
    /// </summary>
    public static float HealthAfter(DamageCase c) =>
        c.HealthBefore - c.Requested * (c.Attacker.IsNpc && c.Attacker.Class == c.Victim.Class ? SameClassScale : 1f);
}
