using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Shared;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// An attack the orc may choose (#10150): its clip, how far its root motion carries it by its first strike (Source
/// units; the clip's mstudiomovement_t), and that strike's reach (its <c>.qct</c> event's <c>range</c>).
/// </summary>
internal sealed record OrcAttackChoice(string Clip, float Travel, float Range)
{
    /// <summary>
    /// From <paramref name="distance"/> (Source units, feet to feet) its strike would land where the donor's landed:
    /// the distance less its travel inside the attack's fitted window (<c>combat.json</c> <c>orc.attackLandings</c>,
    /// the middle half over the recordings). The windows differ by kind (docs/lab.md, "The orc"); the .qct gives none.
    /// </summary>
    internal bool Lands(float distance, float[] window) => distance - Travel >= window[0] && distance - Travel <= window[1];
}

/// <summary>
/// The numbers the orc fights by, from its <c>.qct</c>'s <c>entity_data</c> (#10150; docs/lab.md, "The orc"):
/// <list type="bullet">
/// <item>its rhythm: between attacks it waits <c>AttackCoolMinTime</c>–<c>MaxTime</c> (1–4 s) when cool and
/// <c>AttackBersekerMinTime</c>–<c>MaxTime</c> (0–2 s) when berserk; it changes mode every
/// <c>AttackRythmMinTime</c>–<c>MaxTime</c> (7–10 s), and goes berserk when the player hasn't attacked for
/// <c>PlayerNoAttackDelayToGoBersekerMode</c> (2 s);</item>
/// <item>its zones: <c>Red_zone</c> (10 units, the closest it fights at), <c>Green_zone</c> (70, the ideal),
/// <c>Yellow_zone</c> (140, the farthest it fights at without walking in);</item>
/// <item>its health, <c>npc_health</c> (35);</item>
/// <item>its attacks: each <c>combatmovedatas</c> entry with a clip that strikes (a 2102), but the turn strikes, the
/// dodge strikes, the wounded ones, the grab and the block's counter, with each one's travel and reach.</item>
/// </list>
/// </summary>
internal sealed record OrcMindData(float CoolMin, float CoolMax, float BerserkMin, float BerserkMax, float RhythmMin, float RhythmMax,
    float NoAttackToBerserk, float Red, float Green, float Yellow, float Health, IReadOnlyList<OrcAttackChoice> Attacks)
{
    /// <summary>Attacks the orc's own loop doesn't choose: turned strikes, dodges, wounded ones, the grab (#10185), counters, downward strikes.</summary>
    static bool Special(string clip) =>
        clip.StartsWith("Turn_strike", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("Dodgestrike", StringComparison.OrdinalIgnoreCase)
        || clip.StartsWith("attack_wounded", StringComparison.OrdinalIgnoreCase) || clip.Equals("attack_grab", StringComparison.OrdinalIgnoreCase)
        || clip.StartsWith("block", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("layer_", StringComparison.OrdinalIgnoreCase)
        || clip.StartsWith("bow", StringComparison.OrdinalIgnoreCase)
        // A downward strike, at a target below: recorded from 63 to 334 units off, which the flat arena never has.
        || clip.StartsWith("attack_down", StringComparison.OrdinalIgnoreCase);

    internal static OrcMindData Read(DonorContent donor, string model, ModelInfo body, CombatData data)
    {
        float N(string key, float shipped) => donor.Number(model, "entity_data", key) ?? shipped;
        var attacks = new List<OrcAttackChoice>();
        foreach (var (name, attack) in data.Character(model)?.Attacks ?? new Dictionary<string, AttackData>())
        {
            // The .qct names its attacks in a case the model's clips may not share (attack_Right, attack_right).
            var clip = body.Clips.Values.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (clip is null || Special(clip.Name) || attack.Events.Count == 0) continue;
            float strike = clip.At(2102).FirstOrDefault(float.NaN);
            if (float.IsNaN(strike)) continue;
            attacks.Add(new OrcAttackChoice(clip.Name, clip.MovedAt(strike).X, attack.Events[0].Range));
        }
        return new OrcMindData(N("AttackCoolMinTime", 1), N("AttackCoolMaxTime", 4), N("AttackBersekerMinTime", 0), N("AttackBersekerMaxTime", 2),
            N("AttackRythmMinTime", 7), N("AttackRythmMaxTime", 10), N("PlayerNoAttackDelayToGoBersekerMode", 2),
            N("Red_zone", 10), N("Green_zone", 70), N("Yellow_zone", 140), N("npc_health", 35), attacks);
    }
}

/// <summary>
/// The orc's fight rhythm (#10150): cool or berserk, how long until it may attack again, and which attack it picks.
/// Seeded, so fights and the smoke repeat.
/// </summary>
internal sealed class OrcMind(OrcMindData data, int seed = OrcMind.Seed)
{
    internal const int Seed = 10150;
    Random random = new(seed);
    float modeLeft, wait, sincePlayerAttack;

    internal OrcMindData Data => data;
    /// <summary>Berserk: the shorter waits between attacks.</summary>
    internal bool Berserk { get; private set; }
    /// <summary>It may attack: its wait since the last attack is over.</summary>
    internal bool Ready => wait <= 0;

    internal void Reset()
    {
        random = new Random(Seed);
        Berserk = false;
        modeLeft = Between(data.RhythmMin, data.RhythmMax);
        wait = Between(data.CoolMin, data.CoolMax);
        sincePlayerAttack = 0;
    }

    float Between(float min, float max) => min + random.NextSingle() * MathF.Max(0, max - min);

    /// <summary>Time passes: the mode changes on its rhythm, or goes berserk while the player holds off.</summary>
    internal void Step(float seconds, bool playerAttacked)
    {
        wait -= seconds;
        sincePlayerAttack = playerAttacked ? 0 : sincePlayerAttack + seconds;
        modeLeft -= seconds;
        if (modeLeft <= 0)
        {
            Berserk = !Berserk;
            modeLeft = Between(data.RhythmMin, data.RhythmMax);
        }
        if (!Berserk && sincePlayerAttack >= data.NoAttackToBerserk)
        {
            Berserk = true;
            modeLeft = Between(data.RhythmMin, data.RhythmMax);
        }
    }

    /// <summary>An attack began: the wait until the next may begin, by the mode (counted from this one's start).</summary>
    internal void Attacked() => wait = Berserk ? Between(data.BerserkMin, data.BerserkMax) : Between(data.CoolMin, data.CoolMax);

    /// <summary>
    /// One of the attacks whose strike lands from <paramref name="distance"/> (Source units) inside its window, at
    /// random; null for none. An attack with no window isn't chosen.
    /// </summary>
    internal OrcAttackChoice? Choose(float distance, IReadOnlyDictionary<string, float[]> windows)
    {
        var landing = data.Attacks.Where(a => windows.TryGetValue(a.Clip, out var w) && a.Lands(distance, w)).ToList();
        return landing.Count == 0 ? null : landing[random.Next(landing.Count)];
    }

    /// <summary>The nearest distance (Source units) any of its attacks can be chosen from: its window's low end plus its travel.</summary>
    internal float Nearest(IReadOnlyDictionary<string, float[]> windows) =>
        data.Attacks.Where(a => windows.ContainsKey(a.Clip)).Select(a => windows[a.Clip][0] + a.Travel).DefaultIfEmpty(0).Min();

    /// <summary>A random pick, for its waiting moves.</summary>
    internal float Roll() => random.NextSingle();
}
