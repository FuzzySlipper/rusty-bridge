using System.Numerics;
using DarkMessiah.Combat;
using RustyBridge.Wire;
using Xunit;

namespace DarkMessiah.Combat.Tests;

/// <summary>
/// A small set of game data in .qct form, written the way Arkane's files are
/// (quoted and bare keys, comments, nested event parts, the orc's missing
/// brace), and one animation file with part events.
/// </summary>
static class GameData
{
    static readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["models/npc/orc/npc_orc.qct"] = Text("""
            $keyvalues
            {
             "entity_data" { "strength" "1" // Npc' strength.
               "level_1" { "strength" "4" }
               "hitboxes_modifier" { "1" "3" "2" "1.0" } }
            "combatmovedatas"
            {
            attack_Right { "HitPow" "1" "breakparade" "0" "AttackMove" "1" "range" "70" }
            attack_Bash { "HitPow" "1.5" "forcebreakparade" "1" }
            attack_Fast { "event0" { "HitPow" "0.4" "breakparade" "0" } "event1" { "HitPow" "0.8" "breakparade" "0" } }
            attack_KickBash { "event0" { "HitPow" "0.5" "breakparade" "1" "AttackMove" "4" "range" "60" } "event1" { "HitPow" "1.5" "breakparade" "0" } }
            layer_attack_right { "HitPow" "1" }
            }
            """),
        ["models/npc/ghoul/npc_ghoul.qct"] = Text("""
            $keyvalues { "entity_data" { "strength" "11" // "NWDmg" "5"
            } }
            "combatmovedatas"
            {
            attack_bash { "HitPow" "1" "breakparade" "1" }
            attack_forward { "HitPow" "1" "breakparade" "0" }
            }
            """),
        ["models/npc/villager_male/npc_villager_male.qct"] = Text("""
            $keyvalues { "entity_data" { "strength" "6" "hitboxes_modifier" { "1" "3.0" "2" "0.5" "5" "1.25" } } }
            """),
        ["models/items/weapons/sword_orc/sword_orc.qct"] = Text("""
            $keyvalues { "entity_data" { "WDmg" "7" // To be added to damage done.
            } }
            """),
        ["scripts/mm_game_settings.txt"] = Text("""
            Game
            {
             "Combat" { "mul_damages_Npc_Not_alerted" "3" // VAlue that multiply damages done by player when npc is not alerted
                "mul_damages_Npc_Not_alerted_level" "0" "player_damage_multiplier" "1.0" }
            }
            """),
        ["models/items/weapons/sword_short/sword_short.qct"] = Text("""
            $keyvalues { "entity_data" { "WDmg" "2" } }
            """),
        ["models/npc/player/first_basic.qct"] = Text("""
            $keyvalues { }
            "combatmovedatas"
            {
            layer_fastattack_sword_noshield_A { "Event0" { "HitPow" "0.7" "breakparade" "0" } }
            layer_bigslash_sword_noshield_out { "HitPow" "1.5" "breakparade" "1" }
            }
            """),
        ["models/npc/orc/npc_orc.mdl"] = Mdl(("attack_Fast", [(0.02f, 2192), (0.34f, 2102), (0.46f, 2174), (0.54f, 2139)]),
            ("attack_KickBash", [(0.18f, 2102), (0.64f, 2139)]), ("attack_Bash", [(0.43f, 2102)])),
        ["models/npc/ghoul/npc_ghoul.mdl"] = Mdl(("attack_forward", [(0.24f, 2102), (0.56f, 2139)]), ("attack_bash", [(0.37f, 2102)])),
    };

    static byte[] Text(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    /// <summary>A version-44 studio header with sequences and their events (80-byte records, cycle and id).</summary>
    static byte[] Mdl(params (string Name, (float Cycle, int Event)[] Events)[] sequences)
    {
        const int seqAt = 400, stride = 212;
        var b = new List<byte>(new byte[seqAt + sequences.Length * stride]);
        void Put(int at, int v) { var x = BitConverter.GetBytes(v); for (int i = 0; i < 4; i++) b[at + i] = x[i]; }
        "IDST"u8.ToArray().Select((c, i) => b[i] = c).ToList();
        Put(4, 44); Put(188, sequences.Length); Put(192, seqAt);
        for (int i = 0; i < sequences.Length; i++)
        {
            int desc = seqAt + i * stride;
            Put(desc + 4, b.Count - desc);
            b.AddRange(System.Text.Encoding.ASCII.GetBytes(sequences[i].Name + "\0"));
            Put(desc + 24, sequences[i].Events.Length);
            Put(desc + 28, b.Count - desc);
            foreach (var (cycle, id) in sequences[i].Events)
            {
                var e = new byte[80];
                BitConverter.GetBytes(cycle).CopyTo(e, 0);
                BitConverter.GetBytes(id).CopyTo(e, 4);
                b.AddRange(e);
            }
        }
        return b.ToArray();
    }

    public static CombatModel Model() => new(new CombatData(path => Files.GetValueOrDefault(path)));
}

public sealed class ModelTests
{
    const string OrcModel = "models/npc/orc/npc_orc.mdl", GhoulModel = "models/npc/ghoul/npc_ghoul.mdl";
    const string Cleaver = "models/items/weapons/sword_orc/sword_orc.mdl", ShortSword = "models/items/weapons/sword_short/sword_short.mdl";
    static readonly CombatModel Model = GameData.Model();

    static Party Orc(string sequence = "idle", float cycle = 0, params string[] layers) => new("npc_orc_sword", sequence, layers, cycle, "#7", OrcModel);
    static Party Ghoul(string sequence, float cycle) => new("npc_ghoul", sequence, [], cycle, "#8", GhoulModel);
    static Party Victim(string cls, string model = "") => new(cls, "idle", [], 0, "#9", model);
    static readonly Party Villager = Victim("npc_villager_male", "models/npc/villager_male/npc_villager_male.mdl");
    static readonly Party Player = new("player", "idle_leftfoot", ["layer_bigslash_sword_noshield_in", "layer_fastattack_sword_noshield_A"], 0, "#1", "models/npc/player/player_basic.mdl");

    static DefenseCase Blow(Struck struck, Party victim, float front = 1) =>
        new(1, victim, Player, "weapon_arx_short_sword", struck, front, DefenseResult.Hit);

    [Fact]
    public void WhatTheBlowStruckDecidesFirst()
    {
        Assert.Equal(DefenseResult.Parry, CombatModel.Verdict(Blow(Struck.Weapon, Orc())));
        Assert.Equal(DefenseResult.Shield, CombatModel.Verdict(Blow(Struck.OffHand, Orc())));
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(Blow(Struck.Body, Orc("Combatmove_left_small"))));
    }

    [Fact]
    public void OnTheBodyTheDefendersStanceDecides()
    {
        Assert.Equal(DefenseResult.Parry, CombatModel.Verdict(Blow(Struck.Body, Orc("idle", 0, "layer_parry_B"), front: -0.8f)));
        Assert.Equal(DefenseResult.Shield, CombatModel.Verdict(Blow(Struck.Body, Orc("idle", 0, "layer_block"), front: 0.9f)));
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(Blow(Struck.Body, Orc("idle", 0, "layer_block"), front: -0.9f)));
        // A broken parry and a counter-attack are not defending.
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(Blow(Struck.Body, Orc("Parry_Break"))));
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(Blow(Struck.Body, Orc("Block_attack"))));
    }

    [Fact]
    public void FromWireV11TheStreamedDefenseStateDecides()
    {
        DefenseCase With(Struck struck, uint state, bool noEffect = false, float front = 1) =>
            Blow(struck, Orc("Combatmove_left_small"), front) with { State = state, NoEffect = noEffect };
        // The state word, not the stance: parrying while moving, and a shield up only in front.
        Assert.Equal(DefenseResult.Parry, CombatModel.Verdict(With(Struck.Body, 0x200006)));
        Assert.Equal(DefenseResult.Shield, CombatModel.Verdict(With(Struck.Body, 0x10, front: 0.9f)));
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(With(Struck.Body, 0x10, front: -0.9f)));
        Assert.Equal(DefenseResult.Hit, CombatModel.Verdict(Blow(Struck.Body, Orc("idle", 0, "layer_parry_B")) with { State = 0x800000 }));
        // The no-effect byte comes first, whatever the blow struck.
        Assert.Equal(DefenseResult.NoEffect, CombatModel.Verdict(With(Struck.OffHand, 0x200012, noEffect: true)));
    }

    [Fact]
    public void ThePlayersCriticalsAndBlowsOnUnalertedNpcsMultiply()
    {
        const string sword = "weapon_arx_short_sword";
        var plain = Hit(Player, sword, ShortSword, Orc(), 1);
        Assert.Equal(4.2f, Model.Damage(plain)!.Value, 4);
        // A critical doubles, by the streamed byte or the damage type.
        Assert.Equal(8.4f, Model.Damage(plain with { AttackerCritical = true })!.Value, 4);
        Assert.Equal(8.4f, Model.Damage(plain with { DamageType = 0x10004 })!.Value, 4);
        // An NPC's critical byte doesn't double its blow.
        Assert.Equal(6f, Model.Damage(Hit(Ghoul("attack_bash", 0.38f), "-", "", Villager, 2) with { AttackerCritical = true }));
        // Not alerted (alert level 0, at or below the setting's level): ×3; alerted, or in state 3: not.
        Assert.Equal(12.6f, Model.Damage(plain with { VictimAlert = 0 })!.Value, 4);
        Assert.Equal(4.2f, Model.Damage(plain with { VictimAlert = 4 })!.Value, 4);
        Assert.Equal(4.2f, Model.Damage(plain with { VictimAlert = 0, VictimNpcState = 3 })!.Value, 4);
        Assert.Equal(new GameSettings(3, 0, 1), Model.Data.Settings);
        // A kick (damage type 0x40000) asks the settings' kick_damage, whatever struck.
        Assert.Equal(1f, Model.Damage(Hit(Player, "-", "", Orc(), null) with { DamageType = 0x40000, WeaponFlags = 0x20 }));
    }

    [Fact]
    public void ThePartThatStruckDecidesTheParryBreak()
    {
        DefenseResult Parry(Party attacker) => Model.PlayerParry(new ParryCase(1, attacker, DefenseResult.None));
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry(Orc("attack_Bash", 0.43f)));          // forcebreakparade
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry(Orc("attack_KickBash", 0.23f)));      // the kick part
        Assert.Equal(DefenseResult.PlayerParried, Parry(Orc("attack_KickBash", 0.69f)));          // the bash part
        Assert.Equal(DefenseResult.PlayerParried, Parry(Orc("attack_Right", 0.5f)));
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry(Ghoul("attack_bash", 0.38f)));
    }

    static DamageCase Hit(Party attacker, string weapon, string weaponModel, Party victim, int? hitGroup, bool defended = false, float before = 35, float requested = 0, float after = 35) =>
        new(1, victim, attacker, weapon, hitGroup, 4, 0, requested, before, after, defended, 0, weaponModel);

    [Fact]
    public void DamageIsStrengthAndWeaponTimesThePartTimesTheVictimsHitGroup()
    {
        const string cleaver = "weapon_arxorccleaver";
        // Orc: strength 1 + WDmg 7.
        Assert.Equal(8f, Model.Damage(Hit(Orc("attack_Right", 0.5f), cleaver, Cleaver, Victim("npc_ghoul"), 2)));
        Assert.Equal(12f, Model.Damage(Hit(Orc("attack_Bash", 0.43f), cleaver, Cleaver, Victim("player"), 0)));
        Assert.Equal(3.2f, Model.Damage(Hit(Orc("attack_Fast", 0.35f), cleaver, Cleaver, Victim("player"), 0))!.Value, 4);
        Assert.Equal(6.4f, Model.Damage(Hit(Orc("attack_Fast", 0.56f), cleaver, Cleaver, Victim("player"), 0))!.Value, 4);
        Assert.Equal(12f, Model.Damage(Hit(Orc("attack_KickBash", 0.69f), cleaver, Cleaver, Victim("npc_orc_sword"), 7)));
        // The victim's hitboxes_modifier: the head ×3 against an orc, the chest ×0.5 against a villager.
        Assert.Equal(24f, Model.Damage(Hit(Orc("attack_Right", 0.5f), cleaver, Cleaver, Orc(), 1)));
        // Ghoul: strength 11, unarmed 1.
        Assert.Equal(6f, Model.Damage(Hit(Ghoul("attack_bash", 0.38f), "-", "", Villager, 2)));
        Assert.Equal(15f, Model.Damage(Hit(Ghoul("attack_bash", 0.38f), "-", "", Villager, 5)));
        // Its lunge strikes twice in the animation but has one part in the table: the second asks nothing.
        Assert.Equal(12f, Model.Damage(Hit(Ghoul("attack_forward", 0.27f), "-", "", Victim("player"), 0)));
        Assert.Equal(0f, Model.Damage(Hit(Ghoul("attack_forward", 0.59f), "-", "", Victim("player"), 0)));
        // The player: the short sword's WDmg 2 × the fast attack's 0.7, ×3 on a head.
        Assert.Equal(4.2f, Model.Damage(Hit(Player, "weapon_arx_short_sword", ShortSword, Orc(), 1))!.Value, 4);
        // Struck on the move before Wire v6: the attacker's layer attack.
        Assert.Equal(8f, Model.Damage(Hit(Orc("Combatmove_forward_long", 0.24f), cleaver, Cleaver, Victim("npc_ghoul"), 5)));
        Assert.Equal(0f, Model.Damage(Hit(Orc("attack_Right", 0.5f), cleaver, Cleaver, Victim("player"), 0, defended: true)));
        Assert.Null(Model.Damage(Hit(new Party("npc_goblin", "attack_right", [], 0.5f, "#3", "models/npc/goblin/npc_goblin.mdl"), "-", "", Victim("player"), 0)));
    }

    [Fact]
    public void ARecentAttackStandsInForAStaleSequence()
    {
        var moved = Orc("Combatmove_left_small", 0.1f, "layer_sword_idle") with { LastAttack = "attack_Bash" };
        Assert.Equal(12f, Model.Damage(Hit(moved, "weapon_arxorccleaver", Cleaver, Victim("player"), 0)));
    }

    [Fact]
    public void BlowsBetweenNpcsOfOneClassApplyATenth()
    {
        var friendly = Hit(Ghoul("attack_fast", 0.58f), "-", "", Victim("npc_ghoul"), 2, before: 35, requested: 12, after: 33.8f);
        Assert.Equal(33.8f, CombatModel.HealthAfter(friendly), 4);
        var enemy = Hit(Orc("attack_Right", 0.5f), "weapon_arxorccleaver", Cleaver, Victim("npc_ghoul"), 2, before: 35, requested: 8, after: 27);
        Assert.Equal(27f, CombatModel.HealthAfter(enemy));
    }

    [Fact]
    public void QctFilesParseWithBareKeysCommentsAndAMissingBrace()
    {
        var orc = Model.Data.Character(OrcModel)!;
        Assert.Equal(1f, orc.Strength);                       // the block's own, not level_1's
        Assert.Equal(3f, orc.HitGroup(1));
        Assert.Equal([0.4f, 0.8f], orc.Attack("attack_fast")!.Events.Select(e => e.HitPow));
        Assert.Equal(7f, Model.Data.Weapon(Cleaver)!.Damage);
        Assert.Equal(1, Model.Data.Part(OrcModel, "attack_Fast", 0.56f));
        Assert.Equal(0, Model.Data.Part(OrcModel, "attack_Fast", 0.2f));
        // Each event its own range and move; a block without one takes the move default.
        Assert.Equal([60f, 120f], orc.Attack("attack_KickBash")!.Events.Select(e => e.Range));
        Assert.Equal((70f, 1), (orc.Attack("attack_Right")!.Events[0].Range, orc.Attack("attack_Right")!.Events[0].Move));
        Assert.Equal(4, orc.Attack("attack_KickBash")!.Events[0].Move);
        Assert.Equal(CharacterData.DefaultBaseAttackLength, Model.Data.Character(GhoulModel)!.BaseAttackLength);
    }

    static ReachCase Reach(string model, string attack, int part, Vector2 toVictim, bool armed, Vector2? facing = null) =>
        new(1, model == OrcModel ? "npc_orc_sword" : "npc_ghoul", model, attack, part, "player", toVictim, facing ?? Vector2.UnitX, Reaches.PlayerHalfWidth, false, Armed: armed);

    /// <summary>
    /// The sweep runs from the attacker toward the victim for the part's range (an armed attacker) or its
    /// baseattacklength (an unarmed one), and lands where it enters the player's box grown by the sweep's 1.5.
    /// </summary>
    [Fact]
    public void AnAttackReachesWhenItsSweepEntersTheVictimsBox()
    {
        Assert.True(Model.Reach(Reach(OrcModel, "attack_Right", 0, new(87, 0), armed: true)).Lands);       // enters at 69.5 of 70
        Assert.False(Model.Reach(Reach(OrcModel, "attack_Right", 0, new(88, 0), armed: true)).Lands);
        Assert.False(Model.Reach(Reach(OrcModel, "attack_KickBash", 0, new(80, 0), armed: true)).Lands);    // the kick part reaches 60
        Assert.True(Model.Reach(Reach(OrcModel, "attack_KickBash", 1, new(80, 0), armed: true)).Lands);     // the bash part 120
        Assert.True(Model.Reach(Reach(GhoulModel, "attack_bash", 0, new(67, 0), armed: false)).Lands);      // 49.5 of 50
        Assert.False(Model.Reach(Reach(GhoulModel, "attack_bash", 0, new(68, 0), armed: false)).Lands);
        // The box is world-aligned: on a diagonal the sweep meets its corner, 17.5 × √2 short of the centre.
        var diagonal = Model.Reach(Reach(GhoulModel, "attack_bash", 0, Vector2.Normalize(new(1, 1)) * 70, armed: false, Vector2.Normalize(new(1, 1))));
        Assert.Equal(70 - 17.5f * MathF.Sqrt(2), diagonal.Entry, 2);
        // A turn strike sweeps at each think of its window; one that ends turned away struck at someone else.
        var toward = Reach(OrcModel, "attack_Right", 0, new(90, 0), armed: true) with { Sweeps = [(1, new(90, 0), Vector2.UnitX), (9, new(80, 0), Vector2.UnitX)] };
        Assert.True(Model.Reach(toward).Lands);                                                               // the second think enters at 62.5
        var away = toward with { Sweeps = [(1, new(80, 0), Vector2.UnitX), (9, new(80, 0), -Vector2.UnitX)] };
        Assert.False(Model.Reach(away).Lands);
        // Outside the aim cone (cos 0.3) the sweep follows the facing and passes the victim.
        var behind = Model.Reach(Reach(OrcModel, "attack_Right", 0, new(0, 40), armed: true));
        Assert.False(behind.Lands);
        Assert.False(behind.FacingLands);
    }
}

public sealed class BlowsTests
{
    const ushort Player = 1, Orc = 7, Cleaver = 9, Sword = 11;

    static RecordedFrame Frame(int tick, string[]? orcLayers = null)
    {
        var f = new RecordedFrame { Version = 6, Tick = tick, LevelSerial = 1 };
        f.Classes[0] = "player"; f.Classes[1] = "npc_orc_sword"; f.Classes[2] = "weapon_arxorccleaver"; f.Classes[3] = "weapon_arx_short_sword";
        f.Models[5] = "models/npc/orc/npc_orc.mdl";
        f.Models[6] = "models/items/weapons/sword_short/sword_short.mdl";
        f.Sequences[5] = ["idle", "layer_parry_B", "Combatmove_left_small"];
        f.Entities[Player] = new WireEntity { Index = Player, ClassId = 0, Serial = 1 };
        f.Entities[Orc] = new WireEntity { Index = Orc, ClassId = 1, ModelIndex = 5, Serial = 2, Sequence = 2, Flags = WireEntityFlags.HasAnimation };
        f.Entities[Cleaver] = new WireEntity { Index = Cleaver, ClassId = 2, Serial = 3 };
        f.Entities[Sword] = new WireEntity { Index = Sword, ClassId = 3, ModelIndex = 6, Serial = 4 };
        foreach (var _ in orcLayers ?? []) f.Layers.Add(new WireLayer { Index = Orc, Sequence = 1, Weight = 0.9f });
        return f;
    }

    static WireDefense Check(int tick, DefenseResult result, ushort struck, uint struckSerial) => new()
    {
        Tick = tick, Victim = Orc, VictimSerial = 2, Attacker = Player, AttackerSerial = 1, Weapon = WireLayout.NoEntity,
        Struck = struck, StruckSerial = struckSerial, Result = result,
    };

    static WireDamage Damage(int tick, float amount, float before) => new()
    {
        Tick = tick, Victim = Orc, VictimSerial = 2, Attacker = Player, AttackerSerial = 1, Weapon = Sword, WeaponSerial = 4,
        Inflictor = WireLayout.NoEntity, TraceEntity = WireLayout.NoEntity, Damage = amount, HealthBefore = before, HealthAfter = before - amount,
        DamageType = 4, TraceValid = 1, HitGroup = 5,
    };

    [Fact]
    public void TheDefendersStanceIsTheOneBeforeTheBlow()
    {
        // The parry layer arrives in the same frame as the verdict: it's the reaction, so the case mustn't see it.
        var first = Frame(10);
        var second = Frame(11, ["layer_parry_B"]);
        second.Defense.Add(Check(11, DefenseResult.Parry, Orc, 2));
        var cases = Blows.Read("synthetic", [first, second]);
        var c = Assert.Single(cases.Defense);
        Assert.Empty(c.Victim.Layers);
        Assert.Equal(Struck.Body, c.Struck);
        Assert.Equal("Combatmove_left_small", c.Victim.Sequence);
    }

    [Fact]
    public void ChecksAndDamagePairInOrderWithinATick()
    {
        // One swing: a hit on the body (1.4), then a parry on the cleaver (0 requested).
        var f = Frame(20);
        f.Defense.Add(Check(20, DefenseResult.Hit, Orc, 2));
        f.Defense.Add(Check(20, DefenseResult.Parry, Cleaver, 3));
        f.Damage.Add(Damage(20, 1.4f, 13.6f));
        f.Damage.Add(Damage(20, 0f, 12.2f));
        var cases = Blows.Read("synthetic", [f]);
        Assert.Equal([false, true], cases.Damage.Select(d => d.Defended));
        Assert.Equal([Struck.Body, Struck.Weapon], cases.Defense.Select(d => d.Struck));
        Assert.All(Scorer.Score(cases, GameData.Model()).Scores, s => Assert.Equal(s.Cases - s.Unmodelled, s.Agree));
    }
}

public sealed class ReportTests
{
    [Fact]
    public void TheReportListsEveryMismatchUnlessBrief()
    {
        // Ten parries on the body with no stance: one group of ten mismatches.
        var victim = new Party("npc_orc_sword", "Combatmove_left_small", [], 0, "#7", "models/npc/orc/npc_orc.mdl");
        var attacker = new Party("player", "idle", [], 0, "#1");
        var cases = Enumerable.Range(0, 10).Select(i => new DefenseCase(100 + i, victim, attacker, "weapon_arx_short_sword", Struck.Body, 1, DefenseResult.Parry)).ToList();
        var report = Scorer.Score(new FightCases("synthetic", 6, cases, [], []), GameData.Model());
        Assert.Equal(10, report.Mismatches.Count);
        string full = report.Format();
        Assert.All(Enumerable.Range(0, 10), i => Assert.Contains($"tick {100 + i}:", full));
        Assert.DoesNotContain("more", full);
        string brief = report.Format(brief: true);
        Assert.Contains("… 2 more", brief);
        Assert.DoesNotContain("tick 109:", brief);
    }
}

public sealed class ContactTests
{
    const string Villager = "#9.1";
    static readonly ContactPart Spine = new(Struck.Body, 2, 0, Villager), Thigh = new(Struck.Body, 6, 12, Villager),
        Shield = new(Struck.OffHand, 0, 0, "#10.1", "weapon_mm_shield_orc");

    static ContactCase Case(ContactPart donor, ContactPart? before, ContactPart? after = null, float? donorAlong = null, float miss = 0, bool traced = true) =>
        new(100, "npc_ghoul", "npc_villager_male", "attack_bash", donor, before, after, donorAlong, miss, 5, 30, traced, 18);

    static IReadOnlyList<Mismatch> Mismatches(params ContactCase[] cases) =>
        Scorer.Score(new FightCases("synthetic", 6, [], [], []), GameData.Model(), cases).Mismatches;

    [Fact]
    public void AContactAgreesOnTheKindAndOnTheBodyTheHitGroup()
    {
        Assert.True(Case(Spine, new ContactPart(Struck.Body, 2, 1, Villager)).Agrees);
        Assert.False(Case(Spine, Thigh).Agrees);
        Assert.False(Case(Spine, Shield).Agrees);
        Assert.True(Case(Shield, Shield with { }).Agrees);
        // The same kind of thing on another entity is another contact: a second orc's shield, or a bystander's spine.
        Assert.False(Case(Shield, Shield with { Entity = "#11.1" }).Agrees);
        Assert.False(Case(Spine, Spine with { Entity = "#12.1" }).Agrees);
        // A blow the donor didn't trace only has to meet the victim's body, and no one else's.
        var reach = new ContactPart(Struck.Body, 0, 0, Villager);
        Assert.True(Case(reach, Thigh, traced: false).Agrees);
        Assert.False(Case(reach, Thigh with { Entity = "#12.1" }, traced: false).Agrees);
        Assert.False(Case(reach, null, traced: false).Agrees);
    }

    [Fact]
    public void EachMismatchSaysWhetherPoseTimingSwingPathOrShapeFit()
    {
        var report = Scorer.Score(new FightCases("synthetic", 6, [], [], []), GameData.Model(),
            [Case(Spine, Spine), Case(Spine, Thigh), Case(Spine, Thigh, traced: false)]);
        Assert.Equal(["contact", "contact, untraced"], report.Scores.Select(s => s.Decision));
        Assert.Equal((2, 1), (report.Scores[0].Cases, report.Scores[0].Agree));

        Assert.StartsWith("pose timing: posed as the blow's own frame", Mismatches(Case(Spine, Thigh, after: Spine))[0].Reason);
        Assert.StartsWith("pose timing: the donor's hit point is off", Mismatches(Case(Spine, null, miss: 6))[0].Reason);
        Assert.StartsWith("swing path: the donor's part is where its trace ended", Mismatches(Case(Spine, Shield, donorAlong: 28))[0].Reason);
        Assert.StartsWith("shape fit", Mismatches(Case(Spine, null, miss: 0.4f))[0].Reason);
        // The blow's own frame meeting another orc's shield isn't the donor's shield.
        Assert.StartsWith("shape fit", Mismatches(Case(Shield, null, after: Shield with { Entity = "#11.1" }, miss: 0.4f))[0].Reason);
        Assert.Contains("of weapon_mm_shield_orc #11.1", Mismatches(Case(Shield, Shield with { Entity = "#11.1" }, miss: 0.4f))[0].Model);
        Assert.StartsWith("swing path: the donor reached without a trace", Mismatches(Case(Spine, null, traced: false))[0].Reason);
    }
}

/// <summary>
/// The recorded fights, when local/recordings has them (derived from the
/// game, never committed: docs/recording.md says how to record one).
/// </summary>
public sealed class RecordedFightTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static CombatModel CombatModel() => new(CombatData.FromVpks(Vpks()!));

    static string? Vpks()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                string path = Path.Combine(dir.FullName, "local", "donor", "vpks");
                return Directory.Exists(path) ? path : null;
            }
        return null;
    }

    static string? Recording(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                string path = Path.Combine(dir.FullName, "local", "recordings", name);
                return File.Exists(path) ? path : null;
            }
        return null;
    }

    [Theory]
    [InlineData("l01_a-parrybot.rbrec")]
    [InlineData("l01_a-layers.rbrec")]
    [InlineData("l01_a-v12-arena-ghoul.rbrec")]
    [InlineData("l01_a-orcs-vs-guards-damage.rbrec")]
    [InlineData("l01_a-v13-arena-pose.rbrec")]
    public void EveryMismatchHasAReason(string name)
    {
        if (Recording(name) is not string path || Vpks() is null) { output.WriteLine($"no local/recordings/{name} or local/donor/vpks; skipped"); return; }
        var model = CombatModel();
        FightCases fight;
        List<ReachCase> reaches;
        using (var stream = File.OpenRead(path)) fight = Blows.Read(name, stream);
        using (var stream = File.OpenRead(path)) reaches = Reaches.Read(RecordingReader.Frames(stream), model.Data);
        var report = Scorer.Score(fight, model, reaches: reaches);
        output.WriteLine(report.Format());
        Assert.DoesNotContain(report.Mismatches, m => m.Reason.StartsWith("unexplained", StringComparison.Ordinal));
        Assert.NotEmpty(report.Scores);
        Assert.All(report.Scores.Where(s => s.Decision == "reach" && s.Cases >= 20), s => Assert.True(s.Agree >= s.Cases * 9 / 10, $"reach {s.Agree} of {s.Cases}"));
    }

    /// <summary>
    /// Every untraced blow on the player is a reach case: an attack part, a turn strike's window (events 2175 to 2176),
    /// or the blow itself when the attack isn't on the Wire. The ticks are the turn strikes and the v4 overlay attack
    /// review R10099-1 found missing.
    /// </summary>
    [Theory]
    [InlineData("l01_a-parrybot.rbrec", new[] { 11056, 16034, 17074, 11414 })]
    [InlineData("l01_a-layers.rbrec", new[] { 12853, 13029, 16351, 17553 })]
    public void EveryUntracedBlowOnThePlayerIsAReachCase(string name, int[] named)
    {
        if (Recording(name) is not string path || Vpks() is null) { output.WriteLine($"no local/recordings/{name} or local/donor/vpks; skipped"); return; }
        var model = CombatModel();
        List<ReachCase> cases;
        using (var stream = File.OpenRead(path)) cases = Reaches.Read(RecordingReader.Frames(stream), model.Data);
        var blows = new List<(int Tick, string Attacker)>();
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
                foreach (var d in f.Damage)
                    if ((d.DamageType & 0x84) != 0 && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) == "player"
                        && f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId, "").StartsWith("npc_", StringComparison.Ordinal))
                        blows.Add((d.Tick, f.Classes[a.ClassId]));
        bool Covered(int tick, string attacker) => cases.Any(c => c.Victim == "player" && c.Attacker == attacker && c.Landed
            && tick >= c.Span.First - 12 && tick <= c.Span.Last + 12);
        Assert.NotEmpty(blows);
        Assert.All(blows, b => Assert.True(Covered(b.Tick, b.Attacker), $"blow at tick {b.Tick} by {b.Attacker} has no reach case"));
        Assert.All(named, t => Assert.Contains(blows, b => b.Tick == t));
        Assert.Contains(cases, c => c.Sweeps is { Count: > 1 });
    }

    /// <summary>
    /// Wire v11 sessions carry the defenders' state, their no-effect byte, the
    /// player's critical byte and the victims' alert levels: the model agrees
    /// with every verdict and every damage request.
    /// </summary>
    [Theory]
    [InlineData("l01_a-v11-damage.rbrec")]
    [InlineData("l01_a-v11-crit.rbrec")]
    public void WithTheV11InputsEveryDecisionAgrees(string name)
    {
        if (Recording(name) is not string path || Vpks() is null) { output.WriteLine($"no local/recordings/{name} or local/donor/vpks; skipped"); return; }
        using var stream = File.OpenRead(path);
        var report = Scorer.Score(Blows.Read(name, stream), CombatModel());
        output.WriteLine(report.Format());
        Assert.All(report.Scores.Where(s => s.Decision is "NPC verdict" or "damage requested"), s => Assert.Equal(s.Cases, s.Agree));
    }
}
