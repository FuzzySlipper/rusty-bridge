using RustyBridge.Wire.Donor;

namespace DarkMessiah.Combat;

/// <summary>One contact of an attack: its damage factor (HitPow) and whether it breaks a parry.</summary>
public sealed record AttackEvent(float HitPow, bool BreaksParry);

/// <summary>
/// An attack from a model's <c>combatmovedatas</c>, keyed by sequence name.
/// An attack split into parts lists one event per contact (`event0`,
/// `event1`...); any other has one.
/// </summary>
public sealed record AttackData(string Name, IReadOnlyList<AttackEvent> Events);

/// <summary>
/// A character's combat table from its model's .qct: strength, the damage
/// multiplier per hit group (`hitboxes_modifier`), and its attacks.
/// </summary>
public sealed record CharacterData(string Model, float Strength, IReadOnlyDictionary<int, float> HitGroups, IReadOnlyDictionary<string, AttackData> Attacks)
{
    public float HitGroup(int group) => HitGroups.GetValueOrDefault(group, 1f);
    public AttackData? Attack(string sequence) => Attacks.GetValueOrDefault(sequence);
}

/// <summary>A weapon's damage from its model's .qct: `WDmg`, "to be added to damage done".</summary>
public sealed record WeaponData(string Model, float Damage);

/// <summary>
/// Dark Messiah's combat tables, read from the `.qct` files beside each
/// model in the game's VPKs: Arkane's commented `$keyvalues` sources, which
/// the game ships alongside the compiled models. Tables are read on first use
/// and cached by model path.
/// </summary>
public sealed class CombatData
{
    /// <summary>The player's first-person model, whose `combatmovedatas` holds the player's attacks.</summary>
    public const string PlayerAttacksModel = "models/npc/player/first_basic.mdl";

    readonly Func<string, byte[]?> _read;
    readonly Dictionary<string, CharacterData?> _characters = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WeaponData?> _weapons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<(float Cycle, int Event)>>> _events = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Animation events that strike: 2102 fires an attack's first part, 2139
    /// its second (Arkane's ids, matched against recorded contacts: an orc's
    /// attack_Fast has them at cycles 0.34 and 0.54, its parts' HitPow 0.4 and 0.8).
    /// </summary>
    public static readonly IReadOnlySet<int> PartEvents = new HashSet<int> { 2102, 2139 };

    /// <summary>
    /// Which part of an attack lands at a cycle: the last part event at or
    /// before it (0 before the first). A blow's streamed cycle is from the
    /// attacker's last think, so it reads at or just after its event.
    /// </summary>
    public int Part(string model, string sequence, float cycle)
    {
        if (model.Length == 0 || sequence.Length == 0) return 0;
        if (!_events.TryGetValue(model, out var events)) _events[model] = events = StudioModel.Events(model, _read);
        if (!events.TryGetValue(sequence, out var list)) return 0;
        int part = -1;
        foreach (var (at, id) in list)
            if (PartEvents.Contains(id) && at <= cycle + 0.005f) part++;
        return Math.Max(0, part);
    }

    public CombatData(Func<string, byte[]?> read) => _read = read;

    /// <summary>The tables from a game's VPK folder (local/donor/vpks, or the install's vpks).</summary>
    public static CombatData FromVpks(string vpks) => new(new VpkIndex(vpks).Read);

    static string Qct(string model) => Path.ChangeExtension(model.Replace('\\', '/'), ".qct");

    List<KeyValues>? Read(string model) => _read(Qct(model)) is byte[] bytes ? KeyValues.ParseAll(bytes) : null;

    public CharacterData? Character(string model)
    {
        if (model.Length == 0) return null;
        if (_characters.TryGetValue(model, out var cached)) return cached;
        CharacterData? data = null;
        if (Read(model) is { } blocks)
        {
            var keyvalues = blocks.FirstOrDefault(b => b.Name.Equals("$keyvalues", StringComparison.OrdinalIgnoreCase));
            var entity = keyvalues?.Block("entity_data");
            // Strength is the block's own; `level_N` sub-blocks hold other settings of the same NPC.
            float strength = entity?.Number("strength") ?? 0;
            var groups = new Dictionary<int, float>();
            if (keyvalues?.Find("hitboxes_modifier") is KeyValues modifiers)
                foreach (var (key, value) in modifiers.Values)
                    if (int.TryParse(key, out int group) && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale))
                        groups[group] = scale;
            // combatmovedatas is a top-level block, or inside $keyvalues where a brace is missing.
            var moves = blocks.FirstOrDefault(b => b.Name.Equals("combatmovedatas", StringComparison.OrdinalIgnoreCase)) ?? keyvalues?.Find("combatmovedatas");
            var attacks = new Dictionary<string, AttackData>(StringComparer.OrdinalIgnoreCase);
            foreach (var attack in moves?.Blocks ?? [])
            {
                var parts = attack.Blocks.Where(b => b.Name.StartsWith("event", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var events = (parts.Count > 0 ? parts : [attack]).Select(Event).ToList();
                attacks.TryAdd(attack.Name, new AttackData(attack.Name, events));
            }
            data = new CharacterData(model, strength, groups, attacks);
        }
        return _characters[model] = data;
    }

    static AttackEvent Event(KeyValues b) =>
        new(b.Number("HitPow") ?? 1, (b.Number("breakparade") ?? 0) > 0 || (b.Number("forcebreakparade") ?? 0) > 0);

    public WeaponData? Weapon(string model)
    {
        if (model.Length == 0) return null;
        if (_weapons.TryGetValue(model, out var cached)) return cached;
        WeaponData? data = null;
        if (Read(model)?.FirstOrDefault()?.Find("entity_data")?.Number("WDmg") is float damage)
            data = new WeaponData(model, damage);
        return _weapons[model] = data;
    }
}
