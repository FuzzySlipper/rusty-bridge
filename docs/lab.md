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
| F | Kick (`layer_sword_noshield_kick_medium`), also cutting off a swing whose hit window has passed |
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
| Whether an orc's blow reaches the player | the donor's reach test (`CombatModel.Reach`, docs/combat.md "Reach"): a 1.5-unit sweep for the part's `range` (60 to 120), meeting the player's ±16 box |

**The lab's own numbers**, in `content/combat.json`, which reloads live and is tunable:
- the power-hold threshold;
- movement while attacking;
- the kick's window, range and push;
- the player's health;
- the orc's spawn, engage distance, walk speed, attack cooldown, parry chance and health.

## Calibration

`lab/tests/DarkMessiah.Lab.Smoke` measures the moves against every local recording of Wire v9 or later. Those recordings carry the client's view-model layers.

| Move | Recorded player blows inside the lab's window (±0.06) | Clip rate, lab against donor |
| --- | --- | --- |
| Fast attack, window 0.33–0.78 | 47 of 47 (cycles 0.28–0.73) | 3.67 against 3.47 cycles/s |
| Power attack, window 0.13–0.22 | 22 of 22 (cycles 0.13–0.18) | 1.03 against 1.01 cycles/s |
| Kick, window 0.36–0.42 | 29 of 29 (cycles 0.38–0.41) | 1.20 against 1.17 cycles/s |

**A blow belongs to the newest move.** A kick can start while a power
attack's layer is still fading out under it. Credited to the first layer
listed, seven kicks counted as power blows late in the power clip
(cycles 0.76–1.00), which looked like a second contact. Calibration now takes
the layer with the highest order. Those kicks start when the power attack is
at about cycle 0.45, after its hit window has closed. That is why the lab's
kick may cut off a swing then.

The smoke also checks, on the real product headless:
- **The data:** the sword's range and both weapons' attack speeds; the windows come from the events.
- **The orc's side:** it walks in and its blows reach the player.
- **The player's blows:**
  - a fast attack strikes the orc once;
  - a power attack on the body asks `WDmg × HitPow × hit-group modifier`, computed independently from the `.qct` files;
  - a parrying orc turns the blade;
  - a kick asks `kick_damage` and pushes the orc.
- **The player's guard:** it meets the orc's blows, which it parries, or breaks against.

## Replay

Recordings of Wire v12 and later carry the player's buttons (docs/wire.md,
"The player's input"). `lab.replay NAME` replays one against the lab and
reports where the outcomes agree. NAME is a path, or a file in
`local/recordings`. The smoke replays every such recording it finds.

**Each recorded tick is one lab step of that tick's game time:**
- **The sword:** the held attack and attack2 bits drive it (`PlayerSword`),
  from the recorded eye and view angles. Kicks aren't a button bit, so a kick
  starts when the donor's kick clip starts on the view model.
- **The orc:** the lab's orc follows the donor orc the player aims at,
  within 4 m (else the nearest) (`Orc.Follow`):
  - it stands, faces, plays and has the health the recording gives it, and plays on between
    streamed states at the recorded rate;
  - a parry layer is its parry state, and an attack sequence or `layer_attack_*` overlay its attack;
  - each attack part's event it crosses is a blow, as in the lab's own fight.
- **What the lab decides:** its trace, verdict and damage for the player's blows, and its reach
  and the player's parry for the orc's. The donor's decisions come from the same recording
  (`Blows`, and the kick damage records).

**The report pairs blows within 0.3 s:**
- **agree:** the same verdict, and the same damage and hit group when the blow lands;
- **differ:** they were paired, but those differ;
- **donor only, lab only:** no partner within the window;
- **not compared:** blows by or on NPCs the lab wasn't following.

The moves each side began are paired by clip the same way.

**`l01_a-v12-arena-bot.rbrec` (the parry bot, 120 s):**

| What | Donor | Lab | Agree |
| --- | --- | --- | --- |
| Fast attacks, power attacks, kicks begun | 7, 9, 8 | 8, 9, 8 | 24 of 24 begin together, the lab 4 ms ahead on average |
| The player's blows on the followed orc | 16 | 14 | 4 agree, 6 differ |
| The orc's blows on the player | 40 | 54 | 38 agree |

**Where they differ:**
- **Which part a swing strikes:**
  - Donor blows on hit group 1 (4.2 and 9 damage) were the lab's on group 4 (1.4 and 3).
  - One donor hit was a lab parry, and one donor NoEffect a lab hit.
  - Why they can differ: the lab traces from the eye toward its own blade, against an orc posed from
    states streamed every few ticks. Neither the donor's sweep nor the orc's exact pose is reproduced.
- **Parry or shield:** where the donor's verdict is Parry, the lab's trace met
  the orc's shield and decided Shield.
- **Kicks:** three donor kicks landed where the lab's missed. The lab's kick
  reach is its own number (`KickRange`).
- **The orc's blows:** they use the donor's reach test. The lab-only ones are
  parts the lab's followed orc fires where the donor's orc struck nothing.
  - **The likely cause:** the lab plays the orc on between streamed states, so it fires parts on its own timing, not the donor's thinks.
  - **Not checked case by case:** the reach model alone agrees on 80 of the recording's 84 attack parts (docs/combat.md).
- **Two parts at once:** the lab sometimes lands both of an attack's parts in
  one step (`attack_KickSwing`, `attack_Fast`), where the donor lands one.

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
    Replay/                InputTimeline (a v12 recording's input and orcs), CombatReplay (the run and its report)
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
  - `lab.replay NAME` replays a recording's input and reports how the outcomes compare;
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
- **Pose timing** (#10098): NPC poses between streamed states.
- **Physics:**
  - knockdowns into ragdolls;
  - a kick that throws the orc into walls;
  - body pushes. fptester notes the Engine has no bump-to-push yet.
- **First person:**
  - the first-person camera follows the eye, not `SK_Camera_Eyes`'s animation (Dark Messiah's swing camera motion);
  - combos (`layer_combo_*`);
  - the shield-holding moves.
