# Recordings and the combat stepper

A recording (`.rbrec`) is Wire frames saved to disk, so a fight can be
stepped through tick by tick after the game has moved on. `rbwire record`
writes recordings, `RecordingReader` replays them in C#, and
`viewer/timeline.html` steps through them in a browser.

## Recording v10

Little-endian. A 16-byte file header (`magic` u64 `"RBREC001"`, `version` u32
= 10, reserved u32), then records until the end of the file. Each older
version is the newer one with less in it:

| Version | Entity | Damage | Defense | Also missing |
| --- | --- | --- | --- | --- |
| 10 | 80 bytes | 152 bytes | 48 bytes | |
| 9 | 64 (no parent or owner) | 152 | 48 | |
| 8 | 64 | 152 | 48 | the client view's camera and viewmodel animation (an 80-byte client view) |
| 7 | 64 | 152 | 48 | the client view |
| 6 | 56 (no link) | 152 | 48 | wall clock, player state, slow time |
| 5 | 56 | 152 | 48 | overlay layers |
| 4 | 52 (no serial) | 128 (no serials) | 32 (no serials) | |
| 3 | 52 | 128 | none | |
| 2 | 52 | none | none | |
| 1 | 32 (no combat fields) | none | none | Sequences records |

Each older record is a prefix of the current one, and readers accept all
ten. Each record is
`kind` u8, `length` u32 and `length` bytes of payload. Readers skip kinds
they don't know.

| Kind | Payload |
| --- | --- |
| 1 Level | `level_serial` u32, `tick_interval` f32, map (u16 length + ASCII) |
| 2 Class | `class_id` u16, name (u16 length + ASCII) |
| 3 Model | `model_index` i32, path (u16 length + ASCII) |
| 4 Frame | `frame` u64, `tick` i32, `curtime` f32, `flags` u8, `changed` i32, `changed` × 52-byte Wire entities, `removed` i32, `removed` × u16 edict indices, then (v3) `damage_dropped` u32, `damage` i32, `damage` × Wire damage records, then (v4) `defense_dropped` u32, `defense` i32, `defense` × Wire defense records, then (v6) `layer_dropped` u32, `layers` i32, `layers` × 20-byte Wire layer records, then (v7) `wall_seconds` f64, the 24-byte player state, `slow_time_dropped` u32, `slow_time` i32, `slow_time` × 32-byte Wire slow-time records, then (v8) the client view: 80 bytes in v8, 232 from v9 |
| 5 Sequences | `model_index` i32, `count` u16, `count` names (u16 length + ASCII) in `m_nSequence` order |

Frame flags: 1 = keyframe (the frame lists every live entity; drop prior
state), 2 = gap (frames before this one were missed; it's also a keyframe).

- Overlay layers are stored whole in every frame (their cycles advance
  every tick), not delta-coded.
- A frame stores only the entities whose Wire record changed since
  the previous frame, plus the indices that disappeared. Static entities cost
  nothing after their first appearance.
- A keyframe is written at the start, after a level change or a gap, and
  every 330 frames (5 s), so a reader can recover from any of them.
- Class, model and sequence records come before the first frame that uses
  them, and a Level record resets all three.
- `rbwire record` gets sequence names from the model files in the game's
  VPKs (`--game`, by default the game folder the Wire file sits in).
  `rbwire sequences MODEL.mdl` prints one model's list.
- An edict index can be reused by a new entity within a level. From v5 an
  entity is its (index, serial), and event records name entities the same
  way. For older recordings the viewer guesses a reuse from a change of
  class or model.

The first real recording (`local/recordings/l01_a-orcs-vs-guards.rbrec`, v1,
115 s) is 1.7 MB, about 220 bytes per tick. With combat fields, cycles change
every tick for everything animating, so `l01_a-orcs-vs-guards-combat.rbrec`
(v2, 70 s) is 2.6 MB, about 570 bytes per tick. Raw Wire slots would be about
75 KB per tick.

## Recording a fight

On den-win11, with the game running and the tap streaming:

```
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe record C:\Users\agent\rusty-bridge\fight.rbrec --seconds 120
C:\Users\agent\rusty-bridge\rbwire\rbwire.exe info C:\Users\agent\rusty-bridge\fight.rbrec
```

`tap/darkmessiah/rbduel.cfg` stages a fight with the player in it: two
shielded orcs, no `notarget`, a short sword, and 30 bursts of `+attack`.
That fight produced the first blocks. To stage a fight without playing one,
`tap/darkmessiah/rbfight.cfg` (copy it to `mm\cfg\`) teleports the
player beside the l01_a gate guards and spawns two armed orcs and a goblin
there with Arkane's `mm_npc_create`. Run `exec rbfight` from the console
after the opening horse ride ends (about two minutes in). `setpos` is ignored
while the player is mounted.

- `mm_npc_create` needs a model path for every class. With a class alone
  (`mm_npc_create npc_goblin`) it precaches a NULL model and the server
  drops with `Host_Error: Bad string`. Working lines are in `server.dll`'s
  strings, for example
  `mm_npc_create npc_goblin models/NPC/Goblin/Npc_Goblin.mdl weapon_arxclub weapon_mm_shield_gob`.
- Arkane's `valve.rc` does not exec `autoexec.cfg`, so binds have to come
  from the console.

## The stepper

`viewer/timeline.html` is one self-contained page. Open a `.rbrec` with the
button or by dropping it on the page. `python3 viewer/embed.py IN.rbrec
OUT.html` writes a copy that opens with the recording already loaded.

- Top-down map: wheel to zoom, drag to pan, click to select. Each actor's
  dot shows a facing tick and a fading trail. Weapons and armour are drawn
  too, so a weapon's trail traces its swings.
- The timeline shows the selected entity's ground speed, or the number of
  moving entities when nothing is selected. Click or drag to scrub.
- ← and → step one donor tick, Shift steps 10, Space plays at 0.1× to 2×.
- The detail panel shows position, angles, ground and vertical speed, yaw
  rate, update rate, health, and the current sequence by name with its cycle,
  playback rate and how long it has run. From v6 it also lists the entity's
  active overlay layers (name, cycle, weight). The map shows the heaviest
  action layer, such as a `layer_parry_B`, under the actor's main sequence.
- With v2 recordings, actors on the map show their current action
  (`attack_right 42%`), get a red ring for 10 ticks after losing health and a
  hollow marker once dead. The timeline adds the selected entity's health and
  an action band coloured by kind: attack, hit reaction, block/parry/dodge,
  movement, idle, death.
- "Swings and hits" lists every attack start, every hit and every kill, for
  the selection (hits taken and dealt) or for everyone; click one to jump
  there, hover for the full damage record. From v3, hits are the tap's damage
  records ("orc_sword #365 → goblin #401 with orccleaver: 6 slash to head,
  kill"). Older recordings infer hits from health drops and deaths from
  removals. Zero-damage hits on scripted-invulnerable targets are left out.
- From v4, blocked blows are listed too ("orc_sword #364 shield-blocked
  ghoul #400's blow"), and the defender gets a blue arc labelled "shield",
  "parry" or "no effect" for 15 ticks. Hover shows what the blow struck.
- A hit draws, for 20 ticks, the swing trace (start to end), an X at the
  damage position and a dashed attacker-to-victim line. The detail panel
  shows the selection's last hit taken and dealt.
- From v7:
  - frames where the player is in slow motion are shaded on the timeline;
  - the clock shows the game-time scale when it isn't 1;
  - the event list adds each bullet-time call with its caller, and each
    knockdown;
  - kicks read "player #1 → orc_sword #377 kicks";
  - the detail panel names a knocked-down NPC's ragdoll (and a ragdoll's
    NPC), and the player's adrenaline and slow motion;
  - the player's timeline draws adrenaline, dashed.
- From v8, the player's detail panel adds the client's view angles, its eye,
  and how far the client's (predicted) player is from the server's; from v9
  the camera the client drew with and the client viewmodel's sequence and
  layers.
- Action kinds come from sequence names (`actionKind` in the page):
  `attack_*`, `*strike*` (but not `nostrike`), `*kick*` and `*bash*` are
  swings; `Hit_*`, `Push_*`, `stunned_*` and `StandUp_*` are hit reactions.

## Checking damage against health

`l01_a-orcs-vs-guards-damage.rbrec` (v3, 70 s) has 709 damage records. 22
of them are hits that cost an actor health: 16 hits and 6 kills. The rest
are burn ticks on props and scripted no-damage hits. The stepper lists
those 22 alongside 51 swing starts. For each of the 22 ticks in which an actor lost
health, the record's `health_before` equals the streamed health the tick
before, and `health_after` equals the streamed health after (16 of 16
non-kills). All 6 kills are removed within a tick.

## The first blocks

`l01_a-duel-defense.rbrec` (v4, 70 s, the rbduel fight) has 50 defense
verdicts: 47 hits, each followed by its damage in the same tick, and 3
shield blocks. In each block a ghoul's claw struck `weapon_mm_shield_orc`
instead of the orc, and the same tick's damage records show 0 to both the
shield and the orc. Before v4 those blows looked like zero-damage noise. No
parries (result 1) or no-effect verdicts (4) occurred yet.

## Player parries

`l01_a-parry.rbrec` (v4, 105 s, `rbparry.cfg`: two orcs, the player
alternating block and attack) has two player records. At 73.6 s and 100.2
s, orc #392's blow broke the player's parry (result 18). The orc was 43%
through `attack_Bash`, and the same tick's damage record shows the blow did
nothing (god mode). So the bash is the parry breaker: weapon flag 0x10.

Three staged attempts with fixed `+attack2` holds produced no clean parry
by the player (17) and no weapon parry by an NPC (1).
`l01_a-parry-attempt3.rbrec` (5 s guard windows) has 90 checked hits and no
parries. The parry bot below caught both.

## The parry bot

`rbwire parrybot [--seconds N] [--agent URL]` plays the player from the
Wire. It runs on den-win11 next to the game, with the game in front.

- **Block:** when an orc, goblin or ghoul within 220 units starts an attack
  sequence (a new sequence, or a cycle wrap, whose name is an attack), the
  bot holds block (right mouse) for 700 ms, after a delay it cycles through
  0/200/400/600 ms.
- **Attack:** otherwise it attacks every 1.4 s at an enemy within 110
  units, cycling a fast swing (left click), a power attack (left held for
  1 s) and a kick (F).
- **Seek:** it turns toward its target and walks up to it (W). It keeps one
  target until it dies or another enemy is under 0.6 of its distance, so
  two enemies either side of the player don't flip it back and forth.
- **Input:** all of it goes through one desktop lease on the playtest agent
  (`click` steps; button 3 is right).
- **Turning:** relative mouse motion (`move` steps) doesn't turn Dark
  Messiah's view, so the staging cfgs bind J and L to `+left` and `+right`
  with `cl_yawspeed 90`. The bot holds them for 0.6 of the angle over a turn
  rate it measures at the start (about 36°/s through the agent; the first
  hold after the console closes can be lost, so it retries), then waits
  300 ms and looks again. It walks while turning when the target is within
  60° and far.
- **The view's yaw is the viewmodel's.** The player entity's yaw is its body,
  which turns in place in steps of about 90° (`Player_Turn_45_*`) once the
  view has moved far enough, and not at all for small turns.
- **Staging:** `tap/darkmessiah/rbbot.cfg` (sword, two orcs that come to
  the player) or `rbseek.cfg` (`notarget`, two unshielded orcs 800 units
  behind the player). `god` and `notarget` toggle, so after the first
  `exec` in a session, type them again. `ent_fire npc_orc_sword sethealth
  10` lets the short sword kill.
  Launch nothing on the desktop during a run: `run-on-desktop.ps1` and
  `dotnet publish` take focus and minimize the game.
- **Output:** the bot prints each block with its delay and the threat, and
  each defense outcome it sees.

`l01_a-parrybot.rbrec` (v4, 125 s) and its bot log
(`l01_a-parrybot.log`) contain 41 blocks, 64 swings, 10 player parries
(17), 8 broken parries (18), 2 NPC weapon parries (1) and 1 shield block
(8). An earlier bot run, without the delay sweep, also produced 2 NPC
parries and 9 breaks.

**Seeking (2026-10-18, `l01_a-v7-seek.rbrec`, `rbseek.cfg`):** from facing
away (172° off), the bot faced its orc within 15° in 7 s and closed from 804
to 46 units in 11 s. Close in, the orcs circle the player and the bearing
changes faster than the bot turns. The other v7 runs, kicks, knockdowns and
slow-motion calls are in [wire.md](wire.md) ("Slow motion", "Kicks,
knockdowns and adrenaline").

**What decides a player parry is the attack, not the timing.**

- Every bash broke the parry, at every delay: orc `attack_Bash` and ghoul
  `attack_bash`, 7 of 7.
- Every other blow was parried: `attack_Fast`, `attack_top`,
  `attack_bottom`, `attack_uppercut`, `attack_left` and `attack_right`, 8 of
  8.
- That matches the attack resolver's rule: a weapon flag 0x10 attack
  breaks a parry.
- Timing only has to cover the impact. Blows landed 22–55 ticks (0.33–0.8
**Seeking (2026-10-18, `l01_a-v7-seek.rbrec`, `rbseek.cfg`):** from facing
away (172° off), the bot faced its orc within 15° in 7 s and closed from 804
to 46 units in 11 s. Close in, the orcs circle the player and the bearing
changes faster than the bot turns. The other v7 runs, kicks, knockdowns and
slow-motion calls are in [wire.md](wire.md) ("Slow motion", "Kicks,
knockdowns and adrenaline").

  s) after the bot saw the wind-up on the Wire.

**NPC parries are weapon contact.** Both times the player's short sword
struck the orc's cleaver (`struck = weapon_arxorccleaver`). The verdict was
1, and the damage records show 0 to the cleaver and the orc.

## What the first recordings showed

- **NPC transforms change every 8 ticks** (120 ms, their think interval).
  The player's change every 2. Per-tick speed therefore alternates between
  zero and spikes, so the stepper measures velocity over an 8-tick window.
  For the Frontend, this means interpolating NPCs across 8 ticks, not the
  one tick the design assumed.
- **Weapons are separate server entities that follow the hands.** Each orc
  cleaver travelled about 6,000 units in 40 s of fighting, far more than its
  orc. Swing timing can be read from weapon trajectories before any
  animation data is streamed.
- **NPCs are removed in the tick they die.** Their life state never
  changes and the stream never shows zero health; a ragdoll appears instead.
  The damage hook catches the killing blow; recordings before v3 show the
  removal as the death ("removed at 11.3 / 35 hp").
- **The weapon is the attacker.** Melee damage names the weapon entity
  (`weapon_arxorccleaver`) as `m_hAttacker`; its owner is the orc.
- **Hits carry their own trace.** `m_bTraceInfoValid` hits come with hit
  group, hitbox, physics bone and the swing trace's start and end, so hit
  location needs no extra tracing. NPC damage has bit 0x40000 set (Source's
  `DMG_RADIATION` bit, evidently reused by Arkane), and every NPC hit
  carries the same slow-time fields (×0.10 for 2 s). Which hits actually
  trigger slow motion isn't known yet; the tick rate did drop to about 44 a
  second during the fight.
- **Every NPC field updates at think rate**, not just the transform: health,
  sequence and cycle also change every 8 ticks. The player updates every 2.
- **The combat vocabulary is in the sequence names.** The staged fight
  produced 41 swings (`attack_Jump`, `attack_down_1`, `Turn_strike_90_left`,
  `attack_Bash`, `throw_stone`...), 42 hits and 4 deaths in 70 s. The orc
  model alone has 333 sequences, mostly from `npc_orc_animations_combat.mdl`.
- **Some death ragdolls are server entities** (`prop_ragdoll`, with
  `phys_bone_follower`s). Stock Source keeps death ragdolls on the client, so
  Phase 2 of the design ("ragdolls need no write-back") has to be checked
  per case.
