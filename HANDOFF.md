# Handoff: stage-1 spike

Written 2026-10-17 when the work moved from the desktop to a dedicated test
machine. The game's fullscreen-then-windowed startup was throwing desktop
windows around, so in-game testing continues there. Start a new session by
reading this file, then [docs/design.md](docs/design.md).

## What the project is

Rusty Bridge runs an old game as the live simulation source while a Rusty
Engine product draws it. Authority then moves from the old game to Rusty
channel by channel ("ship of Theseus"), and each Rusty system is tuned
directly against the old game's data stream. A secondary goal is a combat
stepper: record Dark Messiah fights and step through them tick by tick to
understand why the combat feels good.

- First donor: **Dark Messiah of Might and Magic** (Steam app 2100). It's
  Valve's Source engine (2006, Episode One era), heavily modified by Arkane.
  32-bit.
- House engine: `rusty-engine` (Rust host, wgpu renderer, C# products, full
  GLB pipeline with animated meshes). Sibling offline conversions live
  alongside it (`rusty-dagger`, `rusty-crawler`, `rusty-underworld`,
  `rusty-dungeon`, `rusty-doom`), plus the shared asset workbench
  `asset-pipeline`. On the original machine these were under `P:\dev`.
- Design, channel table, handover order and upstream engine asks:
  [docs/design.md](docs/design.md).

## Stage-1 goal

Confirm that a server plugin loads in Dark Messiah and can list entities
with their class names and model paths.

**Status: done (2026-10-17, den-win11).** The plugin auto-loads at startup,
resolves every interface, detects the edict layout (stride 20, networkable
+8, unknown +12, 255/255 consistent) and dumps ~1500 entities on `l01_a`
with class names and model paths, 0 mismatches. See "Results on den-win11"
below. A sample dump is in `local/dumps/` (gitignored).

**Stage 2 (entity origins): done, same day.** Every dumped entity now has
an origin and angles (1495/1495, 0 mismatches). Checked against `l01_a`'s
BSP entity lump: all static entities match exactly (123 prop_dynamic, 88
prop_physics, 62 func_brush, NPCs, triggers...). The only differences are
doors and their door grids, which had already moved. See "Entity origins"
below.

**Stage 3 (Wire stream): done, same day.** The tap writes every donor tick
(66.7/s, 15 ms) into a shared-memory ring, `mm\addons\rustybridge.wire`.
A C# reader (`wire/`) follows it with no lost frames. Format, protocol and
measurements: [docs/wire.md](docs/wire.md). Downstream Rusty Engine
products are C# (Rust is engine-only), so the reader is C#. The Frontend
product can take `RustyBridge.Wire` as it is.

**Stage 4 (Recorder and combat stepper): first version, same day.**
`rbwire record` saves Wire frames to a delta-coded `.rbrec` (about 220
bytes per tick). `viewer/timeline.html` steps through a recording tick by
tick: a top-down map with trails, per-entity speed, and a scrubbable
timeline. A staged fight (two orcs and a goblin against the l01_a gate
guards) is in `local/recordings/l01_a-orcs-vs-guards.rbrec`. Format, how
to stage a fight, and the first findings (NPCs update every 8 ticks,
weapons are separate entities, some ragdolls are server-side):
[docs/recording.md](docs/recording.md).

**Stage 5 (combat state): done, same day.** Wire and recordings are now v2:
every entity carries health, max health, life state, animation sequence,
cycle and playback rate. Offsets come from the classes' send tables at
runtime (Arkane's health is the float pair `m_fHealth`/`m_fMaxHealth`).
`rbwire record` adds each model's sequence names from the game's VPKs. The
stepper names each actor's current action and shows hit rings, health and
action bands, plus a clickable list of swings, hits and deaths.
`local/recordings/l01_a-orcs-vs-guards-combat.rbrec` holds a 70 s fight
with 41 swings, 42 hits and 4 deaths. Details are in docs/wire.md and
docs/recording.md.

**Stage 6 (damage events): done, same day.** The tap hooks
`CBaseEntity::TakeDamage` (`server.dll+0x3B9820`, a 6-byte detour, checked
before patching and removed on unload). Every hit becomes a Wire v3 damage
record with:

- attacker (the wielder; the weapon is recorded separately) and inflictor
- damage requested, plus health before and after, so killing blows show
- damage type, hit group, hitbox and bone
- position, force and the swing trace
- push force and slow-time fields

The record layout comes from Arkane's own `CTakeDamageInfo` datadesc.
Recordings are v3 too. The stepper lists hits as "orc_sword #365 → goblin
#401 with orccleaver: 6 slash to head, kill" and draws each swing trace and
impact point. In `l01_a-orcs-vs-guards-damage.rbrec`, every record's
health before and after matches the streamed health exactly.

**Stage 7 (blocks and parries): done, same day.**

- **Where the game decides:** each melee blow asks the victim for a verdict
  through virtual slot 393. Arkane's melee damage routine
  (`server.dll+0x407D70`, "Hit blocked or parried, dmg = 0") is one of
  about ten callers.
- **Arkane's NPCs** answer from `server.dll+0x2E0030`: 2 hit, 1 parry, 8
  shield, 4 no effect. RTTI shows the player and the base classes answer 0.
- **The tap** detours that implementation and streams each verdict with
  victim, attacker, weapon, what the blow struck, and the result.
  Wire and recordings are now v4.
- **The stepper** lists blocks and marks the defender.
- **Evidence:** `local/recordings/l01_a-duel-defense.rbrec` (staged with
  `rbduel.cfg`) holds 3 shield blocks, a ghoul's claw striking an orc's
  shield, among 47 checked hits.
- **Open:** no parries seen yet, and the player's own blocking is resolved
  elsewhere.

**Stage 8 (player parries): done, same day.**

- **Where:** the player's parry stance is its action (`+0x20F8` 0x94/0x96).
  Each blow's outcome is written to its parry state (`+0x1C78`: 0 none,
  1 parried, 2 broken) through `CBasePlayer` slot 369
  (`server.dll+0x3FEC50`), mostly from the attack resolver
  `server.dll+0x3B1A90`. A bit-0x10 weapon flag breaks the parry.
- **The tap** detours the setter with a naked-asm stub and streams
  "parried" (17) and "parry broken" (18) defense records, with the
  attacking NPC.
- **The parry bot** (`rbwire parrybot`) plays the player from the Wire. It
  blocks when an enemy within reach starts an attack sequence and swings
  otherwise, through the desktop lease.
- **Evidence:** in `local/recordings/l01_a-parrybot.rbrec` it caught 10
  clean player parries, 8 broken parries, 2 NPC weapon parries (the
  player's sword struck an orc's cleaver) and a shield block.
- **Finding:** the attack decides the outcome, not the timing. Every bash
  broke the parry (7/7) and every other attack was parried (8/8), which is
  the weapon-flag-0x10 rule.
- **Weapon owners:** player weapons keep their owner in `m_hOwner`. The tap
  falls back to it, and player hits now resolve to the player.

See docs/wire.md, "The player's parry".

## What's in this directory

| Path | What |
| --- | --- |
| `docs/design.md` | The handover design. |
| `tap/darkmessiah/src/plugin.cpp` | The stage-1 server plugin, written without Source SDK headers. |
| `tap/darkmessiah/build.ps1` | Builds the 32-bit DLL with MSVC; `-Install` copies it into the game; `-Game <path>` overrides the install path. |
| `tap/darkmessiah/rustybridge.vdf` | Plugin auto-load file for `mm\addons`. |
| `tap/darkmessiah/src/wire.h` | Wire v1 layout (writer side). |
| `wire/` | C# Wire reader and recordings (`RustyBridge.Wire`), its tests, and the `rbwire` CLI (tail, dump, record, info). |
| `viewer/` | `timeline.html`, the combat stepper; `embed.py` bakes a recording into a copy. |
| `docs/recording.md` | Recording format, staging a fight, stepper usage, findings. |
| `docs/wire.md` | Wire format, protocol and measurements. |
| `tools/vpk_list_maps.py` | Lists the `.bsp` maps inside the game's VPK directory files. |

`tap/*/out/` is build output and is ignored; rebuild on the new machine.

## Results on den-win11

The test machine is den-win11 (see the `windows-box` skill). The game is in
Steam's default folder, `C:\Program Files (x86)\Steam\steamapps\common\Dark
Messiah Might and Magic Single Player`, which is fine: Steam gives Users full
control there, so installing and logging need no elevation. `build.ps1` now
defaults to that path. Build from the Linux checkout through `P:`:

```
ssh den-win11 "C:\Users\agent\crew\bin\run-on-desktop.ps1 -Command 'P:\dev\rusty-bridge\tap\darkmessiah\build.ps1 -Install'"
```

The DLL is locked while the game runs, so stop `mm.exe` before installing.
Launch with `steam.exe -applaunch 2100` (through `run-on-desktop.ps1`). The
Steam launch options for 2100 are set to
`-condebug -console -window -w 1280 -h 720 -novid +map l01_a`. Steam only
keeps launch options written to `userdata\<steam-id>\config\localconfig.vdf`
while Steam is shut down; a backup is at `localconfig.vdf.rb-backup`.

What was found:

1. **Plugin paths resolve from `bin\`, not from `mm\`.** `plugin_load
   addons/rustybridge` (and `bin/...`, `rustybridge`, `.dll` forms) prints
   "Unable to load plugin". `plugin_load ../mm/addons/rustybridge` works. The
   `.vdf` now uses `"file" "../mm/addons/rustybridge"` and auto-loads at
   startup, so no launch argument is needed. This is why it never loaded on
   the original machine.
2. **`+plugin_load` on the command line crashes the game** before
   `console.log` starts (`0xC00001A5`, invalid exception handler, inside
   `mm.exe`), even before our DLL is loaded. Don't use it; rely on the `.vdf`.
3. **Arkane's `IServerNetworkable` is not the Episode One order.** Two extra
   slots come before `GetEdict`: 0 GetEntityHandle, 1 unknown, 2
   GetServerClass, 3/4 flag get/or (entity+0x32c), **5 GetEdict, 6
   GetClassName**, 7 Release. Read from `mm\bin\server.dll` (image base
   0x22000000, networkable vtable at +0x5E9878). The `IServerUnknown` /
   `IServerEntity` vtable *does* match Episode One (GetNetworkable 4 =
   entity+0x454, GetModelIndex 6 = [entity+0x278], GetModelName 7 returns a
   `string_t` by hidden pointer). If the layout probe ever scores 0 again it
   logs raw edicts with vtable slots as `server.dll+rva` for disassembly
   (`objdump -d -M intel` on Linux works).
4. Despite `-window`, the game runs fullscreen at 1280x720 and changes the
   desktop resolution; the first launch shows a gamma calibration screen
   (OK at 640,269). It goes back to 1920x1080 on exit. On a shared box, stop
   the game when done.
5. Typing into the in-game console works through a desktop lease
   (`POST http://192.168.1.12:48300/v1/lease`, then `hold` steps with VK
   codes; `` ` `` = 192). The console closes on map load and focus can drop
   to the desktop, so prefer relaunching over typing.

## Build and install

Needs Visual Studio (Build Tools is enough) with the x86/x64 C++ workload.
The script finds it through `vswhere` and builds with `vcvars32.bat`.

```powershell
.\tap\darkmessiah\build.ps1 -Install -Game 'X:\path\to\Dark Messiah Might and Magic Single Player'
```

This puts `rustybridge.dll` and `rustybridge.vdf` into `<game>\mm\addons\`.

## How the plugin works

- It exports `CreateInterface` and answers `ISERVERPLUGINCALLBACKS001`,
  using the 2006 callback vtable order (Load, Unload, Pause, UnPause,
  GetPluginDescription, LevelInit, ServerActivate, GameFrame, ...).
- It logs to `rustybridge.log` next to the DLL (`mm\addons\`). The first
  line, `rustybridge.dll attached`, is written as soon as the DLL is
  loaded, before the engine calls anything. If that line is missing, the
  DLL was never loaded.
- `Load` logs which engine and game interfaces resolve: `VEngineServer021`,
  `VModelInfoServer001`, `ServerGameDLL004`, `ServerGameEnts001`,
  `PlayerInfoManager002`, and others.
- In `ServerActivate` it works out the edict array layout by testing, not
  assuming. A slot counts as valid when its networkable's `GetEdict()`
  returns the slot itself. It tries strides 16–32 and offsets 4–12; the
  expected answer for this engine era is stride 20, networkable at +8,
  unknown at +12.
- It dumps every live entity as index, class name, model index, and model
  path (through `IVModelInfo::GetModel`/`GetModelName`). It dumps at
  ServerActivate, about 5 s into the level, and whenever a file named
  `rustybridge_dump` appears in `mm\addons\` (the file is deleted afterwards).
- Every call into the game is checked first (the vtable must sit in a
  loaded module, and the slot must point at executable code) and wrapped in
  SEH. A wrong guess about Arkane's layouts should show up in the log
  instead of crashing the game.

### Entity origins (stage 2)

`IServerUnknown::GetCollideable` (slot 3, entity+0x3F0) returns the
`CCollisionProperty`. Its vtable (`server.dll+0x59BEB4`; entities use the
subclass copy at `+0x6A060C`) matches Episode One: 0 GetEntityHandle, 1/2
OBBMins/Maxs, 3 WorldSpaceTriggerBounds, 4/5 TestCollision/Hitboxes, 6
GetCollisionModelIndex, 7 GetCollisionModel, **8 GetCollisionOrigin**, **9
GetCollisionAngles**, 10 CollisionToWorldTransform, 11 GetSolid, 12
GetSolidFlags. The plugin calls slot 8, which recomputes a dirty abs transform
and returns `&m_vecAbsOrigin` (entity+0x6C4). It only accepts the result if
slot 0 returns the entity and the pointer is exactly entity+0x6C4. Angles are
then read from `m_angAbsRotation` at entity+0x744, because slot 9 returns zero
for non-rotatable solids. Any failure is counted, and the first three are
logged with the vtable as `server.dll+rva`.

To check a dump against the map: `l01_a.bsp` is in `vpks\depot_2102_000.vpk`
(offset and length come from `depot_2102_dir.vpk`). Lump 0 of the BSP is the
plain-text entity list. Match by (classname, model) and compare origins.

## What was verified on the original machine

- `bin\engine.dll` contains `ISERVERPLUGINCALLBACKS001`, `plugin_load`,
  `addons/*.vdf` and `CServerPlugin`, so plugin support was compiled in.
- Interface versions in the binaries: engine `VEngineServer021`,
  `VModelInfoServer001`, `VEngineServerStringTable001`; `mm\bin\server.dll`
  `ServerGameDLL004`, `ServerGameEnts001`, `ServerGameClients003`,
  `PlayerInfoManager001/002`.
- `gameinfo.txt` says `type singleplayer_only` and `SteamAppId 2100`.
- Maps (from the VPKs): `l00`, `l01_a`, `l02_a`, `l02_b1`, `l02_b2`, …
  through `l12_c`, plus `menu_3d` and `e3_l06_l09`.
- The DLL builds as PE32/i386 and exports `CreateInterface`.

## What happened when it was launched

1. Running `mm.exe` directly (`-game mm ...`) exits immediately. Launch
   through Steam instead.
2. `steam://run/2100//-condebug -window -w 1280 -h 720 -novid +map l01_a/`
   worked: `console.log` shows `Spawn Server l01_a`. With the `.vdf` in
   `mm\addons`, **the plugin did not auto-load**. No `rustybridge.log` was
   created, so the DLL was never even loaded. Either this build doesn't
   scan `addons/*.vdf` at startup, or it resolves the `"file"` path
   differently.
3. A relaunch with `+plugin_load addons/rustybridge +plugin_print +map
   l01_a` didn't start the game. It was probably waiting on Steam's "launch
   with custom arguments?" prompt. Nothing from that attempt reached the
   console log.
4. Side note from `console.log`: "Can't use cheat cvar ... in multiplayer".
   The engine may treat even single player as a multiplayer listen server.
   This might matter for cheat-protected console commands such as
   `host_framerate` or `host_timescale` when stepping.

## Next steps on the test machine

Steps 1–2 below are done (see "Results on den-win11"); they're kept for
reference. Continue at step 3.

1. Set the Steam launch options (Properties → Launch Options) instead of
   using a URL, so no prompt appears:
   `-condebug -window -w 1280 -h 720 -novid +plugin_load addons/rustybridge +plugin_print +map l01_a`
2. Launch, then read `mm\console.log` and `mm\addons\rustybridge.log`.
   - **No `attached` line:** the path wasn't resolved. Try
     `plugin_load ../mm/addons/rustybridge`, then an absolute path, from the
     in-game console (enable it with `-console` or in the options menu).
     `plugin_print` lists what's loaded.
   - **`attached` but no `Load`:** the engine asked for a different
     interface name. Every `CreateInterface(...)` request is logged, so the
     name it wanted will be there.
   - **`Load` but a bad dump:** the `layout:` line and the mismatch counts
     show which assumption broke.
3. Entity origins, the Wire stream, the Recorder, a combat stepper and
   damage events are done. Next options:
   - Animation layers: gestures such as `layer_parry_A` play on overlay
     layers, which the Wire doesn't stream yet.
   - Turning the player's view: relative mouse motion through the lease
     doesn't move it, so the parry bot can't seek enemies. It waits for
     them.
   - Kick and adrenaline mechanics; the kick push force is already in the
     damage records.
   - Which hits trigger slow motion.
   - Hold the donor's frame from the tap, to step the live game.
   - Import l01_a and the fight's characters to GLB (design doc "First
     steps" 4). The design doc's Recorder and Frontend are C# Rusty
   Engine products; see `~/dev/rusty-doom` or `~/dev/rusty-dagger` for how
   a downstream product is set up. See "First steps" in the design doc.

## Leftovers on the original machine

`D:\SteamLibrary\...\Dark Messiah Might and Magic Single Player\mm\addons\`
still holds `rustybridge.dll` and `rustybridge.vdf`. They're harmless while
they don't auto-load. Delete that folder to undo the install.
