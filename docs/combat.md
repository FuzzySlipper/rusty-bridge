# Combat decisions in Shadow

`combat/` holds a C# model of Dark Messiah's melee decisions (design
"Phase 2: Combat decisions"), and a scorer that runs the model over a
recording's captured inputs and reports agreement blow by blow. Every blow
in a recording is a test case. The model reads only what the tap captured;
recomputing contacts from geometry is next, now that rusty-engine #10056
(swept posed shapes with per-part contacts) is in the pinned pair.

```
dotnet run --project combat/DarkMessiah.Combat.Tool -- score local/recordings/l01_a-parrybot.rbrec [more.rbrec ...]
```

It prints per-decision agreement, then every mismatch grouped by its
reason. It exits 1 when any mismatch is "unexplained", so a new gap stands
out. `dotnet test combat/DarkMessiah.Combat.slnx` covers:
- the rules;
- the case reader, on in-memory frames;
- the two recorded fights named below, when `local/recordings` has them.

## The decisions

| Decision | Inputs | Donor output |
| --- | --- | --- |
| NPC verdict | What the blow struck, the defender's stance before the blow, the facing | defense record result (1, 2, 4, 8) |
| Player parry | The attacker's sequence and cycle | defense record result 17 or 18 |
| Damage requested | Attacker, weapon, attack sequence and cycle, hit group, victim class, the verdict | damage record `damage` |
| Damage applied | The request, attacker and victim classes | health before and after |

`Blows` builds the cases.
- **Identity:** an event's entities are taken as last seen with their
  identity, so a victim removed in the tick it died keeps its class.
- **Old spawner bug:** the template-maker swap of taps before 2026-10-17 is
  undone, as the viewer does.
- **Pairing:** a defense check and the damage call after it pair in order
  per tick, defender and attacker. One swing can touch a defender twice in
  a tick, on the body and then on its weapon, and only the second is
  defended.
- **The defender's stance comes from the frame before the blow.** The frame
  a verdict arrives in already shows the reaction: an orc's
  `layer_parry_B..E` start at cycle 0 in the verdict's own tick. Reading
  that frame scored 9 parries "right" by reading the answer.

`CombatModel` holds the rules, each with its evidence:

- **NPC verdict:**
  - A blow that strikes the defender's weapon is a parry; its off-hand item
    (shield), a shield block.
  - On the body, a parry stance parries, and a raised shield blocks blows
    from in front.
  - Otherwise the blow is a hit.
  - This follows `CNPC_BaseArx`'s defense virtual (docs/wire.md "The
    defense hook"). The defense-state bits it reads aren't streamed, so the
    stance stands in for them.
- **Player parry:** a bash, or the kick half (cycle < 0.5) of a
  kick-and-swing, breaks the parry; anything else is parried.
  - The donor's rule is weapon flag 0x10 (docs/wire.md "The player's
    parry"). That flag isn't streamed, and those attacks are the ones that
    carry it.
- **Damage requested:** base × attack phase × hit group.
  - The attacker's base comes from its weapon (`Bases`): the player's short
    sword 1.4, the orc's cleaver 8, a guard's short sword 9, a ghoul 12, the
    cyclops 17.
  - The attack phase covers the heavy swings (×1.5) and the second contacts
    of two-contact attacks (`AttackScales`): attack_Fast is 0.4, then 0.8;
    a kick-and-swing is 1, then 1.5; a ghoul's lunge shove is 0.
  - A head (hit group 1) is ×3 for every victim.
  - Villagers, guards and goblins take ×0.5 on the chest, and villagers and
    goblins ×1.25 on a limb (`VictimScales`).
  - A parried or blocked blow asks for 0: the melee routine zeroes it before
    `TakeDamage`.
- **Damage applied:** the victim loses what was asked, a tenth of it between
  NPCs of the same class.
  - A kill counts as a kill whether the donor left the health at 0 or below
    it; it does both.

None of the numbers came from game data files. No weapon damage is in the
VPKs:
- the weapon models' `$keyvalues` hold only coefficients (all 1) and
  health;
- the map defines no weapons;
- the attack sequences' animation events give the contact timing but no
  amounts.

They're measured from recorded blows, and each table row says which.

## Scores

The tables were built from `l01_a-parrybot` and
`l01_a-orcs-vs-guards-damage`. The other four recordings were held out,
then used to find the rules marked as from them.

| Recording | NPC verdict | Player parry | Damage requested | Damage applied |
| --- | --- | --- | --- | --- |
| l01_a-parrybot (v4) | 67 / 67 | 18 / 18 | 124 / 124 | 16 / 16 |
| l01_a-orcs-vs-guards-damage (v3) | – | – | 20 / 21 | 20 / 20 |
| l01_a-duel-defense (v4) | 50 / 50 | – | 32 / 32 | 14 / 14 |
| l01_a-parry (v4) | 72 / 72 | 2 / 2 | 80 / 87 | 24 / 24 |
| l01_a-parry-attempt3 (v4) | 90 / 90 | – | 37 / 37 | – |
| l01_a-layers (v6) | 82 / 91 | 21 / 21 | 141 / 142 | 35 / 35 |

**What the held-out recordings changed:**
- guards' chest ×0.5;
- the kick breaking the parry, with the blade half of the same attack
  parried;
- the orc's kick-and-bash phases;
- the tenth applied between NPCs of one class;
- pairing checks with damage calls in order within a tick.

**Excluded from "applied":** victims protected in that fight, which the
Wire doesn't say:
- the player, under the staging cfgs' `god`;
- l01_a's villager the ghoul attacks for two minutes, who never loses
  health;
- l01_a's guards, who never drop below 1.

**Every remaining mismatch has a stated reason, all tracked in #10077:**
- **NPC defense state:** the orc chose to parry 9 body blows (l01_a-layers)
  from defense-state bits the Wire doesn't carry.
- **Attack strength:** the player's charged swing did 8.4 to a head,
  2 × the table. The orc's attack_Fast first contact did 4.8 once and 3.2
  six times.
- **Zero-damage contacts:** a ghoul's right turn strike asked 0 on every
  contact in l01_a-parry and 12 in l01_a-parrybot; its attack_wounded_1
  asked 0 once.
- **Untested:** no recorded case has a shield-up block on the body.
