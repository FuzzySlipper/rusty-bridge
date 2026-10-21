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
| Attack playback rate | the weapon's `entity_data` `speed_attack`: 1.10 for the short sword, the daggers and the combat staff, 1 for the long sword and the club (none given), 0.80 for the orc's cleaver |
| When a swing's blade is traced | the clip's events: 2186 opens the window, 2190 closes it. The T and B power attacks have neither (they start with 2170): 2107 to 2110, which bracket 2186–2190 in the clips that have both. The one recorded `B_out` blow lands inside (0.17 in 0.11–0.21); none checks T's (0.13–0.73). With neither 2190 nor 2110 (the staff's A and C, the daggers' and staff's huge slashes) the first 2102 after the open closes it; see "Weapons" |
| Fast or power | whether the wind-up (`layer_bigslash_*_in`) has played: a release from the held idle is a power attack (see "The player's moves") |
| Stamina | `player_basic.qct`'s `skills` block, `stamina_kick_npc`/`stamina_kick_all` in the settings, and server.dll's ConVar defaults (see "The player's moves") |
| The kick's contact | no events in its clip. `combat.json` sets cycles 0.36–0.42; recorded kicks land at 0.38–0.41 |
| How far the blade reaches, and where | the move's `.qct` `range` (120 units when it gives none). Rays from 9 units above the eye along the view, turned by the move's arc (`combat.json` `swingArcs`, fitted to the donor's recorded sweeps), a sphere of 1.5 units; see "The blade's trace" |
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
- **Items:** the weapon and the off-hand by model path, each an imported item (its `.qct`). The lab plays the swords,
  the club, the daggers and the staffs (`models/items/weapons/sword_*`, `club`, `daggers*`, `staff_*`; see "Weapons"),
  and no bow and no shield yet: it refuses those, a shield naming #10149. A new weapon is drawn: the one in hand is put
  away first.
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
and from v16 its skill levels. Each later change (a skill learned, a shield taken) is played from the tick it was
streamed (`InputTimeline.Builds`): in `l01_a-v15-stats` the lab's stamina maximum goes to 12 as `double_sprint` is
learned. Weapon switches come as draws (see "Weapons"). What the lab doesn't play (a shield, #10149; a bow or a spell)
isn't presented as played: the report names it, and no move or blow while it is held is compared
(`InputTimeline.Unplayable`). After the replay the lab plays `build.json` again.

## The player's moves

`PlayerSword` plays the weapon's moves as the hand-played arena fights show them for the short sword
(`l01_a-v14-arena-hand*.rbrec`, read from the v14 commands against the view-model layers), with each weapon class's
clips and differences (see "Weapons").

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
- **A's recovery:** an A nothing chains onto plays `layer_fastattack_*_end`. A press from cycle 0.58 of it starts A
  again at once (`PlayerSword.FastEndOpen`): the `sword-timing2` sweep's presses at 0.12, 0.24, 0.30, 0.41, 0.51 and
  0.56 did nothing, at 0.72 and 0.80 played A two ticks later, as one hand-played at 0.60 did (#10142).
- **Attack in the air:** below, "The jump attack". Every press #10131 read as "power after a kick" was a jump: 126588
  and 128150 pressed mid-jump (`IN_JUMP`, the `jump_in` clip, rising), and the three taps late in a kick (cycle 0.84
  to 1.00) all came in a jump that began during the kick. A press in the air during a kick is a press in a jump like
  any other (`sword-timing2`, R10142-1): held 0.2 s at kick cycles 0.59 to 1.10 it made the neutral power attack, 8 of
  8, cutting the kick; taps of 5 ticks were dropped, 4 of 4 (`AirHoldMin`). The one press that did nothing at about
  0.65, which `KickAttackOpen` (0.75) rested on, was such a tap; the constant is gone. On the ground a tap during a
  kick does nothing, and a kick pressed in the air doesn't start.
- **No parry in the air:** block pressed mid-jump (128149, held 4 ticks) played no parry, and the attack pressed a tick
  later made the jump's power attack (#10144).
- **The timing sweep** (`sword-timing`, R10142-1) bears out the rules on varied timing:
  - **After a kick, on the ground:** presses 0.15 to 0.6 s after a kick ended wound up and played A, 7 of 7: none
    released the power attack with no wind-up (that is the air's, above).
  - **The direction after B:** held through B with a strafe, back or forward starting 0.15 or 0.35 s into it, the
    release took the movement held as B ended, 8 of 8 (right R, left L, back B, forward T).
  - **Short taps:** taps of 1 to 4 ticks played A, 20 of 20.
- **Not explained** (one case each, against the sweep's): a tap released a tick after its wind-up began played nothing
  (127146; pressed as a kick ended); two presses during A (cycles 0.21 and 0.71) didn't chain B (120884, 120893); a
  hold through B released R where the movement at B's end picks B (126828).
- **A parried blow doesn't stop the swing.** The donor plays `layer_bounce_*` on none of the 23 player blows its
  orcs parried or blocked in the arena recordings, and chains and kicks on after them. The lab played a bounce
  there until #10131, which cut off the hand fights' chains.

### Weapons

`first_basic` has a move set per weapon class, and the build's weapon picks it (`LabWeapon`, `SwordClips.From`;
#10148). The class follows the weapon's folder, as the game's entity classes do for every weapon recorded:
`sword_*` and `club` play `sword_noshield`'s clips, `daggers*` the daggers', `staff_*` the staff's. Each weapon's own
numbers come from its `.qct`'s `entity_data`: `rangeattack`, `speed_attack`, `WDmg`, `WCritHit` and `simple_weapon`.

| Weapon | Class | `WDmg` | Reach | Speed | Fast attacks |
| --- | --- | --- | --- | --- | --- |
| Short sword, long sword | sword | 2, 3 | 100 units | 1.10, 1 | A to E with `combat_chained_slash` |
| Club | sword, `simple_weapon` | 3 | 100 | 1 | A and B only |
| Kryss daggers | daggers | 2 | 80 | 1.10 | A to E, then A again |
| Combat staff | staff | 2 | 110 | 1.10 | A to D, then A again |

The scenario fights (`rbwire scenario weapon-*`, recordings `l01_a-v17-weapon-*.rbrec`) show where the classes differ:
- **Equipping in a scenario:** `give` then `use` draws a weapon only with its skill (`mm_player_skills_grosbill 1`
  gives every one; the staff, the daggers and the heavier swords need theirs).
- **The chain:** the daggers chain A to E and the staff A to D, and a press during the last chains A again (the
  daggers' E 6 of 6, the staff's D 8 of 8). The club chains A and B only, every chained-slash module learned (B to C 0
  of 8 bursts; the long sword 8 of 8): the lab caps a simple weapon's chain at two.
- **The recoveries:** the sword recovers with its one `fastattack_*_end` after A or E (as before). The daggers and the
  staff have one per letter (`end_A` to `end_D`, `A_end` to `C_end`): wherever their chain stops, that letter's plays
  (A 28 and 23 times, C 1 and 9, D 1), and the next press from its second half starts A.
- **Power attacks:** five each, as the sword's. The staff's wind-up is 1.54 s (1.40 s at its speed): the first staff
  run's 1.2 s holds were all released before it, and all made fast attacks.
- **In the hand:** each weapon hangs on the arms' attachment the donor parents its entity to on the view model
  (Wire `m_iParentAttachment`): the swords and the club `RightHand` (4), the kryss daggers `LeftHand` (8), the combat
  staff `LeftHand_Staff` (13). A kryss model is one blade and the Wire has one daggers entity: the client draws the
  other, which the lab puts on the arms' `RightHand_Daggers` (`FirstPerson.HandOf`).
- **Hit windows:** the daggers' all have 2186 and 2190 but the huge slash; the staff's A and C close at 2102 (see
  "Where the numbers come from").
- **Switching** (`weapon-switch`): `use` on another weapon puts the one in hand away (`*_undraw`, at the clip's own
  rate: the sword's 0.27 s, the daggers' 0.17 s, the staff's 0.33 s), then draws the new one as soon as the old is
  away (`*_draw`: the sword's 1.83 s, the daggers' 0.8 s, the staff's 1.5 s). The club comes to hand without a draw.
  Taps during either are dropped; attack held through the draw winds up the power attack as it ends.
  `PlayerSword.Equip` plays this, and the weapon's model changes as its draw begins.
- **The quick draw** (`*_quickdraw`) isn't a switch: it plays when the weapon comes back after the hands were empty,
  6.6 s after each of the orc's `attack_grab`s in the long-sword run (4 of 4). The lab's orc doesn't grab yet, so
  nothing calls `PlayerSword.QuickDraw`.
- **Held to the end of a fast attack, the daggers go to the power attack's hold** through their transition
  (`layer_transition_daggers_A`, 0.33 s at the clip's rate), not their recovery: in the goblin fight (`daggers-goblin`) an A
  started from its recovery and held, 6 of 6. The lab does this for every letter of the daggers' chain; only A's is
  recorded. The sword winds up again from the press under it, as before.
- **The backstab** (`backstab`, `backstab-1`; R10148-2): with the daggers, an orc's back turned to the player readies it
  by itself, attack held or not: `backstab_in` (0.53 s), then the held `backstab_idle`. A press while it's held strikes
  (`backstab_out`), 4 of 4 (17 to 45 ticks into the idle); losing the back lets it go (`backstab_cancel`). It readies
  within the settings' `BackStabDist_IN` (100 units; recorded at 35 to 80), stays to `BackStabDist_OUT` (200), with
  the orc's facing within 45° of the player's view (server.dll's `mm_player_backstab_cos`, 0.707: one try at 44°
  didn't ready, three readied ones let go past 51°), on a `backstable` victim (the orc's .qct) that isn't on guard:
  over the daggers recordings the donor readied on 463 of 1147 ticks in that reach with the orc idling unaware, 76 of
  80 turning, 58 of 170 attacking, and on none of 2262 with it in `combatidle`, nor (the lab) getting up (24 of 472).
  The strike kills: the orc plays `die_backstabbed` as it begins, drops to 1 health at the out's 2102 (cycle 0.215;
  the first contact, asking 0) and dies to the blow at cycle 0.41 (60 ticks in, 4 of 4; the .qct's second event).
  Replayed, the lab begins the donor's 4 backstabs with it (lab 6); in the daggers fights it readies once where the
  donor didn't. Its trigger beyond the reach and facing (awareness, the weapon's state) isn't read from server.dll.
- **Not seen:** no staff run showed the breakneck (17 tries, from behind and in front at 20 to 30 units, tapped and
  held, on unaware orcs), so the lab plays none.

### The jump attack

The game's Leaping Attack (Jump+Power Strike), the `combat` skill's level 3 (`combat_vertical_attack`; its description
is `skilldesc_combat_3` in `resource/mm_itemnames_english.txt`). Recorded in `l01_a-v16-jump-attack*` (#10144; the
`jump-attack` and `jump-attack-basic` scenarios, docs/recording.md): 34 jumps with a click 0.1 or 0.3 s in, held 0.06,
0.15, 0.4 or 1 s, with every skill module and with a new game's.
- **The wait:** attack pressed in a jump plays nothing at first. 8 or 9 ticks later (8.4 on average, 31 presses;
  `PlayerSword.AirDelay`, 0.125 s) the move begins. A press let go after 4 or 5 ticks is dropped (13 taps, with or
  without the skill); one let go after 8 still made it (128150; `AirHoldMin`, 0.1 s between them).
- **Just after landing:** a press counts as one in the jump until about 0.35 s after touching down
  (`PlayerSword.Landing`): taps up to 16 ticks after were dropped, from 30 ticks they were ordinary presses (126664,
  128223).
- **Without the skill:** the move is the neutral power attack's hold (the donor shows `combo_sword_fall_*_jump_in`
  for a tick or two over it), and the release is the power attack, at once if attack was let go already.
- **With it, in the air:** the jump attack winds up (`layer_combo_sword_fall_noshield_jump_in`, 0.3 s), holds
  (`_idle`, played through once, 4 ticks, before a release counts) and strikes on release (`_out`, 1.03 s, still in
  the air). Its hit window is 2107 to 2190 (0.03–0.27: it has no 2186), and it asks HitPow 2: 16 on the body, 48 on the
  head, as recorded.
  - Released early in the wind-up (cycles 0.1 to 0.2, six presses) it is the neutral power attack instead; released
    at 0.9 (one) the wind-up plays out and strikes (`LeapReleaseOpen`, 0.5 between them).
  - Landing while it winds up or holds cuts it short (`_cancel`, 0.3 s), then the neutral power attack's hold, or
    its release if attack was let go.
- **The body's jump:** the donor's jumps from flat ground rise 41 to 42 units (1.06 m) and land 0.84 s later (ten of
  them; a few rose 34 units in 0.74 s). The lab's body jumped as high in 0.65 s (6.5 m/s against gravity 20), so it
  landed before the donor's strikes; `player.json` now has 5.05 m/s and 12 m/s² (1.06 m, 0.84 s). The rest of the
  body's movement is #10159's.

### The rush attack

The game's Charge (Sprint+Power Strike), the `combat` skill's level 1 (`combat_rush_attack`; `skilldesc_combat_1`).
Recorded in `l01_a-v16-rush-attack` (the `rush-attack` scenario): a power attack charged and then sprinted with, or
sprinted and then charged.
- **The rush:** with the module, a charge released from its hold while the body sprints plays
  `layer_combo_sword_rush_noshield` (2 s) instead of the power attack, 7 of 7. It strikes twice: 2186 to 2190 (cycles
  0.09–0.17) and 2107 to 2190 (0.47–0.57; its blows landed at 0.466 and 0.48, before that part's 2186). The parts ask
  HitPow 0.5 and 1.5 (12 on the head and 12 on the body, as recorded). Each window hits once (`LabCombat` settles a
  swing by its window).
- **A short press while sprinting** winds up (`bigslash_L_in`) and, let go before the hold, plays nothing: 6 of 6, no
  fast attack.
- **Sprinting needs 3 to start:** asked with 2.52 to 2.82 stamina the body only ran, and the sprint began the tick
  stamina reached 3.00; once going it went on below (`StaminaRules.NeededForSprint`). A press then is an ordinary
  one (12895: a fast attack).

### Adrenaline, the Fatality and the Whirlwind

Recorded in `l01_a-v16-adrenaline*`, `-fatality*`, `-whirlwind` and `-disarm` (#10145; the scenarios of the same
names, docs/recording.md), with the bot fights' two huge slashes (`l01_a-v11-crit`, `-slowmo`) and hand2's behead.
- **The gauge in replays:** replays hold the lab's gauge to the recording's, since the lab's replayed blows still land
  where the donor's do only part of the time (#10177: "The blade's trace" below). The lab's own gauge, run from the
  donor's start on the lab's blows, stays within 1 of the donor's on 19% to 100% of a replay's ticks (each replay
  reports it); per landed blow it agrees 159 of 162 times (below).
- **The gauge** (`PlayerAdrenaline`): each blow that takes health gives its `.qct` event's `adrenalineadd` (a fast
  attack 0.5, a power attack 3, the jump and rush attacks 1, a disarm 8), up to 20 (the Wire's `MaxAdrenaline`): 159
  of 162 landed blows fill it as the lab's does (`AdrenalineCalibration`; the three are a power attack's layer
  fading under a fast attack, and a kick that gave 0.5). A swing that lands nothing gives nothing, and the gauge
  didn't fall in 12 s standing.
- **The Fatality** (the game's: "Once your Adrenaline bar is full, your next Power Strike becomes a Fatality";
  `combat_huge_slash` and `combat_fatality`, the combat skill's level 0): with the gauge full, a charge released from
  its hold plays `layer_combo_sword_huge_noshield` (2.1 s) instead of the power attack (`PlayerSword.Fatal`): none of
  the 25 recorded releases at full was the power attack (22 huge slashes, 2 beheads, 1 Whirlwind).
  A quick release is still a fast attack. It strikes twice (2186–2190 at 0.14–0.22 and 0.40–0.48, HitPow 30: 240 on
  the body, 720 on the head) and a head blow cuts the head off (`cutheadflag`): it killed orcs of 100000 health
  (`AttackEvent.CutsHead`). A landed huge slash empties the gauge; one that strikes nothing leaves it full.
- **The behead:** twice the Fatality was `layer_behead_sword_noshield_out` instead (hand2 129273; the first
  `adrenaline` run), each time a killing blow, but a huge slash on an orc set to 40 health didn't behead. Not
  explained; the lab plays the huge slash (the replays count both as the Fatality).
- **The Whirlwind** (the game's: Crouch+Adrenaline+Power Strike; `combat_trample`, the combat skill's level 3):
  crouched with the gauge full, the release played `layer_combo_sword_trample_noshield` (1.9 s) and its blow landed
  the tick it began: 240 on the orc 55 units in front (HitPow 30). Its clip has no hit events; the lab strikes the orc
  within `LabCombat.WhirlwindReach` (2 m, authored) as it begins. It left the gauge full (one case). Two other crouched
  releases at full played the huge slash: not explained, so the lab's Whirlwind replay begins 2 where the donor began 1.
  The slow motion its `.qct` asks for (×0.5 for 2.5 s) is #10147's.
- **Disarm** (Power Strike on a stunned enemy's arm, `combat_disarm`, level 2): not reproduced. Pairs of power strikes
  (the first stunning, `chancetostun` 0.8) gave a fast attack second: the second press came while the first played.
  It needs the orc's stun (#10152) and a recorded disarm; the clip (`layer_combo_sword_disarm_noshield`, 1.6 s, HitPow
  0, `disarmflag`) has no hit window.
- **An orc getting up takes ×2:** four blows on an orc in `StandUp_*` asked twice the lab's, the only unexplained
  doubles (one of them the old `l01_a-layers` fast attack). The lab's orc is never down (#10152).

### Slow motion

Dark Messiah slows the world on its signature kills (#10147; docs/wire.md, "Slow motion"). Over every recording
(`SlowTimeCalibration`, 7 of 7):
- **A Fatality that kills** (the huge slash or the behead) asks ×0.1 for 2 s, 0.2 s late: the damage record's own
  slow-time fields (`SlowTime.FatalityKill`). Four did, each one; no other kill asked anything (a kick under a fading
  huge slash's layer is a kick's kill).
- **The Whirlwind** asks its `.qct` slow time as it begins: ×0.5 for 2.5 s (`AttackEvent.SlowTime`).
- **Map scripts** ask their own (`ActiveSlowMotion`, from outside server.dll); the arena has none.

The request runs the world at the factor for the duration of wall time, which is duration × factor of game time (0.2
s for a Fatality kill, 1.25 s for the Whirlwind), while look and controls keep host time. `LabCombat` keeps the request
on the fight's game time (`TimeScale`), and the product hands it to `WorldTime`, which runs the Engine's gameplay time
at that rate (or the chosen rate, if slower) until it ends. Replays step on the recording's own game time, so they
aren't slowed.

### The finishing moves

- **The behead** (`layer_behead_sword_noshield_out`): two recorded, both Fatalities at full adrenaline, both killing
  blows; its trigger against the huge slash isn't found (above, "Adrenaline, the Fatality and the Whirlwind"). The lab
  plays the huge slash for it, and replays count both as the Fatality.
- **Cutting the head off:** the lab kills the orc (a `cutheadflag` head blow) but leaves its body whole: head and limb
  removal is rusty-engine #10176.
- **Impales** (`layer_Impal_*`, `layer_Ground_Impal_*`, `impalflag`): never recorded. They need an orc knocked down or
  stunned (#10152).

### The blade's trace

What a swing strikes is what the donor's sweep would (#10177, #10179; docs/wire.md, "The melee sweep"):
- **The donor's rays (Wire v17):** each tick of a swing, server.dll sweeps a box of 1.5 units from 9 units above the
  eye, 120 units long, along several rays 7° to 11° apart that fan across the arc the swing covered since the last
  tick, stopping at the first contact. The arc is a line in the clip's cycle, in a tilted plane: fast A from 37°
  right to 61° left over its window, B back from 39° left to 60° right, C and E like A, D like B, the power attack
  from 7° right to 66° left and 52° down. The player's own angles equal the client's view, so the arc isn't camera
  sway.
- **The lab's:** `combat.json` `swingArcs` holds each clip's side and up angle at its window's opening and close,
  from those fits (the directional power attacks, the jump attack and the rush keep #10177's averages; no v17
  recording has them). Each step `LabCombat` traces from `TraceRise` above the eye along rays at most 10°
  (`RayStepDegrees`) apart from where the last step left the arc to where it is now, a sphere of 1.5 units, the
  move's `.qct` range long; the first contact is the blow.
- **Checked against the donor's rays** (`SweepCalibration`): 76 of 89 recorded ticks lie within 10° of the lab's arc
  (fast attacks 37 of 38; the power attack 37 of 47, whose short, steep window the per-tick cycle samples coarsely;
  the huge slash 2 of 4).
- **The lab's first-person blade** was the trace's old guide: its tip sits low and to the side of the camera (42° to
  69° off the view, 22° to 69° down), which struck legs where the donor struck heads.
- **The replay's orc carries the recording's kit** (`OrcPose.Kit`, from Wire v10's owners): no recorded orc has had
  a shield, so a followed orc shows and traces only its cleaver. The lab's own orc keeps its shield.
- **And its body model** (`OrcPose.Model`, R10177-2): the followed orc is drawn and swept as the recorded entity's
  model, the lab's `npc_orc` when the import lacks it. Every recorded `npc_orc_sword` so far is `npc_orc.mdl`; the
  dismembered orc models (`npc_orc_noRightArm`...) appear only as one-clip ragdoll props, which pose their one clip.
- **A head cut needs a Fatality:** the power attack has `cutheadflag` too, but its head blows never killed; a
  Fatality's head blow turned an orc of 100000 health into a ragdoll.
- **What it gives:** the player's blows that agree with the donor's (same verdict and hit group): the parry bot 9 of
  16 (#10177: 8), ghoul2 4 of 6 (1), hand1 4 of 10 (6), hand2 11 of 31 (10), the fatality run 13 of 27 (10), the
  Whirlwind run 4 of 10 (1). With the aim matching the donor's rays, what still differs is the orc: the lab poses it
  from states streamed every few ticks, between which it plays on (#10098), and the swing's start a tick or two off;
  the mirror, posing victims as the blows found them, meets the donor's traces 53 of 53. Replays keep holding the
  adrenaline gauge to the recording's (its own runs within 1 on 11% to 100% of a replay's ticks).

### Criticals

A critical doubles a blow (docs/combat.md, "The decisions"). The lab rolls one as the donor does (#10146):
- **Which blows:** those whose `.qct` event has `critical_enable`: the power attacks' releases, the jump attack, the
  rush and the Fatality; never a fast attack (none of 91 recorded fast blows was critical).
- **The chance:** `2 × player_dexterity_{critical_hit level + 1}` percent plus the weapon's `WCritHit` (0 for the
  short sword): 2% for a new game, 6% at level 1, 12% at level 2 (`CombatData.PlayerCriticalChance`, `LabCombat.CriticalChance`).
  The recordings bear it out: 1 critical in 45 power blows with a new game's skills, 13 in 111 with `critical_hit` 2.
  The level-*L* reading of the table (0, 2 and 6%) fits neither.
- **The roll:** `LabCombat.RollCritical`, seeded (`CriticalSeed`), so fights, replays and the smoke repeat; 20000 rolls
  land at 1.96% and 12.21%.
- **Replays and calibration** take the donor's recorded critical instead (a lab blow within 12 ticks of a critical
  donor blow; `DamageCalibration` asks with the recorded byte), so criticals no longer differ.
- **Slow time:** `slowtime_critical_*` in the settings asks for none (duration 0, factor 1).

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
| A sprint may start with | 3 | measured (#10144, "The rush attack"); it goes on below |
| A kick costs | 3 when it lands on an NPC, otherwise 1 | `stamina_kick_npc`, `stamina_kick_all` in the settings |

The ConVars' defaults are in server.dll; no content file holds them.

Simulated from the hand fights' commands with these numbers, every kick the donor began had at least 3.06, and each
refusal on the ground with the arm free had less than 3. Charging the 1 as well as the 3, or always 3, would have
refused a kick the donor made (116732, at 1.06 or 0.84).

Sprint is the command's action byte +0x95 (Wire `PlayerActions.Sprint`): running ticks with it set have a median
speed of 229 units/s, against 195 for all running ticks. The lab's body sprints with the sprint key or the replayed
action, from 3 stamina, and drains it while moving; the sprint stops at 0 (not measured: the recordings stopped
sprinting first).

## Calibration

`lab/tests/DarkMessiah.Lab.Smoke` measures the moves against every local recording of Wire v9 or later. Those recordings carry the client's view-model layers.

| Move | Recorded player blows inside the lab's window (±0.06) | Clip rate, lab against donor |
| --- | --- | --- |
| Fast attack A, window 0.33–0.78 | 91 of 91 (cycles 0.28–0.76) | 3.67 against 3.50 cycles/s |
| Fast attack B, window 0.50–0.83 | 12 of 12 (cycles 0.53–0.72) | 2.75 against 2.71 cycles/s |
| Fast attack C, window 0.50–0.83 | 6 of 6 (cycles 0.57–0.74) | 2.75 against 2.70 cycles/s |
| Fast attack D, window 0.42–0.75 | 5 of 5 (cycles 0.52–0.70) | 2.75 against 2.72 cycles/s |
| Fast attack E, window 0.55–0.91 | 6 of 6 (cycles 0.53–0.80) | 3.00 against 2.93 cycles/s |
| Power attack, window 0.13–0.22 | 41 of 41 (cycles 0.11–0.18) | 1.03 against 1.01 cycles/s |
| Power attack B, window 0.11–0.21 | 1 of 1 (cycle 0.17) | 0.87 against 0.86 cycles/s |
| Power attack R, window 0.19–0.42 | none recorded | 1.06 against 1.13 cycles/s |
| Power attacks T (0.13–0.73) and L (0.04–0.25) | none recorded | not seen playing long enough to measure |
| Kick, window 0.36–0.42 | 70 of 70 (cycles 0.38–0.41) | 1.20 against 1.18 cycles/s |
| Jump attack's strike, window 0.03–0.27 | 2 of 2 (cycles 0.13–0.16) | 1.00 against 0.99 cycles/s |
| Rush attack, windows 0.09–0.17 and 0.47–0.57 | 4 of 4 (cycles 0.09–0.49) | 0.51 against 0.50 cycles/s |
| Huge slash (the Fatality), windows 0.14–0.22 and 0.40–0.48 | 13 of 13 (cycles 0.15–0.45) | 0.48 against 0.47 cycles/s |

**The other weapons (#10148)** calibrate on their scenario fights (`l01_a-v17-weapon-*`), each class's clips at each
weapon's speed. The long sword and the club (speed 1) put 32 of 32 A blows, 19 of 19 B, 7, 9 and 7 of C to E and 11 of 11
power blows inside the sword's windows, at 3.33 against 3.10 cycles/s for A (the short sword's are 3.67 against 3.50:
the donor plays every class's clips 2 to 7% slower than their frames say).

| Move | Recorded blows inside the window (±0.06) | Clip rate, lab against donor |
| --- | --- | --- |
| Daggers A to E | 30 of 31, 13 of 13, 6 of 6, 8 of 8, 5 of 5 | A 4.71 against 4.53; E 1.50 against 1.50 cycles/s |
| Daggers' power attack, windows 0.13–0.23 and 0.50–0.63 | 21 of 23 | 0.83 against 0.83 cycles/s |
| Daggers' huge slash, windows 0.15–0.22 and 0.49–1.00 | 2 of 2 (cycles 0.22 and 0.57) | 0.73 against 0.73 cycles/s |
| Staff A, window 0.57–0.79 (2186 to 2102) | 29 of 29 (cycles 0.60–0.81) | 2.36 against 2.31 cycles/s |
| Staff B, C (2186 to 2102) and D | 8 of 8, 9 of 9, 3 of 3 | 1.10, 1.10, 0.77 against 1.08, 1.09, 0.77 cycles/s |
| Staff's power attack, windows 0.15–0.20 and 0.48–0.56 | 5 of 5 | 0.40 against 0.40 cycles/s |

Their damage, asked with each blow's weapon (`WDmg` 3 for the long sword and the club, 2 for the daggers and the
staff), agrees on every sword and daggers blow (36 and 46). Of the staff's 51, 26 agree; the others are the orc's state:
stunned (the staff's blows stun: ×1.5, from server.dll, not the settings), getting up (×2) or not alert (×3).

**A blow belongs to the newest move.** A kick can start while a power
attack's layer is still fading out under it. Credited to the first layer
listed, seven kicks counted as power blows late in the power clip
(cycles 0.76–1.00), which looked like a second contact. Calibration now takes
the layer with the highest order. Those kicks start when the power attack is
at about cycle 0.45, after its hit window has closed. The hand-played fights
show kicks cutting into charges and swings anywhere, so the lab's kick may cut
off any move but another kick or a broken parry.

**Damage against recorded blows.** `DamageCalibration` takes every recorded player blow on an orc (fast, power, jump
and rush attacks with any weapon the lab plays, by the one in hand then, and kicks) and asks the lab's own damage path for it (`LabCombat.PlayerAsks`, with the
recorded move and hit group). The lab asks with the donor's own clip, B and the directional power attacks included,
whose `HitPow` comes from `first_basic.qct` like the others', and with the recording's player strength (from v16, its
`extra_damage` level; docs/combat.md, "The player's strength"), and the recorded critical. 278 of 289 agree:
- **Fast attacks:** A 69 of 71, B 8 of 8, C 3 of 3, D 2 of 2, E 2 of 2. The chained-slash recording's player has
  `extra_damage` 3, strength 6: its blows ask four times a new game's.
- **Power attacks:** 35 of 36, and the one `B_out` blow.
- **Kicks:** 63 of 63. The jump and rush attacks' blows carry the kick's damage bit too (they always push), and the
  jump attack's strike makes a zero-damage push call of its own: a kick is a blow whose frame plays a kick or no move.
- **The Fatality and the Whirlwind:** the huge slash 11 of 13, the Whirlwind 1 of 1.
- **The jump and rush attacks:** 1 of 1 and 3 of 3, the rush's second part by its clip's cycle (docs/combat.md). Most of
  their recorded blows struck an orc whose health didn't fall, which the calibration leaves out with the dead.
- **The differences:**
  - two blows on an orc not yet alert (×3) at the start of the chained-slash recording;
  - four blows on an orc getting up (×2, above);
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
| The player's blows on the followed orc | 16 | 13 | 6 agree, 5 differ (#10177; 4 before) |
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
  layer but the quick draw (`ReplayTick.Armed`). The lab's sword doesn't go away there: that was an orc's grab, which
  the lab's orc doesn't make (see "Weapons", the quick draw).
- **The jump attack:** above, "Attack in the air" and "The jump attack". Its rules from #10144 (the wait, the
  landing window, no parry in the air) keep both fights where #10142 left them.
- **When A begins:** above.

What's left: the direction after B (126828: R, the lab B), the unexplained tap and chain (above), and a finishing
move (`layer_behead_*_out`, 129270) the lab plays as the neutral power attack.

**The movement attacks (#10144)**, replayed from their scenarios with the recordings' builds:

| Recording | What | Begun together, lab |
| --- | --- | --- |
| `l01_a-v16-jump-attack-hold` (0.4 s holds) | jump attacks' strikes | 7 of 7, lab 7 |
| | power attacks | 5 of 5, lab 5 |
| `l01_a-v16-jump-attack` (0.06, 0.15, 1 s holds, and drops) | jump attacks' strikes | 1 of 1, lab 1 |
| | power attacks | 15 of 15, lab 15 |
| `l01_a-v16-jump-attack-basic` (the same, without the skill) | power attacks | 16 of 16, lab 16 |
| `l01_a-v16-rush-attack` | rushes | 6 of 6, lab 6 |
| `l01_a-v16-fatality` | Fatalities | 11 of 11, lab 11 |
| | power attacks | 44 of 44, lab 44 |
| `l01_a-v16-whirlwind` | Whirlwinds | 1 of 1, lab 2 |
| | power attacks | 15 of 15, lab 15 |

**The other weapons (#10148)**, replayed from their scenarios with the recordings' weapons:

| Recording | Fast attacks begun together, lab | Power attacks |
| --- | --- | --- |
| `l01_a-v17-weapon-long-sword` | 47 of 47, lab 51 | 5 of 5, lab 5 |
| `l01_a-v17-weapon-club` | 43 of 45, lab 49 | 7 of 7, lab 7 |
| `l01_a-v17-weapon-daggers-1`, `-daggers` | 64 of 65, lab 75; 62 of 62, lab 64 | 11 of 11; 11 of 12 |
| `l01_a-v17-weapon-staff-1`, `-staff` | 48 of 50, lab 51; 76 of 78, lab 81 | the first run's one is a jump's; 10 of 11 |
| `l01_a-v17-weapon-switch` | 6 of 6 | 4 of 4; put away 24 of 24, drawn 18 of 18 |

Replays follow the donor's switches (`InputTimeline.WeaponChanges`): the lab's sword puts its weapon away as the
donor's undraw begins, since the weapon on the Wire changes only as the draw does.

Replays hold the lab's adrenaline to the recording's each tick (`ReplayTick.Adrenaline`): the donor also fills it on
blows the lab doesn't follow.

**Ground the arena lacks** (R10144-1): two donor strikes came off geometry the lab's arena doesn't have, a 2.4 m
ledge stepped off (`jump-attack-hold`, 17494) and a running jump that rose 2 m and landed 0.22 m below the lab's
floor (`jump-attack`, 14395). The lab's body landed long before the donor did, so its sword cut the jump attack short.
The replay's sword now counts the donor as in the air while it falls from above the lab body's feet (more than
0.15 m up, dropping over 1 cm a tick), and, holding the jump attack, until the donor's feet are still for 4 ticks.
Both strikes now begin with the donor's: 8 of 8.

A scenario's `setpos` teleports the donor; where it raises the donor (the drops from 200 units), the replay raises
the lab's body as far (`IReplayBody.Lift`), to fall as the donor did.
- **Movement:** commands run at 1000, about 1400 on a diagonal, and 150 for the slow walk while charging or guarding.
  The replay scales them against a run. The lab's body averages 4.20 against the donor's 4.69 m/s, and 2.78 against 1.68.
- **Jumps:** all 12 came through.

**Where they differ:**
- **Which part a swing strikes:**
  - Donor blows on hit group 1 (4.2 and 9 damage) were the lab's on group 4 (1.4 and 3).
  - One donor hit was a lab parry, and one donor NoEffect a lab hit.
  - Why they can differ: the lab traces the donor's average arc for each move ("The blade's trace"), against an orc
    posed from states streamed every few ticks. Neither the donor's exact ray nor the orc's exact pose is reproduced.
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
- **One replay from the smoke:** `RB_REPLAY=local/recordings/....rbrec dotnet run --project lab/tests/DarkMessiah.Lab.Smoke`
  prints that replay's whole report and stops; `RB_TRACE=FROM-TO` adds the lab sword's move on each replayed tick in
  that range.

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
  - the disarm (above), the behead's trigger, and the quick draw after an orc's grab;
  - the daggers' backstab and the staff's breakneck (see "Weapons");
  - the shield-holding moves.
- **Stamina:** what empty stamina does to the sprint (the lab stops it at 0), and the costs of parrying, blocking and dodging
  (`Stamina_Parry`, `Stamina_Block`, `Stamina_Dodge` in the settings) aren't measured or modelled.
