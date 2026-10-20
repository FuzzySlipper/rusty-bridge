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
files, `scripts/mm_game_settings.txt` and the clips' animation events. The import also carries the game's other
tables for the mechanics still to come (docs/import.md, "Gameplay tables"): spells and skills through
`DonorContent.Tables`, and every weapon's, shield's and item's `.qct` and model:

| What | Source |
| --- | --- |
| The moves' clips and their lengths | `first_basic`'s clips: frames over fps |
| Attack playback rate | the weapon's `entity_data` `speed_attack`: 1.10 for the short sword, 0.80 for the orc's cleaver |
| When a swing's blade is traced | the clip's events: 2186 opens the window, 2190 closes it. The T and B power attacks have neither (they start with 2170): 2107 to 2110, which bracket 2186–2190 in the clips that have both. The one recorded `B_out` blow lands inside (0.17 in 0.11–0.21); none checks T's (0.13–0.73) |
| Fast or power | whether the wind-up (`layer_bigslash_*_in`) has played: a release from the held idle is a power attack (see "The player's moves") |
| Stamina | `player_basic.qct`'s `skills` block, `stamina_kick_npc`/`stamina_kick_all` in the settings, and server.dll's ConVar defaults (see "The player's moves") |
| The kick's contact | no events in its clip. `combat.json` sets cycles 0.36–0.42; recorded kicks land at 0.38–0.41 |
| How far the blade reaches | the sword's `rangeattack`, 100 units (2.54 m). The trace runs from the eye toward the blade's tip, as the donor's do |
| What a swing struck | the shared `HitSweep` through the orc's hit boxes and its items' hulls: the same measure as the mirror's Shadow contacts |
| The orc's verdict, damage, the player's parry | `CombatModel` (docs/combat.md): the same rules scored against recordings |
| The kick's damage | the settings' `kick_damage` (1) |
| Which part of an orc's attack lands | its clip's 2102 and 2139 events |
| Whether an orc's blow reaches the player | the donor's reach test (`CombatModel.Reach`, docs/combat.md "Reach"): a 1.5-unit sweep for the part's `range` (60 to 120), meeting the player's ±16 box |

**The lab's own numbers**, in `content/combat.json`, which reloads live and is tunable:
- each move's phase timing (`phases`): the fast attack's, power attack's and kick's windup (before the hit window),
  active part (the window, the blade traced) and recovery (after it), as multiples of the donor clip's own time.
  All are 1 by default, so the moves play as Arkane timed them. The Sword tunables page has a slider for each.
  A window tuned shorter than a step is still traced: a step whose cycles overlap it traces the blade at a cycle
  inside it, once per swing;
- movement while attacking;
- the kick's window, range and push;
- the player's health;
- the orc's spawn, engage distance, walk speed, attack cooldown, parry chance and health.

## Builds

The player's build is `content/build.json`, live and on the developer shell's tunables page (the Build group, one
slider per skill, saved back to the file):
- **Skills:** every skill of `scripts/mm_skills_infos.txt`, at a level from 0 to its `max_level`. A skill at level
  *n* gives the modules of its levels 0 to *n*. Only `combat` has level-0 modules (`combat_big_slash`,
  `combat_huge_slash`, `combat_parry`, `combat_fatality`), so every build has those, as a new donor game does
  (Wire v15 shows exactly them).
- **Requirements:** a skill above 0 needs one of the skills its `requires` names ("a|b" is either) above 0, and
  those need theirs: `adrenaline` needs `critical_hit` or `extra_damage`, which need `combat` or `projectile`.
- **Items:** the weapon and the off-hand by model path, each an imported item (its `.qct`). The lab plays only the
  short sword and no shield yet, and refuses others, naming #10148 and #10149.
- **Spells:** known spells by Arkane's id (`air_1`...), each in `spells.txt`; nothing reads them yet.

An invalid file is refused with the field and the reason, and the last build stays. Mechanics ask the build
(`CharacterBuild.Has`) for a module:
- **Stamina:** `double_sprint` (the `stamina` skill's level 1) raises the maximum to `player_stamina_double_sprint_max`
  (12), as the v15 recording shows after every skill was learned.
- **Damage:** the `extra_damage` level sets the player's strength (docs/combat.md, "The player's strength").
- **The chained slash:** `combat_chained_slash` chains C, D and E after B.
- **The parry:** needs `combat_parry`. Every build has it, so this changes nothing yet; the gate is the module's
  name, not checked against the donor.

**Replays** of Wire v15 recordings run the donor player's build: its modules and items at the first frame with stats,
and from v16 its skill levels.
The report says what it was, and how often it changed later (the replay doesn't follow changes). After the replay
the lab plays `build.json` again.

## The player's moves

`PlayerSword` plays the short sword's moves as the hand-played arena fights show them
(`l01_a-v14-arena-hand*.rbrec`, read from the v14 commands against the view-model layers).

### Fast and power attacks

- **The wind-up:** pressing attack plays `layer_bigslash_*_in`, then its held idle. The movement as the wind-up
  begins picks the power attack: back gives `B`, otherwise a strafe `L` or `R`, otherwise forward `T`, standing still
  the neutral one. The fights show the order: a strafe with forward gave `L` and `R`, a strafe with back `B`.
- **Fast or power:** a release before the wind-up has played is a fast attack (`layer_fastattack_*_A`), one from the
  held idle that power attack (`_out`). It isn't a fixed hold time: the directional wind-ups are longer (0.61 s
  against the neutral 0.45 at the sword's speed), and releases 0.57 and 0.63 s after their press, with `T_in` and
  `B_in` still playing, were fast attacks. The wind-up starts when the arm is free: a press during a kick winds up
  when the kick ends.
- **When A begins:** 2 to 3 ticks after the release, 2.6 on average over 12 releases (`PlayerSword.FastDelay`,
  0.04 s; #10142). The arm stays in the wind-up meanwhile, and a press then already chains B. Without the delay the
  lab's chains ran a few ticks ahead, so presses the donor dropped in B landed after the lab's B and began an A
  (the replays' mean lag went from −36 ms to −10 ms).
- **The direction:** the command rule picks all but four of the hand fights' wind-ups (each with the −150 back-walk:
  one gave neutral, one R with a strafe, two B), and the one hold through B whose idle went R while moving back
  (126804). The body's velocity explains that one but misses more of the others (40 against the command rule's 52
  of 74 picks, idle restarts included), so the command rule stays.
- **The chain:** a press while A plays chains `layer_fastattack_*_B`, which begins as A ends: 18 to 20 ticks after
  A in all 16 recorded chains. The press also winds up under A and B: still held when B ends, the charge goes on
  from there, its direction picked then. B ends ready.
- **The chained slash (#10143):** with `combat_chained_slash` (the `combat` skill's level 1) the chain goes on through
  C, D and E (`PlayerSword.ChainLength` 5; 2 without the module). In `l01_a-v16-chained-slash` (the `chained-slash`
  scenario, docs/recording.md) each burst of quick clicks plays A, B, C, D, E, then `_end`: every next clip begins as
  the last ends (20, 24, 26 and 24 ticks), a press queues one clip, more presses in it do nothing, and nothing chains
  after E. An E nothing chains onto recovers like A. C and D unchained aren't recorded: the lab ends them ready, as B.
- **A's recovery:** an A nothing chains onto plays `layer_fastattack_*_end`. A press in its second half starts A again
  at once; one at cycle 0.32 did nothing, one at 0.60 played A two ticks later (`PlayerSword.FastEndOpen`, 0.5).
- **Attack in the air (the jump attack):** attack pressed in the air holds the neutral power attack's idle at once,
  with no wind-up, and the release is the power attack. The donor also plays `layer_combo_sword_fall_noshield_jump_in`
  over it, which the lab doesn't yet (#10144). Every press #10131 read as "power after a kick" was a jump: 126588 and
  128150 pressed mid-jump (`IN_JUMP`, the `jump_in` clip, rising), and the three taps late in a kick (cycle 0.84 to
  1.00) all came in a jump that began during the kick. A press in the air during a kick before about cycle 0.75 did
  nothing (`PlayerSword.KickAttackOpen`); on the ground a tap during a kick does nothing.
- **Not explained** (one case each): a tap released a tick after its wind-up began played nothing (127146; pressed as
  a kick ended); two presses during A (cycles 0.21 and 0.71) didn't chain B (120884, 120893).
- **A parried blow doesn't stop the swing.** The donor plays `layer_bounce_*` on none of the 23 player blows its
  orcs parried or blocked in the arena recordings, and chains and kicks on after them. The lab played a bounce
  there until #10131, which cut off the hand fights' chains.

### Kicks

A press of F asks for a kick, and stands while F is held. The kick starts on the first step the body may kick: no
kick playing (a broken parry also stops it), on the ground, and stamina of at least 3. Starting consumes the press,
so a new kick needs F to go down again. Kicks cut into charges and swings. In the hand fights:
- **A held press waits:** pressed during a kick and held, the next kick began the tick the first ended (126233).
  Pressed in the air and held to the landing, it began on landing (116736).
- **What refused the rest:**

  | Refusals | Why |
  | --- | --- |
  | 10 | a kick was playing, and F was let go before it ended |
  | 4 | stamina below 3, after sprinting |
  | 3 | in the air, F let go before landing (one of them also below 3) |
  | 1 | no weapon in hand (below, "Replay": the view model shows none) |

### Stamina

`PlayerStamina`, from the donor's numbers:

| What | Value | Source |
| --- | --- | --- |
| Maximum | 9 | `player_basic.qct`, `skills`: `player_stamina_base_max` |
| Regeneration | 0.06 every 0.12 s | `player_stamina_add_value`, `player_stamina_add_interval` |
| Sprinting | −2 a second while moving | ConVar `mm_player_stamina_megasprint_cost_per_second` |
| A kick may start with | 3 | ConVar `mm_player_stamina_needed_for_kick` |
| A kick costs | 3 when it lands on an NPC, otherwise 1 | `stamina_kick_npc`, `stamina_kick_all` in the settings |

The ConVars' defaults are in server.dll; no content file holds them.

Simulated from the hand fights' commands with these numbers, every kick the donor began had at least 3.06, and each
refusal on the ground with the arm free had less than 3. Charging the 1 as well as the 3, or always 3, would have
refused a kick the donor made (116732, at 1.06 or 0.84).

Sprint is the command's action byte +0x95 (Wire `PlayerActions.Sprint`): running ticks with it set have a median
speed of 229 units/s, against 195 for all running ticks. The lab's body sprints with the sprint key or the replayed
action, and drains stamina while moving. What empty stamina does to the sprint isn't measured, so it doesn't stop it.

## Calibration

`lab/tests/DarkMessiah.Lab.Smoke` measures the moves against every local recording of Wire v9 or later. Those recordings carry the client's view-model layers.

| Move | Recorded player blows inside the lab's window (±0.06) | Clip rate, lab against donor |
| --- | --- | --- |
| Fast attack A, window 0.33–0.78 | 87 of 87 (cycles 0.28–0.76) | 3.67 against 3.50 cycles/s |
| Fast attack B, window 0.50–0.83 | 9 of 9 (cycles 0.56–0.72) | 2.75 against 2.70 cycles/s |
| Fast attack C, window 0.50–0.83 | 6 of 6 (cycles 0.57–0.74) | 2.75 against 2.70 cycles/s |
| Fast attack D, window 0.42–0.75 | 5 of 5 (cycles 0.52–0.70) | 2.75 against 2.72 cycles/s |
| Fast attack E, window 0.55–0.91 | 6 of 6 (cycles 0.53–0.80) | 3.00 against 2.93 cycles/s |
| Power attack, window 0.13–0.22 | 31 of 31 (cycles 0.13–0.18) | 1.03 against 1.01 cycles/s |
| Power attack B, window 0.11–0.21 | 1 of 1 (cycle 0.17) | 0.87 against 0.86 cycles/s |
| Power attack R, window 0.19–0.42 | none recorded | 1.06 against 1.13 cycles/s |
| Power attacks T (0.13–0.73) and L (0.04–0.25) | none recorded | not seen playing long enough to measure |
| Kick, window 0.36–0.42 | 70 of 70 (cycles 0.38–0.41) | 1.20 against 1.18 cycles/s |

**A blow belongs to the newest move.** A kick can start while a power
attack's layer is still fading out under it. Credited to the first layer
listed, seven kicks counted as power blows late in the power clip
(cycles 0.76–1.00), which looked like a second contact. Calibration now takes
the layer with the highest order. Those kicks start when the power attack is
at about cycle 0.45, after its hit window has closed. The hand-played fights
show kicks cutting into charges and swings anywhere, so the lab's kick may cut
off any move but another kick or a broken parry.

**Damage against recorded blows.** `DamageCalibration` takes every recorded player blow on an orc (fast and power
attacks with the short sword, and kicks) and asks the lab's own damage path for it (`LabCombat.PlayerAsks`, with the
recorded move and hit group). The lab asks with the donor's own clip, B and the directional power attacks included,
whose `HitPow` comes from `first_basic.qct` like the others', and with the recording's player strength (from v16, its
`extra_damage` level; docs/combat.md, "The player's strength"). 179 of 182 agree:
- **Fast attacks:** A 69 of 71, B 8 of 8, C 3 of 3, D 2 of 2, E 2 of 2. The chained-slash recording's player has
  `extra_damage` 3, strength 6: its blows ask four times a new game's.
- **Power attacks:** 31 of 32, and the one `B_out` blow.
- **Kicks:** 63 of 63.
- **The differences:**
  - two blows on an orc not yet alert (×3) at the start of the chained-slash recording;
  - a critical (×2), which the lab doesn't roll;
  - a blow in a recording before Wire v11 asking twice the lab's, most likely a critical. Neither the critical byte
    nor the orc's alert level is on the Wire before v11.
- **Kick push calls:** a kick that pushes also makes a zero-damage, untraced call for the push, which isn't a blow and
  isn't counted.

The smoke also checks, on the real product headless:
- **The data:** the sword's range and both weapons' attack speeds; the windows come from the events.
- **The orc's side:** it walks in and its blows reach the player.
- **The player's blows:**
  - a fast attack strikes the orc once;
  - a power attack on the body asks `WDmg × HitPow × hit-group modifier`, computed independently from the `.qct` files;
  - a parrying orc turns the blade, and the swing plays on to its recovery;
  - a kick asks `kick_damage` and pushes the orc.
- **The player's guard:** it meets the orc's blows, which it parries, or breaks against.
- **Input between steps:** a tap whose press and release both arrive on updates that admit no simulation step, or in
  one batch, is still one fast attack. The product keeps an attack or block press until a step has seen it. A hold
  is still a power attack.
- **The moves on authored clips:** the chain, A's recovery, a hold through B, each direction, a long wind-up released
  early, kicks waiting, dropped and cutting a charge, attacks in the air (the jump attack), the delay before A, and the stamina rules.
- **Phase timing:** windup ×2 opens the fast attack's window twice as late, recovery ×3 lengthens its end, and the
  retuned clips reach the fight through `LabCombat.Retune`.

## Replay

Recordings of Wire v12 and later carry the player's buttons, and from v14 the
user commands themselves: movement, buttons and the kick request (docs/wire.md,
"The player's input"). `lab.replay NAME` replays one against the lab and
reports where the outcomes agree. NAME is a path, or a file in
`local/recordings`. The smoke replays every such recording it finds.

**Each recorded tick is one lab step of that tick's game time:**
- **The sword:** the held attack and attack2 buttons drive it (`PlayerSword`),
  from the recorded eye and view angles. From v14 the command's movement picks a power attack's direction, and its
  sprint action drains stamina. From v15 it plays the recording's build (above, "Builds").
- **Kicks:** from v14, the command's kick action is F: going down asks for a kick, held keeps asking. The lab's
  sword can refuse it, as the donor can, by its own rules (busy kicking, in the air by the replayed body, short of
  stamina), so the report counts requests and which side began a kick on each. Before v14 the request isn't on
  the Wire, and a kick is asked for when the donor's kick clip starts on the view model.
- **The body (v14):** the recorded movement, view, jump (pressed and held), duck (held) and use drive the lab's own player body
  (`IReplayBody`, `LabProduct`), through the same step it takes in play, in the lab's arena. The donor's level isn't
  the lab's, so its position isn't compared, only how it moves: mean speed over the ticks with movement input, and
  jumps. The fight still uses the donor's recorded eye and feet, so its encounter stays the donor's.
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

The moves each side began are paired the same way, by family (fast attack, power attack, kick) and by clip (A or B,
each power attack's direction): the lab has to play the donor's own variant for a pair by clip. A clip already
playing when the recording starts isn't counted as begun.

**`l01_a-v12-arena-bot.rbrec` (the parry bot, 120 s):**

| What | Donor | Lab | Agree |
| --- | --- | --- | --- |
| Fast attacks, power attacks, kicks begun | 7, 9, 8 | 8, 9, 8 | 24 of 24 begin together, the lab 4 ms ahead on average |
| The player's blows on the followed orc | 16 | 14 | 4 agree, 6 differ |
| The orc's blows on the player | 40 | 54 | 37 agree |

**`l01_a-v14-arena-bot.rbrec`** (90 s: the parry bot, then scripted walking, a jump, kick spam and kicks mid-swing):
- **Kicks:** 15 requests. The donor began a kick on 13, the lab on 13, both on 12: the lab kicks or refuses as the
  donor did on 13.
- **The moves:** 30 of 31 begin together, each as its own clip.
- **Movement:** the lab's body moves at 3.88 m/s on average where the donor moved at 2.36. The lab's controller is
  rusty-fptester's, not yet calibrated to Dark Messiah's speeds.
- **Jumps:** the one jump came through.

**Hand-played (`l01_a-v14-arena-hand.rbrec`, 150 s, and `-hand2`, 51 s)**, which used the moves the bot never does
(B chains, directional power attacks, kicks held, mid-swing and in the air, sprinting):

| What | First fight | Second fight |
| --- | --- | --- |
| Moves begun together, by family | 19 of 20 | 45 of 45 |
| The same, by clip | 19 of 20 | 43 of 45 |
| Fast attacks begun, donor and lab | 13, 13 | 27, 28 |
| Kick requests the lab kicks or refuses as the donor did | 19 of 19 | 15 of 15 |

Before #10131 the lab played every variant as its neutral move and bounced its sword off parries: 14 of 20 and 21
of 46 began together. After #10131 it began 21 and 35 fast attacks to the donor's 13 and 27. #10142 found why:
- **No weapon in hand:** from 115491 to about 115890 the first fight's view model shows no layer at all, until a
  quick draw (`layer_sword_noshield_quickdraw`): the sword was away, and the donor ignored 30 attack presses and two
  kick requests. The replay now feeds the lab's sword no attack, block or kick while the donor's view model shows no
  layer but the quick draw (`ReplayTick.Armed`). The lab doesn't put its sword away (#10148, quick draw).
- **The jump attack:** above, "Attack in the air".
- **When A begins:** above.

What's left: the direction after B (126828: R, the lab B), the unexplained tap and chain (above), and a finishing
move (`layer_behead_*_out`, 129270) the lab plays as the neutral power attack.
- **Movement:** commands run at 1000, about 1400 on a diagonal, and 150 for the slow walk while charging or guarding.
  The replay scales them against a run. The lab's body averages 4.20 against the donor's 4.69 m/s, and 2.78 against 1.68.
- **Jumps:** all 12 came through.

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
  content/                 lab.json (arena), player.json (body and camera), combat.json (the fight's own numbers), build.json (skills and items)
  src/DarkMessiah.Lab/
    LabProduct.cs          composition: input, body, fight, cubes, world time, publish
    Donor/                 DonorContent (packs and opens dm-import's output; models, .qct numbers, CombatData), GameTables (spells, skills)
    Sword/                 PlayerSword (the moves), PlayerStamina, FirstPerson (arms, sword, blade trace), Rig, CombatTuning
    Npc/Orc.cs             the orc: walk in, attack, parry, react, fall and come back
    Combat/LabCombat.cs    the fight: traces, verdicts, damage, the orc's blows, the overlay
    Calibration/           SwordCalibration against recordings
    Replay/                InputTimeline (a v12 recording's input and orcs), CombatReplay (the run and its report)
    Player/ Scene/ Time/ Developer/ Ui/ Interaction/   from rusty-fptester (below); Player/CharacterBuild is the lab's
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
- **Tunables:** Body and View (`player.json`), Sword (with each move's windup, active and recovery) and Orc (`combat.json`), and World time.
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
  - combos (`layer_combo_*`), the finishing moves (`layer_behead_*`), the jump attack's fall clip and damage, and the
    quick draw;
  - the shield-holding moves.
- **Stamina:** what empty stamina does to the sprint, and the costs of parrying, blocking and dodging
  (`Stamina_Parry`, `Stamina_Block`, `Stamina_Dodge` in the settings) aren't measured or modelled.
