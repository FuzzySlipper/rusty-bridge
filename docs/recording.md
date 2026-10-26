# Recordings and the combat stepper

A recording (`.rbrec`) is Wire frames saved to disk, so a fight can be
stepped through tick by tick after the game has moved on. `rbwire record`
writes recordings, `RecordingReader` replays them in C#, and
`viewer/timeline.html` steps through them in a browser.

## Recording v20

Little-endian. A 16-byte file header (`magic` u64 `"RBREC001"`, `version` u32
= 20, reserved u32), then records until the end of the file. Each older
version is the newer one with less in it:

| Version | Entity | Damage | Defense | Also missing |
| --- | --- | --- | --- | --- |
| 20 | 88 bytes | 360 bytes | 56 bytes | |
| 19 | 88 | 360 | 56 | the wizard eye |
| 18 | 80 (no size or shrink) | 360 | 56 | |
| 17 | 80 | 360 | 56 | an NPC's alert level and state on its entity (zero there) |
| 16 | 80 | 360 | 56 | the melee sweeps |
| 15 | 80 | 360 | 56 | the skill levels (an 80-byte player stats record) |
| 14 | 80 | 360 | 56 | the player stats |
| 13 | 80 | 360 | 56 | the user commands (a 16-byte player input) |
| 12 | 80 | 152 (no victim animation) | 56 | |
| 11 | 80 | 152 | 56 | the player input |
| 10 | 80 | 152 (no critical byte, alert level) | 48 (no defense state) | |
| 9 | 64 (no parent or owner) | 152 | 48 | |
| 8 | 64 | 152 | 48 | the client view's camera and viewmodel animation (an 80-byte client view) |
| 7 | 64 | 152 | 48 | the client view |
| 6 | 56 (no link) | 152 | 48 | wall clock, player state, slow time |
| 5 | 56 | 152 | 48 | overlay layers |
| 4 | 52 (no serial) | 128 (no serials) | 32 (no serials) | |
| 3 | 52 | 128 | none | |
| 2 | 52 | none | none | |
| 1 | 32 (no combat fields) | none | none | Sequences records |

Each older record is a prefix of the current one, and readers accept every
version. Each record is
`kind` u8, `length` u32 and `length` bytes of payload. Readers skip kinds
they don't know.

| Kind | Payload |
| --- | --- |
| 1 Level | `level_serial` u32, `tick_interval` f32, map (u16 length + ASCII) |
| 2 Class | `class_id` u16, name (u16 length + ASCII) |
| 3 Model | `model_index` i32, path (u16 length + ASCII) |
| 4 Frame | `frame` u64, `tick` i32, `curtime` f32, `flags` u8, `changed` i32, `changed` × Wire entities (88 bytes; older sizes above), `removed` i32, `removed` × u16 edict indices, then (v3) `damage_dropped` u32, `damage` i32, `damage` × Wire damage records, then (v4) `defense_dropped` u32, `defense` i32, `defense` × Wire defense records, then (v6) `layer_dropped` u32, `layers` i32, `layers` × 20-byte Wire layer records, then (v7) `wall_seconds` f64, the 24-byte player state, `slow_time_dropped` u32, `slow_time` i32, `slow_time` × 32-byte Wire slow-time records, then (v8) the client view: 80 bytes in v8, 232 from v9, then (v12) the player input: 16 bytes, 48 from v14, then (v15) the player stats: 80 bytes, 112 from v16, then (v17) `sweep_dropped` u32, `sweeps` i32, `sweeps` × 56-byte Wire sweep records, then (v20) the 36-byte wizard eye |
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

## Scenarios

`rbwire scenario NAME` stages one mechanic and records it, from one command on den-win11. Run it through the
desktop helper so it outlives the SSH session and its input reaches the game:

```
ssh den-win11 'C:\Users\agent\crew\bin\run-on-desktop.ps1 -NoWait -Command "C:\Users\agent\rusty-bridge\rbwire\rbwire.exe scenario chained-slash"'
```

**What it does:**
1. **Launch:** closes the game rbwire launched last (unless `--reuse`), and starts `mm.exe -game mm -steam` straight into `rb_start`
   (below, "A save past the ride"), with Steam's app id in the environment (`SteamAppId`/`SteamGameId` 2100; without
   them the game can't mount its content). Steam's own launch options are left alone, and no Steam restart is needed.
   **Another's game:** rbwire notes each game it launches (process id and start time, `game.pid` beside rbwire). A
   running game it didn't launch is another agent's or someone's session: the scenario stops and leaves it running,
   unless `--replace` says to close it. `rbwire close-game` closes rbwire's own game and no other, for batch scripts.
2. **The level:** waits for the tap log's `dump (level+5s)` line.
3. **Setup:** sends the scenario's console lines through the tap's control file, one at a time.
4. **Record and play:** records for the scenario's seconds to `C:\Users\agent\rusty-bridge\recordings\l01_a-v<Wire version>-NAME.rbrec`
   (or `--out`), and meanwhile plays its scripted input through the playtest agent's desktop lease. With `--hand`, or a
   scenario without `play`, it records without input while someone plays over Moonlight.
5. **The log:** everything it does goes to the recording's `.log` beside it; `rbwire info` follows on the console.

**Input only to the game.** Before every input step the game must be running and in front (it is brought forward
once if not); otherwise the input stops and the recording carries on. A dead game leaves other agents' windows in
front, and input would land in them.

**Scenario files** are `tools/scenarios/NAME.json`, copied beside rbwire as `scenarios/` when it's published
(`dotnet publish wire/RustyBridge.Wire.Cli -c Release -o C:\Users\agent\rusty-bridge\rbwire`):
- `description`, `seconds` (default 60), `launch` (the game's arguments; default the `rb_start` ones);
- `setup`: console lines, `{"wait": seconds}`, and `{"approach": "npc_goblin", "distance": 60}`, which puts the player
  that many units in front of the nearest living NPC of that class on the Wire, facing it (`"height"`: that many
  units above the NPC's feet, default 4, to drop from; `"pitch"`: that many degrees down; `"model"`: only entities
  whose model path holds it, so a prop of one kind can be approached, `{"approach": "prop_physics", "model":
  "barrel01"}`; a prop needn't have health). NPCs spawned with `mm_npc_create` land where the view points,
  often out of reach: set the position and view first;
- `play`: console lines, `{"point": [x, y], "of": [1280, 720]}` (the cursor to a place in a desktop capture of that
  size, for the game's menus), `{"click": 1, "ms": 80}` (1 left, 3 right), `{"hold": ["F", "W"], "ms": 150}` (letters,
  digits, `SPACE`, `SHIFT`, `CTRL`, `ALT`, `ESC`, `ENTER`, `TAB` or virtual-key codes), `{"wait": seconds}`,
  `{"repeat": n, "steps": [...]}`, and `approach` as in the setup (with no NPC left to approach, the input stops and
  the recording carries on).
- **Keys held across other input:** the agent plays its steps one after another, so a key held while clicking goes as
  console lines: `+forward`, `+jump`, `+sprint`, `+attack`, then `-attack`, `-sprint`... (`rush-attack` holds every key
  that way).
- **`--reuse` and toggles:** `god` toggles, so a scenario that sends it run on a game that already had it turns it off;
  the player died in one such run and the game stopped ticking. Run those on a fresh launch.

To add one, copy a file, change its setup and play, publish, and run it. Recordings of skills or items other than
the arena's go in `local/recordings/experiments/` (the lab's replays of `local/recordings` expect fights). Perception
recordings whose orcs fight a god-mode player standing still (`squad-probe` and the l05_a ones) go in `local/perception/`, which only the
perception checks read: their fights would skew the orc's behaviour and defence calibrations.

| Scenario | Setup | Play |
| --- | --- | --- |
| `chained-slash` | every skill (all at their maximum level), the short sword, one orc with 1000 health, approached | 20 bursts of 8 quick clicks, then 3 kicks |
| `jump-attack` | every skill (`combat_vertical_attack`, the Leaping Attack, among them), the short sword, one orc with 1000 health | from 80 units, `+jump` then a click 0.1 or 0.3 s later, held 0.06, 0.15 or 1 s (three each); drops from 200 units up with the same holds (two each) |
| `jump-attack-basic` | the same with a new game's skills | the same |
| `adrenaline` | every skill, the short sword, one orc with 100000 health | seven power strikes to fill the gauge, then one at full, six times from 55 or 130 units; three crouched; taps; 12 s standing |
| `fatality` | every skill, the short sword, a new orc with 100000 health for each of six cycles | ten power strikes, then one at full: crouched, on an orc set to 40 health, or standing |
| `whirlwind` | the same, crouched (`+duck`) through each of three cycles | twelve power strikes |
| `disarm` | every skill, the short sword, one orc with 1000 health | fourteen pairs of power strikes (none disarmed: the second press came while the first played) |
| `rush-attack` | every skill (`combat_rush_attack`, the Charge, among them), the short sword, a new orc with 1000 health for each rush | from 360 units, three each: a 1 s charge, then `+forward +sprint` and the release 0.6 s later; a sprint, then a 1 s or 0.3 s hold; a run after a charge |
| `daggers-goblin` | every skill, the kryss daggers drawn, a goblin with club and shield approached | taps, a power attack and a kick, 6 times |
| `weapon-long-sword`, `weapon-club` | every skill, the weapon given and drawn, a new orc with 100000 health | approached to 55 units before each move, nine times: a burst of 8 clicks, a 1.2 s hold, two taps |
| `weapon-daggers` | the same with the kryss daggers | the same at 45 units, then three power and three fast attacks at an unaware orc turned away (no backstab landed) |
| `weapon-staff` | the same with the combat staff | the same at 65 units with bursts of 24 clicks and 1.8 s holds (its wind-up is 1.4 s), then the same tries at an unaware orc (no breakneck) |
| `weapon-switch` | every skill, the long sword, kryss daggers, combat staff and club given, a new orc with 100000 health | `use` each in turn, three rounds, attacking 0, 0.3 or 0.8 s after: two clicks, then a 1.2 s hold |
| `shield` | every skill (`combat_shield_enabled`), the short sword and `weapon_arxshield` (the small orc shield) given and used, an orc with 100000 health that fights back | six 4 s blocks from the front (`+attack2`), four turned about 60° left, eight bashes (a click 0.4 s into a block), three cycles of a fast chain and a 1.2 s power attack |
| `weapons-probe` | every skill, the save's empty hands | `give` and `use` each of five weapons, then `use` again: all equip and put away |
| `telekinesis` | every skill, telekinesis deployed (`Deploy_Spell_AIR_1`), standing 70 units from the start area's jug, looking 11° down at it | three 2.5 s holds: the first lifted it (mana 26.3 to 22.3) |
| `spells-probe` | every skill | `Deploy_Spell_FIRE_1` and three casts (mana down 4 to 6.6 each), then telekinesis |
| `perception-behind` | every skill, the short sword, the level's other NPCs removed; a new orc with its AI on for each try at (0, -1500), facing away (it keeps its spawn yaw) | from 800 units behind it: a run, a crouched walk, a sprint; then standing 150 behind it for 8 s |
| `perception-front` | the same, the orc facing the player | standing 1400, 1000, 600 and 300 units in front for 8 s; a crouched walk from 1000 |
| `sneak-attack` | the same, the orc facing away with 1000 health | 55 units behind it: a power attack, a fast attack, a power attack |
| `call-for-help` | the same, two orcs facing away, the second 400 then 1000 units to the side (spawned from elsewhere, or it lands on the first) | 55 units behind the first, a power attack, then 8 s |
| `squad-probe` | the same, both orcs put in one squad (`ent_fire npc_orc_sword setsquad rbsquad`; Dark Messiah handles `SetSquad`, which `developer 2` shows) | the second 400, 600 and 1000 units to the side; the first struck from behind: none came |
| `sight-l05`, `sight-l05b`, `sight-l05c` | `+map l05_a` (`launch`; not in `nostealth`), a 40 s wait for its intro (setpos does nothing before), every skill, the short sword, the map's NPCs removed; `notarget` on while each orc is spawned and placed (b and c) | an orc spawned from (266, 1100) looking north, so it faces south, moved to (-7, 4978); the player standing still in front of it at 1700 down to 300 units, and 10° to 60° off, 8 s each |
| `call-l05` | the same | two orcs both spawned from (266, 1100) (a second spawn from (566, 1100) dies at once), the second beside or beyond the first, in one squad or none; the player appearing 400 units ahead of the first, or striking it from behind, 10 s each |
| `movement-cvars` | sv_cheats | nothing played: the movement ConVars and every `mm_`, `sv_` and `cl_` one printed to the game's `console.log` (#10159) |
| `movement` | the short sword, the level's NPCs removed | from (0, -2400), facing north then east: a run forward, a strafe, a diagonal, backing off, crouched, with `+speed` and `+walk`, each for 2 s from standing, then let go; standing, running, crouched and backing jumps (#10159) |
| `jump-hold` | the same | standing jumps, two each with jump held 0.05 to 1 s (#10159) |
| `sprint`, `sprint-double` | the short sword, the level's NPCs removed; a new game's skills (stamina 9), or every skill (`double_sprint`, 12) | round a small square turned every second: a sprint from full held 3 s past empty, a rest, a sprint tried below 3, a rest to full, a kick in a sprint, a kick standing (#10160) |
| `fall` | god off, `notarget`, every skill (80 health), the level's NPCs removed | the health set to 80, then `setpos` 209 to 484 units above the ground at (0, -2400), eight drops 5 s apart (`fall-1` dropped 100 to 400 at whatever health it had) (R10159-1) |
| `telekinesis-heavy` | the same as `telekinesis-throw` | an orc in the lower room, its health set to 200 (`ent_fire npc_orc_sword sethealth 200`) and frozen, takes the barrels (80 kg) held 5 s, and the stool and table (40) held 3 s (#10164, R10164-1) |
| `telekinesis-barrel` | the same, mana filled before each grab (three belt slots drunk twice) | in the lower room: the stool (40 kg) as a control, then each barrel (80) held 6 s and thrown south into the room (#10164, R10164-1) |
| `fireball` | every skill, god, `notarget`, the level's NPCs removed, `Deploy_Spell_FIRE_3` | before each cast the last orc removed and a new one spawned in the start courtyard at (0, -2300); casts (a 0.1 s `+attack`): at its chest from 200 units south (twice) and east, at the floor 60, 100 and 150 units short of it, and one held 1.5 s with the view turned 10° after 0.3 s; 7 s after each (#10165) |
| `attack-spells` | every skill, god, `notarget`, the level's NPCs removed, sixteen mana potions | before each spell a new orc at (0, -2300), two belt slots drunk, `Deploy_Spell_*`, the player 200 units south: the lightning bolt tapped twice, held 1.5 s, and with a second orc 150 units east; the freeze, 26 s after; the inferno held 3 s; poison spray and drain (neither deployed); the fire trap at the orc's feet (the orc had walked off) (#10166) |
| `attack-spells-2` | the same, each orc frozen with `ai_disable` | poison spray and drain 3 s after drinking (neither deployed again), the fire trap (not cast), the inferno held 1.5 s, the lightning bolt with a second orc (it took the first's place), the freeze then a bolt |
| `support-spells` | every skill, `notarget`, the level's NPCs removed, sixteen mana potions | before each spell a new orc at (0, -2300), two belt slots drunk, `Deploy_Spell_*`, the player 200 units south: cure wounds after `hurtme 10` (deployed, then tapped); weaken at the orc, then `god` on and `notarget` off 12 s (its blows); charm with a second orc beside it; shrink (`earth_3`, not deployed); magic reflection (`earth_2`); wizard eye (`water_1`) held 3 s (#10167) |
| `support-spells-2` | the same | cure wounds three times from a set health (`ent_fire !player addoutput "health N"`): 10 and deployed (it casts), 10 and tapped, 44 and tapped; weaken at a new orc, then `god` on and `notarget` off 25 s, the sword three times from 5 s; the sword on a new orc (it didn't come); charm at a new orc with a second beside it, `god` on and `notarget` off 30 s |
| `shrink-probe` | as `support-spells` (Wire v18) | four times a new orc at (0, -2300), two belt slots drunk, `mm_player_add_adrenaline 100` before and after `Deploy_Spell_AIR_4`, cast from 200 units south: the first shrank and was walked into; the orcs spawned after it came up with no health and never moved, so the rest stopped (#10348) |
| `shrink` | the same, three orcs spawned first at x 0, 300 and -300 (y -2287) | each cast as the probe's, from 213 units south: the east orc shrunk and walked into (the stomp); the west one shrunk and kicked (the sword's approach then found no shrunk orc, and the input stopped) (#10348) |
| `shrink-2` | the same, two orcs at x 300 and 0 | the east orc shrunk, then the short sword at it from 50 units, 40° down (it struck the floor); the middle one shrunk (it had walked out of the cone: the cast missed) and watched 45 s, then `god` on and `notarget` off 20 s (#10348) |
| `shrink-3` | the same, two orcs at x 300 and -300 | the east orc shrunk, then the sword crouched (`+duck`) from 45 units, 15° down (the floor again); the west one shrunk, then the player 120 units from it with `god` on and `notarget` off 25 s (#10348) |
| `reflect-probe` | as `support-spells`, `god`, the level's necromancers removed | a necromancer spawned at (0, -2287), the player 413 units south, `notarget` off 20 s, then magic reflection twice; an orc archer in its place: neither NPC ever saw the player (l01_a's don't), so nothing was cast (#10349) |
| `reflect-probe-2` | the same | the necromancer woken by a lightning bolt: 20 s of its flame arrows, then `Deploy_Spell_EARTH_2` (it casts as it's deployed: the presses after it cast the bolt again) twice, 20 s each; the archer woken the same way, but the second bolt killed it before it shot (#10349) |
| `reflect-probe-3` | the same | the archer alone, woken by one bolt: 12 s of its arrows, then `Deploy_Spell_EARTH_2` alone, 16 s, twice (#10349) |
| `wizard-eye` | as `support-spells` (Wire v20) | `Deploy_Spell_WATER_1` (it's on as it's deployed), a tap, 35 s; deployed again, held 3 s, 35 s (#10350) |
| `wizard-eye-look` | as `wizard-eye` | the same place and view; the game's `jpeg` screenshots (`mm/screenshots/rb_we_*.jpg`) before the cast, 2 s and 15 s in, and 3 s after it's over (#10358) |
| `held-look` | as `wizard-eye-look`, one orc at a time, its AI off: one with its cleaver, then, removed, one given `weapon_mm_shield_orc` after its crossbow (as `rbfight.cfg` gives it) in its place | the game's `jpeg` screenshots (`rb_hl_*.jpg`) of each orc from 100 units in front and to each side, the viewmodel and HUD hidden; then the short sword in first person ahead and 30° down, the power attack's wind-up held, a fast attack under way and a parry held, then with `weapon_arxshield` on the arm ahead, down and blocking (#10395; the third run, `l01_a-v20-held-look-3`, has them all) |
| `npc-archery` | every skill, `god`, no `notarget`, the level's NPCs removed, the classic bow | an `npc_orc_sword` with the crossbow first, then second, each woken by an arrow from 550 or 800 units north (the courtyard is open that way) and left still, strafing or backing off: none shot; with the crossbow first it struck with it as a club (#10169) |
| `npc-archery-2` | the same | `npc_orc_bow` (the maps' archer: `rangeweapon` the long bow) by `mm_npc_create`, the cleaver then the long bow (as the maps equip it), then the other way round; by `ent_create` (didn't spawn); an `npc_orc_sword` with the player noclipped 300 units up (it lost him) |
| `npc-archery-3` | the same | the archer woken from 550 north, then the player noclipped up (it lost him), on the ground 900 north (it stood at 471 and shot every 2.16 s), noclipped and strafing |
| `npc-archery-4` | the same; the setup lists the `mm_ai`, `ai_` and `npc_` ConVars to the console log (`mm_ai_dist_surprised` 314) | the archer woken from 550 north, the player then 900 north still, 700 still, 900 strafing left and right 3 s at a time, 700 backing off 5 s |
| `npc-archery-5` | the same | four times the archer woken and the player 900 north still 30 s (when it rushes in); then a big crate (`prop_physics_create`) put south of the player, crouched behind it (it didn't hide him) |
| `npc-archery-6` | the same | the archer woken, then the player in the wooden building south of the courtyard (it rushed in); the crate again 100 units off (it didn't hide him) |
| `bow-cvars` | sv_cheats | nothing played: the `arrow_`, `bow_`, `mm_arrow_` and `mm_bow_` ConVars printed to the console log (`arrow_speed` 2100; #10168) |
| `bow` | every skill, god, `notarget`, the level's NPCs removed, the classic bow (`weapon_arxcrossbow`) given and drawn | an orc spawned at (0, -2300) and frozen (`ai_disable 1`): shots from 200 units held 0.1 to 2.5 s, from 400 and 600, one from 300 east, one held 6 s, five 0.5 s draws back to back; then a second orc at 700 units, `notarget` off, six 1 s draws at it (#10168). The orcs have their 35 health (`sethealth` came too soon): the first arrow killed each |
| `bow-basic` | the same with a new game's skills | shots from 200 units held 0.3, 1 and 2.5 s, one from 400 held 6 s, five draws back to back |
| `bow-more` | as `bow` | attack2 during a draw and before one; a head shot (missed: the chest); a third orc that never appeared; the long, sniper and explosive bows into the world (their orcs vanished as they spawned); the rope bow (`weapon_arxcrossbowrope`) at six angles |
| `bow-zoom` | as `bow`, no NPC | `+zoom` held 3 s while drawn, tapped while drawn, and at rest |
| `bow-sway` | as `bow-basic`, no NPC | shots released 2.05 to 4.6 s into the draw, at and between the camera's sway peaks |
| `bow-r2` | as `bow` | the explosive bow at the floor 30, 60 and 15 units short of a fresh orc from 300 units (the 60-short arrow bounced, and its second impact's blast took 15 from the orc 12 units off); the classic bow at three orcs with `notarget` toggled off (they didn't come); the rope bow up into three wooden beams' undersides (the third, near (362, -3167), made a rope) (R10168-1 to -3) |
| `bow-r4`, `bow-r5` | every skill, `god`, no `notarget`, the level's NPCs removed, the classic bow, two orcs at 1000 health (`addoutput "health 1000"`) | for each: an arrow from 550 units north at it standing (it charges), then three times drawn 2.1 s, the player put 300 (`r4`) or 500 (`r5`) units in front of it (`approach`) and let go: three arrows struck it running (R10168-3). South of the courtyard, 300 units in front of a running orc is inside the wooden building |
| `bow-rope` | as `bow` | the explosive bow at a frozen orc's chest (killed by the arrow); the rope bow at l01_a's wooden wall at x 745 and the overhang near (245, -2964, 308): no rope |
| `tk-probe` | every skill, god, `notarget`, the level's NPCs removed, telekinesis deployed | nothing: 15 minutes recorded for console probing with `rbwire exec` (#10164) |
| `telekinesis-mass` | every skill, god, `notarget`, the level's NPCs removed, twelve mana potions on the belt | l01_a's props where they stand: the upper hall's jug (4 kg), bread (1), table (40) and wood bin (30), the lower room's broom (5), barrels (80) and stool (40), the crate room's crates (40, 300, 300); each gripped (`+attack` held), the view turned, let go and pressed again (the throw); then an orc, spawned where the view meets the hall's floor, has a table, stools and a log thrown at it. Two belt slots drunk and `Deploy_Spell_AIR_1` again before each grab (drinking puts the sword back) (#10164) |
| `telekinesis-throw` | the same | an orc in the upper hall, frozen with `ai_disable`, takes the jug, bread, a log, two stools, the table and a wood bin thrown at it; a second in the lower room, the two barrels (#10164) |
| `health` | `notarget`, 60 skill points; two life potions, a red apple, a roasted chicken, cooked bread and a full life potion given (the save's player has 15 of 50 health) | belt slots 1 to 9 used 3 s apart (`give` puts the potions on the belt, not the food); in the sheet's misc tab stamina, `extra_health` to 3 and `fast_metabolism` learned one double click at a time; `hurtme 10`, 20 s, `hurtme 5`, 10 s (#10162) |
| `lean` | the short sword, the level's NPCs removed | facing north from (0, -2400): lean left 2 s, right 2 s, a 0.3 s tap, a walking lean; then strafing left into a wall, leaning into it, stepping off 27 and 54 more units and leaning again (R10161-2) |
| `mantle`, `mantle-crates` | the short sword, the level's NPCs removed | jumps, held or tapped, into world steps of 32 and 37 units, a tall wall and the 57.6- and 75-unit crates: no mantle came (#10163; the trigger is #10329's) |
| `health-look` | god, `notarget`, 30 skill points, the sheet open | its misc tab clicked, to see the stamina line's icons: stamina (868, 323), `extra_health` (848, 409), `poison_recovery` (890, 409), `fast_metabolism` (848, 471) of the 1280×720 capture |
| `backstab` | every skill, the kryss daggers, an unaware orc (`ai_disable 1`) spawned for each try and turned away | approached from behind to 25-100 units, tapped 0.3-1.3 s after; landed at 40, 60 and 80 units (the first round, `backstab-1`, once at 35) |
| `breakneck` | every skill, the combat staff, the same orcs | approached from behind and in front to 20-30 units, tapped or held: no breakneck in 17 tries over two rounds |
| `sword-timing` | the short sword, a new game's skills | presses 0.15-0.6 s after a kick, holds through B with a strafe or back/forward from 0.15 or 0.35 s in, taps of 1-4 ticks (#10142) |
| `sword-timing2` | the same | a second tap through A's recovery (`FastEndOpen`); a kick, a jump, and attack held 0.2 s at points of the kick (`KickAttackOpen`) |
| `chained-slash-levels` | the short sword, 10 skill points, an orc with 100000 health | combat learned to 1 in the sheet (`point` and a double click), 10 bursts of 8 clicks; to 2, 10 more (#10143) |
| `charsheet`, `charsheet-keys` | sv_cheats; F7 bound to `togglecharsheet` | the sheet opened and closed from the console, then the keyboard (the action bytes +0x9B, +0x9D) |

**Items, spells and skills from a scenario** (#10170). Everything the sweep needs in hand comes from console lines,
plus one `point` step for the character sheet; nothing goes through the inventory or belt UI:

| To have | Scenario lines |
| --- | --- |
| a melee weapon (swords, club, daggers, staffs) | `mm_player_skills_grosbill 1` (or the weapon's skill), `give weapon_...`, `use weapon_...` |
| a spell (telekinesis included) | `mm_player_skills_grosbill 1` (it learns them), `Deploy_Spell_AIR_1` ... `Deploy_Spell_EARTH_4` |
| one skill level | `mm_player_add_skillpoints N`, `mm_open_charsheet`, a `point` at the skill's icon and a double click, `togglecharsheet` |
| another weapon | `use` it: the one in hand is put away and it's drawn |

`daggers-goblin` (the daggers' clips in all 225 stats states), `telekinesis` (mana down 4 as it grips the jug),
`spells-probe` and `chained-slash-levels` record this way unattended.

**What the console can't stage, and the details:**
- **Belt slots:** `usebelt1` ... `usebelt9` use what the belt holds (in the `rb_start` save `usebelt1` put the weapon
  away and `usebelt5` drew the club); filling a slot is the inventory UI's and isn't scripted. `use` and
  `Deploy_Spell_*` reach every item without it.
- **Skills one by one:** the console gives every skill at its maximum level (`mm_player_skills_grosbill 1`) or skill
  points (`mm_player_add_skillpoints N`); a level is learned in the character sheet by double-clicking a skill's
  icon, which a scenario does with a `point` step (`chained-slash-levels`: the combat icon at (884, 336) of the
  1280×720 desktop capture, `charsheet-look` to see it). `skills_set` and `skills_add` are item data keys, not
  commands. Wire v15 and v16 record the modules and levels either way.
- **Weapons in hand:** `give` then `use` draws a weapon only when the player has its skill: without skills only the
  short sword and the club came to hand, and the daggers and staffs stayed in the inventory. With
  `mm_player_skills_grosbill 1` every melee weapon tried draws (`weapons-probe`, #10148), and `use` on another switches.
- **Spells in hand:** the console's `Deploy_Spell_FIRE_1` ... `Deploy_Spell_EARTH_4` (`find spell` lists them) put a
  learned spell in hand, and clicks cast it, mana falling. `give spell_fire_1` and `give spell_air_3` equip but don't
  cast; `use` of the spell weapons and the belt slots select nothing, and `mm_spellshortcut` and `mm_book` are no
  commands. There is no `prop_physics_create`: telekinesis takes a prop the level has (the start area's jug).
- **Telekinesis** (#10164): attack held grips the body in view and holds it; let go, it stays where it was held;
  pressed again, it's thrown. Console `+attack`/`-attack` drive it (`rbwire exec` or a scenario's lines). `+attack2`
  is the parry: with the spell in hand it puts the spell away (the sword comes back), as drinking from the belt does;
  `Deploy_Spell_AIR_1` takes it out again (`use weapon_MMTelekinesis_physcannon` empties the hand). `addoutput
  origin` moves a prop's entity but not its physics body, which springs back when touched, and there's no
  `ent_create`: props are tried where the level put them. An orc frozen with `ai_disable` takes a thrown body's full
  damage (the settings' 8 for a small one), unaware or not.
- **A still NPC:** `ai_disable 1` holds it still, but it then counts as unaware (the not-alerted ×3 and more), so its
  damage doesn't calibrate; `chained-slash` lets the orc fight.
- **Crashes:** `give weapon_mmspell`, `give spell_air_1` and `give weapon_MMSpell_weapon_AIR_1` shut the game down
  (`Deploy_Spell_AIR_1` is the way).
- **Respawned orcs:** after five or six `mm_npc_create`s in one run a new orc stopped appearing (`backstab`, twice), so
  the input stopped; keep a run to a few spawns.

## A save past the ride

l01_a opens with a two-minute horse ride, and `setpos` is ignored while the player is mounted. To skip it, there is a save just after the ride:
- **The save:** `rb_start`, in `mm\save` on den-win11. Made with `save rb_start` before any cheat. It stands in the scripted corridor scene, at 15 of 50 health, with enemies near.
- **Launching into it:** `opts-arena.ps1` (in `C:\Users\agent\rusty-bridge`) sets the launch options to `-condebug -console -window -w 1280 -h 720 -novid +load rb_start`. `opts-normal.ps1` sets them back to `+map l01_a`. Each restarts Steam.
- **The fight:** `tap/darkmessiah/rbarena.cfg` (copy it to `mm\cfg\`) gives god mode and a short sword, and spawns two orcs at the gate. `mm_player_skills_grosbill 1` unlocks every skill module, Trample among them. The combat model doesn't see skills, so it is off by default, and fights recorded with it go in `local/recordings/experiments/`. Run `exec rbarena` as soon as the level is up: without `god` the save's enemies kill the player within about 20 s.
- **Not during the load:** a command sent while the save is still loading shuts the game down. Wait for the tap log's `dump (level+5s)` line.
- **Toggles:** `god` and `ai_disable` toggle whatever their argument, so a second `exec rbarena` turns god mode off.
- **Lines in a cfg:** they split at `;`, comments included.
- **Spawning:** there is no `ent_create`. `give <class>` spawns an entity, but crashes the game for `mm_player_inputs`.

**Playing it remotely.** Sunshine runs on den-win11, and Moonlight streams the console desktop without taking it over, unlike RDP.
- **The mouse:** in Moonlight, turn off "Optimize mouse for remote desktop instead of games" and let it capture the mouse (Ctrl+Alt+Shift+Z). Absolute positions pin the view to an edge.
- **Keyboard look:** `bind uparrow +lookup; bind downarrow +lookdown; bind leftarrow +left; bind rightarrow +right` with `cl_yawspeed 90; cl_pitchspeed 90` gives look on the arrow keys.

**Console commands from the tap:**
- **What works:** `rbwire exec` runs console commands through the client's command buffer, but `+attack` and `+kick` sent that way don't move the player. Real input goes through the playtest agent's desktop lease, as the parry bot's does.
- **Quotes:** from Windows PowerShell 5.1, a quoted parameter needs `\"` to reach the game (`& rbwire exec 'ent_fire x Input \"1 2 3\"'`).

## Recording input for the lab

From v12 a recording carries the player's buttons (docs/wire.md, "The player's
input"), and the lab replays them (docs/lab.md, "Replay"). An arena fight:
1. Launch into `rb_start` (`opts-arena.ps1`).
2. Once the tap logs `dump (level+5s)`, run `exec rbarena`.
3. Play, or run the parry bot, while `rbwire record` runs.

`l01_a-v12-arena-bot.rbrec` is 120 s of the parry bot against `rbarena`'s two
orcs:

```
rbwire record C:\Users\agent\rusty-bridge\l01_a-v12-arena-bot.rbrec --seconds 120
rbwire parrybot --seconds 120 --agent http://192.168.1.12:48300
```

- **The game in front:** the bot's keys go to whatever window has the focus.
  If Steam's window comes up in front, the game pauses and Steam gets the
  input. `C:\Users\agent\rusty-bridge\front.ps1` minimizes Steam and brings
  the game forward. Run it through `run-on-desktop.ps1` before the bot.
- **`rbwire dump`:** prints the input line (`input: held Attack, Forward,
  pressed Attack, released None`). `rbwire info` counts the frames with input
  and the buttons they held.
- **The stepper:** shows it as a row under the frame.

## Recording slow motion

Arkane's own input arms slow motion through the request the tap hooks (docs/wire.md, "Slow motion"):

```
rbwire record C:\Users\agent\rusty-bridge\l01_a-v11-slowmo-input.rbrec --seconds 40
rbwire exec 'ent_fire mm_player_inputs ActiveSlowMotion \"0.8 3 0.2\"'
rbwire exec 'ent_fire mm_player_inputs ActiveSlowMotion \"0 2 0.5\"'
```

The second command is sent 15 s after the first.
- **`rbwire info`:** reports game time against wall time, and the calls by caller.
- **The stepper:** shades the frames where the player's `m_bInBulletTime` is set, lists each call with its caller, and shows "game time ×0.20" next to the clock inside them.
- **Its check:** `viewer/tests/parse.test.mjs` checks both intervals (40 and 66 ticks) and their scales on that recording, when `local/recordings` has it.

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
