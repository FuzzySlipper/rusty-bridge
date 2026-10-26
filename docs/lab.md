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

**The window's UI** is drawn by Servo, the Engine's default since the pin 0.1.0-dev.fb7b3343dc70; `rusty dev --window-ui chromium` (or `RustyEngineProductWindowUi=chromium`) draws it with Chromium instead. On den-win11 both ran the lab windowed for over ten minutes with no frame skipped and no UI import failed, the HUD, pause menu and developer shell alike. Under Servo the developer shell's panels show no scroll bars and its dropdown is smaller; its panels overlap the lab's title under either engine.

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
| Right button, hold | Parry (`layer_parry_sword_noshield_*`); with a shield, block (`layer_parry_sword_shield_*`) |
| Left button while blocking | Shield bash (`layer_parry_sword_shield_bash`) |
| F | Kick (`layer_sword_noshield_kick_medium`), also cutting off a swing whose hit window has passed |
| E / left button | Pick up a crate / throw it (fptester's cubes) |
| G | God: the orc's blows still count but take no health |
| Z / X, hold | Lean left / right (#10161) |
| Walk into a ladder | Climb it (the loft's); back off its top to climb down; Space lets go (#10163) |
| 1–9 | The belt's slots (`build.json`'s `belt`): a life potion, the full life potion... (#10162); 3 the fireball, 5 the classic bow, 6 the lightning bolt, 7 the freeze, 8 the inferno |
| Left button, with the bow | Draw; the arrow goes once drawn and let go (#10168) |
| V, hold, with the bow drawn | Zoom (`bow_zoom`; the donor's `+zoom`) |
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
- the orc's spawn and respawn time, and two numbers fitted to the
  recordings: the landing window each of its attacks is chosen in (`orc.attackLandings`) and its walking speed
  (`orc.walkSpeed`; see "The orc"). Its health, zones and attack rhythm are its `.qct`'s.

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
  and a shield (`models/items/armors/shield*`) with a sword or the club and `combat_shield_enabled` (the combat skill's
  level 2; see "The shield"). It refuses a bow, any other off-hand item, and a shield without those. A new weapon is
  drawn: the one in hand is put away first.
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
learned. Weapon switches come as draws (see "Weapons"), and a shield taken is worn from its tick. What the lab doesn't
play (a bow or a spell) isn't presented as played: the report names it, and no move or blow while it is held is compared
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
  At each level (`chained-slash-levels`, R10143-1: combat learned to 1, then 2, in the character sheet) the chain is
  the same: A to E, then `_end`, 10 bursts of 10 at level 1 and at level 2 as at level 3. Level 1 brings
  `combat_chained_slash` and `combat_rush_attack`, level 2 `combat_disarm` and `combat_shield_enabled`. Replayed, the
  build rebuilt at each purchase, the lab begins all 100 fast attacks with the donor, 20 of 20 of each clip.
- **A's recovery:** an A nothing chains onto plays `layer_fastattack_*_end`. A press late in it starts A again at once
  (`PlayerSword.FastEndOpen`). Of every press in the recovery over the sword-class runs (the `sword-timing2` sweep, the
  long sword and the club; #10142), those up to a streamed cycle of 0.53 did nothing (10 of 10), at 0.56 and 0.57 five
  of eight played A two ticks later, from 0.60 all did (4 of 4). The streamed cycle reads about a tick ahead of the
  press: replayed, the lab's own recovery stood at 0.54 where those A began, so the lab opens it at 0.525.
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
  6.6 s after each of the orc's `attack_grab`s in the long-sword run (4 of 4). Replays follow it (R10148-1): where
  the donor's view model empties in a stretch that ends in a quick draw, the lab's sword is away
  (`PlayerSword.TakeHands`: no input counts, nothing is drawn), and its quick draw begins as the donor's does, 4 of 4.
  The lab's own orc doesn't grab yet (#10185), so in a live fight nothing takes the hands.
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

### The shield

With a shield on the arm the sword plays `first_basic`'s `sword_shield` set (#10149; `rbwire scenario shield`,
`l01_a-v17-shield`: the short sword and the small orc shield against an orc that fights back). The build wears its
off-hand when that is a shield and it has `combat_shield_enabled`; `LabShield` reads the shield's `.qct`:

| What | Value | Source |
| --- | --- | --- |
| The clips | `layer_sword_shield_idle`, `fastattack_sword_shield_A`–`E` and `_end`, `bigslash_sword_shield_*`, `parry_sword_shield_*`, the shield combos | `first_basic`; the chain, ends and power attacks as the sword's (recorded: 15 fast attacks and 3 power attacks, begun with the donor's) |
| On the arms | `Leftshield` (attachment 6, `SK_L_Wrist`) | Wire `m_iParentAttachment` of `weapon_arxshield` |
| How far round it blocks | `parry_cos` 0.7 (46°) | the shield's `entity_data`; every shield has 0.7 |
| Its health | `health` × 3: the small orc shield's 80 is 240 | the settings' player `HealthCoeffShield` (the shields' own 10 isn't the player's) |

- **Blocking:** block held raises it (`parry_sword_shield_in`, `_idle`, `_out`), and the game counts a block as the
  player's parry (`PlayerParried`). The verdict is `CombatModel.PlayerBlock`: a blow from within `parry_cos` of the
  facing is blocked. Recorded: 19 of 19 from 0° to 41° blocked, 12 of 12 from 68° to 126° let through to the player
  with the shield raised. Each blocked blow plays a reaction (`parry_sword_shield_A`, `B` or `C`) the lab doesn't.
- **What breaks it:** nothing recorded did. The orc's `attack_KickSwing`, whose first part's `breakparade` breaks a
  sword's parry, was blocked twice. The lab takes `forcebreakparade` (the orc's bashes, which no recorded orc made) to
  break the block, as a parry's: not recorded.
- **The shield's wear:** a blocked blow costs the player nothing and the shield its damage (`Stamina_Block` is the
  AI's settings block, not the player's: the player's stamina stayed at 12 through every block and bash). Replayed,
  the lab's blocks wear it 19 times by 137.6 against the donor's 136, 17 of 19 alike. The recorded shield was never
  worn through; the lab takes it off the arm at no health, not recorded. A reset (R, and each replay's start and end)
  puts the build's shield back on whole, worn or broken (R10149-1).
- **The bash:** attack pressed while blocking plays `parry_sword_shield_bash` (1.27 s; its window 2186–2190, cycles
  0.18 to 0.29), 8 of 8, which plays on with block let go. Its blow strikes with the shield (the donor's verdict names
  `weapon_arxshield`), which has no `WDmg`, so it asks (strength + 1) × `HitPow` 0.01 × the hit group: 0.21 on the
  head and 0.07 on the body with strength 6, as recorded. It pushes the orc (its event's `push` and `alwayspush`):
  the three that landed pushed it 1.28 to 1.62 m over 1.2 s, playing `Push_*_Front_small`, and the lab pushes it
  `LabCombat.BashPush` (1.4 m). Its `breakparade` takes it through a parry (no orc parried one). Its arc is fitted to
  its 11 recorded sweeps (all within 10°); the shield set's other swings take the sword's arcs
  (`CombatTuning.ArcFor`: 9 of their 13 recorded sweeps within 10°).
- **Replayed:** the lab bashes with the donor 8 of 8, and the three bashes that landed land in the lab too, one on the
  donor's hit group (the others strike the head where the donor's struck the body, as the followed orc's pose goes;
  #10198). The orc's blows on the player agree 86 of 88.
- **Not recorded:** other moves with `push` (the staff's power attacks, the daggers' rush) push nothing in the lab;
  shields other than the small orc shield; the shield combos (`combo_sword_*_shield`), which play by the sword's rules.

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
  landed before the donor's strikes; `player.json` then had 5.05 m/s and 12 m/s² (1.06 m, 0.84 s). #10159 refitted
  the jump by how long jump is held (below, "The body's movement").

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
  the body, 720 on the head). Its head blows ask their 720 and no more: `cutheadflag` cuts the head off only a blow that
  kills (the one recorded, on an orc at 40 health, left `npc_orc_noHead`), and none of the head blows on 100000-health
  orcs killed (#10198: those #10147 saw fall were knocked down and got up). A landed huge slash empties the gauge; one
  that strikes nothing leaves it full.
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
- **Cutting the head off:** a `cutheadflag` blow that kills cuts the head off in the donor; the lab's orc dies whole
  (head and limb removal is rusty-engine #10176).
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
- **A head blow doesn't kill by itself** (#10198): no head blow on a 100000-health orc killed, the Fatality's 720
  included; `cutheadflag` cuts the head off a blow that kills (one recorded).
- **The followed orc as streamed** (#10198): its sequence and cycle, and every overlay at its streamed cycle and
  weight (`OrcPose.Overlays`), played on between updates.
- **Dodges can't be touched** (#10198): from a dodge's 2133 to its 2145, or its 2134 when that comes first
  (`CombatData.Untouchable`), every player blow was the no-effect verdict, 50 of 50 (Dodge_right to 0.23, Dodge_left to
  0.35, Dodgestrike_Right to 0.09); past it they land (Dodge_right at 0.36), and the wounded dodges, with no events,
  are struck. The window matches the donor's streamed no-effect byte on 33 of 34, 31 of 32 and 55 of 56 blows
  (`ADodgesWindowIsTheNoEffectByte`; the misses are blows on the tick a dodge or block begins). The lab's verdict
  passes it as the no-effect input, and such a blow does nothing.
- **An unaware orc takes ×3** in a replay as the donor's did: the replay gives the lab's blow the alert level the
  donor's damage record streamed within 12 ticks (`LabCombat.RecordedAlert`). The lab's own orc is always alert.
- **Turn strikes** (`Turn_strike_*`, no part events): the followed orc sweeps every step inside its 2175–2176
  window until a sweep lands, as the donor's does at every think. The fatality run's orc blows agree 79 of 79 (lab 81).
- **Each window counted:** a swing's later hit windows (the huge slash's second, 40 ticks on) are blows of their own
  in the comparison (`WindowApart`, 15 ticks).
- **What it gives:** the player's blows that agree with the donor's (same verdict and hit group): the fatality run 26
  of 43 (before #10198: 15 of 40), the Whirlwind run 6 of 11 (4 of 10), jump-attack-hold 2 of 2, rush-attack 1 of 4,
  hand1 4 of 10, hand2 10 of 31, the parry bot 9 of 16, ghoul2 4 of 6.
- **What still differs is the orc's body, not the aim** (#10205): sweeping the donor's own recorded rays through the
  followed orc gives the same blows as the lab's arcs. At 12472 in the fatality run the same ray meets the lab's orc's
  head where the donor's met its chest, with the same sequence, cycle, place and facing; in a lunge (`attack_right`,
  10385) the donor's eye-height ray passed the orc and struck nothing. The mirror's Shadow, posing the victim as the
  blow found it (v13), agrees on 37 of 51 there. None of these clips is a blend grid: what the Wire lacks is likely
  the orc's pose parameters, bone controllers or procedural bones. Replays keep holding the adrenaline gauge to the
  recording's (the lab's own runs within 1 on 28% to 45% of a fill-and-Fatality replay's ticks, ahead on blows only
  the lab lands).

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

### The body's movement

The body is the Engine's character controller, set from the donor's measured movement (#10159; `player.json`,
`MovementCalibration`). Dark Messiah's own files carry no movement numbers: `player_basic.qct` has none, and its ConVars
(`tools/scenarios/movement-cvars.json` prints them) are Source's: `sv_maxspeed` 320, `sv_accelerate` 10, `sv_friction`
4, `sv_stopspeed` 50, `sv_gravity` 600, `sv_stepsize` 18, `cl_forwardspeed` and `cl_sidespeed` 1000, `cl_backspeed`
150. The game caps the player's speed itself. Measured from Wire v14 recordings (the user command and the player's
origin, speeds over 0.2 s since the origin reaches the Wire every few ticks), mostly the `movement` and `jump-hold`
scenarios (docs/recording.md):

| | The donor | The lab (`player.json`) |
| --- | --- | --- |
| Run forward, strafe, diagonal | 180 units/s (a diagonal no faster) | `speed` 4.572 m/s (180) |
| Back (the full back key, a command of 150) | 60 | `backSpeed` 1.524 (60) |
| Walking (`+speed`, the ALT key: IN_BULLRUSH) | 100 | `walkSpeed` 2.54 m/s (100), on the left ALT key |
| `+walk` (IN_WALK) | 180: it does nothing | nothing |
| Crouched | 50, no jump (none of three crouched jumps left the ground) | `crouchSpeed` 1.29 m/s, which runs at 51 against the controller's friction; `crouchAcceleration` 5.6 (fitted to the start); jump does nothing |
| Charging an attack, guarding | 180, 185: no slower | `attackMovementScale` 1 (was an authored 0.5) |
| From standing to 90% | 0.48 s; backing 0.57 s | `acceleration` 4.6, fitted: 0.48 s; `backAcceleration` 5.2 (back alone): 0.57 s |
| Stopping (from 180 to under 18) | 0.61 s, sliding 42 units | `friction` 4 and `stopSpeed` 1.27 (50 units: `sv_friction`, `sv_stopspeed`), `braking` 0: 0.57 s, 36 units |
| Leaving the ground | 2 to 5 ticks after jump goes down | `jumpDelay` 0.075 s |
| A jump held to its peak | rises 42 units, 1.07 s in the air as measured | `jumpSpeed` 4.8, `gravity` 11.5, rising at `jumpHeldGravity` 0.95 of it while held (fitted on the lab's body, R10159-2) |
| A jump let go early | held 0.05 s 26 units, 0.15 s 33, 0.3 s 41, longer 42 | `jumpReleaseGravity` 1.6: gravity's multiple for the rest of the rise once jump is let go (`mm_jump_rework` is on) |
| A fall | none taken from drops under 300 units; above, Source's fall damage ("Falls") | `FallRules` |
| Sprint | 270 running straight | `sprintSpeed` 6.86 m/s (#10160, "Stamina") |

- **Not measured:** moving after a kick or with the jump attack held, which cut the donor's jump short
  (`sword-timing2`'s jumps rose 24 to 27 units): those jumps are left out.
- **Against the recordings** (the smoke, R10159-2): case by case, each recording on its own. `MovementCalibration.Compare`
  pairs the donor's and the lab's body on the same replayed ticks: each steady speed by command within 10%, each start
  from rest within 0.15 s, each stop from a run within 0.15 s and its slide within 30%, each jump's rise within 10% and
  time in the air within 0.1 s, and a jump the lab makes where the donor's body didn't leave the ground fails. A case is
  left out only with its evidence, printed: the lab's body against its arena's wall (the Engine's wall block flag), a
  scenario's `setpos` in it (the donor's origin faster than 2000 units/s across, 450 up or 800 down), the donor's body
  held (under half the lab's speed: a steady speed's ticks each, so a sprint cut short by l01_a's walls keeps the rest), stopped dead (to under a quarter of 40 units/s or more in 0.03 s), or jumping
  from a slope (more than 4 units in the 0.2 s before), the lab's body not at rest as a start's movement began (before its first key: a diagonal's second key and sprint
  come a tick or two after; R10159-3), and a sprint
  either side's stamina didn't admit (the lab's own since the replay began; v14 has no stamina on the Wire). Every
  compared case agrees on `movement`, `jump-hold`, `sprint`, `sprint-double` and the three v14 arena fights, and the
  scenarios compare each kind (the run, walk, `+walk`, crouch, back, strafe, diagonal and sprint speeds and starts, stops,
  slides, rises, times in the air) twice or more. `RB_MOVECASES=a,b` prints every case of the recordings whose names
  hold a or b (`RB_MOVETRACE=from-to` the paired traces); `RB_MOVE=1` prints the donor's measures; `RB_MOVEFIT=1`,
  `RB_JUMPFIT`, `RB_MOVEFIT_CROUCH` and `RB_MOVEFIT_RELEASE` run the lab's body through the fitting runs.

### Falls

Fall damage, measured in the `fall` scenario (R10159-1; `l01_a-v18-fall-1` and `-2`, god off, the health set to 80
before each drop; `RB_FALL=path` prints each fall's drop, landing speed and the health it took):

| Drop (units, the origin's highest to its landing) | 115, 173, 229, 258, 277, 284 | 316 | 344 | 371 | 398 | 429 | 452 | 517 | 575 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Health taken | none | 10 | 17 | 24 | 28 | 35 | 39 | 51 | 61 |

It is the Source SDK's fall damage: the landing speed over 526.5 units/s takes 100 for each 396 more, the landing speed
being 32 √drop (a free fit gives 8.14 health per √unit from 271 units; the formula's 8.08 from 271; every landing within
1.3). The drops of 277 and 284 units, which it prices at 1.4 and 3.3, took none, so `FallRules` takes none under 300.
The lab takes it from the highest the body's feet were off the ground to where it lands (a ladder holds none), unless
the player is a god; replayed, the health follows the donor's damage records, which carry the falls'. The scenario's
`setpos` puts the origin higher than asked (209 lands at 239.8), so the drops are the recorded origins'.

### Stamina

`PlayerStamina`, from the donor's numbers:

| What | Value | Source |
| --- | --- | --- |
| Maximum | 9 | `player_basic.qct`, `skills`: `player_stamina_base_max` |
| Regeneration | 0.06 once more than 0.12 s has passed, in the game's 15 ms ticks: every 9 ticks, 0.44 a second, sprinting or not | `player_stamina_add_value`, `player_stamina_add_interval`; the tick and the regeneration while sprinting measured (#10160) |
| Sprinting | −2 a second while moving, so −1.56 with the regeneration (9 to 0 in 5.7 s) | ConVar `mm_player_stamina_megasprint_cost_per_second`; measured (#10160) |
| `double_sprint` | maximum 12, the same drain | `player_stamina_double_sprint_max`; `sprint-double` |
| A kick may start with | 3 | ConVar `mm_player_stamina_needed_for_kick` |
| A sprint may start with | 3 | measured (#10144, "The rush attack"); it goes on below |
| A kick costs | 3 when it lands on an NPC; nothing kicking the air (4 of 4 in `sprint`, `sprint-double`) | `stamina_kick_npc` in the settings; `stamina_kick_all` (1) is for a kick on anything else, a prop or the world, which the lab's kick doesn't strike |

The ConVars' defaults are in server.dll; no content file holds them.

Simulated from the hand fights' commands with these numbers, every kick the donor began had at least 3.06, and each
refusal on the ground with the arm free had less than 3. Charging the 1 as well as the 3, or always 3, would have
refused a kick the donor made (116732, at 1.06 or 0.84).

Sprint is the command's action byte +0x95 (Wire `PlayerActions.Sprint`). Measured in the `sprint` and `sprint-double`
scenarios (#10160; docs/recording.md):
- **Speed:** 270 units/s running straight (no turn of more than 30° in the last half second): `player.json`
  `sprintSpeed` 6.86 m/s. The scenarios run round a square, turned every second; the donor's sprint kept its speed
  through the turns, the lab's loses about 10% after each, so the speeds are compared on the straights.
- **Starting:** with 3. Held below 3, the body runs, and sprints the tick stamina reaches 3 (#10144's rule, again
  2.76 to 3.00 here).
- **An empty gauge:** at 0 the sprint ends, sprint held or not; the body runs at 180 and stamina regains as ever.
- **Against the donor** (the smoke): both scenarios replayed, the lab's stamina within 0.2 of the streamed value on
  every tick (3200 of 3200 and 3668 of 3668), through the sprints, the empty gauge, the restart from below 3, the kicks
  and the rests. The replay's body sprints when the lab's stamina lets it (`IReplayBody.Step`'s `sprint`). The
  regeneration's clock takes the donor's phase from its first 0.06 rise (R10160-1): the donor's runs on, every 9
  ticks, through sprints and rests alike, so where the replay starts would otherwise set it, and a step's phase
  decides the tick a sprint may start (the restart in `sprint-double` came 9 ticks early, 0.24 off, before).
  `RB_STAMINA_TRACE=from-to` prints the two tick by tick. The donor's streamed stamina moves every other tick.
- **Not measured:** the view model's `Megasprint_sword` clip, and attacking out of a sprint (the rush attack is
  #10144's).

### Health

`PlayerHealth` and `PlayerBelt`, from the donor's numbers (#10162; the `health` scenario, docs/recording.md):

| What | Value | Source |
| --- | --- | --- |
| Maximum | 50, and 10 for each `extra_health` level (60, 70, 80) | measured: every recording without the skill 50, at level 3 80; `health` 50, 60, 70, 80 as the levels were learned. No content file holds it: `player_constitution_1`–`4` (5, 6, 7, 8) don't fit |
| A new maximum | leaves the health where it was | `health`: 50 of 50 became 50 of 60 |
| Regeneration | none; with `fast_metabolism` 0.25 once a second, on a game clock (every recording rose at the same phase) | `player_basic.qct`'s `player_metabolism` (0) and `player_metabolism_module` (0.25) |
| A life potion | 10, at once, up to the maximum | `life_potion.qct`'s `give_health`; recorded 15 to 25 |
| The full life potion | 300: to the maximum | `full_life_potion.qct`; recorded 25 to 50 |
| Food | the apple 3 (`give_health`); not on the belt (`no_belt_priority`) | data only: `give` put no food on the belt, and `use` drinks or eats nothing |

The belt holds `build.json`'s `belt` (slots 1 to 9: an item's model and how many), used by the keys 1 to 9 as the
donor's `usebelt1`–`9`; a reset fills it again. Dead, nothing is used; after 2 s the player is back on its feet,
full. `hurtme N` took N.
- **Against the recording** (the smoke): `health` replayed, the lab's health within 0.3 of the donor's on 7665 of 7667
  ticks and its maximum the donor's on every tick, through `hurtme`, two potions, `extra_health` to 3 and the
  regeneration. The replay starts from the donor's health, takes what each of the donor's damage records took (the
  lab's own orc is held off by god), drinks where the donor's health rose by more than its regeneration (the full
  life potion where it reached the maximum past a life potion's 10, else a life potion: the Wire doesn't carry the
  belt's use) and takes the regeneration's phase from its first 0.25. The two ticks off are a damage record that
  arrives two ticks before the player's health shows it. Every other replay with the player's health agrees on every
  tick (the hand fights at 50, the v16 and later fights regaining from 50 to 80).
  `RB_HEALTH=1` prints the donor player's health, maximum and skill levels through every recording; `RB_ITEMS=path`
  when a recording's `item_*` entities appear and go (a drunk potion's goes only when it was the last of its stack).
- **Not measured:** poison (`poison_recovery`, the cure poison potion; nothing in l01_a poisons), the regeneration
  ring (the settings' `life_time_regen` 2 is likely its), food eaten, and the mana potion (#10165; "Mana"). Falls: "Falls".

### Mana

`Magic/PlayerMana.cs` (#10164), from `player_basic.qct`'s stat block:
- **Maximum:** 10 × `player_intelligence_{extra_mana level + 1}` (2, 4, 7, 10): 20, 40, 70, 100. The factor is fitted:
  every recording with `extra_mana` 0 had 20, every one with it at 3 had 100 (`RB_MANA=1` lists them).
- **Regeneration:** `player_mana_recovery_module` (0.7) once a second with the `mana_recovery` module
  (`mana_regeneration`'s), `player_mana_recovery_base` (0.1) without. Every recording with the module rose 0.7 every 66
  or 68 ticks; none without it showed a rise (each sat full).
- **The regeneration's tick:** once more than `mm_player_time_to_add_mana` (1 s) has passed in the game's 15 ms ticks:
  every 67 ticks, 1.005 s, as stamina's (#10160); the streamed mana rose every 66 or 68 ticks, 67 on average (the stats
  stream every other tick).
- **Mana potions** (#10165): on the belt, `give_mana` (50) at once, up to the maximum.
- **Replayed** (#10165): the lab's mana from the donor's at the start, regenerating on its own clock (the donor's phase
  from its first rise of one step), the donor's casts (a fall) and potions (a rise past a step) taken as they come:
  `fireball` within 0.3 on 11806 of 12001 ticks (the smoke asks 97%).

### The fireball

`Magic/Fireball.cs` (#10165; `l01_a-v18-fireball`, docs/recording.md), the spell core's first spell:

| | The donor | The lab |
| --- | --- | --- |
| In hand | `Deploy_Spell_FIRE_3`, or its belt slot | `build.json`'s belt slot 3 (`{"spell": "fire_3"}`, the build's `spells`); its key again, the weapon back; the arms play `layer_spellcasting_ready_fire_3` and the sword isn't drawn |
| Casting | a press: `cost` (8) from mana at once, 7 of 7; the arms play `layer_spellcasting_cast_in_fire_3` | the same; with too little mana nothing is cast |
| Its flight | it forms at the hand for 0.57 s (7 of 7), then flies at 722 to 730 units/s; the ball held on (cast and hold guides it) flew 501 | 0.57 s at the hand, then 725 units/s along the view, going off where it meets the orc's body or the arena, or after `timetolive` (5 s) |
| Its damage | struck, the orc took 10.8 to 10.9 at once and 0.6 to 1.1 over the next 2 s: 11.6 to 11.9 of `effect` 12; off the floor 100 units from its body, 3 and 1.2 more | `CombatModel.SpellDamage`: `effect` falling off to none at `explosion_radius` (120 units) by the distance to the body, times the victim's `Vulnerabilities_spells` (the orc's fire 1) |
| Burning | none: the orcs' health took no `Fire_damage_per_second_for_npc` (3/s) | none |
| Adrenaline | `adrenaline_add` (3) a hit: the gauge rose to 12 over 4 | the same |

- **Not measured:** the falloff (one shot off the body took 4.2 where the rule gives 2; its distance is from the
  projectile's last sample, 0.06 s apart), the 0.9 of the immediate share, guiding (held, the ball steered with the
  view and flew slower), the adrenaline effect (`effect_adrenaline` 200), the other spells (#10166, #10167) and the
  selection by the mouse wheel (`mm_previtem`/`mm_nextitem`).
- **The smoke:** in the arena the slot puts it in hand; a cast costs 8 with the donor's clip, forms in 0.58 s, flies at
  725 units/s; struck, the orc takes 12, off the floor short of it its share; with 5 mana nothing is cast; a mana potion
  gives its 50 up to the maximum. Against the recording: each cast's cost and forming, the speed of those that went off
  within a second, each struck orc's health within 1 of 12, and the replayed mana. `RB_SPELL=path:spell_fire_3` prints
  each projectile (`RB_SPELLTRACE=n` the n-th one's samples).

### Attack spells

`Magic/AttackSpells.cs` (#10166; `l01_a-v18-attack-spells`, `-attack-spells-2`, docs/recording.md), on the fireball's
core: each spell's belt slot puts it in hand (`build.json`'s `spells` and `belt`), its numbers from `spells.txt`.

| | The donor | The lab |
| --- | --- | --- |
| Lightning bolt (`air_3`) | a press strikes 1.78 to 1.87 s later, tapped or held 1.5 s (5 casts); `cost` 8 taken as it strikes; `effect` 15, which the orc takes twice (its `Vulnerabilities_spells` `air_3` 2); the struck orc stunned (`hit_stunned_front`, every one that could react); a strike on a 5.5-health orc left it at 1 (`Hit_electrocuted`), and it died 1.7 s later | strikes 1.8 s after the press for its 8, the live orc nearest the view within 10° and `lightning_NPC_Bounce_Distance` (2000 units) that the arena doesn't hide (R10166-3: none through a wall or crate); `CombatModel.SpellDamage` (30 on the orc), a stun, `adrenaline_add` 3; a strike that would kill leaves 1, death 1.7 s on |
| Freeze (`water_3`) | `cost` 15 at the press; a bolt 0.27 s later at 4320 units/s; the orc it met stood still 7.2 s from it (`Stage1_Duration` 7), then fought on; no damage | the same: the orc's thought, clips and blows stop for 7 s |
| Inferno (`fire_4`) | held: `cost` 20 at the press, then `maintain_cost` 15 a second from 1.5 s until let go (both holds); `effect` 13 on the orc 200 units ahead 0.16 s after the press and every 0.51 s while held; a blow that would kill left it at 1, and the blows after took nothing; the orc with its AI on played `onfire` and died 2.4 s after that blow; one frozen with `ai_disable` played nothing and died when its fire ran out, 5.2 s on (`Fire_Time_Ignite` 5) | the same costs and blows, on every live orc within 15° and 300 units of the view that the arena doesn't hide (an area, R10166-1: only one orc was recorded; its reach and width are the lab's, the recorded orc 200 off); a blow that would kill leaves 1, and it dies 2.4 s on, as the live orc did (R10166-2: the lab's orc always reacts) |

- **Not in the lab, and why:**
  - **poison spray (`water_2`) and drain (`earth_4`):** `Deploy_Spell_WATER_2` and `_EARTH_4` put nothing in hand from
    the console (no deploy clip, no mana spent, in two runs), so neither is recorded;
  - **fire trap (`fire_2`):** its cast, once (15 mana; its projectile `arxspellmissileshot_7` to the floor), and once not
    at all; the orc never walked into a trap, so its trigger and `effect` 34 over `Radius_damage_on_explode` 200 aren't
    recorded;
  - **flame arrow (`fire_1`):** with the bow, which waits for #10168's review;
  - **the bolt's bounce:** none recorded: the second orc spawned beside the first never stood (it took the first's
    place).
- **Not measured:** the bolt held past 1.8 s, the freeze's later stages (6 and 12 s: nothing seen) and its shatter, the
  inferno's reach and width, the inferno's own `adrenaline_add` (0.1), and the lightning's on a frozen orc (it struck
  nothing).
- **The smoke:** in the arena each slot puts its spell in hand; a bolt tapped strikes 1.83 s after for 8, the orc taking
  30 and stunned, and a second leaves it at 1 and it dies 1.7 s on; a freeze costs 15 and its bolt holds the orc still
  6.98 s; an inferno held 3 s costs 20 and 21.8 more from 1.5 s, burns the orc at 0.2, 0.7 and 1.2 s to 1, and it dies
  2.4 s after; held at the orc and its squad-mate side by side, it burns both; a bolt at the orc behind the big crate
  strikes nothing, from beside it the orc. Against the recordings: every bolt's strike time, cost, damage and stun, every
  inferno's costs, blows and their spacing, every freeze's cost and the orc it froze standing 7 s from its bolt, and the
  deaths after a blow that left 1 (1.68 s after the bolt, 2.40 after the inferno's).

### Support spells

`Magic/SupportSpells.cs` (#10167; `l01_a-v18-support-spells`, `-support-spells-2`, docs/recording.md), on the attack
spells' core: each one's numbers from `spells.txt`, `build.json`'s `spells` knowing them (the default belt keeps the
attack spells: put them on it to play them).

| | The donor | The lab |
| --- | --- | --- |
| Cure wounds (`air_2`) | cast as it's put in hand (`Deploy_Spell_AIR_2`; no clip), `cost` 10 then; a third of `effect` 15 at once (5, both casts), then the rest a point at a time at 0.09 to 0.12, 0.21, 0.30, 0.45, 0.60, 0.75, 0.99, 1.29, 1.71 and 2.61 s (the same in both); back to the weapon after, a press casting nothing (two taps; one press 2 s after a cast gave 5 more for no mana, once) | cast by its belt key, the hand keeping what it held; 5 at once, then a point at each of those times (`CureRules.PointSeconds`), up to the maximum |
| Weaken (`air_4`) | in hand (`layer_spellcasting_*_air_4`), `cost` 15 at the press; the orc it's cast at plays its wounded clips (`Hurtwalk`, `Wounded_combatmove_*`) from 0.06 s and strikes only with its wounded attacks (`attack_wounded_1`–`3`, all 7 attacks over both casts), whose `HitPow` 0.5 makes the cleaver's 8 ask 4; its last wounded clip began 15.5 s on (`weaken_duration` 15), its own attacks from 17.1 s asking 8 and 12. The player's sword on it asked 5.6, as on any orc (`weaken_reduction_factor` 1) | the orc nearest the view within 10° and 2000 units (the lightning bolt's reach), weakened 15 s: wounded (its wounded clips, and the stun and fall chances that go with them) and choosing among its wounded attacks only, by their landing windows (`combat.json`, fitted to the recordings as its others); `weaken_damage_multiplicator` (0.5) multiplies nothing more: no recorded blow asked 2 |
| Charm (`water_4`) | in hand with the drain's arm clips (`layer_spellcasting_*_earth_4`), `cost` 15 at the press; with the player in `god` and seen, neither orc struck the player; the other orc ran at the charmed one 8 s on and struck it twice (12 with `attack_Forward`, 8 with `attack_bottom`: what it asks of the player), the charmed one fought back and landed nothing; 25.2 s on it stood idle again (`effect` 25, "the duration in Magic code") | the orc in view charmed 25 s, on the player's side while it lasts (R10167-1: sides by `mm_relationship.txt`; the orcs hate the player and like orcs, the player's side fights those that hate the player): it and its uncharmed squad-mate fight each other, each in the other's place of the player (the reach test against the victim's hull, the cleaver's damage, and a reaction as to the player's blow); two charmed orcs don't fight; with no foe it keeps by the player, holding off; the player is left alone until it ends, and its own side is back. Cast on an unaware orc (as weaken), it leaves its idle (R10167-2) |

- **Split off, and why** (the user's call):
  - **shrink (#10348):** below, "Shrink": weaken cast with the gauge full (`Deploy_Spell_EARTH_3` puts nothing in hand:
    `earth_3` is no player spell);
  - **magic reflection (#10349):** below, "Magic reflection" (a necromancer spawned in l01_a casts at the player);
  - **wizard eye (#10350, #10358):** below, "Wizard eye": no remote view, a 30 s brightening of the arena.
- **Not measured:** cure wounds at full health and its `radius` 300 (on others), weaken's own `adrenaline_add` (2)
  and the stun and fall chances of a weakened orc, charm on a lone orc, an orc charmed by the player striking it, and
  whether a charmed orc's blows land on the other (the recorded one's didn't).
- **The smoke:** on a belt of the three, cure wounds at 10 health costs 10 and gives 5 at once and a point at 0.12
  to 2.62 s (each within a step of the rules' times), 15 in all, the hand keeping its weapon; weaken costs 15 at the
  press and the orc 200 units off walks in `Hurtwalk` and strikes with `attack_wounded_1` and `_3` for 4 for its 15 s,
  then with its own attacks; charm costs 15, and the orc and its squad-mate side by side strike each other (7 blows,
  all landing) and not the player for its 25 s, then the player again; both charmed (the mate 2 s after), they strike
  neither each other nor the player until the first's ends, and both the player after (R10167-1); weaken cast on an
  unaware orc gets it up from its idle and fighting, past its 15 s too, and charm on an unaware one beside its unaware
  mate sets them on each other (R10167-2). `RB_CHARMTRACE=1` prints the both-charmed fight each second. Against the recordings: both cures' cost, their
  5 at once and each point within 1.5 ticks of the rules' times; both weakens' cost, the orc's 5 blows in their 15 s
  wounded attacks asking the model's 4 and the 6 after its own attacks asking the model's, the last wounded clips begun
  14.2 and 15.5 s on, and the player's 2 sword blows on the weakened orc the lab's; the charm's cost, no orc striking the
  player in its 25 s, the other orc's blows all on the charmed one (the one struck in an attack clip asking the model's
  12), and its idling again 25.2 s on.

### Shrink

`Magic/SupportSpells.cs`'s `ShrinkRules` and `Npc/OrcShrink.cs` (#10348; `l01_a-v19-shrink`, `-shrink-2`, `-shrink-3`,
docs/recording.md; how server.dll does it in docs/wire.md, "Shrink"). Shrink is weaken (`air_4`) cast with the
adrenaline gauge full: its spell weapon's `m_adrenalinepower` makes the cast shrink instead of weaken. Wire v19
records the orc's size (`m_fMMSize`) and shrink (`m_iShrinked`).

| | The donor (five shrinks) | The lab |
| --- | --- | --- |
| The cast | `cost` 15 at the press; the orc's health set to 1 and its size to 0.999 that tick; the gauge emptied and air_4's `adrenaline_add` 2 back (20 to 2; a game's first powered cast, three of the five, left it full) | weaken pressed with the gauge full: 15 mana, the gauge to 2, the orc in view (weaken's reach) at 1 health |
| Its size | 0.96 s on (0.93 once), down 0.03 every two ticks to 0.2 at 1.74 s; at 1.95 s it wears `npc_orc_shrinked.mdl` (the orc's at 0.3: its hull 26.9 units tall to 89.5) and its size is 0.999 again, `m_iShrinked` 1, after a 0.12 s `ragdoll` clip | the same steps (`ShrinkRules.SizeAt`), drawn and struck at that size; at 1.95 s the orc's model at 0.3, its box and hit boxes with it |
| Small | stands in `combatidle` and strikes nothing, the player seen or not (25 s at 120 units, `notarget` off); small for as long as it was watched (129 s), never growing back; its health climbing 0.25 every 2 s | stands in `combatidle`, no thought and no blow, for good; its `life_regen` as every orc's (below) |
| Its death | the player's body meeting it, 25.8 units off (26: the player's half-width 16 and its hull's 10), crushes it: a kick-typed blow (0x40000) of 2 from the player with no weapon, killing it at 1.75 and 2.25 health (both walked into, the probe's included); a kick (1 asked at 2 health) killed it too | the player's feet within 26 units of its feet, on its level: a stomp of 2, and it dies (`Stomped` counts them); a kick that reaches it kills it, whatever it asks |

- **Every orc regenerates:** its `.qct`'s `life_regen` (0.25) every 2 s while short of its health (`Orc.RegenSeconds`):
  the shrunk orcs climbed so from 1 (145 rises, 1.92 or 2.04 s apart, 2.00 on average), and so did the hurt orcs of
  `l01_a-v18-support-spells-2` (26 rises) and `-attack-spells` (3). The lab's orcs didn't before.
- **Not measured:** the sword on a shrunk orc (its two swings, from 50 units 40° down and crouched from 45 units 15°
  down, struck the floor), a shrunk orc's own clips past `combatidle` (`walk_shrink`, `run_shrink`, the jumps: none
  played), an aware orc shrunk mid-fight, what the size ramp looks like on screen (the client's own scale is off the
  Wire), and why a game's first powered cast left the gauge full.
- **An orc spawned after a shrink** with `mm_npc_create` came up with no health and never moved (`shrink-probe`, v18),
  so the scenarios spawn their orcs before the first cast.
- **The smoke:** weaken pressed with the gauge full costs 15, leaves the gauge at 2 and the orc 200 units off at 1
  health; its size takes the rules' 28 values from 0.97 s to 0.2 at 1.75 s, and it's small at 1.95 s, drawn at 0.3
  (its box 0.3, its posed height 0.33 in `combatidle` against its approach), striking nothing in 20 s while its health
  rises every 2.00 s; walked at, it's crushed 25 units off; another, kicked from 40 units, dies. Against the
  recordings: all five shrinks' cost and health of 1, their size steps (within two ticks) and swap (1.92 to 1.95 s),
  no blow, `combatidle` (and `ragdoll`), the regeneration's 0.25 every 2 s, the gauge at 2 after the two not first,
  the stomp's 2 at 25.8 units, the kick's kill, and one small for 129 s. The smoke's spell, bow and telekinesis checks
  count what the orc's blows took (`Orc.Taken`), its regeneration aside.

### Magic reflection

`Magic/MagicReflection.cs` (#10349; `l01_a-v19-reflect-probe-2`, `-3`, docs/recording.md). l01_a has no caster, so
the scenarios spawn one: `mm_npc_create npc_necromancer models/NPC/Necromancer/Npc_necromancer.mdl weapon_mm_hook`
(its `.qct`'s `fire_1`, the flame arrow, 0 to 450 units). l01_a's NPCs don't see the player, so a lightning bolt wakes
it (and the archer).

| | The donor | The lab |
| --- | --- | --- |
| The cast | `Deploy_Spell_EARTH_2` casts it as it's put in hand, `cost` 25 then, no arm clip (the hand keeps its spell); its entity stands at the player's feet 14.95 to 14.98 s (`effect` 15; five casts) | its belt key casts it, the hand keeping what it held: 25 mana, the shield up 15 s, a new cast starting it again |
| What it turns | server.dll: each tick a sphere query of `shield_radius` (60) about the player's origin 20 units up (+0x474606), turning the flame arrow's, the fireball's and the freeze's missiles (`CARXSPELL_FIRE_1_Missile`, `_FIRE_3_`, `_WATER_3_`), not the player's own (+0x4746D9), and into a held spell staff, which takes them. Recorded: the necromancer's flame arrows (one every 1.68 s, 6 a blow, 1140 units/s), turned where their origin came within 74.7 to 75.9 units of that point (the query meets the missile's box), off from the player's origin through where they were, at their speed (720 in and out), back and up past the necromancer: 11 of 11, none striking anyone. The archer's arrows went through: all 15 under two shields struck the player for 5 | `ReflectionRules.Turn`: an enemy's flame arrow, fireball or freeze missile within 75 (`shield_radius` and 15) of that point is sent off from the player's origin through where it is at its speed; arrows aren't (the archer's go on as before). `LabCombat.TurnSpell` asks it in lab space while the shield is up; the lab has no enemy caster to ask yet |

- **Not measured:** a fireball or freeze cast at the player (only the necromancer's flame arrows were), the spell staff
  taking a missile, the shield moving with the player (the player stood still), a turned missile striking its caster
  (none came back at it), and its `recovery` (2).
- **The shield isn't drawn:** the HUD says it's up.
- **The smoke:** cast from belt slot 1 for 25 mana, the hand keeping its weapon, up 15 s and down 15.00 s on; a flame
  arrow flown level at the player's chest turns back and up at 720 units/s, an arrow isn't turned, and none is once the
  shield is down. Against the recordings: the five shields' 25 mana and 15 s; every one of the 27 flame arrows flown
  along its recorded line at the lab's rule, the shield up where the donor's was: the 11 the donor's turned turned on
  the donor's tick, within 1° and 1% of its way off, and the 16 others didn't; and the 12 arrows loosed under the
  shields all struck the player.

### Wizard eye

`Magic/WizardEye.cs` (#10350, #10358; `l01_a-v20-wizard-eye`, `-wizard-eye-look`; Wire v20, docs/wire.md "Wizard eye"). The wizard eye isn't a remote
view: server.dll's `EnterWizardEye` is an empty stub, and the spell brightens the lightmaps and props for its
duration. `Deploy_Spell_WATER_1` turns it on as it's put in hand, for `cost` 0, with no press: the player's
`m_bMMWizardEyeEnable` on, `m_g_MM_fWizardEyeDuration` from 30 (`effect_duration`) down, the lightmap add
`Lightmap_Max_Add` (0.002, its LDR value 0.01) and the props' add its fade times `Props_Max_Add` (0.01). The fade
rises 0.03 every two ticks to 1 in 1.02 s, and at 30.00 s it's off and the fade falls 0.06 every two ticks to 0 in
0.48 s (the FX value at half that speed). A tap and a 3 s hold while it was on changed nothing; cast again once it was
off, it did the same.

- **How it looks** (`wizard-eye-look`: the game's own `jpeg` screenshots in the l01_a courtyard by day): 2 s and
  15 s in, 98% of the view is white, the props and the sky as well as the world, and the HUD's belt greyed; 3 s after
  it's over the view is as before it.
- **In the lab:** cast as it's put in hand (a belt slot, or `lab.spell water_1`), for its cost, the hand keeping its
  weapon; on for `effect_duration`, its fade rising 1 a second and falling 2 a second once it's off (the recorded 0.03
  and 0.06 every two ticks). The brightening is a white ambient light over the Engine's neutral rig, its fade times 40
  (`WizardEye.FullLight`): in headless captures the arena goes white and the crate nearly (its mean 91 to 242 of 255),
  as the donor's world and props do. A cast while it's on starts its 30 s over.
- **Differences:** the orc's darker skin keeps some of its texture, the lab's background (the clear colour) isn't
  brightened, and the donor's screen effect (`fx/WizardEye/dudv`) and tonemap (`wizardeye_tonemap_scale` 0.3) aren't
  played.
- **Not measured:** its `radius` (300) and `effect` (30), a cast while it's on, and how it looks somewhere dark.
- **The smoke:** both recorded eyes on 30.00 s for 0 mana, their adds `spells.txt`'s, the fade in 1.02 s and out
  0.48 s, the props' add with it, and the presses changing nothing; the lab's eye cast from the belt for 0 mana, on
  30 s, its fade within 0.06 of the donor's at every recorded sample of both eyes, and the arena's light following
  it.

### The bow

`Ranged/Bow.cs` (#10168; `l01_a-v18-bow`, `-bow-basic`, `-bow-more`, `-bow-zoom`, `-bow-sway`, `-bow-rope`, `-bow-r2`,
`-bow-r4`, `-bow-r5`, docs/recording.md). The bow's numbers are its model's `.qct` (`WDmg`, `ammo`), the arms' and the head's clips, and
server.dll's ConVars (`bow-cvars`); the rest is measured.

| | The donor | The lab |
| --- | --- | --- |
| In hand | `give`/`use weapon_arxcrossbow` (the classic bow; `weapon_mm_bow_*` the others) | `build.json`'s belt slot 5 (`{"bow": ".../bow_classic.mdl"}`); its key again, the weapon back; the arms play `layer_bow_idle` with the bow on `LeftHand_Bow` and the nocked arrow on `RightHand_Arrows` |
| The draw | a press at rest draws 0.12 s later (every press); `layer_bowattack_in` always plays its 2 s; a tap's arrow leaves 2.19 s after the press, a hold's as it's let go; let go and pressed again during the draw, it waits for the next release; parry pressed during the draw puts the arrow down | the same: each phase lasts its clip and a tick (`Settle`, 15 ms: the recorded phases came 0.24, 0.54, 0.51, 2.01 and 1.02 s for clips of 0.23, 0.53, 0.50, 2.00 and 1.00) |
| In the air | a press while the player is in the air or landing (`jump_out_bow`, 0.75 s) draws once it's down (three, 0.12 to 0.24 s late); a fall with the arrow drawn puts it down (twice, then a new draw) | the same: the body off the ground, and the landing clip's length after |
| The reload | with `bow_reload` attack held in `layer_bowattack_fastidle` nocks at once (`layer_bowattack_fastreload`, 1 s): shot to shot 1.29 s; without, `layer_bowattack_out` (0.69 s) lowers it, and a press then draws only if held at rest (0.03 s after) | the same: 1.30 s, and 3.0 without |
| The flight | `arrow_speed` 2100 units/s; falling 600 units/s² (586 to 606 over 15 flights); leaving 2.8° above the view (2.7° to 3.1°, views 4.6° down to 42° up; `mm_arrow_pitch_compensation`), less from 45° up (`mm_arrow_pitch_compensation_start` −45: 2.04°, 1.96°, 1.81°, 1.51° at 45°, 50°, 60°, 80°) | the same (1.34° + the view pitch's cosine from 45° up); the arrow leaves from the eye 0.03 s into the shot, along the view, and stops on the orc's hit boxes (`HitSweep`, 1 unit) or the arena |
| Its damage | (strength + `WDmg`) × `layer_bowattack_shoot`'s `HitPow` 1.5 × the hit group, ×3 on an unaware orc: 12 into the world and on aware orcs with the classic bow at strength 6 (`extra_damage` 3), 15, 16.5, 19.5 and 10.5 with the long, sniper, explosive and rope bows, 9 and 36 on unaware orcs at strengths 0 and 6; the draw's length changes nothing (2 to 6 s) | the combat model's player blow with the shoot clip and the model of the bow that shot it (R10168-4: switching or putting the bow away while it flies changes nothing): the same on every recorded arrow; the clip's `adrenalineadd` (2) a hit |
| A running orc | arrows into orcs charging the player (`bow-r4`, `bow-r5`: the player put 300 or 500 units in front of the orc, `approach`, and the arrow let go): three struck orcs moving 27 to 87 units in the 0.3 s before | through the lab's bow and impact path (R10168-5): the shot's bow drawn and let go from the donor's start along its view, and the lab orc, posed by its own clips, put on the recorded orc's origin each step, facing the archer: its hit boxes meet the arrow within 2.5 ticks of the donor's blow (the lab's 0.008 to 0.035 s later), on the donor's hit group 3, for its 12 |
| Bounces | an arrow meeting the floor at 6° to 12° bounces once, at 0.53 to 0.59 of its speed (40 arrows; less the steeper), and stops where it lands next | grazing (under 15°) the world's surface, once, ×0.56; steeper, it stops. The arena's colliders report no normal: the lab takes the plane through the hit and two rays beside it |
| The explosive bow | each impact of its arrow casts a blast (`weapon_MMDummyCaster`, `spell_fire_1`): `explosive_damage` 15 from the orc 12 units from one, none from the ten 52 units or more off | `explosive_damage` from every live orc whose feet are within `explosive_radius` (30 units) of each impact, a bounce's too, as a `fire_1` spell's damage |
| The rope bow | a rope arrow shot up into the underside of a wooden beam (near (362, -3167)) stuck and made a rope (`keyframe_ropeserver` at z 135 and 239); two into similar beams and ten into walls and overhangs made none | its arrow sticks where it stops in the world, an anchor (`RopeAnchors`); the rope hanging from it and climbing it are #10330's (R10168-2, split) |
| The zoom | `+zoom` held with the bow drawn: 101.4° to 57.8° across at 16:9 (45° at 4:3; 34.5° high) in 0.37 s, back in 0.21 s once let go; at rest nothing; the damage unchanged (`mul_damage_zoom` 1) | V held: the view narrows to 34.5° high in 0.38 s (on the tangent of the half angle) and widens in 0.2 s |
| The sway | the camera, not the arrow: the head plays `layer_bowattack_in` as it's drawn (a 5° dip up and back by 1.4 s), then the 1 s loop `layer_bow_stabilisation`, fading over 4 s (2.5° off at release left the arrow where the view was); with `stability`, `layer_bowattack_stability_in` and nothing after | the same clips: the head within 0.20° (RMS) of the donor's camera over a 6 s hold with `stability`, 0.27° without |

- **Releases the reader leaves out:** a setpos jumps the nocked arrow with the player, sometimes at an arrow's speed;
  `BowCalibration` takes a release only with its shoot clip.
- **Not measured:** a head shot (none landed), what makes a surface take a rope (one of three beams did), the bow's kicks
  (`layer_bow_kick_*`) and its fatality (`layer_bowattack_fatalitie_*`, `HitPow` 30), the freeze, poison, holy and
  mass bows' effects, `ArrowAvoidanceBaseChance` (orcs dodging arrows), the arrows' count (the lab's quiver is the
  bow's `ammo`), the reload's sway, the orc's reaction to an arrow (struck unaware it limped, `Hurtwalk`, and stayed
  unaware until it heard the player), and the explosive blast's falloff (one blast struck).
- **The recordings' arrows elsewhere:** an arrow's damage record carries the kick's bit (0x40000) with no swing on
  screen, so the kick and reaction calibrations leave arrows out by their inflictor (`Calibration/Arrows.cs`).
- **The smoke:** in the arena the slot puts the bow in hand; a tap's arrow leaves 2.2 s after it, a 3 s hold's 0.03 s
  after the release, at 2100 units/s, 2.8° above the view, and strikes the held orc's hit boxes for the model's ask;
  a parry during the draw shoots nothing; with `bow_reload` the next arrow comes 1.30 s after, without 3.0; the zoom
  narrows in 0.37 s and widens in 0.22, and not at rest; an arrow keeps the damage of the bow that shot it when another
  is taken out while it flies; an explosive arrow 20 units short of the orc takes 15 from it, 50 short nothing; a classic
  arrow grazing the floor bounces at 0.56 and stops at its next impact; a rope arrow leaves an anchor. Against the
  recordings: the donor's input (and its landings) through the lab's bow releases every arrow within 2.5 ticks; each
  flight's pitch, speed and gravity are the rules', and the lab's arrow from each start stays within 3 units and 1% of
  its way of the donor's; each arrow's damage is the model's; the arrows into running orcs, through the lab's hit boxes; the blasts against their
  radius; the bounces; the rope; the zoom's field and times, and the sway with and without `stability`. `RB_BOWTRACE=name`
  prints a recording's donor and lab release times and its bow clips, `RB_SWAYTRACE=1` the sway curves.

### NPC archery

`Npc/OrcArchery.cs` (#10169; `l01_a-v18-npc-archery` to `-6`, docs/recording.md): `combat.json`'s `orc.bow` makes the
first orc an archer, as the maps' `npc_orc_bow` (l05_a, l05_b, l07_a, l09_a: `rangeweapon` the long bow,
`additionalequipment` the cleaver); its squad-mate stays a swordsman. Read by `NpcArcheryCalibration`.

| | The donor | The lab |
| --- | --- | --- |
| Who shoots | `npc_orc_bow` only: an `npc_orc_sword` given the crossbow never shot, first or second (struck with it as a club), nor with the player out of reach | the first orc with `orc.bow` set |
| Its approach | woken, it runs at the player (95 units/s) to 471 units (most stops; 476 to 548 with the player moving), shooting on the way, and stands in `bow_attack`; no standing shot was farther than 559 units: it ran in again | the same speed and stand-off; past 559 it runs in again |
| Its arrows | nocked 1.2 s after it woke (1.12 to 1.24) and every 2.16 s; standing anew, 0.36 s after it stood (11 of 12; once 0.6, a turn first), then every 2.16 s (57 gaps); let go 1.08 s after the nock standing, 1.2 s on the move (`layer_Bow_Attack` over its run), but for two let go at 1.08 as it stopped; one nocked as it stopped is put away; faster the nearer the player, 7311 − 6.29 units/s a unit off (5140 at 345 units, 2540 at 758; 92 within 3%); the line through the player 11 units below the eye standing (crouched too), 11.5 above on the move | the same cadence, delays and speed rule (an arrow nocked running and let go as it stopped is put away instead), straight from its hand (65 units up) at where the player's eye is, less 11 or plus 11.5 |
| Its aim | every arrow let go at the player struck: 93 of 93 at 217 to 758 units, the player still, strafing at up to 206 units/s or backing off; no `bow_precision` (20) error and no lead were seen | no error: the arrow, 12 units each way, meets the player's box (16 units each way, to 8 over the eye) or the arena. The donor's strafing player was 9 to 11 units beside its arrows as they struck; the lab's flight lets him drift 21 (its arrow's start, 150 units out at its first sample, isn't modelled), which the 12 takes |
| Its damage | 5 every arrow: the orc's strength 1 and the long bow's `WDmg` 4, × `bow_attack`'s (or `layer_Bow_Attack`'s) `HitPow` 1 | the combat model's |
| To the blade | after 10 arrows standing it moves off, looses one more and rushes in (190 units/s), 33.1 and 33.3 s after it woke (and so in every 30 s stand-off that ended after its 11th); on 2 of 9 arrivals after 1 standing, 14.7 s after waking (both); out of sight (in the wooden building) at once; within `bow_kick`'s `range` (60) it kicks (2.5: `HitPow` 0.5), `drop_bow` 0.96 s after, `unsheathe_sword` 0.36 s after that, then the cleaver's attacks (8, 12, 3.2 and 6.4) | the same, the 10 or 1 rolled on arriving (2 in 9 the 1), the brief one moving off 1.3 s first as the donor's did; after its last arrow on the move it moves on 1.8 s (both ways); the kick at the clip's own speed |
| Too near | nearer than 314 units (`mm_ai_dist_surprised`) it plays `surprised` and backs off to 338 to 362 units, shooting | `surprised`, then back to 350 |
| Charmed, and down | not recorded | charmed (R10169-2), it fights as the charm sets it ("Support spells"): at its squad-mate, its arrows aimed at the mate's middle and striking it through the model; with no one to fight, it holds off and lets go nothing. Struck down in its kick or its drop (R10169-3), it lies until it respawns, as any orc; a hit or a stun then plays out before the drop or the draw goes on |

- **Not in the lab, and why:**
  - **hiding behind a prop:** `prop_physics_create` put no crate between them (12 arrows struck the crouched player,
    twice), so cover short of leaving its sight isn't recorded;
  - **the crossbow orc:** `mm_npc_create`'s `npc_orc_sword` with `weapon_arxcrossbow` never shot it; the maps' archers
    are `npc_orc_bow`.
- **Not measured:** shots past 811 units (`ThrowPrecisionMaxDist` 950) and `ThrowPrecisionNbShootToInc`'s first-shot
  error (none seen), the arrows' arc (a few units over 600: the lab's fly straight), an archer at a jumping player, its
  defence (parries, dodges) while it holds the bow, `ArcheryKickInterval` (5 s), and `mm_ai_dist_not_rush_on_seen` (400).
- **The smoke:** in an open field, an archer woken 900 units from the player lets go its first arrow 2.43 s on,
  running, stands 470 units off, lets go 10 standing 2.16 s apart, every one of its 12 striking for 5, and kicks 31.7
  s after it woke (the donor's 33.1 and 33.3) for 2.5, drops the bow 0.97 s after and draws the cleaver 1.33 s after,
  then fights with it; standing for one, it lets go 3 and kicks 13.7 s on (the donor's 14.7); the player strafing at
  206 units/s from 4 s, 7 of 7 strike; out of sight it rushes in and kicks 4.4 s on, letting go nothing; 250 units off
  it plays `surprised` and stands 350 off to shoot. Against the recordings: 93 of 93 arrows struck, each asking the
  model's 5, every delay, the first nocks (1.18 s median), the nocks on standing (0.36 s) and the 57 gaps standing
  (2.16 s), 91 speeds within 4% of the rule, the aim's medians, and 7 kicks (2.5; the drop 0.96 s and the cleaver
  1.32 s after). Round 2: its arrows are drawn as they fly (the product's arrows, with the player's: R10169-1); charmed
  with its mate 500 units off, it lets go at the mate (once in 12 s: the mate runs at it, and it backs off whenever
  it comes within 314), striking it, and none at the player, and with the mate down lets go nothing more (R10169-2); killed in its kick or its drop it lies until it respawns (R10169-3). `RB_ARCHERY=1`
  prints the donor's archers and stops.

### Telekinesis

`Magic/Telekinesis.cs` and `Interaction/CubeInteraction.cs` (#10164; `l01_a-v18-telekinesis-mass`, `-throw`, `-heavy` and
`-barrel`, docs/recording.md):

| | The donor | The lab |
| --- | --- | --- |
| The bodies | l01_a's props | l01_a's props (R10164-2): `lab.json`'s `cubes` name their models (the crates `crated_new`, 40 kg, and `cratea_new`, 300; the barrel `barrel01`, 80; two jugs `jug01`, 4), drawn as themselves, their boxes their collision's (turned as its joint), their masses their `.phy`'s |
| Gripping | attack held grips the body in view within `PlayerTelekinesisDist` (850 units); a small one (under `Small_Object_Mass`, 20 kg: the jug, bread, logs) rose in 0.27 s, a big one (the stool, table, crates) in 0.36, the barrels in 0.7 to 1.4, 32 to 67 units | E grips the arena body in view within 850 units, its tether reeling it to the hold |
| Its mana | small 4 (`ManaCost_Small_Object`), big 15 (`ManaCost_Big_Object`, to `Big_Object_Mass` 120 kg), as it grips: 25 of 25 grips | the same, from `spells.txt`; refused, nothing spent, when the mana doesn't cover it |
| Huge bodies | the 300 kg crates: nothing gripped, no mana spent (`ManaCost_Huge_Object` 9999; the skill's text: adrenaline lifts "massive objects") | refused |
| The hold | its origin a median 54 units from the eye, small, 74 big (the table, held by its foot, 98) | 54 and 74 units ahead of the eye |
| Letting go | the body stays where it was held | E drops it |
| The throw | pressed again after letting go: up to 40 kg leaves at 1444 units/s (median of 11: 1432 to 1475, the 40 kg crate 1159); the 80 kg barrels, held up to 6 s with mana full, aren't thrown: they fall from the hold at 0 to 57 units/s (6 of 7; one left at 365), the stool beside them thrown at 1464 | the mouse throws it at 1447 units/s up to 60 kg (between the 40 thrown and the 80 not; R10164-1); heavier, the throw only drops it |
| Its damage | the game settings: from `PhysicsSpeedToDamageNPCs` (70 units/s), `Physics{Small,Medium,Big}DamagesTelekinesis` (8, 15, 50) by `PhysicsWeightToDamageNPCs*` (medium from 10 kg, big from 50); the frozen orc took two hits of 8 from each of the thrown jug and log, the second contact of each impact at 37% and 47% of the first's force (its rebound) | each impact on the orc's body (swept against its hit boxes, not touching it the step before) takes the class's damage, and again for its rebound while that's fast enough (restitution 0.42, from the forces); the body bounces off at that restitution (R10164-3); one that comes back is a new impact |

- **The donor's controls:** the spell in hand (`Deploy_Spell_AIR_1`), attack held grips; let go, it stays held; pressed
  again, it's thrown. The lab has no telekinesis in hand yet, so it keeps E and the mouse.
- **Not measured:** a thrown medium or big body's damage (the hall's orc died before the stool reached it, and a
  barrel isn't thrown), where between 40 and 80 kg the throw stops, the barrel that once left at 365, what a body swung
  while held does (a held table swung into the orc took the settings' 5, `PhysicsMediumDamages`, not telekinesis'), how
  the rise depends on mass, adrenaline's NPC and huge grips (`ManaCost_Small_NPC` 15, `fearfactor`) and
  `mm_telekinesis_coeff_damage` (0).
- **The smoke:** the arena's bodies are l01_a's props with their `.phy` masses, one of each class; the jug grips for 4,
  held 54 units off, thrown at 1432 units/s; a 40 kg crate for 15; the barrel for 15, and the throw only drops it; the
  300 kg crate refused, and a crate with 10 mana; a jug thrown at the orc, frozen in place, takes 16 in two hits, as the
  donor's did. Against the recordings: every grip's cost by its model's `.phy` mass (dm-import writes it,
  docs/import.md), no mana for a press that gripped nothing, the throw speed's median within 10%, the barrels' under 100
  units/s, every small hit 8 and each throw's whole loss the lab's at its speed. `RB_TK=path` prints each grab
  (`RB_TKTRACE=1`, `RB_TKHITS=1`, `RB_TKMODEL=part:from-to`, `RB_TKNPCS=1` add traces).

### Ladders

`PlayerLadder.cs` (#10163): the arena's ladders (`lab.json`'s `ladders`: a foot against a face, a top, the way a
climber faces it; the loft in the north-east corner has one) are climbed on the Engine's climb rail
(`CharacterMovementMode.Climbing`), a vertical line the body's radius plus `mm_player_ladder_offset` (9 units) out
from the face, with server.dll's ConVar defaults (read from their registrations; l01_a has no ladder, so none is
recorded):
- **Mounting:** walking into it, within 0.35 m of the rail and looking toward it (`sv_ladderautomountdot` 0.4); or
  from its top, backing off the edge facing the ladder's way.
- **Climbing:** `mm_player_ladder_speed`, 80 units/s, up with forward and down with back. Forward climbs down
  looking more than 60° down (`mm_ladder_friction_pitch` taken so; not measured).
- **Getting off:** at the top, onto what it leads to, `mm_dist_to_exit_ladder` (5 units) past it
  (`sv_autoladderdismount` 1); at its foot climbing down, the same distance back; a jump lets go at once.
- **Getting on and off takes** `mm_time_to_exit_enter_ladder`, 1 s: an eased move onto the rail or off it, the
  controls held off meanwhile (R10163-1). The body's `Player_ladder_*` clips aren't played. Not measured against the
  donor.
- **Standing:** the ladder stands the body (R10163-3). A crouched one walking into it rises over its feet as the move
  onto the rail begins, the rail's ends are the standing body's, and crouch does nothing on the rail.
- **Drawn:** two side rails and a rung every 0.3 m, 0.5 m wide, against the face from its foot to its top
  (`LadderRules.Pieces`; drawn only, the climb is the rail's), so it's found where it's climbed (R10163-2).
- **The smoke:** the drawn ladder stands 0.05 m off its face either side of the climb (the rail 0.53 m off); got on
  walking into it in 1.02 s without climbing, 80 units/s up and down, off onto the loft in 1.02 s, on again from the
  top, off at the foot, a jump letting go; crouched into it, it stands onto the rail with its feet never below the
  floor and climbs with crouch held, no recovery needed.

Mantling is #10329 (its trigger in server.dll's movement code, 0x223F2707, not yet reproduced) and climbing ropes
#10330.

### The camera

The camera the donor draws with (Wire v9's client view, its `CViewSetup`) against the player's view (#10161;
`FirstPerson`, `HeadMotion`):
- **Where it sits:** the view model (`first_basic`) stands at the eye, turned with the camera's heading and 0.4 of its
  pitch (see "Held items"), and the camera 9 units
  above the eye and 2.1 ahead in the view's frame, turned with it (`FirstPerson.CameraOffset`), never at the eye
  itself: so on 5057 of the `movement` scenario's ticks, crouched (the eye 35 units up, not 61) as standing, and
  through every swing and kick. Not the arms' `eyes` attachment (6.8 up, 4 to 6 ahead, moving with each clip), nor its
  reference pose's. The player's camera composes it with the current eye and look whenever it's drawn, so looking
  about while the world holds turns the view (`CameraRig`, R10161-3).
- **The head:** Arkane's `player_head_mvt.mdl`, one node with 194 clips, turns the camera. It plays the clip named
  as the view model's layer (`layer_bigslash_sword_noshield_out`, `_T_out`, `layer_combo_sword_huge_noshield`, the
  parries...) at its cycle; the node's turn about the model's Z is the camera's pitch, about X its yaw, about Y its roll
  (the power attack's 6.2°, 6.2°, −1.4° at cycle 0.3, recorded 6.5°, 6.0°, −1.4°). Moves it has no clip for leave the
  camera still, as recorded: the fast attacks, and the kicks (whose head clips are `layer_sword_kick_*`).
- **Landing:** `jump_out_idle`, `_walk`, `_run` or `_sprint` (by the body's speed: under 30, 150, 230 units/s) once the
  body falls faster than `mm_player_speed_to_active_fall` (125 units/s), dipping the view 7.9° at 0.19 s.
  `player.json`'s `landingDip` scales it for comfort (1 as the donor's, 0 none; the developer shell's "Landing dip").
- **Strafing:** Source's view roll, `sv_rollangle` 3° at `sv_rollspeed` 200 units/s to the side, in proportion below
  it; the right side goes down moving right. With the sword at rest it was the whole of the camera's roll: 1.7° (RMS)
  off the view on `l01_a-v14-arena-hand`, 0.4° off this.
- **Struck:** an orc's blow that lands kicks the view (the head has no clip for it): up to its peak in 0.17 s and
  gone by 0.5 s, half a cosine each way, its size by the orc's attack, not its damage nor its `impactdir`
  (`HeadMotion.PunchOf`, from 84 blows in the hand fights, `arena-bot` and `chained-slash`): 7.8° down for most
  (`attack_top`, `_bottom`, `_KickSwing`, the turned strikes: 7.0° to 8.6°), 23.5° down for `attack_Jump`, 15° left for
  `attack_left`, 10° left for the fast attack's second blow (HitPow 0.8), 2.6° right for `attack_right`, the uppercut,
  the forward thrust and the fast attack's first blow. A replay kicks it with the donor's own blows (the followed
  orc's sequence, HitPow from the damage), since when the lab's orc lands them is the fight's comparison.
- **Grabbed:** when an orc's grab takes the hands, the head plays `player_grabed` (40.2° down at its height, as
  recorded), cut at 2.35 s where the donor's is back at the view, then `player_knocked_down` 3.12 s after the grab
  began, as the player is thrown (the three recorded grabs; the grab itself is #10185).
- **Leaning** (Z and X, the donor's `config.cfg` binds; `PlayerLean`; `l01_a-v18-lean`): `+leanleft` and `+leanright`
  put nothing in the user command; the client rolls the view 15° (`cl_leanangle`) and moves the eye 25 units to that
  side (`cl_leanmove`, the view offset itself), easing at `cl_leanspeed` (10 a second: 70% of the way in 0.12 s, in
  and out), so the camera stands 27.3 units out. Against a wall it comes back along the line from the unleaned eye to
  5 units short of the wall (`mm_player_lean_hull`): touching it, 11 units out, at that line's height; 15 units off
  it, 26.5. The lab casts a ray from the eye to the leaned camera and keeps the camera 5 units from what it meets,
  square to it (in the arena, touching a wall: 7.2 units out, the lab's body being 11.8 units across to the donor's
  16). Driven from each recorded lean's start and release, its roll is within 1° of the donor's on 1147 of 1271
  leaning ticks (the smoke). The eye's move doesn't change the sword's trace (not measured).
- **No bob:** `cl_bob` and `mm_headbobbing` are 0 and the head's walk and run clips don't play; `player.json`'s
  `headBobMetres` is 0.
- **Against the recordings** (the smoke): on the ticks where the donor's camera turned more than 1° from the view,
  the lab's within 1.5° on 1487 of 2057 (`arena-hand`, median 0.6°) and 524 of 1146 (`arena-hand2`, 1.8°), checked at
  40% and 2°; the other arena fights 1.7° to 2.7° at the median, checked within 3°; the experiments' sword and club
  fights 1.5° to 2.7°, reported. What it doesn't follow: the power attack's first ticks
  (the donor blends its head in from the charge), the kicks of attacks not measured (the ghouls', the orc's dodge
  strikes), the landing's dip a tick or two apart, and the daggers' and staff's fights (3.4° to 5.6°: the staff's
  power attack releases its head at cycle 0.4 where the lab holds it, and its camera sits 5° down at rest, which no
  clip explains), reported and not checked. In place, every tick: within 1 unit on 9424 of 10001 (`arena-hand`,
  median 0.04) and 2895 of 3413 (`arena-hand2`, 0.06), checked at 80% and 0.5 on the arena fights; the experiments
  85% to 94% (the staff's 78%). The worst are orcs' grabs: one or two ticks at a time, up to 252 units, while the
  grab carries the player (the eye and the camera read at different moments), and its own camera move
  (`mm_player_grab_camera_move_dist` 20, up to 20 units through the hands' taking; #10185). `RB_PLACE_TRACE=1`
  prints each tick more than 5 units off. `RB_CAMERA_TRACE=1` prints each tick
  compared; `RB_CAMERA=1` the head's and the `eyes` attachment's curves by clip; `RB_LEAN=path` the drawn camera
  against the eye every tick.
- **Not measured:** `mm_player_lean_eye` (72), and the head clips of moves the lab doesn't play.
- **Field of view:** 69° high (`player.json`), the donor's: 85° across at 4:3, which Source keeps high as the screen
  widens (the recorded `fov` is 101.4° across at 1280×720, the view model's the same). It was fptester's 75° before
  #10395.

### Held items

Where the hands hold what they hold (#10395), checked against the game's own screenshots (`held-look`, docs/recording.md)
and the recordings, not set by eye. The side-by-sides are kept with the screenshots, outside the repository:
`local/recordings/experiments/held-look-3/compare/` (`fp_*.png`: each first-person screenshot beside the lab posed as the
recorded view model was, with `lab.dev.arms CLIP CYCLE`; `orc_*.png`: each orc screenshot beside the mirror on that
tick):
- **The orcs' items** hang at their holder's attachment by their origin: the cleaver on `RightHand` (7), the shield on
  `LeftShield` (11), as Wire v10 streams them parented. Posed as `l01_a-v10-held` streams its orcs, the lab's cleaver
  and shield are within 1.8 cm and 0.0° of the donor's streamed transforms at 834 and 417 ticks
  (`HeldItemCalibration`; `RB_HELD=path` prints a recording's). The mirror, hanging them the same way, draws
  `held-look`'s orcs as the game's screenshots show them: the cleaver down along the leg in its idle, the shield
  (`weapon_mm_shield_orc`, the third run's second orc) on the left forearm, its painted face out. The mirror's eye camera
  doesn't follow the scenario's `setpos`/`setang` jumps, so its views look from elsewhere; the items are what compare.
  A frozen orc's (`ai_disable`) streamed items aren't updated, so `held-look`'s own are off by 80°: only fighting orcs
  measure.
- **First-person items are seated by their handle**, not hung by their origin (`FirstPerson.Seated`): the item's
  `handle` attachment is brought to the hand's attachment. Every sword's handle is a 120° turn of its axes and the
  daggers' the identity. Without one (the shields), a one-joint item's joint is brought there and turned by
  `FirstPerson.GripTurn`: the swords' handle turn, then half a turn about Y. Hung by its origin, as the server's weapon
  entity is (within 1 cm and 3° of the lab's hand attachment in the idle), the short sword pointed right, out of view at
  rest; the game draws it rising from below the frame, its tip at (905, 317) of `rb_hl_fp_ahead` (1280×720). Seated,
  its tip projects to (907, 312), and its collision hull drawn over the screenshots (`RB_HULLS=file`) lies on the
  game's blade, looking ahead and 30° down. Posed as the recorded view model was, the lab draws the power attack's
  held wind-up, a parry held and a fast attack under way as the game does (`compare/fp_*.png`; the fast attack's frame
  is known only to within its second, so its cycle is approximate).
- **The shield** (the small orc shield, `weapon_arxshield`): seated by its joint alone, it stood larger and higher than
  the game's with its bossed face to the player; with the grip turn it lies low and wide at the bottom left looking down
  and across the bottom blocking, its inner side to the player with its two rivets where the game's screenshots show
  them. Its hull's top projects within 60 px of the game's shield's top in both, and its bosses face away from the
  camera (the smoke). The other half turns either showed its face or put the rivets elsewhere.
- **The arms pitch 0.4 of the view** (`FirstPerson.ViewmodelPitch`): the recorded view model turned 0.399 of the
  client's view pitch (0.397 to 0.403 over 110 samples of `l01_a-v14-arena-hand`), its heading the view's. The camera
  takes the whole view, so looking down brings the hands and what they hold up into it.
- **The view model's clips** play as the donor's: its main sequence stays `anm_reference` and every move is an overlay
  layer (Wire v9's client layers: `layer_sword_noshield_idle` at weight 1 at rest); `first_basic` and its seven
  `$includemodel` animation files share one skeleton, so the lab's playing the layer as its main clip poses the same.
- **Landmark pairs** (R10395-2, `Sword/HeldLandmarks.cs`; docs/playtest-inspection.md, "Landmark pairs"): the grips
  are published as the overlay set `held.landmarks` and measured by the renderer on the poses it drew
  (`engine.inspect.landmarks held.landmarks`, `lab.held.landmarks`; `lab/tests/landmarks-live.mjs` checks them in a
  headless session, the smoke's test host having no renderer). The player's hand: `player.blade`, the blade along the
  knuckles' line (`Landmark.Between` the pinky's and index's bases, `LandmarkAim.Axis`, within 35°); `player.palm`, the
  palm's surface facing the grip's line and within 5 cm (`LandmarkAim.Line`); `player.guard`, the web within 11 cm of the
  guard. The orc's: `orc.guard`, and `orc.shield`, its forearm's middle within 18 cm of its shield's middle. Seated, all
  are within (blade 5°, palm 2.9 cm and 52°, guard 9.2 cm; the orc's 7.7 and 15.9 cm); hung by its origin as before
  (`lab.held.seat off`), the pairs catch the turned sword (blade 91°, palm 78°, guard 12.9 cm). The reaches are the
  donor's own holds with a little over; the blade pair is the one that tells.
- **What the landmark tooling lacked:** `HandRig` and `Landmark.Palm` assume four fingers (a palm needs index, middle and
  pinky bases); the orc's hand has three and a thumb, so its last finger stands for both ring and pinky, and its knuckle
  line and palm read 82° and 90° off on a pose the recordings confirm, so the orc's grip is judged by its guard alone. The
  palm's default depth (out of the palm) put its reference point inside a closed fist on the grip, where the angle to the
  grip's line is noise: the pairs measure from the palm's surface (`depth: 0`). Neither is filed as an Engine task: a
  three-fingered rig option and a fist-aware palm would be worth one.
- **Not done:** the bow, its arrow and other multi-joint items in the hands keep their origin (no `handle`, and no
  screenshot of them yet).

## The orc

The lab's own orc fights by its `.qct` (#10150; `Npc/OrcMind.cs`), against the donor's recorded orcs
(`OrcCalibration`: every `npc_orc_sword` that fought the player in `local/recordings`, 1700-odd attacks):

| What | Number | Source |
| --- | --- | --- |
| Health | 35 | `entity_data` `npc_health` |
| Zones | red 10, green 70, yellow 140 units | `Red_zone`, `Green_zone`, `Yellow_zone` |
| Wait between attacks (from one's end, R10151-1) | 1–4 s cool, 0–2 s berserk; wounded always cool | `AttackCoolMinTime`/`MaxTime`, `AttackBersekerMinTime`/`MaxTime`; server.dll stamps the time while the NPC attacks (+0x2DFD59) and picks the cool times for a wounded one (+0x2BBB72) |
| Cool ↔ berserk | every 7–10 s; berserk after 2 s with no player attack | `AttackRythmMinTime`/`MaxTime`, `PlayerNoAttackDelayToGoBersekerMode` |
| Each attack's travel to its strike | attack_Jump 96, attack_Forward 77, attack_right 28, KickSwing and KickBash 18–20, the uppercut, bash and fast attack 0–2 | the clip's root motion (`mstudiomovement_t`, which dm-import now writes; docs/import.md) |
| Where each attack's strike lands | its middle half over the recordings, e.g. the uppercut 34–43, the side and top swings 53–86, the lunges 24–71, the wounded ones (a weakened orc's only, #10167) 2–89 | `combat.json` `orc.attackLandings`, fitted (`OrcCalibration.Landings`; the smoke refits them) |
| How near it comes | 32 units feet to feet | two human hulls; the donor's closest attacks began 33–37 off |
| Walking in | 61 units a second | `combat.json` `orc.walkSpeed`, fitted: the donor's orcs' median between position updates (its CombatWalk's root motion would be 96; Source's navigator moves an NPC slower than the clip's feet) |

- **The loop:** it walks in (`CombatWalk`, its clip at the donor's 1.2 cycles a second, its feet at the donor's 61
  units a second) to its green zone, or until an attack it may make lands from where it stands. Between its red and yellow zones it
  holds its guard or strafes (`Combatmove_left_small`, `_right_small`; inside the red zone `_backward_small`), the
  recorded orcs strafing about four times as often as they idled. Once its wait is over it picks, at random, an attack
  whose strike lands inside that attack's window from here; nothing landing, it walks in, or, pressed nearer than any
  window allows (at its hull's 32 units, inside the uppercut's 34), steps back (`Combatmove_backward_small`, which the
  donor's orcs attacked out of) and fights on: 1.4 s from contact to its next attack (R10150-1; its wait runs from its last attack's end). Every clip but the
  walk moves it by its root motion, as the donor's entity moves.
- **Not chosen:** the turn strikes (a replay's followed orc makes them), the dodge strikes, the wounded attacks, the
  grab (#10185), the block's counter, and `attack_down_1`, a downward strike recorded from 63 to 334 units off.
- **Against the donor** (the smoke; fifteen minutes of a scripted fight, the player swinging when it is near at the
  recorded players' gaps between swings with an orc near, drawn at random from 794 of them, median 1.5 s, 46% over
  2 s, and backing off 4 m every 8 s; `OrcCalibration.PlayerSwingGaps`, R10151-5): every attack the lab made at
  least 5 times began within the donor's 10th–90th percentile of distance for it (11 of 11), the median time from one
  attack's start to the next is 3.45 s against the donor's 2.88 (within the smoke's 30%), it walks at the donor's 61
  units a second, and its walks of half a second or more end a median 114 units off, within the donor's middle half
  (56–214; R10150-2). Its wait starts once for every attack that ends, played out, dodged out of or cut short
  (R10151-4, R10152-3; the smoke checks a fixed 2 s wait both ways). The mix differs: the lab's scripted player stands its ground
  between its backing-off steps, where the recorded ones pressed in, so the uppercut leads the lab's attacks.
- **`Hold`** (`Orc.Hold`) keeps it from attacking: the smoke's still orc.

### The orc's defence

The orc parries and dodges by its `.qct` (#10151; `Npc/OrcDefence.cs`, server.dll's defence around +0x2A8968 and its
dodge test at +0x2ACB30), against every player swing a recorded orc saw start within 150 units (`DefenceCalibration`:
what the orc began within 40 ticks or before the next swing, by the swing, the orc's situation and how many swings
it had seen):

| A swing it sees | It | When |
| --- | --- | --- |
| One it may parry (the fast attacks) | parries on a 0–100 roll over its `difficulty` (60): 40% | standing or walking (the gate, +0x2A897C, skips an NPC flagged attacking, not one moving); a swing that began while it attacked or recovered it answers once free, while the blade is still live (until the swing's hit window closes): the donor's orcs parried 12 of 28 fast attacks whose start their attack ended within 0.2 s of, none of 194 later (R10151-5) |
| One that breaks parries (the power attacks) | dodges (`Dodge_left`/`_right`) on the same roll and then `base_dodge_chance` (0.5), at most once every `DodgeDelay` (15 s) | standing, walking or attacking: the dodge test doesn't ask whether it attacks, so it dodges out of its own attack |
| A kick | dodges on `dodge_on_kick_init_chance` (0.2), no delay | the same |

- **Wounded** (hurt or exhausted, below) it doesn't dodge: the recorded wounded orcs dodged 1 of 57 power attacks
  and kicks, against 13–20% otherwise. The gate in server.dll isn't pinned down.
- **Its parry** is one of `layer_parry_A`–`E` at random, never the same twice running (server.dll's SetParryLayer).
- **A dodge** moves it 75 units to the side by its root motion and can't be touched in its window, by a swing or a
  kick (#10198, R10151-3).
- **Its stamina** (R10151-2; `OrcStamina`): it starts at its `.qct`'s `stamina` (30; the `level_1` block's 50 is for
  an orc placed at that level) and nothing restores it. A parry spends `Stamina_Parry` (1), a block `Stamina_Block`
  (3), a dodge `Stamina_Dodge` (4) and a stun `Stamina_Stun` (10), while it fights and isn't exhausted
  (server.dll+0x2BF250, from the defence at +0x2E01C4, the dodge at +0x2DFF89 and the stun at +0x2C5C45). Down to
  `Stamina_Exhausted` (0) it is exhausted (+0x2BF210): the flag server.dll's stun and knockdown tests read as wounded
  (+0.3 each), its preventive dodge skips, and its waits between attacks are the cool ones. Stamina gates no parry
  or dodge. The recorded orcs the staff stunned again and again turned to their wounded sequences at full health once
  they had spent 38 and 44; three stuns exhaust the lab's.
- **Its rhythm** shapes its chances to parry: its wait runs from its attack's end (server.dll stamps NPC +0x2980
  with the time while the NPC is flagged attacking, +0x2DFD59; the donor's orcs idled a median 1.2 s between attacks,
  only 16 of 693 idles under half a second), and its dice go on from one life to the next.

Against the donor (the smoke; an hour's fight through the lab's combat, the orc attacking throughout, the scripted
player swinging in the orc's idle moments or every 1.8 s: two fast attacks, a neutral power attack (the recorded
players' were, 186 of 188), a kick; on an orc reeling from a blow, two fast attacks and a power attack 0.3 s apart;
and at every other attack's end, a fast attack as it comes free):

| Situation | Recorded | Lab |
| --- | --- | --- |
| Fast attacks, ready (standing or walking) | parried 40% of 336 | 40% of 288 |
| Fast attacks, attacking, its attack over within 0.2 s | parried 43% of 28 | 30% of 57 |
| Fast attacks, attacking, its attack over later | parried 0 of 194 | 0 of 328 |
| Fast attacks, recovering (hit, pushed, stunned, down, dodging) | parried 9% of 175 | 0 of 98 (within the tolerance) |
| Power attacks, ready (all neutral) | dodged 12%, parried 2% of 188 | dodged 15% of 132 |
| Power attacks, attacking | dodged 13% of 135 | 15% of 129 |
| Power attacks, recovering | none of 27 | none of 42 |
| Power attacks, wounded | dodged 3% of 36 | none of 84 |
| Kicks, ready | dodged 20%, parried 9% of 46 | dodged 24% of 126 |
| Kicks, attacking | dodged 14%, parried 7% of 29 | 16% of 180 |
| Fast attacks on a ready orc, its 4th–10th swing / 11th on | parried 50% of 56 / 42% of 239 | 38% of 169 / 47% of 43 |

Every situation with 25 recorded swings is compared (R10151-5), and the smoke fails if its fight gives one fewer than
30; a compared one agrees within two standard deviations of the difference between the two rates (both are samples)
plus 5 points. Thinner recorded ones are reported as uncertain: wounded fast attacks (21) and kicks (21), kicks on a
recovering orc (13), each kick count (14, 10, 22), and the directional power attacks (2 recorded). Fast attacks on an
attacking orc are compared by how soon its attack ended, since the mix is the player's timing (the recorded 10% of
239 includes turn strikes and grabs, which the lab's orc doesn't make).

- **Repeated swings:** the donor's orcs parried a ready orc's fast attacks 12% of 41 over their first three swings,
  50% of 56 over the next seven and 42% of 239 after: no growth, and the first few lower, likely the defence's
  combat-state gate (+0x2A8856, the NPC's state +0x1A5C at 3), which an orc reaches as it becomes aware (#10154; the
  lab's orc is always in combat). Kicks dodged by the orc's kick count: 1 of 14 first, 1 of 10 second, 7 of 22 after
  (`dodge_on_kick_add_chance`, 0.4 "added after every kick", isn't modelled: too few to tell from the same gate).
- **Not modelled:** the preventive dodge (`PreventiveDodgeChance` 0.2 every 15 s against one attack class, not
  identified); parries while recovering (9% of 175 recorded, none in the lab: not yet looked into, likely the same
  freed-while-the-swing-comes timing as an attack's end). The
  lab's orc carries a shield but never blocks: no recorded orc carried one.
- `Orc.Defending.Forced` makes it answer every swing one way: the smoke's checks.

### Stuns and knockdowns

A player's blow can stun the orc or knock it down (#10152; `Npc/OrcStagger.cs`), by rules read from server.dll and
the data, against every recorded player blow on an orc (`KnockdownCalibration`, `RB_KNOCK=1` prints them):

| What | Rule | Source |
| --- | --- | --- |
| Knocked down | a roll under the blow's `.qct` event's `chancetofall`, plus `WoundedFallChance` (0.3) on a wounded orc (below `life_tohurt`, 0.2 of its health); a jump attack (the player's attack type +0x27A8 at 12, set in code for the falling attack: its `.qct` event has no `AttackMove`) only when the player is `PlayerVerticalAttackHeightToMakeNPCFall` (50 units) or more below where they left the ground (R10152-1; `LabCombat.Drop`) | the melee routine, server.dll+0x407FE0–0x4080AB: the weapon's factor (+0x11BC, 1 unless `second_chance_perc` is 0, which no weapon sets) × the event's chance, sets the damage's fall flag; for a jump attack, +0x40801D compares the player's last height on the ground (+0x1950, saved as its ground trace first finds nothing below, +0x2243C63B) with its height now |
| Stunned, by a staff | every blow | the staff classes' virtual +0x704 returns true (the swords' and daggers' false), which sets the damage's stun flag |
| Stunned, otherwise | once it has taken `nbhittostun` (6) × the weapon's `NpcNbhittostunMultiplicator` (1; none sets it) hits within `delaytostun` (10 s), a roll under the event's `chancetostun` × the weapon's `stunmultiplicator` (1, the axes 2), plus `WoundedStunChance` (0.3) wounded | the NPC's stun test, server.dll+0x2AB04F–0x2AB15D |
| Falling | an Engine ragdoll (#10216, `OrcRagdoll`): eleven bodies from its pose as the last Engine call settled (rusty-engine #10222 evaluates it without a renderer), pushed 120 N·s along the blow's `impactdir` (sideways for the power attacks) 0.3 m above the chest joint, until it rests or 1.3 s have passed; with no pose yet (stepped within one call, as the calibrations do), after 0.1 s the stand-in: the pose it was struck in blends into `StandUp_<side>_firstframe` in 0.6 s (`Orc.FallSeconds`, R10152-2) | the donor's NPC drops a `prop_ragdoll`; the orc's model has no fall clip (its death clips fall forward) |
| Lying | the ragdoll at rest for `KnockedDownTime` (2 s), then it blends back in 0.4 s into the first frame of the side it lies on (its chest's front up: `back`) and, in the step the blend ends, plays `StandUp_back` (3.25 s) or `_front` (2.25 s); the stand-in lies in the blow's first frame 2 s plus 1.3 s | the orc's sequences: the donor's NPC holds the first frame while its ragdoll falls, and gets up on the side the ragdoll came to rest on (12 of 35 the other side from the first frame) |
| Its body to the player | from 0.3 s into the ragdoll's fall (halfway through the stand-in's) until it gets up, around where its pelvis lies, a box 0.6 m high around its lying body (2.1 m along its facing, 0.9 across; world-aligned, as the Engine's character obstacles are), else 1.8 m standing (`Orc.Hull`) | `StandUp_*_firstframe`'s bones span −0.9 to 1.2 m along its facing and rise 0.6 m |
| Stunned for | `hit_stunned_<side>` (the side the blow came from), then `Stunned` for `stunned_duration` (4 s), then `Stunned_out`; a hit ends it | the `.qct`; every recorded stun was ended early by a hit |
| Damage while down | ×2 getting up (`StandUp_*`), ×1.5 stunned (`CombatModel.DownMultiplier`) | measured (#10145, #10148) |

- **Jump attacks** (R10152-1): through the lab's combat, ten from 30 units up felled none and ten from 60 felled 5
  (`chancetofall` 0.6). The recordings hold one jump attack on a standing orc, after a 5-unit drop, which didn't fell
  it (`KnockdownCalibration` measures the drop from the player's take-off height).
- **Against the recordings:** 12 falls in 501 blows on a standing orc where the rule expects 17.0 (±6.4); a staff's
  blow stunned or felled 21 of 22 alert orcs that were up (idle, unaware ones 5 of 12, another path); every other
  weapon's blows stunned 2 of 436, as the six-hit rule allows (the power attack's `chancetostun` 0.8 needs the six
  hits first). Kicks felled 1 orc of 48: a kick's fall is the hazards' (#10157).
- **The lab's orc:** the smoke fells it headless, each update its own Engine call, until it has got up from both
  sides (3 blows): each lay with its head 0.06–0.11 m up, got up 3.70 s after the blow, inside the donor's 2.9–4.2 s
  (median 3.3), and no pose from its fall through the blend back into its get-up stood as it was struck (its head at
  most 0.49 m; 1.82 m before R10216-1's fix). `lab/tests/ragdoll-live.mjs` fells it 20 times in a live
  `rusty dev --headless` session: every blow dropped a ragdoll, which lay on its back to a blow from in front, its
  head and pelvis 0.27 and 0.22 m up (the donor's fell on their backs 31 of 35 times), and got up on the other side
  from the blow's first frame 9 of 20 times, the donor 12 of 35. Stepped within one call, the smoke's stand-in falls
  in 0.6 s and is up 6.68 s from the blow, inside the donor's 5.6–10.2 s (35 knockdowns, median 6.3). Its hit boxes
  and items follow the drawn bodies while it is down.
- **Not modelled:** `GlobalStunChanceMultiplier` (1.5) multiplies the stun chance when an NPC flag (+0x2270) is set,
  which isn't identified; the idle, unaware orcs' staff stuns.

### Hit reactions

A player's blow plays a reaction on the orc (#10153; `Npc/OrcReaction.cs`), by a rule fitted to every recorded player
blow on an orc (`ReactionCalibration`: what it began within 16 ticks, a reaction sequence or a hit overlay; `RB_REACT=1`
prints them). The orc's model has the table: `Hit_<side>_medium`/`_long`, `Push_<side>_medium`/`_long`,
`Hit_head_<side>_small`/`Hit_Body_<side>_small` (overlays) and `Push_Head`/`Body_<side>_small`. server.dll looks the
small ones up by index in each NPC class's sequence cache (+0x2C0F40, read through +0x2C1170).

| A blow | Plays | Recorded |
| --- | --- | --- |
| Its side | where the impact comes from: the move's `impactdirX`/`Y` (X ahead, Y to the attacker's left) turned by the attacker's facing into the orc's frame, reversed, in quarters | the side right on 295 of 337 (the misses at the quarters' edges) |
| A kick | `Push_<side>_medium`, sliding it the kick's push | 57 of 63 recorded medium pushes the same kind |
| `alwayspush` (the daggers' power attack, the rush, the Whirlwind, the bash) | `Push_<side>_long` from 10 damage (medium under 10 a hit); the bash's (under 0.5) `Push_<Head\|Body>_<side>_small` | |
| `push` (the sword's power attack) on a critical | `Push_<side>_long` | 11 of 11 criticals pushed, no other blow of it |
| A fast attack (`impactcoeff` 0.5) | the small hit as an overlay, over whatever the orc does (an attack goes on under it): the head's on a head blow or a swing to the left (`impactdirY` > 0: A, C, E), else the body's (B, D, the daggers' A); none within a second of the last small hit | 24 of 26 blows 30–60 ticks after a small hit played none, from 70 ticks they played |
| Anything else, and every blow on a stunned orc | `Hit_<side>_long` from 10 damage, else `_medium`, in its place: it cuts short an attack, a parry or a stun | medium to 9 damage, long from 12 |
| On an orc dodging, grabbing, dying, down or getting up | none | 11 blows on dodging orcs, 10 in `attack_grab` |

- **Against the recordings** (the smoke): the rule plays the donor's reaction, clip and side, on 395 of 488 recorded
  blows (81%; replays follow the recorded orc's own sequences, so this is the replays' measure). By kind: long hits
  113 of 124, head small hits 80 of 88, medium pushes 56 of 63, no reaction 70 of 102, medium hits 37 of 57, long
  pushes 21 of 31. What differs: the quarters' edges, the daggers' power attacks on idle orcs (likely unaware, #10154),
  and the staff's 16.8-damage blows on stunned orcs (medium 4 of 5 where the rule says long).
- **Wounded** (below `life_tohurt` or exhausted) it walks in `Hurtwalk` and strafes with `Wounded_combatmove_*`, as the
  recorded orcs did below a fifth of their health. Its wounded attacks (`attack_wounded_*`) aren't chosen.
- **Not read:** the selector in server.dll (the measured rule stands in for it), and the electrocuted, disarmed and
  shield hits (`Hit_electrocuted`, `Hit_Disarmed`, `Hit_Shield`).

#### Procedural reactions (an experiment)

#10382 asks whether a procedural reaction, with no reaction clips, can do what the donor's do for the player, the
recordings serving to tune it rather than to be copied. `Npc/OrcFlinch.cs` is that reaction; `combat.json`'s
`orc.flinch` holds its numbers, off by default (`"on": false`; `lab.orc.flinch on|off` switches it live).

**What the recordings say a reaction does.** `ReactionCalibration` now follows each hit or push in the orc's place to its
end: how long it was off its footing (busy), how far it moved and how far it turned. A reaction cut short by the next blow
isn't measured, also one that ends or plays again on the very tick a blow lands (R10382-3, `ReactionCalibration.FollowEnd`). Whether a blow cut the orc short went by the move, not its damage: power attacks 151 of 154, kicks 63 of
79, fast attacks rarely, and every blow on a stunned orc. How long it was out went by the damage: a medium hit's
0.6 s (to 9 damage), a long one's 1.1 s (from 12), a push's 1.1 to 1.3 s. The orcs moved a median 0.65 m (medium hits)
to 1.7 m (pushes) and turned 10° to 17°.

**The model.** A blow carries momentum, from its move: a kick's, or a swing's from its `impactcoeff` (the fast attacks'
0.5), more if it pushes (`alwayspush`, or `push` on a critical). It leans the torso, a damped spring, along the way the
impact travels (the move's `impactdir` turned by the attacker's facing, as an angle, not a quarter), a higher hit
(by its hit group) more. Past its balance (0.35 rad; 0.43 of it stunned) the orc loses its footing: its action is cut
short, it plays no reaction clip (it stands in `combatidle` under the lean), it slides along the impact's way and turns
by how far the impact runs across it (`impactdirY`), and it acts again once the lean is back under its recover share of
the balance, less the harder the damage hurt it. Below its balance it keeps on under the lean, as under the donor's small
hits. The lean is drawn as additive turns of `SK_Spine_1`–`4`, `SK_Neck` and `SK_Head` over whatever it plays
(`JointOverride.AddLocalRotation`), cleared as it stands again; the same model would lean any rig with a spine chain.

**The fit.** `FlinchCalibration.Fit` (`RB_FLINCH_FIT=1` with the smoke prints it) searches eleven of its numbers (the balance stays 0.35: the momenta scale to it) for the
fewest blows cut short wrongly and the smallest busy, slide and turn differences over every donor reaction that ran to
its end (a reaction rule that left the orc standing is off by all of them, so leaving the hard ones out doesn't pay).
Against the 495 recorded blows a reaction rule decides:

| | Cut short as the donor's | Busy off | Slide off | Turn off |
| --- | --- | --- | --- | --- |
| The authored rule (the donor's clips) | 94% | 0.03 s | 0.42 m | 15° |
| The procedural reaction | 92% | 0.14 s | 0.50 m | 11° |

(medians over the 254 donor reactions that ran to their end). The authored rule plays the donor's own clips, so its busy
time is theirs; it turns nothing, where the procedural reaction turns as the impact runs. By the donor's kind of
reaction (busy, slide and turn, medians, the donor's then the procedural one's):

| The donor's | Blows | Busy | Slide | Turn |
| --- | --- | --- | --- | --- |
| `Hit_<side>_long` | 120 | 1.12 s, 1.22 s | 1.50 m, 0.68 m | 16°, 12° |
| `Push_<side>_medium` (kicks) | 56 | 1.33 s, 1.25 s | 1.66 m, 1.73 m | 9°, 0° |
| `Hit_<side>_medium` | 50 | 0.61 s, 1.08 s | 0.63 m, 0.44 m | 17°, 5° |
| `Push_<side>_long` | 25 | 1.09 s, 1.25 s | 1.09 m, 1.42 m | 17°, 13° |

What it gets wrong: the fit leaves damage almost no say (`pain` 0.02), so a medium hit keeps the orc out about as long
as a long one (1.08 s against the donor's 0.61); a long hit slides it half as far as the donor's clips carried it; a
kick, straight on, never turns it, where the donor's turned 9°. The smoke prints this table.

Its cleaver, shield and hit boxes follow the lean: while it leans they take the pose with its pose controls.

**The arms** (R10382-2): each arm is a spring of its own, a half again quicker than the torso's, turned about the lean's
axis at its shoulder (0.6 of its turn) and elbow (0.4). A blow on an arm (hit group 4 the left, 5 the right) pushes that
arm along the impact by `armKick` (1) of the blow's momentum; any other blow, and the other arm, lag the torso by
`armInertia` (0.5) of it, against it. No arm turns past `armLimit` (0.6 rad). These three are chosen, not fitted: no
recording streams the donor's arm bones. The smoke strikes the right arm, which swings 0.34 rad to the left's 0.17, and
the lean and arms settle until their joint turns clear (2.4 s).

**In replays** (R10382-1): replays follow the recorded orc. With `orc.flinch.on` the reaction governs it: each of the
lab's blows leans it over its recorded pose, and one the model says cuts it short takes it over (`Orc.Follow`): it plays
no recorded sequence, overlay or blow, slides and turns as the model has it, and follows the recording again once it has
its footing back (or once the recording follows another orc or its orc dies). The report sets each blow against the
donor's orc (`CombatReplay.DonorReaction`: a hit or push begun from the blow's own tick to 16 ticks on that it wasn't
already playing, R10382-4) and, where both were cut short and the lab's ran its course, its busy time, slide and turn
against the donor's. The smoke replays `l01_a-v14-arena-hand` both ways: off, the orc follows the recording throughout;
on, its 7 blows lean the orc, 6 cut short as the donor's orc was, and the one the model cut short that ran its course
held it 1.26 s and slid it 1.73 m (the model's 1.25 s and 1.73 m; the donor's orc 1.40 s and 2.28 m), with no blow while
taken over; the other was given back when the recording followed another orc.

**Not done.** Knockdowns stay the ragdoll's; a partial-physics knockdown would want rusty-engine #10383's powered
ragdolls. The arms' numbers aren't measured.

### Perception

An orc can be unaware of the player (#10154; `Npc/OrcPerception.cs`; `combat.json` `orc.spawnUnaware`, off by
default, so the arena's orc walks in to fight). Its alert level is server.dll's (NPC +0x36EC, which Wire v18 streams
every tick; docs/wire.md, "Alertness"): 0 unaware, 4 fighting. Measured in scenarios on l01_a and l05_a with the
level's other NPCs removed (docs/recording.md; `PerceptionCalibration`; the l05_a recordings and `squad-probe` in
`local/perception`, apart from the fights the other calibrations read):

| The player | The recorded orcs | The lab |
| --- | --- | --- |
| In l01_a, standing still in plain view 300, 600, 1000 units in front, or 150 behind, for 8 s | never noticed | never notices: l01_a is in the settings' `nostealth` list, where it doesn't see (`combat.json` `orc.map`, l01_a by default, the recordings' arena) |
| In l05_a, standing still in front of an idle orc (R10154-2) | seen at 289 to 499 units within 16° of its facing; not at 546 to 1698 straight ahead, nor 297 to 598 units 30° to 61° off, for 1.5 s or more | sees within 540 units and 27° either side, after 0.4 s |
| Crouched, walking up from 1000 in front to 558, or from 800 behind to 458 | never noticed | hears a crouched step (`SoundPresenceDist_Inaudible`) at 100 units |
| Running (180 units a second) or sprinting up from behind | alert 1, 3, then 4: heard 1.4 s after the player came within 500 units (at 260 and 119 units, both times) | hears a run within `SoundPresenceDist_Big` (500) after `delayhear` (1 s) and 0.4 s of thinking; a walk within `_Average` (300) |
| Striking it | 0 to 4 on the blow's tick (3 of 3) | a blow makes it fight at once |

- **Sight** (R10154-2): the `.qct`'s `maxsightdistancequiet` (1500) and `fieldofviewquiet` (90) times the settings'
  `NPCRangeOfViewRatioWhenStealth` (0.6) fit the measured edges: 1500 × 0.6 × 0.6 = 540 units, 90 × 0.6 = 54°. How
  server.dll combines them isn't read, nor the player's light (`stealth_multiplier_hdr`, shadows), which the lab
  doesn't have. The delay is `delaysee` (0) and the AI's think (0.4 s): the one l05_a run that didn't hide the player
  saw an orc notice it 0.57 s after it appeared. The other runs hid the player (`notarget`) while placing it, out of
  the Wire's view, so they say whether an orc noticed, not when.
- **Unaware**, it idles where it stands (`idle_0`–`idle_3`), facing as it was, and doesn't defend (the defence's gate,
  +0x2A8856, needs the NPC's combat state); noticing the player it plays `surprised`, then fights.
- **Its damage:** a blow on an unaware orc (alert 0, state 1) asks the settings' `mul_damages_Npc_Not_alerted` ×3
  (`CombatModel`, now from the lab's own orc): the recorded sneak attacks asked 36 = 3 × 12 (power) and 16.8 = 3 ×
  5.6 (fast), as the model asks them.
- **The backstab** readies on an unaware orc's turned back (it idles, never on guard) and kills it, as the daggers'
  does (#10148).
- **Against the recordings** (the smoke): the lab's rule, run on each recorded approach's path, gaits and the orc's
  facing, notices when the donor's orc did (within 0.6 s where the Wire shows when it could), and not otherwise: 47
  of 47, 34 of them in l05_a (17 seen, 17 not). A scenario that froze its orcs (`ai_disable` in its log:
  `telekinesis-throw`) is left out, and so is an approach while a spell was out (its `fearfactor` alerts NPCs: the
  `fireball` scenario's orcs rose as the ball came; not modelled, #10165) as one the player struck is.
- **Calls for help** (R10154-1, `call-l05`): two spawned orcs facing the same way, the second beside or beyond the
  first, put in one squad (Source's `SetSquad` input, which Dark Messiah handles). An orc that saw the player brought its
  squad-mate 300 units off 2.4 s later (searching from 1.6 s), and one 600 off never; one struck from behind brought
  no one, squad or not (five pairs, 300 to 1000 apart, in l01_a and l05_a). A mate with no squad 300 off came 5.9 s
  later, likely by its own senses as the fight moved. The lab: an orc that sees the player calls its squad-mates within
  `SquadAwarnessDist` (500), who fight 2.4 s later (`OrcPerceptionData.CallDelay`), skipping `surprised`.
  `call4helpdist` (700) and `ouchcall4helpdist` didn't show: no blow called, and 600 off didn't come.
- **The second orc** (`combat.json` `orc.squadMate`, a place, or null): the first orc's squad-mate. It fights on its
  own, sees the player's swings and defends as the first does, and its blows land on the player. The player's blows
  land on the orc they reach (R10154-3): a swing on the one its sweep meets first along the ray, a kick or the Whirlwind
  on the nearest in reach, the backstab on the one whose back it readied on (`LabCombat.Victim`). The smoke checks it
  through the lab's combat in l05_a: it answers its mate's sighting 2.42 s after, not 600 units off, and not a blow
  from behind; a fast attack and a kick at it hurt it, not the first orc 20 m off.
- **The archer** (`combat.json` `orc.bow`, a bow's model path, or null): the first orc an archer with that bow, the
  long bow as the maps' `npc_orc_bow` (#10169; "NPC archery"). Its squad-mate stays a swordsman.
- **Factions** (`mm_relationship.txt`): orcs like orcs and hate the rest, the player, guards, goblins and ghouls
  included.

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
Both strikes now begin with the donor's: 8 of 8. And for 0.15 s after a jump press, the donor's feet rising count as in the air (R10159-2): its body leaves the
ground 2 to 5 ticks after the press, the lab's after its `jumpDelay` (5), and a charge begun in between was the lab's on
the ground (`arena-hand2` at 1898.8 s: the donor's power attack, the lab's fast attack).

A scenario's `setpos` teleports the donor; where it raises the donor (the drops from 200 units), the replay raises
the lab's body as far (`IReplayBody.Lift`), to fall as the donor did.
- **Movement:** commands run at 1000 forward or sideways, about 1400 on a diagonal, and 150 back (the donor's
  `cl_backspeed`). The replay scales each axis by its full key (#10159). The replayed body passes the arena's loose props
  (they aren't the donor's level, #10164). In the fights the lab's body walks into its own
  arena's walls, which the donor's level hasn't: those cases are left out with that evidence, the rest compared ("The body's movement").
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

`content/lab.json` holds the arena: native blockout, 24 m square with 4 m walls, two pillars, a low wall, a loft with
its ladder, and l01_a's props: two crates, a barrel, two jugs and a big crate.

**Why blockout:** the l01_a gate courtyard is about this size, and blockout is quick to change.
- **The imported level:** it would need its BSP collision imported as well; today the import has only its drawable faces. That's a later step.
- **The props:** l01_a's own models (#10164, R10164-2), a cube entry's `model`: drawn as themselves, their boxes their
  collision's, their masses their `.phy`'s; an entry without one is a coloured cube of its `size` and `mass`.

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
    Magic/                 PlayerMana, Telekinesis (its rules; Interaction/CubeInteraction grips and throws), Fireball (its rules and flight; LabCombat casts), AttackSpells (lightning, freeze, inferno; LabCombat casts)
    Ranged/Bow.cs          the bow: its rules, its draw and reload (LabBow), its arrows' flight (LabArrow); LabCombat shoots and strikes
    Calibration/           SwordCalibration against recordings
    Replay/                InputTimeline (a v12 recording's input and orcs), CombatReplay (the run and its report)
    Player/ Scene/ Time/ Developer/ Ui/ Interaction/   from rusty-fptester (below); Player/CharacterBuild is the lab's
  src/ui/                  HUD, pause menu, developer shell
  tests/DarkMessiah.Lab.Smoke/
shared/DarkMessiah.Shared/ ModelInfo (attachments, hit boxes, hulls, clips) and HitSweep, used by the lab and the mirror
```

**The Engine pin** is the repository's, in the root `Directory.Build.props`. The mirror, the lab, `shared/` and `dm-combat` all build on it.

**Drawing:** the lab publishes its whole scene each update, and the orc's and the arms' items aren't joint-attached children. They are placed at the holder's sampled joint pose times the attachment, the same pose the hit tests use; while the orc is a ragdoll, its drawn pose.

## Developer tooling

- **Commands:**
  - `lab.inspect` and `lab.fight` read the lab and the fight;
  - `lab.orc.spawn` and `lab.orc.place x y z` spawn and place the orc;
  - `lab.orc.hold on|off` keeps the orc from acting, `lab.orc.flinch on|off` has the orcs react procedurally or by the authored clips (see "Hit reactions"), `lab.orc.react fast|power|kick DEGREES` strikes the orc with a blow that takes no health from DEGREES about it and reports how it reacted, `lab.orc.pose CLIP CYCLE YAW` freezes it in a clip, `lab.dev.arms CLIP CYCLE` (`off 0` to stop) holds the first-person arms in one, `lab.held.landmarks` reads the grips' landmark pairs and `lab.held.seat on|off` seats first-person items by their handle or hangs them by their origin as before #10395, `lab.orc.knockdown DEGREES SIDEWAYS` fells it with a blow from the player's side turned DEGREES (SIDEWAYS 1 for a power attack's push), and `lab.orc.ragdoll` reads its ragdoll and the knockdowns so far;
  - `lab.god` toggles god mode;
  - `lab.spell SPELL` puts `spells.txt`'s SPELL in hand as its belt slot would (`water_1`, the wizard eye, casts at once);
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
- `Content/Authored`;
- `Scene/`: `LabScene`, `LabDefinition`, `LabGeometry`;
- `src/ui/`: `main.js`, `pause.js`, `developer.js`.

**Adapted:**
- `LabProduct` (from `FpTesterProduct`): the fight replaces the action model's use;
- `Interaction/CubeInteraction`: telekinesis' mana, reach, hold and throw, and thrown bodies' damage, replace the
  authored reach, hold and throw (#10164);
- `Developer/LabDeveloper` and `LabTunables`: fight commands and the Sword and Orc groups replace the action ones;
- `Ui/LabHud` and `LabObservation`: the fight's readouts;
- `main.js`: the HUD and help;
- `LabScene.Publish`: takes the fight's appearances.

**Left out:**
- fptester's action model (`Actions/`, `content/actions.json`). Dark Messiah's moves are timed by their own clips and events, so a generic windup/commit/recovery doesn't fit them.
- `Combat/LabCombat` was replaced.

## Not done yet

- **Orc AI:** its rhythm, zones, walk, attack choice, parries and dodges are the donor's (see "The orc" and "The
  orc's defence"); its reactions are one hit and one push clip (#10153), and it doesn't grab (#10185).
- **Pose timing** (#10098): NPC poses between streamed states.
- **Physics:**
  - a kick that throws the orc into walls;
  - body pushes. fptester notes the Engine has no bump-to-push yet.
- **First person:**
  - the first-person camera follows the eye, not `SK_Camera_Eyes`'s animation (Dark Messiah's swing camera motion);
  - the disarm (above), the behead's trigger, and the quick draw after an orc's grab;
  - the daggers' backstab and the staff's breakneck (see "Weapons");
  - the shield's block reactions (`parry_sword_shield_A`–`C`) and its breaking (see "The shield").
- **Stamina:** what empty stamina does to the sprint (the lab stops it at 0). `Stamina_Parry`, `Stamina_Block` and
  `Stamina_Dodge` are in the settings' AI block: the player's shield blocks and bashes cost nothing (see "The shield").
