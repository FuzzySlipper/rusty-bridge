# The combat lab

`lab/` is a native Rusty Engine product where Dark Messiah's melee runs as C#
gameplay code: the player's sword against an orc, in a courtyard. It is
calibrated against recorded donor fights, and the bridge (tap, Wire,
recordings, `dm-combat`) is its measuring instrument. Den's
`rusty-bridge/native-combat-lab-direction` says why the repository went this
way instead of handing the running donor's channels over one by one.

## Running it

The lab plays with the donor's content, which isn't tracked. Import l01_a from
the local recordings first (docs/import.md):

```
dotnet run --project import/DarkMessiah.Import.Tool -- level local/recordings/l01_a-parrybot.rbrec local/recordings
```

Then, from `lab/`:

```
rusty dev
```

**Where the donor's content comes from:**
- **The pack:** at start the lab finds `local/import` above where it runs and packs it into `local/import.rpak`. The pack is a content container, which it opens at run time.
- **When it repacks:** whenever the pack is older than the import, which takes a few seconds for l01_a's 320 MB.
- **Overriding it:** `RUSTY_BRIDGE_IMPORT` names a container or an import folder to use instead.
- **What's staged:** only `lab/content` (the arena, the player's tuning, the fight's own numbers).

**Controls:**

| Input | Does |
| --- | --- |
| Left button, tap | Fast attack (`layer_fastattack_sword_noshield_A`) |
| Left button, hold | Power attack: winds up (`layer_bigslash_*_in`, held), strikes on release (`_out`) |
| Right button, hold | Parry (`layer_parry_sword_noshield_*`) |
| F | Kick (`layer_sword_noshield_kick_medium`) |
| E / left button | Pick up a crate / throw it (fptester's cubes) |
| G | God: the orc's blows still count but take no health |
| P / O | Hold the world / step it one frame |
| R / M / F3 | Reset / menu / developer shell |

## Where the numbers come from

Almost everything the fight decides is Arkane's, read from the imported `.qct`
files, `scripts/mm_game_settings.txt` and the clips' animation events:

| What | Source |
| --- | --- |
| The moves' clips and their lengths | `first_basic`'s clips: frames over fps |
| Attack playback rate | the weapon's `entity_data` `speed_attack`: 1.10 for the short sword, 0.80 for the orc's cleaver |
| When a swing's blade is traced | the clip's events: 2186 opens the window, 2190 closes it |
| The kick's contact | no events in its clip. `combat.json` sets cycles 0.36–0.42; recorded kicks land at 0.38–0.41 |
| How far the blade reaches | the sword's `rangeattack`, 100 units (2.54 m). The trace runs from the eye toward the blade's tip, as the donor's do |
| What a swing struck | the shared `HitSweep` through the orc's hit boxes and its items' hulls: the same measure as the mirror's Shadow contacts |
| The orc's verdict, damage, the player's parry | `CombatModel` (docs/combat.md): the same rules scored against recordings |
| The kick's damage | the settings' `kick_damage` (1) |
| Which part of an orc's attack lands | its clip's 2102 and 2139 events |

**The lab's own numbers**, in `content/combat.json`, which reloads live and is tunable:
- the power-hold threshold;
- movement while attacking;
- the kick's window, range and push;
- the player's health;
- the orc's spawn, engage distance, walk speed, reach, front cone, attack cooldown, parry chance and health.

The orc's reach stands in for the donor's reach test until #10099 measures it.

## Calibration

`lab/tests/DarkMessiah.Lab.Smoke` measures the moves against every local recording of Wire v9 or later. Those recordings carry the client's view-model layers.

| Move | Recorded player blows inside the lab's window (±0.06) | Clip rate, lab against donor |
| --- | --- | --- |
| Fast attack, window 0.33–0.78 | 39 of 39 (cycles 0.28–0.70) | 3.67 against 3.47 cycles/s |
| Power attack, window 0.13–0.22 | 16 of 21 | 1.03 against 1.01 cycles/s |
| Kick, window 0.36–0.42 | 17 of 17 (cycles 0.38–0.41) | 1.20 against 1.17 cycles/s |

The power attack's five other blows land late in its clip, at cycles 0.76–1.00. They are a second contact the lab doesn't trace yet.

The smoke also checks, on the real product headless:
- **The data:** the sword's range and both weapons' attack speeds; the windows come from the events.
- **The orc's side:** it walks in and its blows reach the player.
- **The player's blows:**
  - a fast attack strikes the orc once;
  - a power attack on the body asks `WDmg × HitPow × hit-group modifier`, computed independently from the `.qct` files;
  - a parrying orc turns the blade;
  - a kick asks `kick_damage` and pushes the orc.
- **The player's guard:** it meets the orc's blows, which it parries, or breaks against.

## The arena

`content/lab.json` holds the arena: native blockout, 24 m square with 4 m walls, two pillars, a low wall and three crates.

**Why blockout:** the l01_a gate courtyard is about this size, and blockout is quick to change.
- **The imported level:** it would need its BSP collision imported as well; today the import has only its drawable faces. That's a later step.
- **The crates:** fptester's loose cubes, kept as props to kick and throw.

## How it's built

```
lab/
  content/                 lab.json (arena), player.json (body and camera), combat.json (the fight's own numbers)
  src/DarkMessiah.Lab/
    LabProduct.cs          composition: input, body, fight, cubes, world time, publish
    Donor/DonorContent.cs  packs and opens dm-import's output; models, .qct numbers, CombatData
    Sword/                 PlayerSword (the moves), FirstPerson (arms, sword, blade trace), Rig, CombatTuning
    Npc/Orc.cs             the orc: walk in, attack, parry, react, fall and come back
    Combat/LabCombat.cs    the fight: traces, verdicts, damage, the orc's blows, the overlay
    Calibration/           SwordCalibration against recordings
    Player/ Scene/ Time/ Developer/ Ui/ Interaction/   from rusty-fptester (below)
  src/ui/                  HUD, pause menu, developer shell
  tests/DarkMessiah.Lab.Smoke/
shared/DarkMessiah.Shared/ ModelInfo (attachments, hit boxes, hulls, clips) and HitSweep, used by the lab and the mirror
```

**The Engine pin** is the repository's, in the root `Directory.Build.props`. The mirror, the lab, `shared/` and `dm-combat` all build on it.

**Drawing:** the lab publishes its whole scene each update, and the orc's and the arms' items aren't joint-attached children. They are placed at the holder's sampled joint pose times the attachment, the same pose the hit tests use.

## Developer tooling

- **Commands:**
  - `lab.inspect` and `lab.fight` read the lab and the fight;
  - `lab.orc.spawn` and `lab.orc.place x y z` spawn and place the orc;
  - `lab.god` toggles god mode;
  - `lab.dev.view x y z yaw pitch` stands at a viewpoint;
  - `lab.time`, `.rate`, `.hold`, `.run` and `.step` control world time.
- **Tunables:** Body and View (`player.json`), Sword and Orc (`combat.json`), and World time.
- **Inspection targets:** `eye`, `reach`, `orc`, `last-blow` and `spawn`.
- **The `lab.combat` overlay set:** each traced step of recent swings (green struck, blue parried or blocked, grey nothing), and the orc's reach.
- **The developer shell (F3):** shows the overlay while open.

## Provenance

The lab started as a copy of rusty-fptester at `61f58a9` (Den #10124, its maintained first-person reference), per `rusty-fptester/docs/reference.md`.

**Copied as they were** (renamed namespace, product id, `lab.*` commands):
- `Player/`: `PlayerController`, `PlayerTuning`, `PlayerRecovery`, `BodyEvents`, `InputDiagnostics`;
- `Time/WorldTime`;
- `Interaction/CubeInteraction`;
- `Content/Authored`;
- `Scene/`: `LabScene`, `LabDefinition`, `LabGeometry`;
- `src/ui/`: `main.js`, `pause.js`, `developer.js`.

**Adapted:**
- `LabProduct` (from `FpTesterProduct`): the fight replaces the action model's use;
- `Developer/LabDeveloper` and `LabTunables`: fight commands and the Sword and Orc groups replace the action ones;
- `Ui/LabHud` and `LabObservation`: the fight's readouts;
- `main.js`: the HUD and help;
- `LabScene.Publish`: takes the fight's appearances.

**Left out:**
- fptester's action model (`Actions/`, `content/actions.json`). Dark Messiah's moves are timed by their own clips and events, so a generic windup/commit/recovery doesn't fit them.
- `Combat/LabCombat` was replaced.

## Not done yet

- **Orc AI:** a minimal loop stands in for it. It walks in, attacks in turn, sometimes parries, reacts to hits and kicks. Its behaviour is next to measure against Shadow decisions.
- **Input replay** (#10122): recording the player's inputs on the Wire and replaying them here.
- **Reach and pose** (#10099, #10098): the orc's reach test and NPC pose timing.
- **Physics:**
  - knockdowns into ragdolls;
  - a kick that throws the orc into walls;
  - body pushes. fptester notes the Engine has no bump-to-push yet.
- **First person:**
  - the first-person camera follows the eye, not `SK_Camera_Eyes`'s animation (Dark Messiah's swing camera motion);
  - the power attack's late second contact;
  - combos (`layer_combo_*`);
  - the shield-holding moves.
