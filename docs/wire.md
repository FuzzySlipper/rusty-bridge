# Wire v11

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
A control file beside the Wire holds the donor
on a tick, steps it, and runs console commands; there's no other input or
write-back yet.

v2–v11 replaced v1 on 2026-10-17 and 2026-10-18. Readers check `version`; the tap writes
only the current one.

## Transport

A file, `rustybridge.wire`, next to the tap DLL (`mm\addons\`). The tap
creates it, sizes it to 23,799,040 bytes and maps it read-write; readers open
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
| 590,080 | 128 × 181,320 | Ring of slots |

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

- up to 2048 entities of 80 bytes (64 before v10)
- at slot offset 163,936, up to 64 damage records of 152 bytes
- at 173,664, up to 32 defense records of 56 bytes (48 before v11)
- at 175,456, up to 256 overlay layer records of 20 bytes
- at 180,576, up to 16 slow-time records of 32 bytes
- at 181,088, the 232-byte client view (80 bytes in v8)

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
| 42 | `flags` u8 | 1 = health fields valid, 2 = animation fields valid |
| 43 | `life_state` u8 | `CBaseEntity::m_lifeState` (+0x300) |
| 44 | `cycle` f32 | `CBaseAnimating::m_flCycle` (+0xEBC), 0..1 |
| 48 | `playback_rate` f32 | `CBaseAnimating::m_flPlaybackRate` (+0xE70) |
| 52 | `serial` u32 | the entity's EHANDLE serial (`IServerEntity::GetRefEHandle() >> 12`) |
| 56 | `link` u16 | v7: an `npc_*`'s `m_hRagdollFall` (+0x29E4), or a `prop_ragdoll`'s `m_hUnragdoll` (+0x16A0); 0xFFFF for none |
| 58 | reserved u16 | |
| 60 | `link_serial` u32 | the linked entity's serial |
| 64 | `parent` u16 | v10: `m_hMoveParent` (+0x3E4, the send prop `moveparent`): what the entity is attached to; 0xFFFF for none |
| 66 | `parent_attachment` u8 | v10: `m_iParentAttachment` (+0x3E0): the parent model's attachment, numbered from 1 in the model's order (the import's `asset.extras.attachments`); 0 for none |
| 67 | reserved u8 | |
| 68 | `parent_serial` u32 | |
| 72 | `owner` u16 | v10: `m_hOwnerEntity` (+0x500), or a weapon's `m_hOwner` when that is empty (the player's weapons); 0xFFFF for none |
| 74 | reserved u16 | |
| 76 | `owner_serial` u32 | |

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
| `+0x2CB841` | the damage info's `m_fSlowTime*`, when the victim's flag +0x2798 bit 13 is set |
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
  through `+0x2CB841`, for victims with that flag.
- **Not seen:** a call with a factor below 1. Map scripts and the other
  callers (scripted kills, impales) are the remaining sources.

The stepper shades the frames where `m_bInBulletTime` is set, lists each
call with its caller, and shows the game-time scale next to the clock when
it differs from 1. `rbwire info` groups calls by source and caller.

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
