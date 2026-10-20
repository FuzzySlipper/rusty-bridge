using RustyBridge.Wire.Donor;

namespace DarkMessiah.Combat;

/// <summary>
/// One contact of an attack: its damage factor (HitPow), whether it breaks a parry, how far its sweep reaches
/// (`range`, Source units), how the attacker moves through it (`AttackMove`: 1 in place, more for lunges, jumps
/// and kicks; 0 when not given), the adrenaline it gives the player when it lands (`adrenalineadd`), and whether a
/// blow on the head cuts it off (`cutheadflag`: the huge slash's head blows killed an orc of 100000 health), and whether
/// a player's blow with it may be a critical (`critical_enable`: the power attacks' releases and the combos, not the
/// fast attacks), and the slow motion the move asks for as it begins (`slowtime_delay`, `_duration`, `_factor`: the
/// Whirlwind's ×0.5 for 2.5 s; null when it asks none).
/// </summary>
public sealed record AttackEvent(float HitPow, bool BreaksParry, float Range = AttackEvent.DefaultRange, int Move = 0, float Adrenaline = 0,
    bool CutsHead = false, bool CanCritical = false, SlowTime? SlowTime = null)
{
    /// <summary>A combat move's range when its block gives none: the move's constructor sets 120 (server.dll+0x3DD56C).</summary>
    public const float DefaultRange = 120;
}

/// <summary>
/// A slow-motion request, as the game's request (server.dll+0x147560) takes it: after <paramref name="Delay"/> seconds the
/// world runs at <paramref name="Factor"/> for <paramref name="Duration"/> seconds of wall time, which is Duration ×
/// Factor of game time (docs/wire.md, "Slow motion").
/// </summary>
public sealed record SlowTime(float Delay, float Duration, float Factor)
{
    /// <summary>
    /// What a Fatality's killing blow asks: the damage record's own slow-time fields (×0.1 for 2 s, 0.2 s late), which
    /// reach the request from server.dll+0x2CB846. Every recorded Fatality kill asked it, no other kill did (#10147).
    /// </summary>
    public static readonly SlowTime FatalityKill = new(.2f, 2, .1f);

    /// <summary>How long the world runs slowed, in game time.</summary>
    public float GameSeconds => Duration * Factor;
}

/// <summary>
/// An attack from a model's <c>combatmovedatas</c>, keyed by sequence name.
/// An attack split into parts lists one event per contact (`event0`,
/// `event1`...); any other has one.
/// </summary>
public sealed record AttackData(string Name, IReadOnlyList<AttackEvent> Events);

/// <summary>
/// A character's combat table from its model's .qct: strength, the damage
/// multiplier per hit group (`hitboxes_modifier`), its attacks, and how far
/// it reaches without a weapon (`baseattacklength`).
/// </summary>
public sealed record CharacterData(string Model, float Strength, IReadOnlyDictionary<int, float> HitGroups, IReadOnlyDictionary<string, AttackData> Attacks,
    float BaseAttackLength = CharacterData.DefaultBaseAttackLength)
{
    /// <summary>An unarmed attack's sweep length when entity_data gives none: the loader's default, 50 (server.dll+0x3B16D7).</summary>
    public const float DefaultBaseAttackLength = 50;

    public float HitGroup(int group) => HitGroups.GetValueOrDefault(group, 1f);
    public AttackData? Attack(string sequence) => Attacks.GetValueOrDefault(sequence);
}

/// <summary>
/// The melee routine's settings from `scripts/mm_game_settings.txt`: a player
/// blow on an NPC at or below <see cref="NotAlertedLevel"/> is multiplied by
/// <see cref="NotAlertedMultiplier"/>, and every player blow by
/// <see cref="PlayerDamageMultiplier"/>. A player's kick asks <see cref="KickDamage"/>, and costs
/// <see cref="StaminaKickNpc"/> stamina when it lands on an NPC, <see cref="StaminaKickAll"/> otherwise.
/// </summary>
public sealed record GameSettings(float NotAlertedMultiplier, int NotAlertedLevel, float PlayerDamageMultiplier, float KickDamage = 1,
    float StaminaKickNpc = 3, float StaminaKickAll = 1)
{
    /// <summary>What the shipped file says, for when it can't be read.</summary>
    public static readonly GameSettings Shipped = new(3, 0, 1, 1, 3, 1);
}

/// <summary>A weapon's damage from its model's .qct: `WDmg`, "to be added to damage done", and `WCritHit`, the critical
/// chance it adds (percent; 0 for the short sword).</summary>
public sealed record WeaponData(string Model, float Damage, float CriticalChance = 0);

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
    /// <summary>The player's body model, whose .qct holds the per-level stat tables (<see cref="PlayerStrength"/>).</summary>
    public const string PlayerModel = "models/npc/player/player_basic.mdl";

    /// <summary>
    /// The player's strength at an <c>extra_damage</c> level: <c>player_strength_{level + 1}</c> from
    /// <c>player_basic.qct</c>'s <c>skills</c> block (0, 0, 2, 3, 6 for 0 to 4). The game reads its table at
    /// level + 1 (server.dll+0x419275; docs/combat.md, "The player's strength"). 0 when the table is missing.
    /// </summary>
    public float PlayerStrength(int extraDamageLevel) => PlayerSkillNumber($"player_strength_{Math.Clamp(extraDamageLevel, 0, 3) + 1}") ?? 0;

    /// <summary>
    /// The player's chance of a critical, percent, on a blow whose move may critical (<see cref="AttackEvent.CanCritical"/>):
    /// twice <c>player_dexterity_{critical_hit level + 1}</c> (1, 3, 6 for levels 0 to 2), plus the weapon's
    /// <c>WCritHit</c>. The roll (server.dll+0x416000) asks <c>RandomFloat(0, 100) &lt; 2 × the stat + the chance</c>; the
    /// recordings bear the level + 1 reading out: 1 critical in 45 power blows with a new game's skills (2%), 13 in 111
    /// with <c>critical_hit</c> 2 (12%; #10146).
    /// </summary>
    public float PlayerCriticalChance(int criticalHitLevel, float weaponChance = 0) =>
        2 * (PlayerSkillNumber($"player_dexterity_{Math.Clamp(criticalHitLevel, 0, 3) + 1}") ?? 0) + weaponChance;

    /// <summary>
    /// A number in <c>player_basic.qct</c>'s stat block: the <c>skills</c> block that holds <paramref name="key"/>. The
    /// per-class starting builds have <c>skills</c> blocks too, earlier in the file, and a missing brace leaves the
    /// file's blocks flattened, so the block is found by its key, not its place. Null when absent.
    /// </summary>
    public float? PlayerSkillNumber(string key)
    {
        static IEnumerable<KeyValues> All(KeyValues b) => b.Blocks.SelectMany(All).Prepend(b);
        return Read(PlayerModel)?.SelectMany(All)
            .FirstOrDefault(b => b.Name.Equals("skills", StringComparison.OrdinalIgnoreCase) && b[key] is not null)?.Number(key);
    }

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
        if (!_events.TryGetValue(model, out var events)) _events[model] = events = _eventsOf?.Invoke(model) ?? StudioModel.Events(model, _read);
        if (!events.TryGetValue(sequence, out var list)) return 0;
        int part = -1;
        foreach (var (at, id) in list)
            if (PartEvents.Contains(id) && at <= cycle + 0.005f) part++;
        return Math.Max(0, part);
    }

    /// <summary>
    /// Which part of a player's first-person attack lands at a cycle of its clip: the last 2107 event at or before it
    /// (0 before the first). 2107 opens each of a multi-part swing's strikes: the rush attack's second part landed at
    /// cycles 0.466 and 0.48 (#10144), past its second 2107 (0.468) and before its 2186 (0.489) and its part event 2139 (0.53).
    /// </summary>
    public int PlayerPart(string clip, float cycle)
    {
        if (clip.Length == 0) return 0;
        if (!_events.TryGetValue(PlayerAttacksModel, out var events)) _events[PlayerAttacksModel] = events = _eventsOf?.Invoke(PlayerAttacksModel) ?? StudioModel.Events(PlayerAttacksModel, _read);
        if (!events.TryGetValue(clip, out var list)) return 0;
        int part = -1;
        foreach (var (at, id) in list)
            if (id == 2107 && at <= cycle + 0.005f) part++;
        return Math.Max(0, part);
    }

    readonly Func<string, IReadOnlyDictionary<string, IReadOnlyList<(float Cycle, int Event)>>>? _eventsOf;

    /// <summary>
    /// A sequence's strike window, when it strikes through one rather than at part events: Arkane's 2175 opens it
    /// and 2176 closes it (the turn strikes, Turn_strike_*). Null when it has none.
    /// </summary>
    public (float Open, float Close)? StrikeWindow(string model, string sequence)
    {
        if (model.Length == 0 || sequence.Length == 0) return null;
        if (!_events.TryGetValue(model, out var events)) _events[model] = events = _eventsOf?.Invoke(model) ?? StudioModel.Events(model, _read);
        if (!events.TryGetValue(sequence, out var list)) return null;
        var open = list.Where(e => e.Event == WindowOpen).Select(e => (float?)e.Cycle).FirstOrDefault();
        var close = list.Where(e => e.Event == WindowClose).Select(e => (float?)e.Cycle).FirstOrDefault();
        return open is float o && close is float c && c > o ? (o, c) : null;
    }

    /// <summary>The events that open and close an NPC's strike window.</summary>
    public const int WindowOpen = 2175, WindowClose = 2176;

    /// <summary>The cycles at which a sequence's attack parts fire (2102, 2139), in order; empty when it has none.</summary>
    public IReadOnlyList<float> PartCycles(string model, string sequence)
    {
        if (model.Length == 0 || sequence.Length == 0) return [];
        if (!_events.TryGetValue(model, out var events)) _events[model] = events = _eventsOf?.Invoke(model) ?? StudioModel.Events(model, _read);
        return events.TryGetValue(sequence, out var list) ? list.Where(e => PartEvents.Contains(e.Event)).Select(e => e.Cycle).ToList() : [];
    }

    /// <param name="read">Reads a donor file (a model's .qct, scripts/mm_game_settings.txt, a .mdl) by donor path, or null.</param>
    /// <param name="eventsOf">
    /// A model's animation events by sequence, when they come from somewhere other than its .mdl: the combat lab reads
    /// them from dm-import's clip extras. Null reads the .mdl through <paramref name="read"/>.
    /// </param>
    public CombatData(Func<string, byte[]?> read, Func<string, IReadOnlyDictionary<string, IReadOnlyList<(float Cycle, int Event)>>>? eventsOf = null)
    {
        _read = read;
        _eventsOf = eventsOf;
    }

    GameSettings? _settings;

    /// <summary>The melee routine's settings, read once from `scripts/mm_game_settings.txt`.</summary>
    public GameSettings Settings => _settings ??= ReadSettings();

    /// <summary>A number under <paramref name="key"/> in this block or any below it, depth first.</summary>
    static float? Deep(KeyValues block, string key) =>
        block.Number(key) ?? block.Blocks.Select(b => Deep(b, key)).FirstOrDefault(v => v is not null);

    GameSettings ReadSettings()
    {
        if (_read("scripts/mm_game_settings.txt") is not byte[] bytes) return GameSettings.Shipped;
        var game = KeyValues.ParseAll(bytes).FirstOrDefault();
        if (game is null) return GameSettings.Shipped;
        var shipped = GameSettings.Shipped;
        return new GameSettings(
            Deep(game, "mul_damages_Npc_Not_alerted") ?? shipped.NotAlertedMultiplier,
            (int)(Deep(game, "mul_damages_Npc_Not_alerted_level") ?? shipped.NotAlertedLevel),
            Deep(game, "player_damage_multiplier") ?? shipped.PlayerDamageMultiplier,
            Deep(game, "kick_damage") ?? shipped.KickDamage,
            Deep(game, "stamina_kick_npc") ?? shipped.StaminaKickNpc,
            Deep(game, "stamina_kick_all") ?? shipped.StaminaKickAll);
    }

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
            data = new CharacterData(model, strength, groups, attacks, entity?.Number("baseattacklength") ?? CharacterData.DefaultBaseAttackLength);
        }
        return _characters[model] = data;
    }

    // An event block inherits nothing from its attack: the game reads each into a fresh move (default range 120).
    static AttackEvent Event(KeyValues b) =>
        new(b.Number("HitPow") ?? 1, (b.Number("breakparade") ?? 0) > 0 || (b.Number("forcebreakparade") ?? 0) > 0,
            b.Number("range") ?? AttackEvent.DefaultRange, (int)(b.Number("AttackMove") ?? 0), b.Number("adrenalineadd") ?? 0,
            (b.Number("cutheadflag") ?? 0) > 0, (b.Number("critical_enable") ?? 0) > 0,
            b.Number("slowtime_duration") is float duration && duration > 0
                ? new SlowTime(b.Number("slowtime_delay") ?? 0, duration, b.Number("slowtime_factor") ?? 1) : null);

    public WeaponData? Weapon(string model)
    {
        if (model.Length == 0) return null;
        if (_weapons.TryGetValue(model, out var cached)) return cached;
        WeaponData? data = null;
        if (Read(model)?.FirstOrDefault()?.Find("entity_data") is { } entity && entity.Number("WDmg") is float damage)
            data = new WeaponData(model, damage, entity.Number("WCritHit") ?? 0);
        return _weapons[model] = data;
    }
}
