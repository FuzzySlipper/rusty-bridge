using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Donor;

/// <summary>One level of a skill: what it costs and the modules it unlocks (<c>combat_chained_slash</c>, <c>bow_zoom</c>...).</summary>
internal sealed record SkillLevel(int Level, float Cost, IReadOnlyList<string> Modules);

/// <summary>A skill from <c>scripts/mm_skills_infos.txt</c>: the skill it requires (empty for none), its maximum level and its levels.</summary>
internal sealed record SkillInfo(string Name, string Requires, int MaxLevel, IReadOnlyList<SkillLevel> Levels);

/// <summary>
/// The donor's gameplay tables that aren't a model's own (docs/import.md, "Gameplay tables"), read from the
/// imported scripts on first use:
/// <list type="bullet">
/// <item>spells from <c>scripts/spells.txt</c>'s <c>spelldatas</c>, by Arkane's id (<c>air_1</c> is telekinesis);</item>
/// <item>skills from <c>scripts/mm_skills_infos.txt</c>;</item>
/// <item>the factions' relationships from <c>scripts/mm_relationship.txt</c> (R10167-1): each faction's disposition to
/// every other and to the player, <c>HT</c> (hates), <c>LI</c> (likes) or <c>NE</c> (neutral).</item>
/// </list>
/// An item's own numbers (<c>WDmg</c>, <c>rangeattack</c>, a bow's <c>ammo</c>) are in its <c>.qct</c>'s
/// <c>entity_data</c>: <see cref="DonorContent.Number"/>.
/// </summary>
internal sealed class GameTables(Func<string, byte[]?> read)
{
    internal const string SpellsFile = "scripts/spells.txt", SkillsFile = "scripts/mm_skills_infos.txt", RelationshipsFile = "scripts/mm_relationship.txt";

    IReadOnlyDictionary<string, KeyValues>? spells;
    IReadOnlyDictionary<string, SkillInfo>? skills;
    IReadOnlyDictionary<string, KeyValues>? relationships;

    /// <summary>Each faction's block of dispositions, by name; empty when the import has no relationships table.</summary>
    internal IReadOnlyDictionary<string, KeyValues> Relationships => relationships ??=
        (Table(RelationshipsFile).FirstOrDefault()?.Blocks ?? []).GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="faction"/>'s disposition to <paramref name="other"/> (a faction, or <c>player</c>): <c>HT</c>, <c>LI</c>, <c>NE</c>, or null when the table hasn't it.</summary>
    internal string? Disposition(string faction, string other) => Relationships.TryGetValue(faction, out var block) ? block[other] : null;

    /// <summary>Each spell's block by id; empty when the import has no spells table.</summary>
    internal IReadOnlyDictionary<string, KeyValues> Spells => spells ??=
        (Table(SpellsFile).Select(t => t.Name.Equals("spelldatas", StringComparison.OrdinalIgnoreCase) ? t : t.Find("spelldatas")).OfType<KeyValues>().FirstOrDefault()?.Blocks ?? [])
        .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A number in a spell's block (<c>Spell("air_1", "ManaCost_Use")</c>); null when absent.</summary>
    internal float? Spell(string spell, string key) => Spells.TryGetValue(spell, out var block) ? block.Number(key) : null;

    /// <summary>Every skill by name; empty when the import has no skills table.</summary>
    internal IReadOnlyDictionary<string, SkillInfo> Skills => skills ??= ReadSkills();

    IReadOnlyDictionary<string, SkillInfo> ReadSkills()
    {
        var root = Table(SkillsFile).FirstOrDefault();
        var result = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in root?.Blocks ?? [])
        {
            var levels = new List<SkillLevel>();
            foreach (var level in skill.Blocks)
                if (level.Name.StartsWith("level_", StringComparison.OrdinalIgnoreCase) && int.TryParse(level.Name[6..], out int n))
                    levels.Add(new SkillLevel(n, level.Number("cost") ?? 0,
                        level.Values.Where(v => v.Key.Equals("module", StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).ToList()));
            result[skill.Name] = new SkillInfo(skill.Name, skill["requires"] ?? "", (int)(skill.Number("max_level") ?? levels.Count - 1),
                levels.OrderBy(l => l.Level).ToList());
        }
        return result;
    }

    List<KeyValues> Table(string path) => read(path) is byte[] bytes ? KeyValues.ParseAll(bytes) : [];
}
