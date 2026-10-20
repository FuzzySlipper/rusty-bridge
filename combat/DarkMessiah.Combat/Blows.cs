using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>
/// An entity as a decision saw it: its class and model, and what it was doing
/// (main sequence and overlay layers). For the player, the layers are the
/// first-person view model's, where its attacks play.
/// </summary>
/// <param name="LastAttack">
/// The attack sequence the entity played most recently, within the last
/// second: an NPC's state reaches the Wire every 8 ticks, so a blow can land
/// after its streamed sequence has moved on to the next move.
/// </param>
public sealed record Party(string Class, string Sequence, IReadOnlyList<string> Layers, float Cycle, string Id = "", string Model = "", string LastAttack = "")
{
    public static readonly Party None = new("-", "", [], 0);
    public bool IsPlayer => Class == "player";
    public bool IsNpc => Class.StartsWith("npc_", StringComparison.Ordinal);
}

/// <summary>What a melee blow physically struck on the defender.</summary>
public enum Struck { Body, Weapon, OffHand, Other }

/// <summary>An NPC defense check: the inputs the donor's verdict had, and the verdict.</summary>
/// <param name="State">From Wire v11: the defender's defense-state word as its verdict read it; null before.</param>
/// <param name="NoEffect">From Wire v11: the defender's no-effect byte was set.</param>
public sealed record DefenseCase(int Tick, Party Victim, Party Attacker, string Weapon, Struck Struck, float Front, DefenseResult Donor, uint? State = null, bool NoEffect = false)
{
    public override string ToString() => $"tick {Tick}: {Attacker.Class} ({Attacker.Sequence}) on {Victim.Class} ({Victim.Sequence}), struck {Struck}, front {Front:F2}";
}

/// <summary>A change of the player's parry state against an attack, and the outcome. <see cref="Contact"/> counts the attack's contacts on the player.</summary>
public sealed record ParryCase(int Tick, Party Attacker, DefenseResult Donor, int Contact = 0)
{
    public override string ToString() => $"tick {Tick}: {Attacker.Class} ({Attacker.Sequence} at {Attacker.Cycle:F2}, contact {Contact})";
}

/// <summary>
/// A damage record from a melee blow: the inputs, the amount the donor
/// requested, and what it applied. <see cref="Contact"/> is which contact of
/// the attacker's swing on this victim it was (0 the first).
/// </summary>
/// <param name="VictimAlert">From Wire v11: an NPC victim's alert level, and its state, which the not-alerted multiplier reads; null before.</param>
public sealed record DamageCase(int Tick, Party Victim, Party Attacker, string Weapon, int? HitGroup, uint DamageType, int WeaponFlags,
    float Requested, float HealthBefore, float HealthAfter, bool Defended, int Contact = 0, string WeaponModel = "",
    int? VictimAlert = null, int VictimNpcState = 0, bool AttackerCritical = false, float PlayerStrength = 0)
{
    public float Applied => HealthBefore - HealthAfter;
    public override string ToString() =>
        $"tick {Tick}: {Attacker.Class} ({Attack} at {Attacker.Cycle:F2}, contact {Contact}) with {Weapon} on {Victim.Class}, hit group {(HitGroup is int g ? g : "-")}";
    string Attack => Attacker.IsPlayer && Attacker.Layers.Count > 0 ? string.Join("+", Attacker.Layers.Where(l => l.Contains("attack", StringComparison.OrdinalIgnoreCase) || l.Contains("slash", StringComparison.OrdinalIgnoreCase))) : Attacker.Sequence;
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

    sealed class Swing
    {
        public string Sequence = "";
        public float Cycle;
        public readonly Dictionary<string, (int Count, int LastTick)> Contacts = [];

        /// <summary>Follows the attacker's streamed state; a new sequence, or its cycle starting over, ends the swing.</summary>
        public void Observe(string sequence, float cycle)
        {
            if (sequence != Sequence || cycle < Cycle - 0.01f) Contacts.Clear();
            Sequence = sequence;
            Cycle = cycle;
        }
    }

    /// <summary>An attack sequence by its name: Arkane names them attack_*, *strike*, Block_attack, bow_kick.</summary>
    static bool IsAttack(string sequence) =>
        sequence.Contains("attack", StringComparison.OrdinalIgnoreCase) || sequence.Contains("strike", StringComparison.OrdinalIgnoreCase)
        || sequence.Contains("kick", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long after an attack sequence a blow is still taken as that attack's: one second of ticks at 15 ms.</summary>
    const int AttackMemoryTicks = 66;

    public static FightCases Read(string name, IEnumerable<RecordedFrame> frames)
    {
        var seen = new Dictionary<(ushort, uint), (WireEntity Entity, string Class, string Model, string[]? Sequences)>();
        // Characters as the previous frame had them: a defender's stance before the blow. The
        // frame a verdict arrives in already shows its reaction (a parry layer at cycle 0).
        var before = new Dictionary<(ushort, uint), (WireEntity Entity, string Class, string Model, string[]? Sequences)>();
        var beforeLayers = new List<WireLayer>();
        var swings = new Dictionary<string, Swing>();
        var lastAttack = new Dictionary<string, (string Sequence, int Tick)>();
        var defense = new List<DefenseCase>();
        var parries = new List<ParryCase>();
        var damage = new List<DamageCase>();
        uint version = 0, level = uint.MaxValue;
        foreach (var f in frames)
        {
            version = f.Version;
            if (f.LevelSerial != level) { seen.Clear(); before.Clear(); beforeLayers.Clear(); swings.Clear(); lastAttack.Clear(); level = f.LevelSerial; }
            foreach (var (index, e) in f.Entities)
                seen[(index, f.Version >= 5 ? e.Serial : 0)] = (e, f.Classes.GetValueOrDefault(e.ClassId, "?"), f.Models.GetValueOrDefault(e.ModelIndex, ""), f.Sequences.GetValueOrDefault(e.ModelIndex));
            string IdOf(ushort index, in WireEntity e) => f.Version >= 5 ? $"#{index}.{e.Serial:x}" : $"#{index}";
            foreach (var (index, e) in f.Entities)
            {
                if ((e.Flags & WireEntityFlags.HasAnimation) == 0 || f.Sequences.GetValueOrDefault(e.ModelIndex) is not { } names || e.Sequence < 0 || e.Sequence >= names.Length) continue;
                string id = IdOf(index, e);
                if (swings.TryGetValue(id, out var swing)) swing.Observe(names[e.Sequence], e.Cycle);
                if (IsAttack(names[e.Sequence])) lastAttack[id] = (names[e.Sequence], f.Tick);
            }

            // The player's attacks play as layers on its first-person view model.
            var viewModel = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");

            List<string> LayerNames(IEnumerable<WireLayer> layers, ushort index, string[]? names) =>
                layers.Where(l => l.Index == index).OrderBy(l => l.Order)
                    .Select(l => names is not null && l.Sequence >= 0 && l.Sequence < names.Length ? names[l.Sequence] : "").Where(n => n.Length > 0).ToList();

            Party Who(ushort index, uint serial, bool previous = false)
            {
                if (index == WireLayout.NoEntity) return Party.None;
                var key = (index, f.Version >= 5 ? serial : (uint)0);
                var layerSource = previous && before.ContainsKey(key) ? beforeLayers : f.Layers;
                if (!(previous && before.TryGetValue(key, out var s)) && !seen.TryGetValue(key, out s)) return new Party($"#{index}", "", [], 0);
                string Name(int sequence) => s.Sequences is { } names && sequence >= 0 && sequence < names.Length ? names[sequence] : "";
                var layers = LayerNames(layerSource, index, s.Sequences);
                if (s.Class == "player" && viewModel.Value.ModelIndex != 0)
                {
                    var vmNames = f.Sequences.GetValueOrDefault(viewModel.Value.ModelIndex);
                    layers = LayerNames(f.Layers, viewModel.Key, vmNames);
                    // The server view model doesn't always carry its layers; from Wire v9 the client's,
                    // which drew the swing, are on the Wire too.
                    if (layers.Count == 0 && f.ClientView.Flags.HasFlag(ClientViewFlags.ViewmodelLayers))
                        layers = f.ClientView.ViewmodelLayers.OrderBy(l => l.Order)
                            .Select(l => vmNames is not null && l.Sequence >= 0 && l.Sequence < vmNames.Length ? vmNames[l.Sequence] : "").Where(n => n.Length > 0).ToList();
                }
                string id = f.Version >= 5 ? $"#{index}.{serial:x}" : $"#{index}";
                string recent = lastAttack.TryGetValue(id, out var a) && f.Tick - a.Tick <= AttackMemoryTicks ? a.Sequence : "";
                return new Party(s.Class, (s.Entity.Flags & WireEntityFlags.HasAnimation) != 0 ? Name(s.Entity.Sequence) : "", layers, s.Entity.Cycle,
                    id, s.Model, recent);
            }

            // Which contact of the attacker's swing on this victim a blow is. A swing ends when the
            // attacker's streamed sequence changes or its cycle starts over (followed every frame);
            // contacts in one tick are one.
            int Contact(Party attacker, Party victim, int tick)
            {
                if (!swings.TryGetValue(attacker.Id, out var swing))
                {
                    swings[attacker.Id] = swing = new Swing();
                    swing.Observe(attacker.Sequence, attacker.Cycle);
                }
                var (count, last) = swing.Contacts.GetValueOrDefault(victim.Id, (0, int.MinValue));
                if (last == tick) return count - 1;
                swing.Contacts[victim.Id] = (count + 1, tick);
                return count;
            }

            foreach (var d in f.Defense.Where(d => d.Result is not (DefenseResult.PlayerParried or DefenseResult.PlayerParryBroken)))
            {
                var victim = Who(d.Victim, d.VictimSerial, previous: true);
                var attacker = Who(d.Attacker, d.AttackerSerial);
                var struckBy = d.Struck == d.Victim && d.StruckSerial == d.VictimSerial ? Struck.Body : Classify(Who(d.Struck, d.StruckSerial).Class);
                defense.Add(new DefenseCase(d.Tick, victim, attacker, Who(d.Weapon, d.WeaponSerial).Class, struckBy,
                    Front(d, f.Entities.TryGetValue(d.Victim, out var v) ? v : null), d.Result,
                    d.StateValid != 0 ? d.VictimState : null, d.NoEffect != 0));
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

            // Contacts in tick order: player parries (a parried blow makes no damage call) and melee damage.
            var contacts = new List<(int Tick, int Order, Action Add)>();
            foreach (var d in f.Defense.Where(d => d.Result is DefenseResult.PlayerParried or DefenseResult.PlayerParryBroken))
            {
                var attacker = Who(d.Attacker, d.AttackerSerial);
                var victim = Who(d.Victim, d.VictimSerial);
                var record = d;
                contacts.Add((d.Tick, contacts.Count, () => parries.Add(new ParryCase(record.Tick, attacker, record.Result, Contact(attacker, victim, record.Tick)))));
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
                var record = d;
                contacts.Add((d.Tick, contacts.Count, () => damage.Add(new DamageCase(record.Tick, victim, attacker, weapon.Class,
                    record.TraceValid != 0 ? record.HitGroup : null, record.DamageType, record.WeaponFlags,
                    record.Damage, record.HealthBefore, record.HealthAfter, defended, Contact(attacker, victim, record.Tick), weapon.Model,
                    record.VictimAlertValid != 0 ? record.VictimAlert : null, record.VictimNpcState, record.AttackerCritical != 0))));
            }
            foreach (var c in contacts.OrderBy(c => c.Tick).ThenBy(c => c.Order)) c.Add();

            before.Clear();
            foreach (var (index, e) in f.Entities)
            {
                string cls = f.Classes.GetValueOrDefault(e.ClassId, "?");
                if (cls == "player" || cls.StartsWith("npc_", StringComparison.Ordinal))
                    before[(index, f.Version >= 5 ? e.Serial : 0)] = (e, cls, f.Models.GetValueOrDefault(e.ModelIndex, ""), f.Sequences.GetValueOrDefault(e.ModelIndex));
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
