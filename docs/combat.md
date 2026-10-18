# Combat decisions in Shadow

`combat/` holds a C# model of Dark Messiah's melee decisions (design
"Phase 2: Combat decisions"), and a scorer that runs the model over a
recording's captured inputs and reports agreement blow by blow. Every blow
in a recording is a test case. The model's numbers are the game's own,
read from the `.qct` files in its VPKs; the recordings only check them. The
model decides from what the tap captured; recomputing contacts from geometry
is next (#10078), now that rusty-engine #10056 (swept posed shapes with
per-part contacts) is in the pinned pair.

```
dotnet run --project combat/DarkMessiah.Combat.Tool -- score local/recordings/l01_a-parrybot.rbrec [more.rbrec ...] [--vpks DIR]
```

`--vpks` defaults to `local/donor/vpks` (docs/import.md says how to copy
them), then the Steam install.

It prints per-decision agreement, then every mismatch grouped by its
reason. It exits 1 when any mismatch is "unexplained", so a new gap stands
out. `dotnet test combat/DarkMessiah.Combat.slnx` covers:
- the rules;
- the case reader, on in-memory frames;
- the two recorded fights named below, when `local/recordings` and
  `local/donor/vpks` have them.

## The game's data

Arkane shipped the `$keyvalues` sources of its models: a `.qct` file beside
each `.mdl` in the VPKs, commented. `CombatData` reads them by model path
(`npc_orc.mdl` → `npc_orc.qct`) with the shared KeyValues reader:

| Where | What the model uses |
| --- | --- |
| A character's `entity_data` | `strength` (the block's own; `level_N` sub-blocks are other settings of the NPC) |
| A character's `hitboxes_modifier` | the damage multiplier per hit group it takes |
| A character's `combatmovedatas` | per attack sequence: `HitPow`, and `breakparade`/`forcebreakparade`; an attack in parts lists `event0`, `event1`… |
| A weapon's `entity_data` | `WDmg`, "to be added to damage done" |
| `models/npc/player/first_basic.qct` | the player's attacks, named by the first-person layers they play as |

`combatmovedatas` is a top-level block in most files and sits inside
`$keyvalues` in `npc_orc.qct`, which misses a brace.

Which part of an attack struck comes from the attack's animation events in
the `.mdl`: event 2102 fires the first part and 2139 the second (an orc's
`attack_Fast` has them at cycles 0.34 and 0.54, its parts' `HitPow` 0.4 and
0.8). A blow belongs to the last part event at or before its streamed
cycle. Some animations strike more often than their table has parts (a
ghoul's lunge strikes twice and lists one part); those strikes ask for 0.

Three numbers aren't in the data:
- unarmed attackers add 1 to their strength (the `NWDmg` lines are
  commented out; ghoul 11 + 1 = 12, cyclops 16 + 1 = 17);
- a blow between NPCs of the same class applies a tenth;
- the player has no strength term (its stats aren't in a `.qct`; 2 × 0.7 =
  1.4 with the short sword).

The official Dark Messiah SDK (Valve's 2007 tools: Hammer, studiomdl,
vbsp/vrad, HLMV, Faceposer) and wiltOS's Advanced SDK (compiled
`server_sdk.dll`/`client_sdk.dll` hook modules, GPL-3, no source
published) hold no combat data.

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
- **Player parry:** broken when the part of the attack that struck has
  `breakparade` or `forcebreakparade` (the donor's weapon flag 0x10,
  docs/wire.md "The player's parry"); parried otherwise.
- **The attack:** the attacker's streamed sequence if it is in its table,
  else an attack it plays as a layer (an NPC striking on the move, the
  player's first-person swing), else the attack it played within the last
  second (an NPC's state reaches the Wire every 8 ticks). Before Wire v6
  there are no layers: an NPC's blow on the move is taken as its
  `layer_attack_*` (`HitPow` 1), and the player's as its fast attack.
- **Damage requested:** (strength + weapon `WDmg`, or 1 unarmed) × the
  struck part's `HitPow` × the victim's `hitboxes_modifier` for the hit
  group. A parried or blocked blow asks for 0: the melee routine zeroes it
  before `TakeDamage`.
- **Damage applied:** the victim loses what was asked, a tenth of it between
  NPCs of the same class.
  - A kill counts as a kill whether the donor left the health at 0 or below
    it; it does both.

## Scores

The first model measured its tables from `l01_a-parrybot` and
`l01_a-orcs-vs-guards-damage`, then held out the other four recordings.
Every measured rule turned out to be a `.qct` value: the orc's 1.5 heavy
swings, its 0.4/0.8 fast parts, 0.7 left turn strikes, the kick parts that
break a parry, the villagers', guards' and goblins' 0.5 chest and 1.25
limbs. The model now reads them, and scores at least as well everywhere:

| Recording | NPC verdict | Player parry | Damage requested | Damage applied |
| --- | --- | --- | --- | --- |
| l01_a-parrybot (v4) | 67 / 67 | 18 / 18 | 124 / 124 | 16 / 16 |
| l01_a-orcs-vs-guards-damage (v3) | – | – | 20 / 21 | 20 / 20 |
| l01_a-duel-defense (v4) | 50 / 50 | – | 32 / 32 | 14 / 14 |
| l01_a-parry (v4) | 72 / 72 | 2 / 2 | 80 / 86, 1 unmodelled | 24 / 24 |
| l01_a-parry-attempt3 (v4) | 90 / 90 | – | 37 / 37 | – |
| l01_a-layers (v6) | 82 / 91 | 21 / 21 | 141 / 142 | 35 / 35 |

The data explained what the measured tables couldn't: a ghoul lunge's
second strike asks 0 because its table has one part, and its
`attack_wounded_1` asks 0 because its table has no such attack.

**Excluded from "applied":** victims protected in that fight, which the
Wire doesn't say:
- the player, under the staging cfgs' `god`;
- l01_a's villager the ghoul attacks for two minutes, who never loses
  health;
- l01_a's guards, who never drop below 1.

**Every remaining mismatch has a stated reason, all tracked in #10077:**
- **NPC defense state:** the orc chose to parry 9 body blows (l01_a-layers)
  from defense-state bits the Wire doesn't carry.
- **A player blow above its fast attack:** 8.4 to a head straight out of a
  parry (`layer_parry_sword_noshield_out` at weight 0.8), 2 × the table: a
  riposte or charge the Wire doesn't identify.
- **The orc's attack_Fast:** its first part asked 0.6 of the base once where
  the table says 0.4 (3.2 six times).
- **The ghoul's Turn_strike_90_right:** its animation has no part event
  (only 2175/2176); it asked 0 on every contact in l01_a-parry and 12 in
  l01_a-parrybot.
- **Untested:** no recorded case has a shield-up block on the body.
