using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>An entity as a decision saw it: its class, and what it was doing (main sequence and overlay layers).</summary>
public sealed record Party(string Class, string Sequence, IReadOnlyList<string> Layers, float Cycle, string Id = "")
{
    public static readonly Party None = new("-", "", [], 0);
    public bool IsPlayer => Class == "player";
    public bool IsNpc => Class.StartsWith("npc_", StringComparison.Ordinal);
}

/// <summary>What a melee blow physically struck on the defender.</summary>
public enum Struck { Body, Weapon, OffHand, Other }

/// <summary>An NPC defense check: the inputs the donor's verdict had, and the verdict.</summary>
public sealed record DefenseCase(int Tick, Party Victim, Party Attacker, string Weapon, Struck Struck, float Front, DefenseResult Donor)
{
    public override string ToString() => $"tick {Tick}: {Attacker.Class} ({Attacker.Sequence}) on {Victim.Class} ({Victim.Sequence}), struck {Struck}, front {Front:F2}";
}

/// <summary>A change of the player's parry state against an attack, and the outcome.</summary>
public sealed record ParryCase(int Tick, Party Attacker, DefenseResult Donor)
{
    public override string ToString() => $"tick {Tick}: {Attacker.Class} ({Attacker.Sequence} at {Attacker.Cycle:F2})";
}

/// <summary>A damage record from a melee blow: the inputs, the amount the donor requested, and what it applied.</summary>
public sealed record DamageCase(int Tick, Party Victim, Party Attacker, string Weapon, int? HitGroup, uint DamageType, int WeaponFlags,
    float Requested, float HealthBefore, float HealthAfter, bool Defended)
{
    public float Applied => HealthBefore - HealthAfter;
    public override string ToString() =>
        $"tick {Tick}: {Attacker.Class} ({Attacker.Sequence} at {Attacker.Cycle:F2}) with {Weapon} on {Victim.Class}, hit group {(HitGroup is int g ? g : "-")}";
}

/// <summary>Every decision a recording captured, as test cases for a combat model.</summary>
public sealed record FightCases(string Name, uint Version, IReadOnlyList<DefenseCase> Defense, IReadOnlyList<ParryCase> Parries, IReadOnlyList<DamageCase> Damage);

/// <summary>
/// Reads a recording into decision cases. An event names entities by index
/// (and from v5 by serial); the entity is taken as last seen with that
/// identity, so a victim removed in the tick it died still has its class.
/// </summary>
public static class Blows
{
    /// <summary>Source DMG_* bits that mark a melee blow: slash and club. Fire, falls and spells are not combat decisions here.</summary>
    const uint MeleeBits = 0x4 | 0x80;

    public static FightCases Read(string name, Stream recording) => Read(name, RecordingReader.Frames(recording));

    public static FightCases Read(string name, IEnumerable<RecordedFrame> frames)
    {
        var seen = new Dictionary<(ushort, uint), (WireEntity Entity, string Class, string[]? Sequences)>();
        // Characters as the previous frame had them: a defender's stance before the blow. The
        // frame a verdict arrives in already shows its reaction (a parry layer at cycle 0).
        var before = new Dictionary<(ushort, uint), (WireEntity Entity, string Class, string[]? Sequences)>();
        var beforeLayers = new List<WireLayer>();
        var defense = new List<DefenseCase>();
        var parries = new List<ParryCase>();
        var damage = new List<DamageCase>();
        uint version = 0, level = uint.MaxValue;
        foreach (var f in frames)
        {
            version = f.Version;
            if (f.LevelSerial != level) { seen.Clear(); before.Clear(); beforeLayers.Clear(); level = f.LevelSerial; }
            foreach (var (index, e) in f.Entities)
                seen[(index, f.Version >= 5 ? e.Serial : 0)] = (e, f.Classes.GetValueOrDefault(e.ClassId, "?"), f.Sequences.GetValueOrDefault(e.ModelIndex));

            Party Who(ushort index, uint serial, bool previous = false)
            {
                if (index == WireLayout.NoEntity) return Party.None;
                var key = (index, f.Version >= 5 ? serial : (uint)0);
                var layerSource = previous && before.ContainsKey(key) ? beforeLayers : f.Layers;
                if (!(previous && before.TryGetValue(key, out var s)) && !seen.TryGetValue(key, out s)) return new Party($"#{index}", "", [], 0);
                string Name(int sequence) => s.Sequences is { } names && sequence >= 0 && sequence < names.Length ? names[sequence] : "";
                var layers = layerSource.Where(l => l.Index == index).OrderBy(l => l.Order).Select(l => Name(l.Sequence)).Where(n => n.Length > 0).ToList();
                return new Party(s.Class, (s.Entity.Flags & WireEntityFlags.HasAnimation) != 0 ? Name(s.Entity.Sequence) : "", layers, s.Entity.Cycle,
                    f.Version >= 5 ? $"#{index}.{serial:x}" : $"#{index}");
            }

            foreach (var d in f.Defense)
            {
                var victim = Who(d.Victim, d.VictimSerial, previous: true);
                var attacker = Who(d.Attacker, d.AttackerSerial);
                if (d.Result is DefenseResult.PlayerParried or DefenseResult.PlayerParryBroken)
                {
                    parries.Add(new ParryCase(d.Tick, attacker, d.Result));
                    continue;
                }
                var struckBy = d.Struck == d.Victim && d.StruckSerial == d.VictimSerial ? Struck.Body : Classify(Who(d.Struck, d.StruckSerial).Class);
                defense.Add(new DefenseCase(d.Tick, victim, attacker, Who(d.Weapon, d.WeaponSerial).Class, struckBy,
                    Front(d, f.Entities.TryGetValue(d.Victim, out var v) ? v : null), d.Result));
            }

            // One swing can touch a defender twice in a tick (its body, then its weapon), and the
            // game asks for a verdict before each damage call, so the checks and the damage records
            // for a defender and attacker pair up in order within their tick.
            var verdicts = new Dictionary<(int, ushort, ushort), Queue<DefenseResult>>();
            foreach (var d in f.Defense)
            {
                var key = (d.Tick, d.Victim, d.Attacker);
                if (!verdicts.TryGetValue(key, out var queue)) verdicts[key] = queue = new();
                queue.Enqueue(d.Result);
            }
            foreach (var d in f.Damage)
            {
                if ((d.DamageType & MeleeBits) == 0) continue;
                var attacker = Who(d.Attacker, d.AttackerSerial);
                var weapon = Who(d.Weapon, d.WeaponSerial);
                // Taps before 2026-10-17 17:10 swapped in any owner, so an NPC spawned by an
                // npc_template_maker showed the maker as attacker and itself as weapon.
                if (weapon.Class != "-" && !weapon.Class.StartsWith("weapon_", StringComparison.Ordinal)) { attacker = weapon; weapon = Party.None; }
                var victim = Who(d.Victim, d.VictimSerial);
                bool defended = verdicts.TryGetValue((d.Tick, d.Victim, d.Attacker), out var pending) && pending.Count > 0
                    && (victim.IsNpc || victim.IsPlayer)
                    && pending.Dequeue() is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.NoEffect;
                if (!attacker.IsNpc && !attacker.IsPlayer) continue;
                if (!victim.IsNpc && !victim.IsPlayer) continue;
                damage.Add(new DamageCase(d.Tick, victim, attacker, weapon.Class, d.TraceValid != 0 ? d.HitGroup : null, d.DamageType, d.WeaponFlags,
                    d.Damage, d.HealthBefore, d.HealthAfter, defended));
            }

            before.Clear();
            foreach (var (index, e) in f.Entities)
            {
                string cls = f.Classes.GetValueOrDefault(e.ClassId, "?");
                if (cls == "player" || cls.StartsWith("npc_", StringComparison.Ordinal))
                    before[(index, f.Version >= 5 ? e.Serial : 0)] = (e, cls, f.Sequences.GetValueOrDefault(e.ModelIndex));
            }
            beforeLayers.Clear();
            beforeLayers.AddRange(f.Layers);
        }
        return new FightCases(name, version, defense, parries, damage);
    }

    static Struck Classify(string cls) =>
        cls.Contains("shield", StringComparison.Ordinal) ? Struck.OffHand
        : cls.StartsWith("weapon_", StringComparison.Ordinal) ? Struck.Weapon
        : Struck.Other;

    /// <summary>
    /// How squarely the blow came from in front of the defender: the cosine
    /// between its facing and the direction vector the check was given. In
    /// the parry-bot fight blows from in front read 0.75 to 1 and one from
    /// behind -0.69. 0 when the check carried no direction.
    /// </summary>
    static float Front(in WireDefense d, WireEntity? victim)
    {
        var dir = new Vector2(d.DirectionX, d.DirectionY);
        if (victim is not WireEntity v || dir.LengthSquared() == 0) return 0;
        var facing = new Vector2(MathF.Cos(v.Yaw * MathF.PI / 180), MathF.Sin(v.Yaw * MathF.PI / 180));
        return Vector2.Dot(facing, Vector2.Normalize(dir));
    }
}
