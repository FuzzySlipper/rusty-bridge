using DarkMessiah.Combat;
using RustyBridge.Wire;
using Xunit;

namespace DarkMessiah.Combat.Tests;

public sealed class ModelTests
{
    static Party Orc(string sequence = "idle", float cycle = 0, params string[] layers) => new("npc_orc_sword", sequence, layers, cycle);
    static readonly Party Player = new("player", "idle_leftfoot", [], 0);

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
    public void BashesAndKicksBreakThePlayersParry()
    {
        DefenseResult Parry(string sequence, float cycle) => CombatModel.PlayerParry(new ParryCase(1, Orc(sequence, cycle), DefenseResult.None));
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry("attack_Bash", 0.43f));
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry("attack_bash", 0.38f));
        Assert.Equal(DefenseResult.PlayerParryBroken, Parry("attack_KickSwing", 0.37f));
        Assert.Equal(DefenseResult.PlayerParried, Parry("attack_KickSwing", 0.61f));
        Assert.Equal(DefenseResult.PlayerParried, Parry("attack_right", 0.55f));
    }

    static DamageCase Hit(Party attacker, string weapon, string victim, int? hitGroup, bool defended = false, float before = 35, float requested = 0, float after = 35) =>
        new(1, new Party(victim, "idle", [], 0), attacker, weapon, hitGroup, 4, 0, requested, before, after, defended);

    [Fact]
    public void DamageIsBaseTimesAttackTimesHitGroup()
    {
        const string cleaver = "weapon_arxorccleaver";
        Assert.Equal(8f, CombatModel.Damage(Hit(Orc("attack_right", 0.5f), cleaver, "npc_ghoul", 2)));
        Assert.Equal(12f, CombatModel.Damage(Hit(Orc("attack_Bash", 0.43f), cleaver, "player", 0)));
        Assert.Equal(36f, CombatModel.Damage(Hit(Orc("attack_Forward", 0.45f), cleaver, "npc_goblin", 1)));
        Assert.Equal(8f, CombatModel.Damage(Hit(Orc("attack_KickSwing", 0.37f), cleaver, "npc_ghoul", 2)));
        Assert.Equal(12f, CombatModel.Damage(Hit(Orc("attack_KickSwing", 0.61f), cleaver, "npc_ghoul", 3)));
        Assert.Equal(10f, CombatModel.Damage(Hit(Orc("attack_down_1", 0.52f), cleaver, "npc_goblin", 6)));
        Assert.Equal(6f, CombatModel.Damage(Hit(new Party("npc_ghoul", "attack_bash", [], 0.38f), "-", "npc_villager_male", 2)));
        Assert.Equal(0f, CombatModel.Damage(Hit(new Party("npc_ghoul", "attack_forward", [], 0.59f), "-", "player", 0)));
        Assert.Equal(4.2f, CombatModel.Damage(Hit(Player, "weapon_arx_short_sword", "npc_orc_sword", 1))!.Value, 4);
        Assert.Equal(0f, CombatModel.Damage(Hit(Orc("attack_right", 0.5f), cleaver, "player", 0, defended: true)));
        Assert.Null(CombatModel.Damage(Hit(new Party("npc_goblin", "attack_right", [], 0.5f), "-", "player", 0)));
    }

    [Fact]
    public void BlowsBetweenNpcsOfOneClassApplyATenth()
    {
        var friendly = Hit(new Party("npc_ghoul", "attack_fast", [], 0.58f), "-", "npc_ghoul", 2, before: 35, requested: 12, after: 33.8f);
        Assert.Equal(33.8f, CombatModel.HealthAfter(friendly), 4);
        var enemy = Hit(Orc("attack_right", 0.5f), "weapon_arxorccleaver", "npc_ghoul", 2, before: 35, requested: 8, after: 27);
        Assert.Equal(27f, CombatModel.HealthAfter(enemy));
    }
}

public sealed class BlowsTests
{
    const ushort Player = 1, Orc = 7, Cleaver = 9;

    static RecordedFrame Frame(int tick, string[]? orcLayers = null)
    {
        var f = new RecordedFrame { Version = 6, Tick = tick, LevelSerial = 1 };
        f.Classes[0] = "player"; f.Classes[1] = "npc_orc_sword"; f.Classes[2] = "weapon_arxorccleaver";
        f.Models[5] = "models/npc/orc/npc_orc.mdl";
        f.Sequences[5] = ["idle", "layer_parry_B", "Combatmove_left_small"];
        f.Entities[Player] = new WireEntity { Index = Player, ClassId = 0, Serial = 1 };
        f.Entities[Orc] = new WireEntity { Index = Orc, ClassId = 1, ModelIndex = 5, Serial = 2, Sequence = 2, Flags = WireEntityFlags.HasAnimation };
        f.Entities[Cleaver] = new WireEntity { Index = Cleaver, ClassId = 2, Serial = 3 };
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
        Tick = tick, Victim = Orc, VictimSerial = 2, Attacker = Player, AttackerSerial = 1, Weapon = WireLayout.NoEntity,
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
        Assert.All(Scorer.Score(cases).Scores, s => Assert.Equal(s.Cases - s.Unmodelled, s.Agree));
    }
}

/// <summary>
/// The recorded fights, when local/recordings has them (derived from the
/// game, never committed: docs/recording.md says how to record one).
/// </summary>
public sealed class RecordedFightTests(Xunit.Abstractions.ITestOutputHelper output)
{
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
    [InlineData("l01_a-orcs-vs-guards-damage.rbrec")]
    public void EveryMismatchHasAReason(string name)
    {
        if (Recording(name) is not string path) { output.WriteLine($"no local/recordings/{name}; skipped"); return; }
        using var stream = File.OpenRead(path);
        var report = Scorer.Score(Blows.Read(name, stream));
        output.WriteLine(report.Format());
        Assert.DoesNotContain(report.Mismatches, m => m.Reason == "unexplained");
        Assert.NotEmpty(report.Scores);
    }
}
