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

It prints per-decision agreement, then every mismatch blow by blow, grouped
by its reason (`--brief` keeps the first eight of each group). It exits 1 when any mismatch is "unexplained", so a new gap stands
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
| `scripts/mm_game_settings.txt` | `mul_damages_Npc_Not_alerted` (3) and its `_level` (0), `player_damage_multiplier` (1.0) |

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

- **NPC verdict**, in the order of `CNPC_BaseArx`'s defense virtual (docs/wire.md,
  "The defense hook"):
  - its no-effect byte (+0x2961) set: no effect (4), whatever the blow struck;
  - a blow that strikes the defender's weapon: a parry; its off-hand item (shield): a shield block;
  - on the body, its defense-state word: bit 0x4 parries, bit 0x10 blocks blows from in front;
  - otherwise a hit.
  - From Wire v11 the state word and the byte come with each verdict. Before, the stance the defender
    plays stands in for the state, and no-effect can't be told.
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
  before `TakeDamage`. The player's blows also take:
  - **×2 when critical.** The melee routine's `DmgCrit` is `DmgBase × (1 + (the player's byte +0x21F7 != 0))`.
    The byte is a random roll at the swing: below 2 × an equipment stat plus the weapon's chance,
    for weapons that can critical. A critical power attack also carries damage type bit 0x10000.
    NPCs' bytes are set too, but their blows aren't doubled.
  - **×3 on an NPC that isn't alerted** (`mul_damages_Npc_Not_alerted`): its alert level
    (+0x36EC) is at most `mul_damages_Npc_Not_alerted_level` (0), and its state (+0x1A5C) isn't 3.
  - **× `player_damage_multiplier`** (1.0).
  - **Inputs:** the multipliers come from `scripts/mm_game_settings.txt`. The critical byte and the
    alert level are streamed from Wire v11.
  - **Before v9:** the player's attack comes from the server view model's layers. From v9, when those
    are missing, it comes from the client's.
- **Damage applied:** the victim loses what was asked, a tenth of it between
  NPCs of the same class.
  - A kill counts as a kill whether the donor left the health at 0 or below
    it; it does both.

## How the donor computes melee damage

`mm_show_dmg_computing 1` with `developer 1` makes Arkane's melee routine
(`server.dll+0x407D70`) print each computation to the console through `DevMsg`.
- **What it prints:** `DmgBase`, `DmgCrit`, `Armor`, `HB Modifier`, a dodged or blocked zero, and the
  not-alerted test with the target's alert level.
- **Damage to the player:** a dynamic-difficulty multiplier ("Difficulty dmg mul").
- **The player's blows:** `player_damage_multiplier` ("Final Player dmg multiplier"), then `DmgFinal`.
- **NPC attackers:** `HitPow` is applied after the routine. A ghoul's blow prints `DmgBase 12`, the
  guard's chest modifier 0.5 and `DmgFinal 6`, and the request is that times the struck part's `HitPow`.
- **The player:** `HitPow` is already in its base: the short sword's 2 × 0.7 = 1.4.
- **In the 2026-10-18 sessions:** every target was alerted (levels 3–4) and the difficulty multiplier
  was 1.00 throughout. The not-alerted rule, the player's critical and the difficulty multiplier
  never fired.

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
| l01_a-v11-damage (v11) | 161 / 161 | 12 / 12 | 240 / 240 | 27 / 27 |
| l01_a-v11-crit (v11) | 902 / 902 | – | 158 / 158 | 22 / 22 |
| l01_a-v11-unaware (v11) | 220 / 220 | – | 66 / 66 | 8 / 8 |

The v11 sessions stream the defense state, the no-effect byte, the player's
critical byte and the victims' alert levels. Every verdict and request agrees.
In `l01_a-v11-damage`, 8 parries came from state bit 0x4 while the orc was
moving, and 5 no-effect verdicts came from the byte. Before v11 these were
the stance's misses. In `l01_a-v11-crit`, 5 player blows needed the client's
view-model layers: the server view model had none, about 70% of the session.
These include a 60 from `layer_combo_sword_huge` (`HitPow` 30) and 9 from a
power attack to the head.

The data explained what the measured tables couldn't: a ghoul lunge's
second strike asks 0 because its table has one part, and its
`attack_wounded_1` asks 0 because its table has no such attack.

**Excluded from "applied":** victims protected in that fight, which the
Wire doesn't say:
- the player, under the staging cfgs' `god`;
- l01_a's villager the ghoul attacks for two minutes, who never loses
  health;
- l01_a's guards, who never drop below 1.

**Every remaining mismatch is in a recording made before Wire v11, with a stated reason (#10077):**
- **NPC defense state:** the orc's 9 state-bit parries in l01_a-layers. The state is on the Wire from v11.
- **A player blow at twice its table:** 8.4 to a head (l01_a-layers). It fits a player critical, which
  doesn't always set the damage type's bit. The critical byte is on the Wire from v11.
- **Not taken further:**
  - **The orc's attack_Fast:** it once asked 4.8 where its table gives 3.2. That's ×1.5, which no table
    or setting gives. One blow, in a v3 recording.
  - **The ghoul's Turn_strike_90_right:** in l01_a-parry it asked 0 on six contacts and 12 on one, at the
    same cycle (0.45), in one fight. Nothing streamed (flags, type, hit group, force) tells them apart.
    Its table lists `HitPow` 1. Its animation fires 2175/2176 at 0.21 and 0.79, not the part events.
  - **If either recurs:** the damage prints show whether the routine zeroed the blow or the part's
    `HitPow` did.
- **Untested:** no recorded case has a shield-up block on the body. No v11 session raised state bit 0x10.
