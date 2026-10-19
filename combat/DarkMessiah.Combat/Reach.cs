using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Combat;

/// <summary>
/// One attack part an NPC fired at a victim its melee doesn't trace against shapes (the player, or an NPC the
/// blow names no hit box on), with what the donor did: in Source units, flat, from the attacker's origin.
/// </summary>
/// <param name="Part">Which part of the attack fired (0 the first: Arkane's event 2102, 1 its 2139).</param>
/// <param name="ToVictim">From the attacker's origin to the victim's.</param>
/// <param name="Facing">The attacker's facing (its yaw), unit length.</param>
/// <param name="VictimHalfWidth">Half the victim's collision box: the player's 16 (VEC_HULL), an NPC's hull.</param>
/// <param name="Landed">The donor recorded a blow on the victim, or the player's parry of it.</param>
/// <param name="StruckFirst">The class of another entity the attacker struck instead, or "".</param>
/// <param name="FromDamage">The case is an untraced blow on an NPC: its record is all there is, so it always landed.</param>
/// <param name="Armed">The attacker holds a weapon: its sweep is the attack's range; without one it is its `baseattacklength`.</param>
/// <param name="FacingOther">
/// The class of the nearest other living NPC inside the attacker's aim cone, within <see cref="Reaches.EnemyDistance"/>, or "". The routine aims
/// at the attacker's enemy, which the Wire doesn't carry; that NPC may be it.
/// </param>
public sealed record ReachCase(int Tick, string Attacker, string AttackerModel, string Attack, int Part, string Victim,
    Vector2 ToVictim, Vector2 Facing, float VictimHalfWidth, bool Landed, string StruckFirst = "", bool FromDamage = false, string FacingOther = "", bool Armed = true)
{
    public override string ToString() =>
        FormattableString.Invariant($"tick {Tick}: {Attacker} ({Attack}, part {Part}) at {Victim} {ToVictim.Length():F0} away");
}

/// <summary>
/// Reads the reach decision's cases from a recording. Every NPC attack part that fires while the player stands in
/// its aim (within the melee routine's cone, cos 0.3, and twice its range) is a case on the player; it landed when
/// the attacker's blow on the player, or the player's parry of it, came within <see cref="Window"/> ticks. Every
/// untraced blow on an NPC (hit box 0 in group 0) is a case that landed.
/// <list type="bullet">
/// <item>When a part fires: the attacker's first streamed state past its event cycle, which is the think that ran
/// it. An NPC moves and animates only in its think, every 8 ticks.</item>
/// <item>Where: the attacker and victim as streamed in that frame.</item>
/// </list>
/// </summary>
public static class Reaches
{
    /// <summary>A blow belongs to an attack part this many ticks either side of it: one NPC think and a half.</summary>
    const int Window = 12;
    const uint MeleeBits = 0x4 | 0x80;
    const float AimCosine = .3f;
    /// <summary>How far an NPC in the aim cone may stand and still be the attacker's enemy: well inside NPC sight (1500).</summary>
    public const float EnemyDistance = 1000;

    /// <summary>Half an NPC's collision box by class: Source's human hull (13) and medium hull (16). Others are left out.</summary>
    static readonly Dictionary<string, float> NpcHulls = new(StringComparer.Ordinal)
    {
        ["npc_human_guard"] = 13, ["npc_villager_male"] = 13, ["npc_villager_female"] = 13, ["npc_orc_sword"] = 16, ["npc_ghoul"] = 13,
    };

    /// <summary>The player's collision box: VEC_HULL_MIN/MAX (±16 by 72).</summary>
    public const float PlayerHalfWidth = 16;

    public static List<ReachCase> Read(IEnumerable<RecordedFrame> frames, CombatData data)
    {
        var cases = new List<(ReachCase Case, ushort Attacker)>();
        var playing = new Dictionary<(ushort, uint, string), float>();
        var blows = new List<(int Tick, ushort Attacker, string Victim)>();
        // Who holds a weapon: from Wire v10 a weapon_* entity's owner; before, an attacker whose blows name one.
        var armed = new HashSet<ushort>();
        bool owners = false;
        uint level = uint.MaxValue;
        foreach (var f in frames)
        {
            if (f.LevelSerial != level) { playing.Clear(); level = f.LevelSerial; }
            string Class(ushort index) => f.Entities.TryGetValue(index, out var e) ? f.Classes.GetValueOrDefault(e.ClassId, "?") : "?";
            foreach (var d in f.Damage.Where(d => (d.DamageType & MeleeBits) != 0))
            {
                blows.Add((d.Tick, d.Attacker, Class(d.Victim)));
                if (!owners && Class(d.Weapon).StartsWith("weapon_", StringComparison.Ordinal)) armed.Add(d.Attacker);
            }
            if (f.Version >= 10)
            {
                owners = true;
                foreach (var w in f.Entities.Values)
                    if (w.Owner != WireLayout.NoEntity && f.Classes.GetValueOrDefault(w.ClassId, "").StartsWith("weapon_", StringComparison.Ordinal)) armed.Add(w.Owner);
            }
            foreach (var d in f.Defense.Where(d => d.Result is DefenseResult.PlayerParried or DefenseResult.PlayerParryBroken)) blows.Add((d.Tick, d.Attacker, "player"));
            foreach (var d in f.Defense.Where(d => d.Result is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.Hit or DefenseResult.NoEffect))
                blows.Add((d.Tick, d.Attacker, Class(d.Victim)));

            var players = f.Entities.Values.Where(e => f.Classes.GetValueOrDefault(e.ClassId) == "player").Take(1).ToList();
            foreach (var (index, e) in f.Entities)
            {
                string cls = f.Classes.GetValueOrDefault(e.ClassId, "");
                if (!cls.StartsWith("npc_", StringComparison.Ordinal) || (e.Flags & WireEntityFlags.HasAnimation) == 0) continue;
                if (f.Sequences.GetValueOrDefault(e.ModelIndex) is not { } names) continue;
                string model = f.Models.GetValueOrDefault(e.ModelIndex, "");
                string Name(int s) => s >= 0 && s < names.Length ? names[s] : "";
                var clips = new List<(string Clip, float Cycle)> { (Name(e.Sequence), e.Cycle) };
                clips.AddRange(f.Layers.Where(l => l.Index == index && l.Weight > 0).Select(l => (Name(l.Sequence), l.Cycle)));
                foreach (var (clip, cycle) in clips)
                {
                    var parts = data.PartCycles(model, clip);
                    if (parts.Count == 0) continue;
                    var key = (index, e.Serial, clip);
                    // A clip seen for the first time, or starting over, plays from 0.
                    float before = playing.TryGetValue(key, out var was) && cycle >= was ? was : -1;
                    playing[key] = cycle;
                    if (before < 0 && cycle > .25f) continue;
                    for (int part = 0; part < parts.Count; part++)
                    {
                        if (!(before < parts[part] && cycle >= parts[part]) || players.Count == 0) continue;
                        var p = players[0];
                        var to = new Vector2(p.OriginX - e.OriginX, p.OriginY - e.OriginY);
                        float yaw = e.Yaw * MathF.PI / 180;
                        var facing = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
                        float range = MathF.Max(data.Character(model)?.Attack(clip)?.Events.ElementAtOrDefault(part)?.Range ?? AttackEvent.DefaultRange,
                            data.Character(model)?.BaseAttackLength ?? CharacterData.DefaultBaseAttackLength);
                        if (to.Length() > 2 * range + 2 * PlayerHalfWidth || to.LengthSquared() > 0 && Vector2.Dot(Vector2.Normalize(to), facing) < AimCosine) continue;
                        // Another living NPC inside the aim cone: it may be the attacker's enemy, which the aim follows at any distance.
                        string other = f.Entities.Where(o => o.Key != index && f.Classes.GetValueOrDefault(o.Value.ClassId, "").StartsWith("npc_", StringComparison.Ordinal)
                                && ((o.Value.Flags & WireEntityFlags.HasHealth) == 0 || o.Value.LifeState == 0 && o.Value.Health > 0))
                            .Select(o => (Class: f.Classes[o.Value.ClassId], To: new Vector2(o.Value.OriginX - e.OriginX, o.Value.OriginY - e.OriginY)))
                            .Where(o => o.To.Length() > 1 && o.To.Length() <= EnemyDistance && Vector2.Dot(Vector2.Normalize(o.To), facing) >= AimCosine)
                            .OrderBy(o => o.To.Length()).Select(o => o.Class).FirstOrDefault() ?? "";
                        cases.Add((new ReachCase(f.Tick, cls, model, clip, part, "player", to, facing, PlayerHalfWidth, false, FacingOther: other), index));
                    }
                }
            }

            // Untraced blows on NPCs: the record names hit box 0 in group 0.
            foreach (var d in f.Damage)
            {
                if ((d.DamageType & MeleeBits) == 0 || d.TraceValid == 0 || d.HitBox != 0 || d.HitGroup != 0) continue;
                if (!f.Entities.TryGetValue(d.Attacker, out var a) || !f.Entities.TryGetValue(d.Victim, out var v)) continue;
                // Taps before 2026-10-17 17:10 named an npc_template_maker's spawn as its weapon and the maker as attacker (Blows does the same).
                if (f.Entities.TryGetValue(d.Weapon, out var held) && f.Classes.GetValueOrDefault(held.ClassId, "") is { Length: > 0 } heldClass
                    && !heldClass.StartsWith("weapon_", StringComparison.Ordinal)) a = held;
                string attacker = f.Classes.GetValueOrDefault(a.ClassId, ""), victim = f.Classes.GetValueOrDefault(v.ClassId, "");
                if (!attacker.StartsWith("npc_", StringComparison.Ordinal) || !NpcHulls.TryGetValue(victim, out float half)) continue;
                string model = f.Models.GetValueOrDefault(a.ModelIndex, "");
                var names = f.Sequences.GetValueOrDefault(a.ModelIndex) ?? [];
                string attack = a.Sequence >= 0 && a.Sequence < names.Length ? names[a.Sequence] : "";
                float yaw = a.Yaw * MathF.PI / 180;
                cases.Add((new ReachCase(d.Tick, attacker, model, attack, data.Part(model, attack, a.Cycle), victim,
                    new Vector2(v.OriginX - a.OriginX, v.OriginY - a.OriginY), new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)), half, true, FromDamage: true), d.Attacker));
            }
        }
        return cases.Select(c => c.Case.FromDamage ? c.Case with { Armed = armed.Contains(c.Attacker) } : c.Case with
        {
            Armed = armed.Contains(c.Attacker),
            Landed = blows.Any(b => b.Attacker == c.Attacker && b.Victim == "player" && Math.Abs(b.Tick - c.Case.Tick) <= Window),
            StruckFirst = blows.Where(b => b.Attacker == c.Attacker && b.Victim != "player" && Math.Abs(b.Tick - c.Case.Tick) <= Window).Select(b => b.Victim).FirstOrDefault() ?? "",
        }).ToList();
    }
}
