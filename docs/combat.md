# Combat decisions in Shadow

`combat/` holds a C# model of Dark Messiah's melee decisions (design
"Phase 2: Combat decisions"), and a scorer that runs the model over a
recording's captured inputs and reports agreement blow by blow. Every blow
in a recording is a test case. The model's numbers are the game's own,
read from the `.qct` files in its VPKs; the recordings only check them. The
model decides from what the tap captured. What each swing struck is
recomputed from geometry, with the Engine's swept contact queries over posed
hit boxes ("Contacts" below).

```
dotnet run --project combat/DarkMessiah.Combat.Tool -- score local/recordings/l01_a-parrybot.rbrec [more.rbrec ...] [--vpks DIR] [--import DIR] [--no-contacts]
```

`--vpks` defaults to `local/donor/vpks` (docs/import.md says how to copy
them), then the Steam install. Contacts are swept headless in the Engine's
test host on dm-import's level of the recording's map (`--import`, default
`local/import`). Without one, the contact decision is left out.

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
| Contact | The swing's trace, everyone's pose at the blow | damage record `trace_entity`, `hitgroup`, `hitbox` |

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
  - **A kick** (damage type 0x40000) asks the settings' `kick_damage` (1, "Player's Kick Damage"),
    times that multiplier. No attack table holds it. Recorded kicks ask 1. Kicks aren't scored here: the
    scorer takes slash and club damage only.
  - **Inputs:** the multipliers come from `scripts/mm_game_settings.txt`. The critical byte and the
    alert level are streamed from Wire v11.
  - **Before v9:** the player's attack comes from the server view model's layers. From v9, when those
    are missing, it comes from the client's.
- **Damage applied:** the victim loses what was asked, a tenth of it between
  NPCs of the same class.
  - A kill counts as a kill whether the donor left the health at 0 or below
    it; it does both.

## Contacts

The donor sends a trace with each melee damage record:
- **The trace:** from `trace_start` to the point it struck, `trace_end`.
- **What it met:** `trace_entity`, `hitbox` and `hitgroup`.

`SwingContacts` (the mirror's `Shadow/`) sweeps that trace through the scene as the donor had it at the blow, and reports the first contact.
- **Poses:**
  - **Characters:** each character near the trace is posed by the mirror's views. Its pose is the latest streamed state, unblended (`EntityViews.Exact`): sequence, cycle and overlay layers.
  - **Hit boxes:** a character's hit boxes become parts of its entity: boxes on joints, from its model's default hitbox set (docs/import.md).
  - **Items:** a weapon or shield stands where the Wire streamed it. The convex pieces of its collision model (its `.phy`) are static meshes of the session.
  - **When, before Wire v13:** the frame before the blow, as for the defender's stance. The blow's own frame is swept too, to tell pose timing apart.
  - **When, from v13:** the blow's own frame, with the victim held as its damage record caught it (docs/wire.md, "The victim's pose"):
    - its sequence, cycle and overlays as the blow arrived;
    - where the previous frame had it, when its frame-end state is one think ahead of the record (it thought after the blow, in the same tick).

    Pose parameters are recorded, not applied: the import plays each sequence's first blend animation.
- **The sweep:** a capsule of radius 0.05 units, run 4 units past `trace_end`.
- **Left out:** the attacker, what it holds, and the level.

The scorer compares the first contact with the donor's `trace_entity`:
- **The entity:** the same one, by recording identity (`#index.serial`). Another orc's shield isn't the donor's shield.
- **What it is:** the same kind of thing (body, weapon, shield).
- **On the body:** the same hit group.

A victim the blow killed leaves the Wire in the blow's frame. It is taken as last seen, and swept on the frame before, held as its record caught it from v13.

**The donor traces rays.**
- **On the surface:** a traced blow's `trace_end` lies on the surface it struck. On agreeing blows the sweep meets it within a fraction of a unit.
- **Radius:** a 1-unit sphere already meets thighs that a ray toward a villager's spine passes within an inch of. In `l01_a-parrybot` it named the thigh on many of the ghoul's blows that the donor put on the spine; the 0.05-unit sweep agrees on all of them.

**Items are traced by their collision model, not their hit box.**
- **The evidence:** an item's damage record names hit box 0, and its trace ends up to 1.5 units inside the item's one hit box.
- **Why it matters:** a shield's box is a 23 × 37 × 7 slab. Rays crossed its corners, which the shield doesn't fill, before reaching the orc's arm.
- **How the import keeps it:** the import writes a one-solid `.phy` as convex pieces in the root bone's frame.
- **Result:** in the two target fights, items agree on 5 of their 6 contacts with the hulls, against 4 with the hit box. Body contacts agree 103 of 116, against 100.

**Some blows aren't traced.** These are scored apart, as "contact, untraced":
- **Which:** a blow on the player, and one naming hit box 0 in hit group 0 on an NPC whose hit box 0 is in another group (villagers and guards hit by a ghoul).
- **Where the trace ends:** at a reach point, not on a surface. On the player it is at mid height (36 units), a fixed distance from the player's axis for each attack: 16.8 for most of the orc's, 11.2 to 25.3 for the ghoul's.
- **What's scored:** these only need the sweep to meet the victim's body at all. A miss there means the reach point falls short of the victim's hit boxes ("swing path"). The donor's reach test, not geometry, decided the blow. That test is scored separately, as "reach" (below).

### Contact scores

| Recording | Contact (traced) | On the donor's own hit box | Contact, untraced |
| --- | --- | --- | --- |
| l01_a-parrybot (v4) | 52 / 54 | 49 / 49 | 34 / 70 |
| l01_a-layers (v6) | 58 / 70 | 56 / 56 | 10 / 72 |
| l01_a-v11-damage (v11) | 135 / 138 | 131 / 132 | 45 / 102 |
| l01_a-v11-crit (v11) | 140 / 150 | 138 / 138 | 1 / 8 |
| l01_a-v11-unaware (v11) | 60 / 62 | 58 / 58 | 0 / 4 |
| l01_a-v13-arena-pose (v13) | 53 / 53 | 52 / 52 | 21 / 68 |

Every mismatch is put down to one of:
- **Pose timing.** Either:
  - posed as the blow's own frame instead, the sweep meets the donor's part (the victim moved in the tick);
  - or the donor's hit point lies more than 1.5 units off the part it named, as posed. All of these are in fights with NPC victims whose streamed state was 1 to 7 ticks old (an NPC's state reaches the Wire every 8). Blending toward the next update, and leaving overlay layers out, both did worse.
- **Swing path:** the donor's hit point is on its part, and the straight sweep meets another part first: the orc's arm, shield or cleaver in front of its body. The donor's trace didn't run that line through them.
- **Shape fit:** the donor's hit point is on its part (within 1.5 units), and the sweep misses that part: it grazes past the box's edge, or meets the box beside it.

In `l01_a-layers` the traced mismatches are 8 pose timing and 4 swing path. In `l01_a-parrybot` they are 2 shape fits.

**From v13 the victim is posed as the blow found it.** In `l01_a-v13-arena-pose` every traced contact agrees.
- **What fixed it:** posed the old way, from the frame before, one contact was a pose-timing mismatch. A ghoul
  dodging right had thought in the blow's tick, before the blow, and its record shows the newer state.
- **Pose parameters:** near the middle of their ranges on every blow (0.5, with 0.37 to 0.56 on a few). No contact
  needed them.

The tool also reports a struck entity that couldn't be posed. That happens when the import has no model for it, such as the goblin in `l01_a-orcs-vs-guards-damage`: the level was imported from `l01_a-parrybot`.

## Reach

The decision behind an untraced blow is whether the attack reaches. The melee routine
(`server.dll+0x3B1A90`, docs/wire.md "The melee sweep") decides it with a swept box:
- **Start:** the attacker's centre.
- **Aim:** toward its enemy when the enemy is within cos 0.3 of its facing, otherwise along its facing.
- **Width:** a box of half-size 1.5, the ConVar `collision_inc_size_ray`.
- **Length, armed attacker:** the attack part's `range` from its `.qct` `combatmovedatas` (`event0`, `event1`
  for a split attack). An attack without one takes the move default, 120. The orc's are 60 to 120.
- **Length, unarmed attacker:** its `entity_data` `baseattacklength`, default 50. The ghoul gives none, so 50.
- **What lands:** the sweep meets the victim's collision box first: the player's ±16 (`VEC_HULL`), or an NPC's
  hull (13 for humans and ghouls, 16 for orcs). The box is world-aligned, so on a diagonal the reach ends at its corner.

`CombatModel.Reach` is that test. `Reaches.Read` takes its cases from a recording:
- **On the player:** every NPC attack part (2102, 2139) that fires while the player is in its aim, within twice the
  reach. It fires in the attacker's think, the first streamed state past the event's cycle.
- **Landed:** the attacker's blow on the player, or the player's parry of it, within 12 ticks.
- **On NPCs:** every untraced blow on an NPC (hit box 0 in group 0) is a case that landed.

**What the recorded blows show:**
- **The geometry:** an untraced blow's trace runs from the attacker's centre straight at the victim.
- **On an NPC:** it ends where it enters the victim's box grown by 1.5. Every ghoul blow on a guard or villager ends
  14.5 from the victim's axis along the nearer box face.
- **On the player:** the end is moved to a fixed radius by a second, cylinder test (16.8 for most orc attacks). That
  changes only where the blow lands, not whether.
- **The lengths:** measured live with a temporary hook on the trace routine, 70 and 60 for the orc's attacks and
  50 for the ghoul's, as above.

| Recording | Reach | Landed parts on the player | Missed parts | Untraced blows on NPCs |
| --- | --- | --- | --- | --- |
| l01_a-parrybot (v4) | 83 / 87 | 70 | 11 | 6 |
| l01_a-layers (v6) | 84 / 86 | 71 | 8 | 7 |
| l01_a-v7-bot (v7) | 155 / 162 | 140 | 14 | 8 |
| l01_a-v11-damage (v11) | 118 / 123 | 90 | 21 | 12 |
| l01_a-v12-arena-bot (v12) | 80 / 84 | 75 | 9 | 0 |
| l01_a-v12-arena-ghoul (v12) | 103 / 107 | 78 | 26 | 3 |
| All 17 recordings with cases | 853 / 893 (95.5 %) | | | |

Every mismatch is put down to one of:
- **Struck first:** the sweep stops at the first entity it meets, and the attacker's blow struck another NPC, its
  weapon or an entity gone by the frame instead (24 of the 40). Two orcs side by side in the arena strike each other.
- **Another enemy** (8): the routine aims at the attacker's enemy, which the Wire doesn't carry.
  - Either another NPC was in its aim cone;
  - or a sweep along its facing, its aim when the enemy is out of the cone, passes the player.
- **Near the reach's end** (7): the sweep ends within 12 units of the player's box. The Wire has the attacker and player
  as the frame ended, not at the sweep inside the attacker's think.
- **A lunge** (1): the attack moves the attacker (`AttackMove` above 1: 12 is the orc's jump). The Wire has it only at its thinks.

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
