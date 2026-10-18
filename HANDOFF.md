# Rusty Bridge: working notes

How to run the bridge against Dark Messiah, and what the game has turned out
to be like underneath. The design is in [docs/design.md](docs/design.md).
The Wire format and hooks are in [docs/wire.md](docs/wire.md),
recordings, staging and the stepper in [docs/recording.md](docs/recording.md),
the asset importer in [docs/import.md](docs/import.md), the Frontend
mirror in [docs/mirror.md](docs/mirror.md), and the Shadow combat model in
[docs/combat.md](docs/combat.md).
Tasks, status and decisions live in the Den project `rusty-bridge`.

## What the project is

Rusty Bridge runs an old game as the live simulation source while a Rusty
Engine product draws it. Authority then moves from the old game to Rusty
channel by channel ("ship of Theseus"), and each Rusty system is tuned
directly against the old game's data stream. A secondary goal is the combat
stepper: record Dark Messiah fights and step through them tick by tick, to
understand why the combat feels good.

- **Donor:** Dark Messiah of Might and Magic (Steam app 2100). It runs on
  Valve's Source engine (2006, Episode One era), heavily modified by Arkane,
  32-bit. It's the only donor so far; the repository is meant to hold
  others later (design "Scope").
- **House engine:** `rusty-engine`: Rust host, wgpu renderer, C# products.
  Downstream products are C#. Sibling offline conversions (`rusty-dagger`,
  `rusty-doom`, ...) show how a product is set up.

## What's in this directory

| Path | What |
| --- | --- |
| `docs/design.md` | The handover design. |
| `docs/wire.md` | Wire format and protocol, and how each hook and field offset was found. |
| `docs/recording.md` | Recording format, staging fights, the stepper, the parry bot, findings. |
| `tap/darkmessiah/src/plugin.cpp` | The server plugin (the Tap), written without Source SDK headers. |
| `tap/darkmessiah/src/wire.h` | Wire layout, writer side. |
| `tap/darkmessiah/build.ps1` | Builds the 32-bit DLL with MSVC; `-Install` copies it into the game, `-Game <path>` overrides the install path. |
| `tap/darkmessiah/rustybridge.vdf` | Plugin auto-load file for `mm\addons`. |
| `tap/darkmessiah/rb*.cfg` | Fight staging scripts for `mm\cfg\`. `rbfight`: NPC fight. `rbduel`, `rbparry`: player fights. `rbbot`: setup for the parry bot. |
| `wire/` | C# Wire reader, recordings and donor-file readers (`RustyBridge.Wire`), their tests, and the `rbwire` CLI. |
| `viewer/` | `timeline.html`, the combat stepper; `embed.py` bakes a recording into a copy. |
| `import/` | `dm-import` (`DarkMessiah.Import.Tool`): maps and studio models to GLBs keyed by Wire model path, with its library and tests. |
| `mirror/` | The Frontend mirror: a Rusty Engine product (`DarkMessiah.Mirror`) that replays a recording on the imported level, with its smoke run. Its own Engine pin is in `mirror/Directory.Build.props`. |
| `combat/` | The Shadow combat model (`DarkMessiah.Combat`): NPC verdicts, player parries and damage from captured inputs, and `dm-combat score`, which scores it against recordings. |
| `tools/vpk_list_maps.py` | Lists the `.bsp` maps inside the game's VPK directory files. |
| `local/` (gitignored) | Recordings, dumps and anything else derived from the game: `local/donor/vpks` (a copy of the game's VPKs, which the importer reads) and `local/import` (its GLBs and `<map>.assets.json`). |

`rbwire` subcommands: `tail`, `dump`, `record`, `info`, `sequences`,
`parrybot`. `rbwire help` prints usage; with no arguments it runs `tail`.

## Running on den-win11

The test machine is den-win11; see the `windows-box` skill. Never RDP into
it.

**Game location.** The game is in Steam's default folder, `C:\Program Files
(x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player`.
Steam gives Users full control there, so installing and logging need no
elevation. `build.ps1` defaults to that path.

**Build and install the Tap** from the Linux checkout through `P:`. `P:`
exists only on the console desktop, hence `run-on-desktop.ps1`. The DLL is
locked while the game runs, so stop `mm.exe` first.

```
ssh den-win11 "C:\Users\agent\crew\bin\run-on-desktop.ps1 -Command 'P:\dev\rusty-bridge\tap\darkmessiah\build.ps1 -Install'"
```

This puts `rustybridge.dll` and `rustybridge.vdf` into `<game>\mm\addons\`.
Building needs Visual Studio Build Tools with the x86/x64 C++ workload; the
script finds it through `vswhere`.

**Publish `rbwire`** once per change, also through the desktop:

```
dotnet publish P:\dev\rusty-bridge\wire\RustyBridge.Wire.Cli\RustyBridge.Wire.Cli.csproj -c Release -o C:\Users\agent\rusty-bridge\rbwire --artifacts-path C:\Users\agent\rusty-bridge\artifacts
```

Do this *before* launching the game. Anything run on the desktop takes
focus and minimizes the fullscreen game.

**Launch** with `steam.exe -applaunch 2100` (through `run-on-desktop.ps1`).
The Steam launch options for 2100 are
`-condebug -console -window -w 1280 -h 720 -novid +map l01_a`. Steam only
keeps launch options written to `userdata\<steam-id>\config\localconfig.vdf`
while Steam is shut down; a backup is at `localconfig.vdf.rb-backup`.
Write them from the console desktop (`run-on-desktop.ps1`); `steam -shutdown`
from plain SSH doesn't reach the desktop's Steam. For a donor that draws
nothing and leaves the desktop alone, use
`-condebug -console -noshaderapi -rbhidden -novid +map l01_a` and stage it
with `rbwire exec` (docs/wire.md, "A hidden donor").

**Holding and stepping:** `rbwire hold`, `rbwire step [N]` and `rbwire run`
hold the live donor on a tick (docs/wire.md, "Holding the donor").
`rbwire exec COMMAND` runs a console command without typing into the game.

**Session sequence:**

1. The tap logs to `mm\addons\rustybridge.log`; wait for
   `wire: streaming on`.
2. l01_a opens with a scripted horse ride of about two minutes. Wait until
   the player stops moving: `rbwire tail` prints the player's position.
3. Start `rbwire record` in the background.
4. Run a staging cfg from the console: `exec rbfight`. Typing into the
   console goes through a desktop lease; see "Input through the playtest
   agent" below.
5. Stop `mm.exe` when done. The game changes the shared desktop's
   resolution.

## Findings

**Loading the plugin**

- Plugin paths resolve from `bin\`, not from `mm\`.
  `plugin_load ../mm/addons/rustybridge` works; `addons/rustybridge` and the
  other forms print "Unable to load plugin". The `.vdf` uses that path and
  auto-loads at startup.
- `+plugin_load` on the command line crashes the game before `console.log`
  starts (`0xC00001A5`, inside `mm.exe`), even before the DLL loads. Rely on
  the `.vdf`.
- Running `mm.exe` directly exits immediately; launch through Steam. A
  `steam://run` URL with arguments waits on Steam's "launch with custom
  arguments?" prompt, so set launch options instead.

**The game on the desktop**

- Despite `-window`, the game runs fullscreen at 1280×720 and changes the
  desktop resolution. It goes back to 1920×1080 on exit. The first launch
  shows a gamma calibration screen (OK at 640,269).
- The console log reports "Can't use cheat cvar ... in multiplayer": single
  player runs as a listen server. `sv_cheats 1` from a cfg does enable
  `god`, `setpos`, `give` and `mm_npc_create`.
- Arkane's `valve.rc` doesn't exec `autoexec.cfg`. Binds and staging come
  from the console (`exec <cfg>`).

**Input through the playtest agent**

- The agent listens on 192.168.1.12:48300 only, not on localhost.
- Typing into the console works through a desktop lease: `hold` steps with
  VK codes, and `` ` `` is 192. The console closes on map load, so open it
  again before typing.
- Mouse buttons work (`click`; button 1 attacks, 3 blocks).
- Relative mouse motion (`move`) does not turn the view. Keys bound to
  `+left`/`+right` do (`rbbot.cfg`, `rbseek.cfg`); read the view's yaw from
  the `viewmodel`, since the player entity's yaw is its body.

**Runtime layouts** (details and how each was found in
[wire.md](docs/wire.md))

- Arkane's `IServerNetworkable` vtable differs from Episode One: 5 is
  `GetEdict`, 6 is `GetClassName`, and slot 1 reads `m_hOwnerEntity`
  (+0x500).
- `IServerEntity` and `CCollisionProperty` match Episode One.
  `GetCollisionOrigin` (slot 8) returns entity+0x6C4, and angles are at
  +0x744.
- Health is the float pair `m_fHealth`/`m_fMaxHealth` (+0x310/+0x30C).
  Data-table send props have type 6.
- The edict array has stride 20, with the networkable at +8 and the unknown
  at +12. `server.dll`'s preferred image base is 0x22000000, but it loads
  elsewhere, so the hooks use RVAs.

## How the plugin works

- It exports `CreateInterface` and answers `ISERVERPLUGINCALLBACKS001`,
  using the 2006 callback vtable order.
- It logs to `rustybridge.log` next to the DLL. The first line,
  `rustybridge.dll attached`, is written as soon as the DLL loads. If it's
  missing, the DLL never loaded.
- `Load` resolves the engine and game interfaces and `gpGlobals`, opens the
  Wire file, and installs three detours. Each checks its bytes first and
  stays off when they differ; each is removed on Unload.
  - `CBaseEntity::TakeDamage`, for damage;
  - `CNPC_BaseArx`'s defense verdict, for blocks and parries;
  - the player's parry-state setter.
- `ServerActivate` confirms the edict layout by testing: a slot is valid when
  its networkable's `GetEdict()` returns the slot itself. It then dumps
  every entity and turns streaming on only if the layout and `gpGlobals`
  check out.
- `GameFrame` writes one Wire frame per donor tick: every live edict's
  transform, health and animation, plus the damage and defense records
  queued since the previous frame.
- The first time a class appears in a level, its field offsets come from its
  send table.
- Entity dumps go to the log at `ServerActivate`, about 5 s into the level,
  and whenever a file named `rustybridge_dump` appears in `mm\addons\`.
- Calls into the game are checked (the vtable must sit in a loaded module)
  or wrapped in SEH, so a wrong guess shows up in the log instead of
  crashing the game.

To check entity positions against the map: `l01_a.bsp` is in
`vpks\depot_2102_000.vpk` (offset and length from `depot_2102_dir.vpk`).
Lump 0 of the BSP is the plain-text entity list. Match by class and model,
and compare origins.
