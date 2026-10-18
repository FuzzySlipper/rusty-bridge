# Wire v5

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
serial, so a reused edict index is a different entity. There's no input or
write-back yet.

v2–v5 replaced v1 on 2026-10-17 and 2026-10-18. Readers check `version`; the tap writes
only the current one.

## Transport

A file, `rustybridge.wire`, next to the tap DLL (`mm\addons\`). The tap
creates it, sizes it to 16,718,080 bytes and maps it read-write; readers open
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
| 590,080 | 128 × 126,000 | Ring of slots |

Header: `magic` (u64, `"RBWIRE01"`), `version` (1), the sizes and offsets
above (so a reader can check them), `writer_pid`, `level_serial`,
`tick_interval` (seconds, from the donor), `map[64]`, and `frames_written`
(u64, at offset 136).

Slot: a 48-byte header (`seq` u64, `frame` u64, `tick` i32, `curtime` f32,
`level_serial` u32, `entity_count` u32, `damage_count` u32,
`damage_dropped` u32, `defense_count` u32, `defense_dropped` u32), then:

- up to 2048 entities of 56 bytes
- at slot offset 114,736, up to 64 damage records of 152 bytes
- at 124,464, up to 32 defense records of 48 bytes

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

**Identity is (index, serial).** Edict indices are reused within a level,
often by an entity of the same class. Each entity's own handle carries a
20-bit serial that changes on reuse, and it's the same serial the game's
handles to that entity carry (`handle >> 12`). The edict's second word
(`m_NetworkSerialNumber` in Episode One) holds only its low 10 bits, the
networked width: 0xA6 against a handle serial of 0x20A6, for example. That
is why the Wire takes the serial from the handle, not from the edict.

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
| 40 | `position` 3×f32 | `m_vecDamagePosition` |
| 52 | `force` 3×f32 | `m_vecDamageForce` |
| 64, 76 | `trace_start`, `trace_end` 3×f32 | `m_vecTraceStartPos`, `m_vecTraceEndPos` |
| 88 | `push_force`, `weapon_flags`, `mm_damage_flag`, `mm_spell` i32 | `m_iPushForce`, `m_iWeaponFlags`, `m_iMMDamageFlag`, `m_iMMDamageSpell` |
| 104 | `slow_time_delay`, `_duration`, `_factor` f32 | `m_fSlowTime*` |
| 116 | `mm_direction` 3×f32 | `m_vecMMDirection` |
| 128 | `victim_serial`, `attacker_serial`, `inflictor_serial`, `trace_entity_serial`, `weapon_serial` u32 | EHANDLE serials of the entities above, 0 with no entity |
| 148 | reserved u32 | |

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

## Known gaps

- Server state only. The `viewmodel` entity's server origin is stale; the
  first-person weapon and the predicted player live in `client.dll` (see
  "Client-side state" in the design).
- Angles are `m_angAbsRotation` for every entity, including non-rotating
  solids, whose collision angles would be zero.
- Every live edict is sent, triggers and logic entities included. Filtering
  is the reader's call.

## Tools

```bash
dotnet test wire/RustyBridge.Wire.slnx
```

`rbwire` (`wire/RustyBridge.Wire.Cli`) has four subcommands: `tail`, `dump`,
`record` and `info` (see [recording.md](recording.md)). On den-win11,
publish it once through `run-on-desktop.ps1`, because `P:` exists only on
the desktop. Then run it from SSH while the game runs:

```
dotnet publish P:\dev\rusty-bridge\wire\RustyBridge.Wire.Cli\RustyBridge.Wire.Cli.csproj -c Release -o C:\Users\agent\rusty-bridge\rbwire --artifacts-path C:\Users\agent\rusty-bridge\artifacts
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe tail --seconds 10
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe dump
```
