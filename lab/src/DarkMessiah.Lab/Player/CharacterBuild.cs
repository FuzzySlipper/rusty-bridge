using System.Text.Json.Serialization;
using DarkMessiah.Lab.Content;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// The player's build as authored in <c>content/build.json</c> (live, and on the developer shell's Build group): each
/// skill's level from <c>scripts/mm_skills_infos.txt</c>, the weapon and the off-hand item by model path, and the spells
/// known by Arkane's id (<c>air_1</c> is telekinesis). Validated against the donor's tables by <see cref="CharacterBuild.From"/>.
/// </summary>
/// <param name="OffHand">Null for none, or a shield's model path.</param>
internal sealed record BuildFile(Dictionary<string, int> Skills, string Weapon, string? OffHand, string[] Spells)
{
    internal const string Path = "build.json";

    internal static BuildFile Load(IEngineContext engine) => Authored.Read(engine, Path, BuildJson.Default.BuildFile);
}

/// <summary>
/// What the player can do, as the game decides it (docs/lab.md, "Builds"): the skill modules learned, the weapon and
/// off-hand item in use, and the spells known. Mechanics ask <see cref="Has"/> for a module, not a constant.
/// <list type="bullet">
/// <item>from <c>build.json</c> (<see cref="From"/>): a skill at level n gives the modules of its levels 0 to n, so every
/// build has the combat skill's level 0 (<c>combat_big_slash</c>, <c>combat_huge_slash</c>, <c>combat_parry</c>,
/// <c>combat_fatality</c>), as the donor's new game does;</item>
/// <item>from a recording (Wire v15, <see cref="FromRecording"/>): the modules and items the donor's player had.</item>
/// </list>
/// </summary>
/// <param name="SkillLevels">Each skill's level: the file's, or from Wire v16 the recording's (empty before v16, read as 0).</param>
internal sealed record CharacterBuild(IReadOnlySet<string> Modules, string Weapon, string? OffHand, IReadOnlyList<string> Spells, string Source,
    IReadOnlyDictionary<string, int> SkillLevels)
{
    /// <summary>A skill's level; 0 for one the build doesn't name.</summary>
    internal int Level(string skill) => SkillLevels.GetValueOrDefault(skill);

    internal bool Has(string module) => Modules.Contains(module);

    public override string ToString() =>
        $"{Source}: {Modules.Count} modules ({string.Join(", ", Modules.Order(StringComparer.Ordinal))}), weapon {Weapon}, off-hand {OffHand ?? "none"}" +
        (SkillLevels.Any(s => s.Value > 0) ? $", skills {string.Join(", ", SkillLevels.Where(s => s.Value > 0).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value}"))}" : "") +
        (Spells.Count > 0 ? $", spells {string.Join(", ", Spells)}" : "");

    /// <summary>
    /// The build an authored file describes, checked against the donor's tables:
    /// <list type="bullet">
    /// <item>each skill is one of <c>mm_skills_infos.txt</c>'s, at a level from 0 to its <c>max_level</c>;</item>
    /// <item>a skill above level 0 has what it <c>requires</c>: one of the skills its "a|b" list names, above level 0;</item>
    /// <item>the weapon and off-hand are imported items (their <c>.qct</c>) the lab can play: a sword, the club, daggers or a
    /// staff (<see cref="Sword.LabWeapon.ClassOf"/>), and a shield (<c>models/items/armors/shield*</c>), which needs a
    /// sword or the club in hand and <c>combat_shield_enabled</c> (the combat skill's level 2), as the game's
    /// <c>sword_shield</c> moves do (#10149);</item>
    /// <item>each spell is in <c>spells.txt</c>.</item>
    /// </list>
    /// </summary>
    internal static CharacterBuild From(BuildFile file, GameTables tables, Func<string, bool> isItem)
    {
        const string P = BuildFile.Path;
        var skills = tables.Skills;
        Authored.Require(skills.Count > 0, P, "skills", "the import has no scripts/mm_skills_infos.txt (dm-import tables, docs/import.md).");
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, level) in file.Skills)
        {
            Authored.Require(skills.ContainsKey(name), P, $"skills.{name}", $"isn't a skill in mm_skills_infos.txt ({string.Join(", ", skills.Keys.Order())}).");
            var skill = skills[name];
            Authored.Require(level >= 0 && level <= skill.MaxLevel, P, $"skills.{name}", $"must be 0 to {skill.MaxLevel}; found {level}.");
            if (level > 0 && skill.Requires.Length > 0)
            {
                var options = skill.Requires.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Authored.Require(options.Any(o => file.Skills.GetValueOrDefault(o) > 0), P, $"skills.{name}", $"requires one of {string.Join(", ", options)} above level 0.");
            }
        }
        foreach (var skill in skills.Values)
            foreach (var level in skill.Levels.Where(l => l.Level <= file.Skills.GetValueOrDefault(skill.Name)))
                modules.UnionWith(level.Modules);
        Authored.Require(isItem(file.Weapon), P, "weapon", $"{file.Weapon} isn't an imported item (no .qct).");
        Authored.Require(Sword.LabWeapon.ClassOf(file.Weapon) is not null, P, "weapon", $"{file.Weapon} isn't a sword, the club, daggers or a staff (models/items/weapons/sword_*, club, daggers*, staff_*): the lab plays no other weapon.");
        Authored.Require(file.OffHand is null || isItem(file.OffHand), P, "offHand", $"{file.OffHand} isn't an imported item (no .qct).");
        if (file.OffHand is { } offHand)
        {
            Authored.Require(Sword.LabShield.IsShield(offHand), P, "offHand", $"{offHand} isn't a shield (models/items/armors/shield*): the lab plays no other off-hand item.");
            Authored.Require(Sword.LabWeapon.ClassOf(file.Weapon) == Sword.WeaponClass.Sword, P, "offHand", $"a shield goes with a sword or the club, not {file.Weapon}.");
            Authored.Require(modules.Contains("combat_shield_enabled"), P, "offHand", "a shield needs combat_shield_enabled: the combat skill at level 2.");
        }
        foreach (var spell in file.Spells)
            Authored.Require(tables.Spells.ContainsKey(spell), P, $"spells.{spell}", $"isn't a spell in spells.txt ({string.Join(", ", tables.Spells.Keys.Order())}).");
        return new CharacterBuild(modules, VpkIndex.Normalize(file.Weapon), file.OffHand is null ? null : VpkIndex.Normalize(file.OffHand), file.Spells, P,
            new Dictionary<string, int>(file.Skills));
    }

    /// <summary>
    /// The donor player's build from a v15 recording's stats: the modules learned, and the weapon and shield in hand by
    /// their entities' model paths (null when none); from v16 each skill's level. Spells known aren't on the Wire.
    /// </summary>
    internal static CharacterBuild FromRecording(WirePlayerStats stats, uint version, string? weapon, string? offHand, string recording) =>
        new(SkillModuleNames.Of(stats.Modules).ToHashSet(StringComparer.Ordinal), weapon is null ? "none" : VpkIndex.Normalize(weapon),
            offHand is null ? null : VpkIndex.Normalize(offHand), [], recording,
            version >= 16 ? SkillNames.ByIndex.ToDictionary(n => n, n => stats.SkillLevel(n)) : new Dictionary<string, int>());
}

// Authored: missing constructor values, nulls in non-nullable fields and unknown members are errors, not defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BuildFile))]
internal sealed partial class BuildJson : JsonSerializerContext;
