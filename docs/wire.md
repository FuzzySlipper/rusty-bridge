# Wire v19

The tap's stream of per-tick entity snapshots. Defined in
`tap/darkmessiah/src/wire.h` (writer) and mirrored in
`wire/RustyBridge.Wire/WireLayout.cs` (C# reader). Keep the two in step;
`WireReaderTests.LayoutMatchesTheTapHeader` and the `static_assert`s in
`wire.h` pin the sizes on both sides.

Every donor tick: every live edict's index, class, model and abs
transform, and (since v2) its health, life state, animation sequence, cycle
and playback rate. Since v3, each tick also carries every
`CBaseEntity::TakeDamage` call made since the previous one, and since v4
every melee defense verdict (block, parry or hit). Since v5 every entity,
and every entity named in a damage or defense record, carries its EHANDLE
serial, so a reused edict index is a different entity. Since v6 each tick
also carries every active animation overlay layer: the gestures, parries,
blocks and hit reactions that play over an entity's main sequence. Since v7
each tick carries the tap's wall clock, the player's slow-motion and
adrenaline state and every bullet-time call, and every entity its link: a
knocked-down NPC's ragdoll and back. Since v8 each tick carries the client's
view of the local player: its view angles, predicted transform, eye offset
and first-person viewmodel. Since v11 each NPC verdict carries the
defender's defense state, and each damage record the player's critical byte
and an NPC victim's alert level. Since v10 every entity also carries what it
hangs from (its move parent and the parent's attachment) and its owner.
Since v9 the client view also carries the camera the client
drew with and the client viewmodel's own sequence, cycle and overlay layers.
Since v12 each tick carries the player's input: the button words the server
ran its command with. Since v13 each damage record carries the victim's
animation as the blow arrived, pose parameters included. Since v14 the
player's input carries its user commands as the server received them: the
movement, the buttons and Arkane's action bytes, the kick among them. Since v15 each tick carries the
player's stats: stamina, mana, the skill modules learned, skill points and the items in hand and worn, and since v16
each skill's level. Since v17 each tick carries the melee sweeps the server ran: the start, direction and range of each
ray it swept for a swing. Since v18 every NPC carries its alert level and state each tick (they were on damage
records only), so its alertness is seen climbing. Since v19 every entity carries Arkane's size and shrink, so a shrunk
NPC is seen shrinking (below, "Shrink"). A control file beside the Wire holds the donor
on a tick, steps it, and runs console commands; there's no other input or
write-back yet.

v2–v19 replaced v1 between 2026-10-17 and 2026-10-24. Readers check `version`; the tap writes
only the current one.

## Transport

A file, `rustybridge.wire`, next to the tap DLL (`mm\addons\`). The tap
creates it, sizes it to 27,851,008 bytes and maps it read-write; readers open
it with `FileShare.ReadWrite | FileShare.Delete` and map it read-only.

It's a file and not a named section because the game runs non-elevated, so
it can't create a `Global\` name, and a `Local\` name is invisible to readers
in other sessions (an SSH-launched tool, for one). Views of one file are
coherent across processes on Windows, and the page cache keeps it in memory.

## Layout

All little-endian, 4-byte packing, offsets in bytes.

| Offset | Size | What |
| --- | --- | --- |
| 0 | 144 | Header |
| 256 | 1024 × 64 | Class table: NUL-terminated class names, by class id |
| 65,792 | 4096 × 128 | Model table: NUL-terminated model paths, by engine model index |
| 590,080 | 128 × 212,976 | Ring of slots |

Header: `magic` (u64, `"RBWIRE01"`), `version` (1), the sizes and offsets
above (so a reader can check them), `writer_pid`, `level_serial`,
`tick_interval` (seconds, from the donor), `map[64]`, and `frames_written`
(u64, at offset 136).

Slot: a 96-byte header (`seq` u64, `frame` u64, `tick` i32, `curtime` f32,
`level_serial` u32, `entity_count` u32, `damage_count` u32,
`damage_dropped` u32, `defense_count` u32, `defense_dropped` u32,
`layer_count` u32, `layer_dropped` u32, then v7's `wall_seconds` f64 at 56,
`slow_time_count` u32, `slow_time_dropped` u32 and the 24-byte player state
at 72), then:

- up to 2048 entities of 88 bytes (80 before v19, 64 before v10)
- at slot offset 180,320, up to 64 damage records of 360 bytes (152 before v13)
- at 203,360, up to 32 defense records of 56 bytes (48 before v11)
- at 205,152, up to 256 overlay layer records of 20 bytes
- at 210,272, up to 16 slow-time records of 32 bytes
- at 210,784, the 232-byte client view (80 bytes in v8)
- at 211,016, the 48-byte player input (16 bytes in v12 and v13)
- at 211,064, the 112-byte player stats (80 in v15; below, "The player's stats")
- at 211,176 (v17), the melee sweeps: `count` u32, `dropped` u32, then up to 32 sweep records of 56 bytes (below, "The
  melee sweep")

Entities:

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `index` u16 | edict index |
| 2 | `class_id` u16 | class table row |
| 4 | `model_index` i32 | model table row |
| 8 | `origin` 3×f32 | `ICollideable::GetCollisionOrigin` |
| 20 | `angles` 3×f32 | `m_angAbsRotation` (pitch, yaw, roll, degrees) |
| 32 | `health` f32 | `CBaseEntity::m_fHealth` (+0x310) |
| 36 | `max_health` f32 | `CBaseEntity::m_fMaxHealth` (+0x30C) |
| 40 | `sequence` i16 | `CBaseAnimating::m_nSequence` (+0xEC4) |
| 42 | `flags` u8 | 1 = health fields valid, 2 = animation fields valid, 4 = alert fields valid (v18), 8 = size fields valid (v19) |
| 43 | `life_state` u8 | `CBaseEntity::m_lifeState` (+0x300) |
| 44 | `cycle` f32 | `CBaseAnimating::m_flCycle` (+0xEBC), 0..1 |
| 48 | `playback_rate` f32 | `CBaseAnimating::m_flPlaybackRate` (+0xE70) |
| 52 | `serial` u32 | the entity's EHANDLE serial (`IServerEntity::GetRefEHandle() >> 12`) |
| 56 | `link` u16 | v7: an `npc_*`'s `m_hRagdollFall` (+0x29E4), or a `prop_ragdoll`'s `m_hUnragdoll` (+0x16A0); 0xFFFF for none |
| 58 | `alert` u16 | v18 (reserved before): an `npc_*`'s alert level, the magnitude of +0x36EC (below, "Alertness") |
| 60 | `link_serial` u32 | the linked entity's serial |
| 64 | `parent` u16 | v10: `m_hMoveParent` (+0x3E4, the send prop `moveparent`): what the entity is attached to; 0xFFFF for none |
| 66 | `parent_attachment` u8 | v10: `m_iParentAttachment` (+0x3E0): the parent model's attachment, numbered from 1 in the model's order (the import's `asset.extras.attachments`); 0 for none |
| 67 | `npc_state` u8 | v18 (reserved before): an `npc_*`'s state, +0x1A5C (3 combat) |
| 68 | `parent_serial` u32 | |
| 72 | `owner` u16 | v10: `m_hOwnerEntity` (+0x500), or a weapon's `m_hOwner` when that is empty (the player's weapons); 0xFFFF for none |
| 74 | reserved u16 | |
| 76 | `owner_serial` u32 | |
| 80 | `size` f32 | v19: `m_fMMSize` (+0x12C), Arkane's render scale: 1 unscaled; below, "Shrink" |
| 84 | `shrinked` i32 | v19: `m_iShrinked` (+0x130): 1 once the NPC wears its `_shrinked.mdl` |

A parent or owner is written only when its handle names a live entity with
the handle's serial. In l01_a, NPC items hang from numbered attachments, not
bone merges:
- an orc's cleaver from 7 (`RightHand`) and its shield from 11 (`LeftShield`);
- a guard's sword from 6 (`RightHand`) and its helmet from 17 (`Helmet`);
- the player's quiver from 16 on the player;
- the player's own weapon from 4 on the first-person viewmodel;
- the player, during the opening ride, from its horse at 1.

**Identity is (index, serial).** Edict indices are reused within a level,
often by an entity of the same class. Each entity's own handle carries a
20-bit serial that changes on reuse, and it's the same serial the game's
handles to that entity carry (`handle >> 12`). The edict's second word
(`m_NetworkSerialNumber` in Episode One) holds only its low 10 bits, the
networked width: 0xA6 against a handle serial of 0x20A6, for example. That
is why the Wire takes the serial from the handle, not from the edict.

Readers resolve an event's entity by both fields. It can name an entity
whose index was reused since (a killed NPC whose ragdoll took the index in
the same tick), and it never names the entity that reused it. An identity
the reader never saw stays unresolved: `rbwire info` prints
`#index.serial (not captured)`, and the stepper leaves it out.

`sequence` indexes the model's virtual sequence list: its own sequences,
then each `$includemodel`'s, depth first, skipping names already seen.
`StudioModel.SequenceNames` rebuilds that list from the game's VPKs.

Damage records, one per `CBaseEntity::TakeDamage` call, in the order they
happened. Field names follow Arkane's `CTakeDamageInfo` datadesc. Entity
fields are edict indices, 0xFFFF for none.

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `tick` i32 | tick the damage was applied in |
| 4 | `victim` u16 | the entity `TakeDamage` was called on |
| 6 | `attacker` u16 | `m_hAttacker`, or its owner when that's a held `weapon_*` |
| 8 | `inflictor` u16 | `m_hInflictor` |
| 10 | `trace_entity` u16 | `m_hTraceEntity` |
| 12 | `damage` f32 | `m_flDamage`, as requested |
| 16, 20 | `health_before`, `health_after` f32 | victim `m_fHealth` around the original call |
| 24 | `damage_type` u32 | `m_bitsDamageType` |
| 28 | `custom_kill` i16 | `m_iCustomKillType` |
| 30 | `weapon` u16 | the original `m_hAttacker` when it was a held weapon |
| 32, 34, 36 | `hitbox`, `hitgroup`, `physics_bone` i16 | `m_iTraceHitBox`, `m_iTraceHitGroup`, `m_iTracePhysicsBone` |
| 38 | `trace_valid` u8 | `m_bTraceInfoValid` |
| 39 | `attacker_critical` u8 | v11: the attacker's critical byte (+0x21F7) as the call arrived (see "Melee damage") |
| 40 | `position` 3×f32 | `m_vecDamagePosition` |
| 52 | `force` 3×f32 | `m_vecDamageForce` |
| 64, 76 | `trace_start`, `trace_end` 3×f32 | `m_vecTraceStartPos`, `m_vecTraceEndPos` |
| 88 | `push_force`, `weapon_flags`, `mm_damage_flag`, `mm_spell` i32 | `m_iPushForce`, `m_iWeaponFlags`, `m_iMMDamageFlag`, `m_iMMDamageSpell` |
| 104 | `slow_time_delay`, `_duration`, `_factor` f32 | `m_fSlowTime*` |
| 116 | `mm_direction` 3×f32 | `m_vecMMDirection` |
| 128 | `victim_serial`, `attacker_serial`, `inflictor_serial`, `trace_entity_serial`, `weapon_serial` u32 | EHANDLE serials of the entities above, 0 with no entity |
| 148 | `victim_alert` i16, `victim_npc_state` u8, `victim_alert_valid` u8 | v11: an `npc_*` victim's alert level (magnitude of +0x36EC) and state (+0x1A5C), which the not-alerted multiplier reads; valid 0 otherwise |
| 152 | `victim_sequence` i16, `victim_anim_valid` u8, `victim_layer_count` u8 | v13: the victim's `m_nSequence` as the call arrived (below); valid 0 when not read |
| 156 | `victim_cycle`, `victim_playback_rate`, `victim_anim_time` f32 | v13: its `m_flCycle`, `m_flPlaybackRate`, and `m_flAnimTime` (when its animation last advanced) |
| 168 | `victim_pose` 24×f32 | v13: its `m_flPoseParameter`, normalized as the game stores them |
| 264 | `victim_layer_sequence` 8×i16, `victim_layer_order` 8×i16 | v13: its active overlays, in `m_AnimOverlay` order |
| 296 | `victim_layer_cycle` 8×f32, `victim_layer_weight` 8×f32 | v13: their cycles and weights |

Defense records. There are two sources: each verdict from
`CNPC_BaseArx`'s defense virtual, and each parried or broken change of the
player's parry state:

| Offset | Field | Meaning |
| --- | --- | --- |
| 0 | `tick` i32 | |
| 4 | `victim` u16 | the NPC asked for a verdict, or the player |
| 6 | `attacker` u16 | for player records, only when the attack resolver set the state |
| 8 | `weapon` u16 | the attacker's weapon, if it used one |
| 10 | `struck` u16 | what the blow struck: the victim, or its weapon or shield |
| 12 | `result` i32 | NPC verdicts: 2 hit, 1 parry (struck its weapon, or parry/block state), 8 shield (struck its off-hand item, or shield up and attack from the front), 4 no effect. Player: 17 parried, 18 parry broken |
| 16 | `direction` 3×f32 | the vector passed with the check (zero for player records) |
| 28 | `source` u32 | player records: `server.dll` RVA of the code that set the state |
| 32 | `victim_serial`, `attacker_serial`, `weapon_serial`, `struck_serial` u32 | EHANDLE serials of the entities above, 0 with no entity |
| 48 | `victim_state` u32 | v11: the NPC's defense-state word as its verdict read it (0x4 parry/block, 0x10 shield up) |
| 52 | `state_valid` u8, `no_effect` u8, reserved u16 | v11: whether the state was read; the NPC's no-effect byte (+0x2961) |

Overlay layers, one record per active layer (flag 1 set, weight above 0):

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `index` u16 | the entity it plays on (with that frame's serial) |
| 2 | `slot` u8 | position in the entity's layer vector |
| 3 | `flags` u8 | `m_fFlags`: 1 active, 2 autokill, 4 kill me, 32 dying |
| 4 | `sequence` i16 | `m_nSequence`, in the entity's model's sequence list |
| 6 | `order` i16 | `m_nOrder`, blend order among the layers |
| 8 | `cycle` f32 | `m_flCycle` |
| 12 | `weight` f32 | `m_flWeight` |
| 16 | `playback_rate` f32 | `m_flPlaybackRate` |

Player state (v7, in the slot header; `player` 0xFFFF when edict 1 isn't a
`player`), read from `CMM_Player`:

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `player` u16 | edict index (1) |
| 2 | `in_bullet_time` u8 | `m_bInBulletTime` (+0x20B0) |
| 3 | `bullet_time_sound` u8 | +0x20B1 |
| 4 | `bullet_time_start` f32 | `m_fBulletTimeStart` (+0x20A4), curtime |
| 8 | `bullet_time_duration` f32 | +0x20A8 |
| 12 | `bullet_time_factor` f32 | +0x20AC |
| 16 | `adrenaline` f32 | `m_fAdrenaline` (+0x29D8) |
| 20 | `max_adrenaline` f32 | `m_fMaxAdrenaline` (+0x29D4, a send prop) |

Slow-time records (v7), one per bullet-time call:

| Offset | Field | Meaning |
| --- | --- | --- |
| 0 | `tick` i32 | |
| 4 | `player` u16 | |
| 6 | `sound` u8 | the call's sound flag |
| 7 | `source` u8 | 0 the player's setter, 1 the static request (see "Slow motion") |
| 8 | `start` f32 | setter: the curtime slow motion starts at; request: the delay from now |
| 12 | `duration` f32 | seconds, as asked |
| 16 | `factor` f32 | time scale while it lasts |
| 20 | `caller` u32 | `server.dll` RVA of the return address: the code that asked |
| 24 | `player_serial` u32 | |
| 28 | reserved u32 | |

`wall_seconds` is the tap's `QueryPerformanceCounter` time since `Load`.
Game time over wall time is the effective time scale.

### Finding the layers

Layers live in `CBaseAnimatingOverlay::m_AnimOverlay`, a
`CUtlVector<CAnimationLayer>`: the element pointer at +0, the count at +12
(`CBaseAnimatingOverlay`'s constructor zeroes five words there). The
vector's offset and element size come from the send table, read when the
tap first walks a class that has them:

- `DT_OverlayVars` networks the vector through a `SendPropUtlVector`
  (`_ST_m_AnimOverlay_15`, 15 elements). Each element is a data-table prop
  into `DT_Animationlayer`.
- Each element prop's extra data (`SendProp` +72) is a
  `CSendPropExtra_UtlVector`: element stride at +12, vector offset at +16,
  maximum at +20. Here it reads +0xF78, stride 0x40, max 15. The offset
  matches `CBaseCombatCharacter`'s datadesc entry for `m_AnimOverlay`, an
  independent check.
- The element fields come from `CAnimationLayer`'s datadesc (flags +0x0,
  sequence +0x8, cycle +0xC, playback rate +0x14, weight +0x18) and from
  `DT_Animationlayer` (order +0x34).

At 64 bytes the element has no room for Episode One's `m_pOwnerEntity`, so
the tap can't check a layer's owner. Instead it rejects an active layer
whose weight or cycle is out of range, and counts the rejects in the log.
The vectors are empty until a gesture first plays.

Layers aren't only gestures. In a recorded bot fight they include NPC attack
gestures (`layer_attack_left/right/top`), NPC parries (`layer_parry_A`, `_B`,
`_E`), hit reactions played as layers (`Hit_Body_Front_small`), and the
player's first-person viewmodel layers (`layer_parry_sword_noshield_in`,
`_idle`, `_out`).

## The damage hook

`CBaseEntity::TakeDamage(const CTakeDamageInfo&)` is the one entry point for
damage to any entity. It's at `server.dll+0x3B9820`, found through its two
`"CBaseEntity::TakeDamage:  with inputInfo..."` warning strings. The tap
replaces its first instruction (`sub esp,0xBC`, 6 bytes) with a jump to a
`__fastcall` hook. The hook reads the victim's health, calls the original
through a trampoline, reads the health again, and queues a record for the
next frame. The tap checks the first 14 bytes before patching, so a
different `server.dll` is left alone, and Unload restores them.

`CTakeDamageInfo`'s layout comes from its datadesc in `server.dll` (the
table that names `m_vecDamageForce` ... `m_hTarget` with their offsets).
Episode One's fields run up to `m_iAmmoType` (0x40); Arkane added trace
info (0x44–0x6C), push force (0x78), slow-time (0x7C–0x84), weapon flags
and spell fields (0x88–0x94) and `m_hTarget` (0xB0).

- Dark Messiah names the **weapon** as the attacker of a melee hit. The
  hook swaps in the weapon's `m_hOwnerEntity` (+0x500) when the attacker's
  class is `weapon_*`, and keeps the weapon in `weapon`. NPCs have owners
  too (the `npc_template_maker` that spawned them), so only weapons are
  swapped.
- The killing blow is in the stream: `health_after` ≤ 0, and the victim is
  removed by the next frame.
- Environmental damage is constant. Fires burn props for 0.1 a tick, about
  6 records per second on l01_a.

## The defense hook

When a melee blow lands, the game asks the victim for a verdict through
virtual slot 393 (`vtable+0x624`). Arkane's melee damage routine
(`server.dll+0x407D70`, the one that prints `"Hit blocked or parried, dmg =
0"`) calls it, and so does other attack code, about ten call sites in all.
The routine zeroes the damage on 1 or 8. On 4 it zeroes the damage and sets
bit 0x20 in `m_iMMDamageFlag`.

Through RTTI (type descriptor → complete object locator → vtable),
`CNPC_BaseArx`'s slot 393 is `server.dll+0x2E0030`. `CBaseCombatCharacter`,
`CAI_BaseNPC`, `CBasePlayer` and `CHL2_Player` share a stub at `+0x19EB0`
that returns 0. The tap detours `+0x2E0030`: its first two instructions
(`mov eax,[esp+4]; mov edx,[eax]`, 6 bytes) move to a trampoline. That
catches every verdict on an Arkane NPC, whichever code asks. Arguments:
`(const Vector* dir, Vector* out, CBaseEntity* struck, Vector* outDir,
CBaseEntity* attacker, CBaseEntity* weapon)`, `ret 0x18`.

- The verdict comes from the defender's state and from what the swing
  physically struck. If the blow hits the NPC's weapon entity (handle at
  +0x18F0), the result is a parry. If it hits the off-hand item (+0x18F4),
  usually a shield, the result is a shield block. If it hits the body, the
  NPC's defense-state bits decide (0x4 parry/block, 0x10 shield if the
  attack is in front).
- A shield block still produces `TakeDamage` calls, with 0 damage to both
  the shield and its holder.
- The player inherits the stub, so it has no verdict (0). Its defense is a
  separate mechanism; see below.
- The scripted fights in the l01_a intro don't call the check.

**The defense state (v11).** The verdict reads the state through a getter at
`server.dll+0x2BBCA0`. The getter returns a word cached per tick (+0x23D4, stamped with the tick at
+0x23D8) and recomputes it through `vtable+0xC98` when the stamp is stale.
- **Reading it:** the tap calls the getter on the victim just before the verdict, so the record has the
  state the verdict saw. The getter's code is checked first.
- **No effect:** the verdict's first test is the byte at +0x2961. Set, the result is 4 whatever struck.
- **What the recordings show:** in `l01_a-v11-damage`, every NPC parry (1) had bit 0x4
  (e.g. 0x200006) and no hit (2) did; every no-effect (4) had the byte set.

## Melee damage

Arkane's melee routine (`server.dll+0x407D70`) computes a blow and logs each step through `DevMsg`
when `mm_show_dmg_computing` (a cvar at `[0x2283C8E0]`, flags 0) and `developer` are set.
docs/combat.md has the pipeline. Where its factors come from:

- **Critical:** `DmgCrit = DmgBase × (1 + ([attacker+0x21F7] != 0))`, on the player's path only.
  - **The roll:** at `+0x416000..0x4160B0`, the byte is set when `RandomFloat(0, 100) < 2 × (the
    equipment sum from +0x3E5600) + the chance` (the weapon's +0x1198, or the attacker's +0x1210). It
    needs a can-critical byte too (the weapon's +0x1124, or the attacker's +0x1230).
  - **On the Wire:** the damage record's `attacker_critical`, read when `TakeDamage` arrives inside the routine.
  - **The rates (#10146):** the chance is 2 × `player_dexterity_{critical_hit + 1}` plus the short sword's `WCritHit`
    (0): 2% for a new game, 12% at level 2. Recorded: 1 of 45 and 13 of 111 power blows (docs/combat.md).
- **The player's trace:** its own sweep of rays across the swing (below, "The melee sweep"; Wire v17 records each).
- **Not alerted:** × `mul_damages_Npc_Not_alerted` (settings global `0x2279D1A0`+0x430) when
  `abs([npc+0x36EC]) <= mul_damages_Npc_Not_alerted_level` (+0x434) and `[npc+0x1A5C] != 3`. The
  record's `victim_alert` and `victim_npc_state` carry both.
- **Difficulty:** "Difficulty dmg mul", from the dynamic-difficulty object at settings+0x524
  (`mm_game_settings.txt` `Difficulty*DmgMul`). It was 1.00 in every printed blow; it isn't streamed.
- **Player multiplier:** `player_damage_multiplier`, settings+0x56C, 1.0 shipped.
- **Equipment:** the helpers at `+0x3E5D40` and `+0x3E5600` add up stats over the item slots
  (+0x18F0..+0x18FC, +0x2250).

## The player's parry

- **Stance:** the player parries by holding the parry stance. Its action at
  `+0x20F8` is 0x94 or 0x96; `CBasePlayer` slot 0x5CC (`IsParrying`) tests
  that. In Dark Messiah the stance is held on block (`+attack2`).
- **Outcome:** the result of a blow against the stance goes into
  `+0x1C78`, the field the game's own debug overlay prints as
  `PARRY_STATE_NOPARRY` / `_PARRY` / `_BREAKPARRY` (0/1/2). It is set
  through `CBasePlayer` slot 369 (`server.dll+0x3FEC50`), which ignores the
  change while the state is 2.
- **Callers:** the main one is the attack resolver
  `server.dll+0x3B1A90..0x3B39A9`, where `edi` is the attacking NPC.
  Against a parrying player, an attacker whose weapon flags (`+0x112C`, or
  `+0x1254` for unarmed NPCs) have bit 0x10 sets 2: the parry is broken.
  Other paths set 1. Other callers are at `+0x404D00`, `+0x4A0040`,
  `+0x4D38A0`, `+0x4F2780` and `+0x4FA6C0`.
- **The tap's hook:** it detours the setter through a naked-asm stub that
  captures the return address and `edi`, then jumps to a trampoline holding
  the setter's first instruction (`cmp [ecx+0x1C78],1`, 7 bytes). It
  records states 1 and 2 when the setter will apply them. The attacker is
  taken from `edi` only when the caller is the attack resolver and `edi` is
  an `npc_*`.
- **Map logic:** a successful parry also fires `MM_PlayerInputs`'s output
  `OnPlayerParryNpcAttack` (output 51, `+0x10B8`), for map scripts.
- **Weapon owners:** player weapons keep their owner in
  `CBaseCombatWeapon::m_hOwner` (+0xFA0), not `m_hOwnerEntity`. The tap
  falls back to it when resolving a weapon's wielder. Confirmed: player hits
  now read `player → npc_orc_sword`.

## Slow motion

`CMM_Player` keeps slow motion in four fields: start (+0x20A4), duration
(+0x20A8), factor (+0x20AC) and `m_bInBulletTime` (+0x20B0). The game
frame (`server.dll+0x147610`) sets `m_bInBulletTime` and sends
`mm_host_timescale <factor>` once curtime passes start, and clears it all
when the duration is over. Two functions write those fields; they are the
only writers of +0x20A4 in `server.dll` besides a reset to -1:

- **The setter**, a `CMM_Player` virtual at `server.dll+0x146E80`
  (thiscall: start, duration, factor, sound; `ret 0x10`). Its prologue
  (`mov eax,[esp+4]; mov edx,[esp+8]`, 8 bytes) moves to a trampoline. Its
  RVA came from the strings `slowtime_*` and the player's datadesc.
- **The request** at `server.dll+0x147560` (delay, duration, factor, sound;
  `ret 0x10`, called with `ecx` = a global at 0x227593C4 it never reads). It
  returns at once unless `mm_ai_bullet_time` is set, finds the local
  player through the engine, and writes start = curtime + delay,
  duration × factor, factor and sound directly. Its first two instructions
  are `mov eax,[mm_ai_bullet_time]` (a relocated absolute address) and
  `cmp dword [eax+0x2C],0`, 9 bytes. The hook checks that the relocated
  address is that cvar's before patching. Found as the other writer of
  +0x20A4.

The request has seven callers:

| Call site | What it passes |
| --- | --- |
| `+0x2CB841` | the damage info's `m_fSlowTime*`, on a player's blow with trace info (+0x44) that the victim's check (virtual +0xCB8) accepts, when the player has skill module 13 (`combat_trample`, bit 13 of +0x2798) |
| `+0x2EE9F4` | `slowtime_critical_delay/_duration/_factor` from `scripts/mm_game_settings.txt` (+0x5A0 of the settings global), on a critical hit (damage type bit 0x10000), gated by a cvar |
| `+0x2EED80` | stack values, when +0x1255 bit 1 is set and a cvar is 0 |
| `+0x440F66`, `+0x444C70`, `+0x44D64A` | an entity's delay at +0x1118 and two arguments |
| `+0x44885D` | an entity's +0x1364/+0x1368/+0x136C |

**What the recordings show (2026-10-18, l01_a, five bot runs, about 10 minutes):**
- **No call in normal play:** not on kicks, kills (a kick kill and a power
  attack kill), criticals, knockdowns or full combos. The tap's wall clock
  agreed with game time to 1.00× in every run.
- **Criticals ask for nothing:** Arkane's `mm_game_settings.txt` sets
  `slowtime_critical_delay 0`, `slowtime_critical_duration 0` and
  `slowtime_critical_factor 1`. With `mm_ai_bullet_time_forcealways 1`, 176
  requests came from `+0x2EE9F4` (returning to `+0x2EE9F9`), all ×1.00 for
  0 s, and `m_bInBulletTime` was up for 2 ticks after each.
- **The damage fields aren't slow motion:** every damage record's
  `m_fSlowTime*` (×0.10 for 2.00 s) is a default. It reaches slow motion only
  through `+0x2CB841`. That call also needs the player's Trample skill:
  - **The flag:** the word at +0x2798 is the player's skill modules (`m_uiSkillModuleEnable`), numbered as in the module-name table at `server.dll+0x79DF7C`.
  - **Bit 13:** `combat_trample`, from combat level 3 in `scripts/mm_skills_infos.txt`.
  - **The attack:** `first_basic.qct` gives the Trample attack (`layer_combo_*_trample`) ×0.5 for 2.5 s.
  - **Not reproduced:** with every module enabled (`mm_player_skills_grosbill 1`), no bot or hand-played fight downed an orc and trampled it.
  - **What does reach it (2026-10-20, #10147):** every Fatality kill recorded since asked the damage record's ×0.1 for
    2 s, 0.2 s late, from `+0x2CB846` (the return of `+0x2CB841`): four kills by the huge slash or the behead, one of
    them hand2's (129299) with a new game's modules, so the gate isn't `combat_trample` as read above. No other kill
    asked it (fast and power attacks, kicks). The Whirlwind (`layer_combo_sword_trample`) asks its `.qct` ×0.5 for 2.5 s
    as it begins, returning to `+0x44D64F` (the `+0x44D64A` call above). The lab's rule: docs/lab.md, "Slow motion".
- **Callers outside server.dll:** Arkane's copy-protected code (the `.sll`
  section, `server.dll+0x945000` on) runs from a copy in allocated memory, so
  its calls return outside the module. From 2026-10-18 21:50 the tap writes
  such a caller as `0xFFFFFFFF` (`WireSlowTime.CallerOutside`); earlier taps
  wrote a meaningless RVA.

**A reproducible slow motion (2026-10-18, `l01_a-v11-slowmo-input`).** Arkane's map scripts call the request through `mm_player_inputs`' `ActiveSlowMotion` input, with "delay duration factor" (`l02_b1` fires `0.8 3 0.2`). l01_a places that entity, named `mm_player_inputs`, so the console fires it:

```
ent_fire mm_player_inputs ActiveSlowMotion "0.8 3 0.2"
```

| Call | Request (tick) | Player state written | `m_bInBulletTime` | Game time / wall time |
| --- | --- | --- | --- | --- |
| ×0.2 for 3 s after 0.8 s | 9834, curtime 147.525, caller outside server.dll | start 148.310 (curtime + delay), duration 0.6 (3 × 0.2), factor 0.2 | ticks 9889–9928, from curtime 148.335 | 0.600 s in 2.937 s: ×0.20 |
| ×0.5 for 2 s at once | 10688, curtime 160.335 | start 160.320, duration 1.0, factor 0.5 | ticks 10691–10756 | 0.990 s in 1.970 s: ×0.50 |

- **How long it lasts:** the game time it runs is the asked duration times the factor (the request stores that). The wall time it runs is the asked duration.
- **The flag:** it rises on the first tick whose curtime passes start, and falls when that game time is over.
- **Over the 40 s recording:** game time ran 36.67 s, 3.33 s behind the wall clock: (3 − 0.6) + (2 − 1) = 3.4 s.

The stepper shades the frames where `m_bInBulletTime` is set, lists each
call with its caller, and shows the game-time scale next to the clock when
it differs from 1. `rbwire info` groups calls by source and caller.

## The melee sweep

The addresses behind the reach model (docs/combat.md, "Reach") and the player's swings, found 2026-10-19 in
`server.dll` (image base 0x22000000) by disassembly and checked live. From v17 the tap hooks the sweep (below).

| Address | What |
| --- | --- |
| +0x3B1A90 | the attack resolver (`edi` the attacker), also named under "The player's parry" |
| +0x3B1CED | aims at the attacker's enemy when the cosine to it is above 0.3 (a `double` at +0x56EA38), else along its facing |
| +0x3B2009 | an armed attacker's sweep: its length is `[edi+0x11FC]`, the current attack part's `range` |
| +0x3B2270 | an unarmed attacker's sweep: its length is `[edi+0x1258]`, its `baseattacklength` |
| +0x3CB310 | the sweep (`thiscall` on the attacker: out, length, a flag, a type, start, direction, ignore list) |
| +0x3CAE70 | a cylinder test the sweep calls for some victims; it moves a blow's end point on the player |
| +0x3A7737, +0x3AC3D4 | the attack-data loader: `range` (default 120) into `+0x11FC` |
| +0x3DD56C | a combat move's constructor: `range` defaults to 120 |
| +0x3B16D7 | the entity-data loader: `baseattacklength` (default 50) into `+0x1258` |

**The sweep's size is a ConVar per type.** The type picks one, by the attacker's weapon class:

| Type | ConVar | Default |
| --- | --- | --- |
| 0 | `collision_inc_size_ray` | 1.5 |
| 1 | `collision_inc_size_ray_less_prec` | 2 |
| 2 | `collision_inc_size_ray_only_weapon` | 3 |
| 3, 6 | `collision_inc_size_ray_only_npc` | 5 |
| 4 | `collision_inc_size_trample` | 7 |

- **The defaults:** read from the ConVars' static constructors at +0x54D920..+0x54DA81. `collision_inc_cylinder` (5) and
  `collision_start_size_ray` (0) are there too.
- **Debug view:** `mm_show_testattack 1` makes the routine draw its sweeps.

**How the lengths were confirmed.** A temporary tap build hooked +0x3CB310 and logged each call's caller and
arguments while the parry bot fought two orcs and a ghoul. It isn't committed.
- **The orc:** called from +0x3B200E with 70 and 60, its attacks' ranges.
- **The ghoul:** called from +0x3B2275 with 50, `baseattacklength`'s default, since its `.qct` gives none.
- **Type:** both used type 0.
- **The field at rest:** the ghoul's `+0x11FC` read 120 throughout, the move default, which the unarmed branch doesn't use.

**The hook (v17, #10179).** The sweep's player and NPC paths meet at +0x3CB5C8 with the start at `[esp+0x2C]` and the
direction at `[esp+0x38]`, before it reads the type (`[esp+0xBA8]`) to pick the box: `mov ecx,[cvar slot]` there (6
bytes, the slot's address relocated) becomes a jump to a stub that records the sweep and performs the instruction. The
hook checks those 6 bytes, the relocated slot, and the next instruction (`mov eax,[esp+0xBA8]`), and stays off if they
differ. Each sweep (56 bytes):

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `tick` i32 | |
| 4 | `attacker` u16 | the routine's `this` (ebp), edict index |
| 6 | `type` u8 | the type argument (`[esp+0xBA8]`): the box size above |
| 7 | `flags` u8 | 1: the player held an entity at +0x2388 |
| 8 | `start` 3 × f32 | `[esp+0x2C]` |
| 20 | `direction` 3 × f32 | `[esp+0x38]`, unit |
| 32 | `range` f32 | the length argument (`[esp+0xBA0]`) |
| 36 | `view_angles` 3 × f32 | the player's angles at +0x1F88; zero for an NPC |
| 48 | `caller` u32 | the return address's RVA |
| 52 | `attacker_serial` u32 | |

**What it shows for the player** (`l01_a-v17-chained-slash`, `-fatality`; `rbwire info` groups them by caller):
- **The swing's rays** come from the resolver (+0x3B200E, type 0, range 120, so a box of 1.5 units). A tick sweeps
  several rays, 7° to 11° apart, fanned across the arc the swing covered since the last tick, until one meets something.
- **The start** is 9 units above the client's eye (virtual +0x2D8, `EyePosition`); **the angles at +0x1F88** equal the
  client's view: the arc isn't camera sway.
- **The arc** is a line in each clip's cycle, in a tilted plane: side = a + b × cycle and up = tilt × side, against
  the view. Fits over the two recordings (residuals 3° to 7°):

  | Clip | a | b | tilt |
  | --- | --- | --- | --- |
  | `fastattack_A` | −107.7 | 215.7 | −0.17 |
  | `fastattack_B` | 189.7 | −301.3 | 0 |
  | `fastattack_C` | −139.4 | 221.2 | 0.46 |
  | `fastattack_D` | 104.2 | −163.5 | −0.45 |
  | `fastattack_E` | −148.1 | 218.3 | −0.17 |
  | `bigslash_out` | −111.2 | 805.3 | −0.78 |

  The huge slash's few rays lie between 0° and 31° left, tilted −0.95. Where the arc comes from (an animation the
  sweep's direction virtual +0x2DC turns the view by) isn't read; the lab takes the arcs (docs/lab.md, "The blade's
  trace").
- **Before each attack** a type-2 ray (box 3) of range 100 goes straight ahead from +0x432A13, and types 7 and 8 come
  from +0x42F1E2 and +0x42ECC0: checks, not blows.
- **The NPCs** sweep through the same resolver: an orc's at its attack's `range` (70), a ghoul's at 50 from +0x3B2275.

## The victim's pose

The contact sweep (docs/combat.md, "Contacts") poses a struck character where
the donor's trace met it.
- **What the hit-box test reads:** the server builds the victim's bones from its
  current `m_nSequence`, `m_flCycle`, pose parameters and overlays when the trace
  runs.
- **When those change:** a character changes them only in its own think, every
  8 ticks for an NPC.
- **What the Wire showed before v13:** each entity as the frame ended.
- **v13:** the damage hook copies the victim's animation as the call arrives,
  before calling the original `TakeDamage`, which can already start a hit
  reaction. A damage call follows its trace in the same attack, so this is the
  state the trace met.

**Finding the fields.** They come from the send tables, like the rest (below):
- **`m_flAnimTime`:** a plain prop.
- **`m_flPoseParameter`:** a `SendPropArray3`, a data table of elements `000`..`023` whose own offset is the array's.
  In l01_a every character class has the pose parameters at +0xEC8, just after `m_nSequence` (+0xEC4), and
  `m_flAnimTime` at +0x26C.
- **Overlays:** read as for the frame's layers, active and weighted, up to 8.

**What a recording showed** (`l01_a-v13-arena-pose.rbrec`, the parry bot against two orcs and a ghoul, with
the level's guards):
- **The frame's end:** of 51 traced blows on NPCs, 50 found the victim as the frame ended.
- **The exception:** an orc that thought after the blow in the same tick, whose frame-end cycle was one think
  ahead (0.736 against 0.720).
- **The previous frame:** it held the state for 46.
- **Pose parameters:** near 0.5 throughout, the middle of their ranges.

## Kicks, knockdowns and adrenaline

- **Kicks:** a kick is a damage record with damage type 0x40000, weapon flags 0x20,
  no weapon and `mm_damage_flag` bit 0x400000, while the player plays
  `attack_kick_medium` (view-model layer `layer_sword_noshield_kick_medium`).
  It asks for 1 (the kick's `HitPow` on the player's base) and has about
  four times a sword's force (12,788). `push_force` is 0 on the kick itself.
- **Knockdowns:** a kick that knocks the NPC down sets `mm_damage_flag` bit
  0x1000 (0x401040). It is followed by a second record from the player
  with damage 0, `push_force` 2 and slow time ×1 for 0. The NPC's
  `m_hRagdollFall` then names the `prop_ragdoll` that falls in its place,
  and the ragdoll's `m_hUnragdoll` names the NPC. The tap only writes a link
  when the handle names a live entity of the expected kind (`prop_ragdoll`,
  or an `npc_*`), with a matching serial.
- **Adrenaline:** it rises by the `adrenalineadd` of each attack that lands
  (0.5 for fast attacks, 1–3 for power attacks, from `first_basic.qct`)
  and decays. `max_adrenaline` is 20 in l01_a. The highest seen was 10.5.

## Alertness

Since v18 every `npc_*` entity carries, each tick, the two fields the melee routine's not-alerted multiplier reads
(server.dll+0x407D70; "Melee damage" above): the alert level, the magnitude of NPC +0x36EC (the same read as the
damage record's `victim_alert`, v11), and the NPC state, +0x1A5C (the damage record's `victim_npc_state`). They are
plain reads: no hook patches code for them. The offsets come from the melee routine's own reads, at
server.dll+0x408692 (`mov ecx,[esi+0x36EC]`, its absolute value compared with `mul_damages_Npc_Not_alerted_level`,
settings +0x434) and +0x4086AE (`cmp [esi+0x1A5C],3`). A read that faults leaves flag 4 off for that entity.

## Shrink

Since v19 every entity carries two of Arkane's send props: `size`, `m_fMMSize` (+0x12C, a float, 1 unscaled), and
`shrinked`, `m_iShrinked` (+0x130). They are plain reads, found by name in each class's send table like the animation
fields ("Finding fields" below; registered at server.dll+0x3BE2F5, `push 0x130; push "m_iShrinked"`, and +0x3BE31B,
`push 0x12C; push "m_fMMSize"`), so no hook patches code for them; a class whose table lacks them leaves flag 8 off.

How the donor shrinks an NPC (#10348), read from server.dll:
- **It's air_4 with adrenaline.** `CARXSPELL_AIR_4_Spell`'s cast (vtable slot 363, server.dll+0x471770) sets its
  `bTrueShrink` (+0x12C0, the datadesc entry at +0x7A8FE0) when the spell weapon in the spell manager's slot
  (server.dll+0x838ADC, +0x84 + the spell id × 4, the id at +0x12D8) has its `m_adrenalinepower` byte (+0xBE0) set.
  Without it the cast weakens (#10167). The console's `Deploy_Spell_*` guard
  (server.dll+0x3E67E0) checks the learned module for each spell id; id 19 (`Deploy_Spell_AIR_4`) is checked against
  `shrink`, and id 13 (`Deploy_Spell_EARTH_3`) against nothing, so it never deploys: `earth_3` isn't a player spell.
- **The cast** (+0x471A4B on) keeps the NPC's health (`fHealthBeforeShrink`, +0x128C), sets it to 1
  (`push 1.0; call +0x44B40`), and builds the model name, its own with `_shrinked.mdl` for `.mdl` (+0x4719DC).
- **The shrink** steps `m_fMMSize` toward 0.2 (`0x3E4CCCCD`, +0x471EC1; the step +0x471170), then swaps the model, sets
  `m_fMMSize` to 0.999 (`0x3F7FBE77`, +0x472057) and `m_iShrinked` to 1 (+0x47206F). The weaken path sets 0.9999
  (`0x3F7FF972`, +0x471C0F). `CBaseEntity` reads 0.999 with `m_iShrinked` bit 0 as unscaled (+0x397253).

What the recordings show of it (`l01_a-v19-shrink`, `-2`, `-3`) is in lab.md, "Shrink".

## Finding fields: send tables

The tap doesn't hard-code those offsets. The first time it meets a class name
in a level, it calls the networkable's `GetServerClass` (slot 2), walks the
class's `SendTable` (nested data tables add their offsets) and picks fields
out by name. The log lists what it found per class (`netprops:` lines).

- This build's `SendProp` is Episode One's 76-byte layout, but data-table
  props have type 6, not 5: its `SendPropType` includes `DPT_VectorXY`.
- Arkane replaced `m_iHealth` with floats, `m_fHealth` and `m_fMaxHealth`.
  Most classes don't network them; the shield classes do. Since they're
  `CBaseEntity` members, the offset found there holds for every entity.
- `m_lifeState` never changes for NPCs in practice: they're removed in the
  tick they die (see recording.md).

## Protocol

The tap is the only writer and never waits for readers.

- Frame `f` lives in slot `f % 128`. To write it, the tap sets the slot's
  `seq` to `2f+1`, fills the slot, sets `seq` to `2f+2`, then sets
  `frames_written` to `f+1`. All three stores are `InterlockedExchange64`.
- A reader reads `seq`, copies the slot, issues a full barrier and re-reads
  `seq`. The copy is good if both reads were `2f+2`. Anything else means not
  written yet, overwritten by frame `f+128`, or torn by that overwrite.
- The tap writes one frame per donor tick (when `tickcount` changes in
  `GameFrame`), so nothing is written while the game is paused.
- Class and model rows are written before the first frame that refers to
  them, so a reader can resolve any id it sees in a good frame.
- `LevelInit` clears both tables and increments `level_serial`. Class ids and
  model indices mean nothing across levels; readers key their name caches by
  the slot's `level_serial`.
- On `Load` the tap zeroes the whole file and writes `magic` last. A reader
  that sees the wrong magic should wait.

## Measured (2026-10-17, den-win11, l01_a)

- 66.7 frames/s at a 15 ms donor tick; about 1450 entities and 131 classes
  per frame.
- Tap cost 150–180 µs per tick, about 1% of the tick, with no faults (v1 and
  v2 alike).
- `rbwire tail`, polling every 1 ms from an SSH session, read every frame in
  order: 0 lost, 0 retries.

## The client's view

In single player `client.dll` and `engine.dll` run in the tap's process. v8
reads from them what the server doesn't have, into one 232-byte record per
slot (v8 wrote the first 80 bytes):

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `player` u16 | the client's local player index (`GetLocalPlayer`); 0xFFFF when not read |
| 2 | `viewmodel` u16 | the client entity read as the viewmodel: the server viewmodel's index |
| 4 | `flags` u32 | 1 view angles, 2 player transform, 4 viewmodel transform, 8 view offset |
| 8 | `view_angles` 3×f32 | the client's view, `cl.viewangles` (pitch, yaw, roll) |
| 20 | `origin` 3×f32 | the client's local player abs origin (predicted) |
| 32 | `angles` 3×f32 | its abs angles |
| 44 | `view_offset` 3×f32 | the server player's `m_vecViewOffset` (+0x774, a send prop); the eye is `origin + view_offset` |
| 56 | `viewmodel_origin`, `viewmodel_angles` 3×f32 each | the client viewmodel's abs transform, which the server's `viewmodel` entity doesn't keep |
| 80 | `camera_origin`, `camera_angles` 3×f32 each | v9: the camera the client last drew with: its `CViewSetup` origin and angles, view bob, roll and third person included |
| 104 | `fov`, `fov_viewmodel` f32 | horizontal, degrees (101.4 at 1280×720: Dark Messiah's 85° at 4:3, widened) |
| 112 | `screen_width`, `screen_height` u16 | |
| 116 | `z_near`, `z_far` f32 | |
| 124 | `viewmodel_sequence` i16, reserved u16 | the client viewmodel's `m_nSequence`, -1 when not read |
| 128 | `viewmodel_cycle` f32 | its `m_flCycle` |
| 132 | `layer_count` u8, 3 reserved | |
| 136 | 8 × `{sequence i16, order i16, cycle f32, weight f32}` | its overlay layers with weight, in its own order: Dark Messiah's first-person swings, parries and charges play here while the main sequence stays idle |

Flags 16 (camera), 32 (viewmodel sequence and cycle) and 64 (viewmodel
layers) are v9's.

It is read in the server frame, so it is the client's state as of its last
frame.

**How the interfaces were read (2026-10-18).** A one-off probe logged the
live vtables of `VEngineClient012` (engine.dll), `VClientEntityList003` and
`VClient013` (client.dll), and their slots were read in a disassembly:

- **`IVEngineClient`:** the 2006 SDK's layout with two extra slots near the start.
  - **`ClientCmd` is slot 9:** `mov eax,[esp+4]; push eax; call Cbuf_AddText`, then a newline.
  - **`GetLocalPlayer` is slot 14:** `mov eax,[abs]; add eax,1; ret`.
  - **`GetViewAngles` is slot 21:** it copies three floats from `cl.viewangles` at a fixed address.
  - **How the tap uses them:** it reads the two addresses out of that code, and calls only `ClientCmd`.
- **`IClientEntityList`:** the SDK's order.
  - **`GetClientEntity` is slot 3:** `add ecx,-0x10014`, then `GetClientUnknown`, then `IClientUnknown` slot 6, `GetIClientEntity`.
- **`IClientEntity`:** no `Release` before the getters, unlike the SDK.
  - **`GetAbsOrigin` is slot 9:** `CalcAbsolutePosition`, then `this+0x440`.
  - **`GetAbsAngles` is slot 10:** `CalcAbsolutePosition`, then `this+0x458` or `this+0x324` by an entity flag (0x200).
- **`IBaseClientDLL` (`VClient013`):** the SDK's slots shifted down by one.
  - **`GetAllClasses` is slot 5:** `mov eax,[abs]; ret`. The client's RecvTables use Valve's 60-byte
    RecvProp (name at 0, type at 4, data table at 40, offset at 44; a data table is type 6, as on the server).
  - **`View_Render` is slot 23:** it checks the rect, then calls the global `view` (`IViewRender*`) through
    `mov ecx,[abs]`.
- **`IViewRender` (`CViewRender`, vtable client.dll+0x327294):**
  - **`GetViewSetup` is slot 12:** `lea eax,[ecx+0x8C]; ret`, the `CViewSetup` the client last drew with.
  - **`CViewSetup` (128 bytes), read from a dump on the horse ride:** width +0x0C, height +0x10,
    fov +0x28, fov viewmodel +0x2C, origin +0x30, angles +0x48, zNear +0x54, zFar +0x58, aspect +0x68.
- **`C_BaseViewModel`:** `DT_BaseViewModel` derives from `DT_BaseCombatCharacter`.
  - **Main animation:** `m_nSequence` +0x1878, `m_flCycle` +0x187C.
  - **Overlay layers:** `m_AnimOverlay` is a `RecvPropUtlVector`. Its element props point to
    `CRecvPropExtra_UtlVector`, whose element stride (32), vector offset (+0x18F0) and maximum (15) the tap reads.
    The fields within an element come from `DT_Animationlayer`: sequence +0, prevcycle +4, weight +8, order +12, cycle +20.
- **Before use:** each function's code is checked against these patterns (relocated addresses masked) before it is read or called. A mismatch turns that part off and logs why. engine.dll loads at different bases between runs, which the checks allow.

**Checked against the screen (2026-10-18, held ticks, desktop captures):**
- **First person:** with `rbwire exec setang 0 190 0`, l01_a's gate guard (#361, 125 units
  away), projected through the eye and view angles, falls 6° left of centre. The capture shows it there.
- **Horse ride:** the camera is about 50 units from the reconstructed eye (origin + view
  offset), and its angles carry the ride's bob. The reconstruction is wrong there; the camera is what drew.
- **Third person** (`thirdperson`, on open ground):
  - The camera sits 195 units behind and above the player, at pitch 30 and yaw 136, while the view angles are 10 and 116.
  - Projected through the camera, the player model's middle lands at (637, 434) of 1280×720, where the capture shows it.
  - Through the eye and view angles it would be off screen.
- **A held power attack:** three holds, charging, charged and mid-release, show the client viewmodel's layers
  `layer_bigslash_sword_noshield_in` at 0.898, then `_in` done and `_idle`, then `_out` at 0.398.
  The captures show the sword rising across the view, held high right, then sweeping down left.
  The client's cycles differ from the server viewmodel's (0.644 against 0.800 charged, 0.398 against 0.418 mid-release),
  which is why they're read from the client.
- **Standing:** the client's player sits on the server's; on the horse ride it is about 3 units off.

## The player's input

v12 streams what the player pressed, so a recorded fight can drive the combat
lab with the same input (docs/lab.md, "Replay"). v14 adds the user commands
themselves. One 48-byte record per slot (16 bytes in v12 and v13):

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `player` u16 | the edict the buttons were read from (1); 0xFFFF when not read |
| 2 | `valid` u8 | 1 when read |
| 3 | `commands` u8 | v14: user commands the player received this tick |
| 4 | `buttons` u32 | `m_nButtons` (+0x1FB8): `IN_*` bits held in the tick's command |
| 8 | `pressed` u32 | `m_afButtonPressed` (+0x1FC8): bits that went down in it |
| 12 | `released` u32 | `m_afButtonReleased` (+0x1FCC): bits that went up in it |
| 16 | `forward_move`, `side_move`, `up_move` f32 | v14: the newest command's forwardmove (+0x18), sidemove (+0x1C), upmove (+0x24) |
| 28 | `command_buttons` u32 | v14: the commands' `IN_*` buttons (+0x28), OR'd over the tick |
| 32 | `actions` u32 | v14: bit *i* set when Arkane's action byte +0x95+*i* is set in any of the tick's commands (below, "The action bytes") |
| 36 | `impulse` u8, 3 reserved | v14: the newest command's impulse (+0x2C) |
| 40 | `command_number` i32, reserved u32 | v14: the newest command's number (+0x4) |

The v12 words are read from the player's server entity after each frame, so they
are what the last command left. The view angles are the client view's
(above).

**The user commands (v14).** The tap hooks `CBasePlayer::ProcessUsercmds`
(server.dll+0x430350; `CBasePlayer`'s and `CHL2_Player`'s vtable slot 0x864):
- **When it runs:** the engine's command reader (+0x14880A) calls it with each batch it read. It copies
  the commands into the player's command context, to run later in the frame, so the hook sees them before
  any game code consumes them.
- **Which command:** `cmds[0]` is the newest of the `numcmds` new ones.
- **Patching:** the first 10 bytes are checked before patching (`push ecx; push ebx; mov ebx,ecx; mov eax,[ebx+0x23BC]`),
  and restored on unload.

Arkane's `CUserCmd` is 0xBC bytes, and the delta reader at +0x45E950 gives its fields:
- **The SDK's:** up to +0x48 (command number, tick, view angles, the three moves, buttons, impulse, weapon select, random seed, mouse).
- **Its own:** +0x4C to +0x5C, +0x88 to +0x90, single bytes +0x95 to +0xA1, and +0xA4.

**How the kick was found (2026-10-19).** A temporary build logged every command the reader filled while known keys
went in through the agent's desktop lease:
- **W:** forwardmove 500 then 1000, `IN_FORWARD`.
- **Left and right click:** `IN_ATTACK` and `IN_ATTACK2`.
- **Space and E:** `IN_JUMP` and `IN_USE`.
- **F:** byte +0x97 set for as long as it was held, and nothing else.
- **Sprint (+0x95), from the hand-played fights:** running ticks with it set have a median speed of 229 units/s, against
  195 for all running ticks, and the stamina it drains at 2 a second explains the kicks refused for stamina
  (docs/lab.md, "Stamina").

**The action bytes (2026-10-19, `l01_a-v15-stats.rbrec` and `l01_a-v15-spells.rbrec`).** Each action
`scripts/kb_act.lst` lists was bound to a spare key with `rbwire exec "bind ..."` and pressed through the desktop
lease, with the time of each press logged beside the recording and lined up with it by the sprint:

| Action | In the command |
| --- | --- |
| `+sprint` | byte +0x95 (bit 0), while held |
| `+kick` | byte +0x97 (bit 2), while held |
| `+xana` | byte +0x9A (bit 5), while held (41 ticks for a 0.6 s hold) |
| `mm_previtem`, `mm_nextitem` | byte +0x9E (bit 9), one tick per press. Each session registered one of the two, whichever came first: one byte, likely signed by direction, which the Wire reduces to set or not |
| `togglecharsheet`, `mm_open_charsheet` | opening the sheet sets +0x9B (bit 6, `CharSheetOpen`), closing it +0x9D (bit 8, `CharSheetClose`), one tick each: 8 of 8 opens and closes from the console and the keyboard (`charsheet`, `charsheet-keys`, R10139-3) |
| (unknown) | +0x9F (bit 10) came twice in `l01_a-v15-stats` (a tick after a keyboard open, and near a spell click) and in none of the 8 opens since, nor with 6 fire casts and 6 telekinesis holds (`spells-probe`, `telekinesis`): not the spell click. Not named |
| `+speed` | `IN_BULLRUSH` (bit 22) in the buttons, while held |
| `+zoom` | `IN_WALK` (bit 18) in the buttons, while held |
| `+leanleft`, `+leanright` | nothing in the command, standing or walking |
| `usebelt1`–`usebelt9` | nothing in the command (the first put the weapon away, so the belt travels another way) |

Bytes +0x96, +0x98, +0x99, +0x9C, +0xA0 and +0xA1 never set. `PlayerActions` names sprint, kick, xana, the sheet's
open and close and the item cycle; `InputButtons` notes the two buttons.

**What the server does with them.** `CMM_Player`'s command handling (server.dll+0x45551A on) tests the bytes from
+0x9B on and raises one event each on the map's `mm_player_inputs` entity (+0x3FEED0, which finds the entity by that
name and passes the event to it): +0x9B event 0x17, which also clears the player's button words (+0x1FC0 to +0x1FCC:
the sheet takes the input), +0x9C 0x18, +0x9D 0x29, +0x9E 0x19, +0x9F 0x1A, +0xA0 0x37 and +0xA1 0x38. The entity's
outputs (`On_OpenInventory`, `On_openbelt`, `On_exitbelt`, `OnOpenObjectives`...) are what the map's tutorials hook;
which output each event fires isn't read.

## The player's stats

v15 streams the player's stats and equipment each tick, for the lab's stamina, skills and builds (Den
`rusty-bridge/dark-messiah-inventory`); v16 adds each skill's level. One 112-byte record per slot (80 in v15), after the input, read from `CMM_Player` after
the frame, only when edict 1 is a `player`, inside a fault guard:

| Offset | Field | Source |
| --- | --- | --- |
| 0 | `player` u16, `valid` u8, reserved u8 | edict 1; 0xFFFF and 0 when not read |
| 4 | `stamina`, `max_stamina` f32 | `m_fStamina` (+0x29C4), `m_fMaxStamina` (+0x29C0) |
| 12 | `mana`, `max_mana` f32 | `m_fMana` (+0x29BC), `m_fMaxMana` (+0x29B0) |
| 20 | `modules` u32 × 2 | the skill modules learned (+0x2AF0), bit *i* = module *i* (below) |
| 28 | `skill_points` i32 | `m_iSkillPoints` (+0x2B3C) |
| 32 | reserved i32 | 0 |
| 36 | `weapon`, `shield`, `helmet`, `ring`, `armor` u16, reserved u16 | `m_hActiveWeapon` (+0x18F0), `m_hActiveShield` (+0x18F4), `m_hActiveHelmet` (+0x18F8), `m_hActiveRing` (+0x18FC), `m_hActiveArmor` (+0x2250): edict indices, 0xFFFF for none |
| 48 | their serials u32 × 5, 3 reserved u32 | |
| 80 | `skill_levels` u8 × 32 | v16: each skill's level, in the game's skill table order (26 used, below), from the int array `m_skillLevel` (+0x2AC8) points at |

**How the offsets were found (server.dll, image base 0x22000000).**
- **The datadesc:** as for the buttons (above, "How the offsets were found"): `m_fStamina` (slot 0x227A14F8),
  `m_fMaxStamina` (0x227A1594), `m_fMana` (0x227A1490), `m_iSkillPoints` (0x227A1664), the five `m_hActive*`
  handles (from 0x227989C4).
- **Send props:** `m_fMaxMana` +0x29B0 (`push 0x29b0; push "m_fMaxMana"` at +0x425CD6), beside `m_fDisplayMaxMana` +0x29B4.
- **The armour handle:** `m_hActiveArmor` +0x2250, datadesc slot 0x227A0998 (written at +0x41B816/+0x41B820).
- **Checked at load** (`CheckStatsOffsets`): each datadesc slot above must hold its field's name and offset, the
  max-mana push its bytes and name, and the modules' and levels' reads their instructions
  (`8B 84 96 F0 2A 00 00` at +0x3E55E4, `8B 96 C8 2A 00 00` at +0x3E64FD). If any differs, the tap logs which and
  leaves the stats record invalid (`valid` 0); everything else still records.
- **Which stamina:** a static table also names `m_fCurStamina` at +0x2974, but only code outside the player's (around
  +0x2BEBAE) writes it; the player's code (+0x3F9A87 on) writes +0x29C4. The recordings bear it out (below).
- **The skill modules:** `mm_player_skills_dump_modules` (+0x40D890) asks a has-module function (+0x3E5590) for each
  name in a 37-entry table (0x2282BE80). That function finds the name's index *i* and tests bit *i* of two dwords at
  the player's +0x2AF0. The table is copied at start-up from 0x2279DF38, with two entries swapped (`combat_big_slash`
  and `combat_huge_slash` before `combat_chained_slash`); `SkillModules` and `SkillModuleNames` keep that order.
- **The skill levels (v16):** a get-level function (+0x3E64D0) finds a skill's index in a 26-entry name table
  (0x2282BF18, copied at start-up from 0x2279DFCC in the same order: `combat`, `projectile`, `stamina`, `extra_damage`,
  `extra_health`, `critical_hit`, `extra_mana`, `adrenaline`, `fast_metabolism`, `poison_recovery`,
  `mana_regeneration`, `stealth`, `mechanism`, `focus`, then the `magic_*` skills) and returns that int of the array
  `*(player+0x2AC8)`. `backstab`, in `mm_skills_infos.txt`, isn't in the table. `SkillNames.ByIndex` keeps the order.
  `mm_player_skills_grosbill 1` sets every skill to its maximum level (`l01_a-v16-chained-slash`).
- **Left out:** `m_CurSpell` (+0x3CB0, a static table's) never left 0, even with a spell weapon in hand, and other
  classes write floats there. A spell is a weapon (`spell_fire_1`, `spell_air_3`...), so `weapon`'s class names it.

**What the recordings showed:**
- **Live, on a new game:** stamina 9/9, mana 20/20, and the modules exactly `mm_skills_infos.txt`'s combat level 0
  (`combat_fatality`, `combat_parry`, `combat_big_slash`, `combat_huge_slash`), which confirms the bit order.
- **Stamina:** a 3 s sprint and three kicks took it to 4.32. After `mm_player_skills_grosbill 1` (every module) its
  maximum became 12, `player_stamina_double_sprint_max`.
- **Mana:** it regenerates about 0.7 a second. The skills raised its maximum to 100 and the ring of mana to 110, as
  the HUD showed. Casts spend it (`l01_a-v17-spells-probe`, R10139-2): with `Deploy_Spell_FIRE_1` in hand
  (`weapon_MMSpell_weapon_FIRE_1` in the weapon slot), a 1.5 s held cast took it from 24.2 to 17.6 and two short
  ones from 19.7 to 15.7 and 17.8 to 13.8; telekinesis (`weapon_MMTelekinesis_physcannon`) lifting the jug took it
  from 26.3 to 22.3 as it gripped (`l01_a-v17-telekinesis`). `give spell_fire_1` puts the spell in hand but doesn't
  cast; `give weapon_mmspell` crashes the game.
- **Equipment:** the sword, shield, helmet, ring, two staffs and the two spells each showed in their slot as given.

**How the offsets were found (2026-10-19, server.dll, image base 0x22000000).**
These fields aren't networked, so no send table names them. `CBasePlayer`'s
datadesc names them, but its table is filled in at start-up rather than
stored whole. For each field, a static initializer writes the name
pointer, then the offset, into one table slot:
- **The name:** `mov dword [slot], name` (`c7 05`), for example at
  server.dll+0x41AFF0 for `m_nButtons`.
- **The offset:** `mov dword [slot+4], offset`. The slots are 0x227A0554 for
  `m_nButtons`, 0x227A0658 for `m_afButtonPressed` and 0x227A068C for
  `m_afButtonReleased`.

The same scan gave `m_afButtonLast` +0x1FD0 and `m_nImpulse` +0x2678, which
the tap doesn't read. Nothing is patched. The tap reads only when edict 1's
class is `player`, inside a fault guard.

**What the v12 bits showed (`l01_a-v12-arena-bot.rbrec`, 120 s, the parry bot
swinging, kicking and blocking):**
- **Held and pressed bits:** only `IN_ATTACK`, `IN_ATTACK2`, `IN_FORWARD`,
  `IN_LEFT` and `IN_RIGHT` ever appear. `IN_LEFT` and `IN_RIGHT` are the bot's turning binds.
- **No kick bit:** none of the recording's kicks (eight started the kick
  clip) set a bit, in `buttons` or in `pressed`. The kick is an action byte in the user command (above), which v14 records.
- **Kicks before v14:** the lab's replay reads a kick press from the start of
  the kick's first-person clip (`layer_sword_noshield_kick_medium`), which the
  client view's layers carry. That misses a kick the game refused. From v14
  it replays the requests themselves.

**The view model after a save load.** A loaded save recreates the view
model, often at a high edict index; l01_a's `rb_start` put it at 1279. The
tap used to look for it among the first 64 edicts and cache it for good, so
recordings that began with a loaded save had no view-model layers. The tap
now finds it again when the cached index stops naming a `viewmodel`, and
searches every edict.

## Holding the donor

`rustybridge.control` beside the Wire (4,096 bytes; the tap creates it, a
reader maps it read-write) holds the donor on a tick and steps it:

| Offset | Field | Who writes it |
| --- | --- | --- |
| 0 | `magic` u64 `"RBCTRL01"`, `version` u32 (1), `writer_pid` u32 | tap |
| 16 | `hold` i32 | reader: 1 holds the donor after each tick, 0 runs it |
| 20 | `step_requests` u32 | reader: ticks asked for, a running count |
| 24 | `steps_done` u32 | tap: ticks released while held |
| 28 | `held_tick` i32 | tap: the tick it is holding at, or -1 |
| 32 | `lease_until_ms` u64 | reader: the hold lapses at this `GetTickCount64` time |
| 40 | `holds_lapsed` u32, reserved u32 | tap |
| 48 | `command_seq` u32 | reader: bumped after writing `command` |
| 52 | `command_done` u32 | tap: the `command_seq` it last ran |
| 56 | `command` char[256] | reader: a console command |

- **Where it holds:** the tap holds inside its own `GameFrame`, right after writing a tick's
  Wire frame. The whole host frame waits there, the client too in single
  player, so the game shows that tick and the Wire's latest frame is it.
- **Exact steps:** each `GameFrame` is one server tick, so releasing one call steps exactly one
  tick, even when the engine runs several ticks in a frame to catch up after a hold.
- **While held:** the tap peeks the message queue without removing anything, so Windows
  doesn't mark the window as not responding. The window shows the held frame.
- **Lease:** a hold lapses at its lease, ten minutes after each `rbwire` command, so a reader that
  went away can't freeze the game for good.
- **Commands:** a command is passed to `IVEngineClient::ClientCmd` and runs on the next
  frame. That is how a hidden donor, which takes no keyboard input, is staged.

Measured on l01_a (2026-10-18):
- **Holding:** a hold at tick 399 stayed there 8 s with the process responding.
- **Stepping:** `step`, `step 10` and `step 66` advanced exactly 1, 10 and 66 ticks, one Wire frame each.
- **Resuming:** `run` resumed without a catch-up burst; the engine clamps the time lost.

## A hidden donor

`-noshaderapi` loads `shaderapiempty.dll`: the game draws nothing (a black
screen saying "Running with -noshaderapi"), but it still opens a fullscreen
window and switches the desktop to 1024×768. It ignores `-window`, `-w` and `-h`.
With `-rbhidden` as well, the tap puts the desktop's display mode back
(`ChangeDisplaySettings(NULL, 0)`) and hides the game's windows once a level
has run 5 s.

With `-condebug -console -noshaderapi -rbhidden -novid +map l01_a`:
- **Still works:** the server simulation, the plugin, the Wire (66.9 ticks/s, nothing lost) and
  holding and stepping. The process used about a third of a core.
- **Breaks:**
  - Input through the playtest agent: the hidden window is never in front, so the parry bot and console typing can't reach it. Stage with `rbwire exec`.
  - The client view's eye and viewmodel are still computed, but nothing draws them.
- **Sound** still plays: the user heard the game from den-win11's monitor speakers while it ran hidden.
- **Still needed:** the console session on den-win11, because Steam launches the game there.
- **Stopping:** killing a hidden donor with `Stop-Process` doesn't put the desktop back if the tap hadn't yet. `ChangeDisplaySettings(NULL, 0)` from the console desktop does.

## Mantling and ladders (#10163)

**Mantling** isn't on the Wire nor reproduced. server.dll registers its ConVars at 0x2254F9A0 on
(`mm_player_active_mantling`, object 0x2282C1E0; `mm_player_mantling_min_ground` 0x2282C228;
`mm_player_step_min_mantling_min` 0x2282C270). The movement code tests a mantle from 0x223F2707: with
`mm_player_active_mantling` set, the player not in states 0x150 to 0x16x (+0x20F8) and +0x1ECC clear, IN_JUMP held
(`mv+0x24 & 2`) and a step height (`mv+0x64`) between two ConVars (0x2282C168, 0x2282C288), it traces a hull (mask
0x201400B, 0x223ED130). The defaults: `mm_player_step_mantling` 85, `_step_min_mantling_min` 35 and `_max` 70,
`mm_player_time_mantling` 0.2, `mm_player_mantling_perc_stop` 0.8, `mm_player_max_poly_cos_mantling` 0.7. #10329
reads the rest.

**Ladders** put nothing on the Wire beyond the player's origin; l01_a has none. Their ConVar defaults, read from the
registrations (the default string pushed before each name): `mm_player_ladder_speed` 80, `mm_player_ladder_offset` 9,
`mm_dist_to_exit_ladder` 5, `mm_time_to_exit_enter_ladder` 1, `mm_angle_to_exit_ladder` 0, `mm_ladder_friction_pitch`
60, `mm_min_ladder_move` 0.1, `mm_player_ladder_tolerance_error` 0.001, `sv_autoladderdismount` 1,
`sv_ladderautomountdot` 0.4 (`mm_player_ladder_delta_yaw` 45 is the client's).

## Known gaps

- Slow motion is streamed as the game's requests and its flag, not as
  `host_timescale` itself; a time scale set by a map script through the
  console would show only in the wall clock.

- The server `viewmodel` entity's origin and animation lag the client's;
  read the client view's viewmodel transform, sequence and layers instead.
  The client view keeps at most 8 of the viewmodel's weighted layers.
- Angles are `m_angAbsRotation` for every entity, including non-rotating
  solids, whose collision angles would be zero.
- Every live edict is sent, triggers and logic entities included. Filtering
  is the reader's call.

## Tools

```bash
dotnet test wire/RustyBridge.Wire.slnx
```

`rbwire` (`wire/RustyBridge.Wire.Cli`) reads the Wire with `tail`, `dump`,
`record` and `info` (see [recording.md](recording.md)), and drives the donor
with `hold`, `step [N]`, `run` and `exec COMMAND`. On den-win11,
publish it once through `run-on-desktop.ps1`, because `P:` exists only on
the desktop. Then run it from SSH while the game runs:

```
dotnet publish P:\dev\rusty-bridge\wire\RustyBridge.Wire.Cli\RustyBridge.Wire.Cli.csproj -c Release -o C:\Users\agent\rusty-bridge\rbwire --artifacts-path C:\Users\agent\rusty-bridge\artifacts
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe tail --seconds 10
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe dump
```
