# Rusty Bridge: handover design

Rusty Bridge runs an old game as the live simulation source while a Rusty
Engine product draws it. Authority then moves from the old game to Rusty one
piece at a time, until nothing is left on the old side. The first donor is
Dark Messiah of Might and Magic.

> Bootstrap from the donor. Hand over by channel. Tune against the stream.

This document covers the structure, the authority model, the handover order,
and what Rusty Engine has to add upstream. It's a design: it says what
should be built and why. What already exists is described in
[wire.md](wire.md), [recording.md](recording.md) and the HANDOFF. The design
was revised on 2026-10-17 after the first Tap, Wire and combat stepper had
run against the game. Where a claim rests on a measurement, it says so.

## Scope: one repository for every donor

Rusty Bridge is meant to bridge more than one old game eventually. Each
donor would get its own Tap, Import and per-donor mapping in this
repository, not a repository of its own. Dark Messiah is the only target
today, and nothing is built for a second donor ahead of time.

The expectation is that shared tooling emerges from use. Some pieces are
already mostly donor-neutral:

- the Wire's ring and seqlock;
- the Recorder and the combat stepper;
- divergence measurement;
- the Source-format readers (VPK, MDL sequences), for other Source-engine
  donors.

When a second donor needs one of them, it moves into a generic layer then,
shaped by two real consumers. A speculative framework now would have one.

## Why a bridge, not a conversion

The sibling products (`rusty-dagger`, `rusty-crawler`, `rusty-underworld`,
`rusty-dungeon`, `rusty-doom`) are offline conversions. An `X.Import` tool
reads the donor's formats into `local/`, and a ruleset re-implements the game
in C#. Nothing is playable until the ruleset is.

A bridge reuses the same asset path but leaves the ruleset slot filled by
the running donor. The game is playable through the new frontend from the
first day. Every Rusty system is then written against a live reference: the
donor shows, tick by tick, what the system is supposed to do.

In the house formula, *Engine guarantees, Kit shapes, Ruleset decides,
Bundle assembles, Host launches*, the donor process is the ruleset at the
start. By the end, a compiled C# ruleset has replaced it. The bridge stays
on as a compatibility oracle for that ruleset.

## Structure

| Piece | Owner | Lives where | Responsibility |
| --- | --- | --- | --- |
| Import | Per donor | Offline tool, writes to gitignored `local/` | Donor formats to GLB and descriptors: maps, models, rigs, clips, textures, sounds. Keys each asset by its donor path. |
| Tap | Per donor | Inside the donor process (32-bit C++ server plugin) | Reads simulation state every tick, and catches decisions where the donor makes them (hooks on damage, defense and parry outcomes). Later applies input and write-backs from Rusty. |
| Wire | Generic | A file-backed shared-memory ring next to the Tap ([wire.md](wire.md)) | Per-tick snapshots plus the events of that tick, timestamped by donor tick. Later: input and write-back messages. |
| Frontend | Generic core, per-donor mapping | C# Rusty Engine product | Takes snapshots in, maps donor IDs to admitted assets, presents them, and owns whichever channels have been handed over. |
| Recorder | Generic | A separate C# tool reading the Wire (`rbwire record`) | Saves Wire traffic to disk for scrubbing, stepping and offline tuning. It doesn't need the Frontend running. |

Generic means "has no donor names in it". Everything starts in the Dark
Messiah tree. Code moves into a generic layer only once a second donor needs
it.

### Snapshots and events

Both kinds of data are first-class on the Wire, and the difference matters:

- **Snapshots are for state:** where things are, their health, what they're
  animating.
- **Events are for decisions:** a hit, a block, a parry.

Inferring events from snapshot differences turned out to be wrong in ways
that matter for combat:

- Dark Messiah removes an NPC in the tick it dies, so the killing blow never
  shows as a health change.
- A blocked blow changes nothing in any snapshot.
- Several hits can land in one tick.

So the Tap hooks the donor where it decides, and streams the decision with
its inputs: attacker, weapon, struck entity, trace, damage type, verdict.
Every snapshot-derived event in the viewer is a fallback for older
recordings.

### The asset join

A Source entity names its model by an index into the precache string table.
That string is the model's path in the VPK, which is also the source path the
Import records for the GLB. That path is the join key between Wire and the
asset catalog. Brush entities (doors, breakables, movers) refer to sub-models
of the map (`*N`). The Import exports each one as its own mesh under that key.

Animations join by Source sequence name, which the Import keeps as the GLB
clip name. The Wire carries the sequence index (`m_nSequence`). That index
counts the model's own sequences, then each `$includemodel`'s, depth first,
skipping names already seen. The Import and the reader must build that same
list. `StudioModel.SequenceNames` does it from the VPKs; the orc model has
333 sequences, mostly from `npc_orc_animations_combat.mdl`.

**Entity identity.** An edict index can be reused by a new entity within a
level. The Wire should carry the edict's serial number with the index, so
the Frontend and the Recorder don't have to guess a reuse from a change of
class or model.

**Weapons and armour are entities of their own.** On the server they follow
their holder's hand: each orc cleaver travelled about 6,000 units in 40 s of
fighting. The Frontend should draw them as joint attachments driven by the
holder's pose. The streamed weapon transform is then the reference for
measuring divergence, not the thing that places the model.

## Authority model

Authority is decided per **channel**, not per entity or per system, and each
channel is in exactly one mode:

| Mode | Simulates | Drawn from | Rusty's part |
| --- | --- | --- | --- |
| **Donor** | Donor | Donor stream | Forced into sync with the stream every update. |
| **Shadow** | Donor and Rusty, side by side | Donor stream | Runs its own version from the same inputs. Divergence is measured, not shown. |
| **Rusty** | Rusty (the donor's copy is overridden or ignored) | Rusty | Authoritative. Writes back to the donor whatever the donor still reads. |

Shadow is the important mode. It's where the "tune against the stream" idea
turns into an engineering loop:

1. Run a Rusty system in shadow from the inputs the donor saw.
2. Measure how far it drifts from the donor: joint angle error, impulse
   magnitude and direction, time to rest, hit timing, damage values, and
   for combat decisions the verdict itself.
3. Change the Rusty implementation or its tuning until the drift is
   acceptable. Divergence is a test metric that can sit in CI against
   recorded segments.
4. Flip the channel to Rusty.

Rusty Engine physics worlds step deterministically: the same history and
inputs give the same result. So a recorded donor segment can be replayed into
a fresh world as often as needed, and parameters can be fitted automatically
by minimizing divergence. Ragdoll joint limits and damping, kick impulses and
swing timings are all candidates.

A channel can drop back from Rusty to Shadow or Donor at any time. That's
how regressions get diagnosed.

### Decisions hand over through their hooks

Where the Tap catches a decision with a detour, the same detour is the
handover point:

- **Shadow:** Rusty computes its own verdict from the captured inputs, and
  the mismatch is the divergence. Each blow gives one exact comparison.
- **Rusty:** the detour returns Rusty's verdict, or rewrites the damage
  record, instead of the donor's.

That makes write-back for decisions a return value, not a state correction.
This is far cheaper than forcing physics state, and it's why combat
decisions move earlier in the handover order below.

## Channels

"Donor reads it" asks whether the donor's own simulation depends on the
channel. If it doesn't, handing the channel over needs no write-back. That
answer drives the order in the next section.

| Channel | Source side | Donor reads it | Write-back when Rusty owns it | Engine mechanism today | Phase |
| --- | --- | --- | --- | --- | --- |
| Static world (map geometry, lighting, collision) | Assets | n/a | None | GLB import, bound world collision | 0 |
| Entity transforms | Server | Yes | Per moved entity (only once Rusty moves them) | Object placement, set every update | 0 (Donor) |
| Character animation pose | Server: main sequence, cycle, playback rate; overlay layers (gestures, parries) not yet streamed | Yes: melee traces start from the weapons, which follow the hands | None while the donor keeps its own animation for hit detection | `AnimationPlaybackRequest` with a `Sample` command (normalized point in one clip); `JointOverride` | 1 |
| Held weapons and armour | Server entities following the holder's hand | Yes (they are what a swing traces, and what a block strikes) | None while the donor keeps hit detection | Joint attachments, `HandRig` | 1 |
| First-person weapon model and camera | Client | No | None | Viewmodel fitting, camera control | 1 |
| Effects, decals, sound, screen feedback, hit slow-motion | Client (some server events; slow motion is requested on the server and streamed as Wire v7 slow-time records) | No | None | Presentation services, recorded audio | 1 |
| Combat decisions: damage, defense verdicts, player parry outcomes | Server, at hooked decision points ([wire.md](wire.md)) | Yes | The verdict or damage, returned from the existing detours | Product code | 2 |
| Kick, adrenaline, other combat rules | Server | Yes | Effects on entities (kicks also push physics) | Product code | 2–3 |
| Ragdolls (deaths and knockdowns) | **Server** in Dark Messiah: a separate `prop_ragdoll` entity, swapped in and out (see below) | Yes: they collide, take hits, deal impact damage and can be kicked or grabbed | Bone transforms every tick | `DynamicsRagdollRequest`, `ApplyRagdollImpulse`, `ReadRagdoll`, ragdoll layer blend; needs bodies that follow external targets | 3 |
| Physics props (kickable, breakable) | Server (Havok) | Yes (AI, triggers, damage) | Prop transforms and velocities every tick | Dynamics worlds | 3 |
| Player movement | Client prediction plus server | Yes | Player position, velocity and stance every tick | Character controller | 3 |
| AI decisions and navigation | Server | Yes | Become Rusty actors; the donor's copies are suppressed | Pathfinding | 5 |
| Scripts, quests, inventory, saves | Server | Yes | Whole-world state | Persistence | 5 |

### Ragdolls: swaps, not blends

Dark Messiah moves characters between animation and physics by swapping
entities. It never blends the two.

- **Death.** The NPC is removed in the tick it dies, and a server
  `prop_ragdoll` with its model takes its place. The bridge sees this in
  every recorded kill.
- **Knockdown.** A living NPC "falls in ragdoll" when a kick or attack, ice,
  physics or the void calls for it (`TASK_FALL_IN_RAGDOLL`). Recorded in
  `l01_a-parrybot.rbrec` at 10.68 s:
  - Orc #401, mid-`attack_right`, is knocked down. In the same tick a
    separate `prop_ragdoll` with its model appears.
  - The orc entity stays alive, parked on `StandUp_front_firstframe`, the
    first frame of a get-up clip.
  - The ragdoll falls on its own, and is removed at 12.12 s.
  - The orc is then exactly where the ragdoll came to rest ("Repositionning
    NPC for Unragdoll"), and it plays the get-up from that first frame.
  - `SCHED_MM_STAND_UP_BACK` or `_FRONT` is picked by which way the body
    landed, so the get-up begins near the lying pose.
- **Hit reactions** are animated sequences (`Hit_*`, `Push_*`, `stunned_*`).
  There is no physics-driven "active ragdoll".

The handover looks continuous because of the content: get-up clips that
start lying down, chosen front or back. Nothing blends at runtime on the
server. Whether the client smooths the swap visually is unverified.

**Ragdolls are gameplay objects, not set dressing.** They:

- take slash, big-slash and kick hits (`OnRagdoll_HitBySlash` /
  `HitByBigSlash` / `HitByKick`);
- turn impact speed into damage (`mm_ragdoll_vel_to_damage`);
- can be grabbed by telekinesis and pulled by ragdoll magnets.

An NPC can also be knocked down again during its get-up ("Fall in ragdoll
during stand up").

**For Rusty:**

- **Parity needs no animation/physics blending and no physics motors.**
  It needs:
  - spawning a ragdoll from the current pose with its motion
    (`DynamicsRagdollRequest`);
  - impulses at bones (`ApplyRagdollImpulse`);
  - reading the settled pose (`ReadRagdoll`);
  - product logic to place the character at the ragdoll and start the
    matching get-up clip.
- **Rusty can do better than the donor:** fade the ragdoll layer's blend
  from 1 to 0 over a few frames while the get-up clip starts, instead of
  snapping. That's a visual crossfade with today's API.
- **The gap is interaction.** Because the donor reads its ragdolls, a Rusty
  ragdoll in Shadow mode, or handing authority back, needs bodies that
  follow external targets (upstream ask 3). That keeps ragdolls in
  Phase 3.

## Handover order

The order follows the cost of write-back, not difficulty. Hand over first
what the donor doesn't read, then what can be written back as a single
value, and last what needs state forced every tick.

**Phase 0: Mirror.** Every channel is in Donor mode. The Import produces GLBs,
the Tap streams snapshots and events, and the Frontend places static meshes
where the donor says. The combat stepper belongs here: the Recorder plus a
timeline viewer over Wire traffic. It already shows every swing, hit, block
and parry tick by tick ([recording.md](recording.md)). The Import
([import.md](import.md)) and the Frontend mirror ([mirror.md](mirror.md))
draw a recorded fight in Rusty.

**Phase 1: Presentation.** Character animation is driven from the stream:
the main clip by sequence name, sampled at the streamed cycle, and the
overlay layers once the Wire carries them. Held weapons hang from the
holder's hand joints. Then the first-person weapon, camera, effects, sound
and the hit slow-motion move to Rusty, because the donor's simulation never
reads them. This is where the combat feel gets rebuilt, measured against
recorded donor presentation.

**Phase 2: Combat decisions.** Damage amounts, NPC defense verdicts and
player parry outcomes run in Shadow first, from the inputs the hooks
already capture, and then hand over through those hooks. The donor keeps
physics, animation and AI, and reacts to verdicts Rusty decided. Recorded
fights make this testable offline: every blow in a recording is a test
case ([combat.md](combat.md) scores the Shadow model that way).

What the decisions turned out to be, so far:

- A player parry is broken by any attack whose weapon flags have bit 0x10
  (every bash), and holds against any other.
- An NPC parries when the blow strikes its weapon.
- An NPC shield-blocks when the blow strikes its off-hand item, or when its
  shield is up and the attack comes from the front.

**Phase 3: Physics write-back.** Physics props, server-side ragdolls (deaths
and knockdowns),
kick impulses and player movement. From here on the donor still reads
physics state Rusty owns, so the Tap has to force donor state from Rusty
every tick: teleport props and ragdoll bones, set velocities, override the
player's position. This is the first phase where the Tap writes simulation
state, not just return values.

**Phase 4: The rest of the rules.** Adrenaline, the remaining combat rules,
and whatever Phase 2 left in donor hands.

**Phase 5: The rest.** AI, scripts, quests and saves. Expect the hardest
part here. With most of the world owned by Rusty, keeping a mostly-overridden
donor consistent costs more than it gives back. At some point the remaining
channels are re-implemented without shadowing, and the donor is retired from
the live path. It stays as an oracle for recorded segments.

## Time

- Snapshots carry the donor tick number and curtime. Read the tick interval
  from the donor; don't assume Source's defaults. On l01_a it is 15 ms.
- **Each entity has its own update cadence.** Measured: NPC state changes
  only every 8 ticks (their 120 ms think interval). That covers
  transform, health, sequence and cycle alike. The player's state changes
  every 2 ticks.
  - The Frontend interpolates each entity across its own last two updates,
    not across one tick.
  - Velocities measured from the stream need a window at least that long.
- **Donor time is not wall time** when slow motion runs: the game frame
  sends `mm_host_timescale` (docs/wire.md, "Slow motion"). The Frontend's
  presentation clock follows donor curtime, so slow motion arrives for free
  in Donor mode. In Rusty mode it becomes something Rusty produces. Since
  Wire v7 each frame carries the tap's wall clock. In about 10 minutes of
  recorded l01_a fights game time kept to 1.00× wall time: the shipped
  settings make critical hits ask for ×1 for 0 s.
- Events carry the tick they happened in and are delivered with the next
  snapshot. Don't copy the stock client's 100 ms interpolation delay. One
  NPC update interval plus transport is the floor on shared memory.
- The local player is the exception. Take the donor client's predicted state
  for the player, not the server's, or every input lags by a tick or more.
- Stepping in the stepper means fixing the donor's frame rate and holding its
  frame from the Tap. Scrubbing reads the Recorder, not a donor replay: the
  donor's Havok simulation shouldn't be assumed deterministic.

## Upstream asks for Rusty Engine

These were checked against the installed SDK pair (0.1.0-dev.f0e905c8749b)
and the engine docs (`skeleton-poses.md`, `ragdolls.md`,
`multiplayer-sessions.md`, `csharp-lifecycle.md`) on 2026-10-17.

### Already enough

- **Clip playback at a given time.** `AnimationPlaybackRequest` has a
  `Sample` command that holds a normalized point in a clip. Sending the
  streamed `m_flCycle` each update pins the main clip to the donor.
- **Absolute pose correction.** `JointOverride` in `Local`, `Model` or
  `World` space with its own weight places joints wherever the donor says,
  over or instead of clips.
- **Reading back the drawn pose.** `ReportJoints` with `ReadJointPose` gives
  Rusty's actual pose, for comparing against the donor.
- **Held items.** Joint attachments and `HandRig` can hang weapons and
  shields from the hands.
- **A ragdoll authority dial.** The ragdoll layer's blend mixes the
  simulated bodies over the pose below them, and blend 0 leaves the pose
  unchanged while the bodies keep simulating. That's Shadow mode for
  ragdolls, already. The same dial covers knockdown and get-up: spawn the
  ragdoll from the current pose (`DynamicsRagdollRequest`), push it at a
  bone (`ApplyRagdollImpulse`), and fade it out over the get-up clip.
- **Reproducible physics.** Deterministic world stepping makes offline
  fitting against recorded segments possible.

### Needed

1. **Externally driven transforms with interpolation (Phase 0).** The engine
   adds no entity replication (`multiplayer-sessions.md`). The bridge needs a
   timestamped snapshot buffer that samples transforms at a render time
   between updates. Each entity keeps its own update cadence (every 8 ticks
   for NPCs, every 2 for the player), and the presentation clock follows a
   source time that can slow down (the donor's hit slow-motion). Optional
   smoothing applies when a correction arrives. Multiplayer could use it
   too. The mirror (#10060) does this in product C# for about 300
   entities, so it isn't blocking Phase 0 ([mirror.md](mirror.md)). Ask
   again if a second product needs the same buffer.
2. **Layered pose input (Phase 1).** Playback is one clip per instance.
   Source blends a main sequence with overlay layers. Dark Messiah plays its
   parries and attack gestures on overlays (`layer_parry_A` to `_E`,
   `layer_attack_right`, `layer_block`). This needs either several clips
   sampled at explicit times and weights in one instance, or one call that
   sets a whole local pose array. One `JointOverride` per joint every update
   works, but costs too much per character. Filed as rusty-engine #10073,
   which shipped clip layers (`IAnimationService.SetLayers`) in pair
   0.1.0-dev.a64d20f624c7. The mirror still draws the main sequence only.
3. **Bodies that follow external targets (Phase 3).** A Dynamics body (and a
   ragdoll bone) that tracks a target transform from outside, the way a
   kinematic body would, and reports how far its own simulation would have
   gone. Props need this, and so do Dark Messiah's server-side ragdolls
   (deaths and knockdowns), to run in Shadow mode and to switch authority
   without a pop.
4. **Switching authority without a pop (Phase 1 onward).** Moving a channel
   between modes should blend over a few updates, not snap. The ragdoll layer
   already works this way. The same blend-in, blend-out treatment is wanted
   for object transforms and dynamics bodies. It's also needed when an NPC
   is replaced by its ragdoll in a single tick.

What stays out of the engine: donor names, the Wire format, divergence
metrics, the Recorder and the stepper. Those belong to the bridge.

## Risks and open questions

- **Hooks are tied to one binary.** The damage, defense and parry hooks
  patch fixed addresses in the current Steam build of `server.dll`.
  Each checks its bytes before patching and stays off if they differ. A
  different build needs the addresses found again: the routes to them
  (strings, RTTI, datadesc tables) are written down in
  [wire.md](wire.md).
- **Client-side state.** Source 2006 has no supported client plugin
  interface. Server-side covers more than expected: damage, defense, parry
  outcomes and slow-motion parameters are all server-side. What remains on
  the client is the predicted player, the camera and view angles, and the
  viewmodel. Reading them means hooking `client.dll`, still the most fragile
  part of the Tap. Input doesn't fill the gap: the parry bot turns the view
  with keys bound to `+left`/`+right`, since relative mouse motion through
  the playtest agent doesn't turn it.
- **Overlay layers.** The parry and gesture layers are on the Wire since v6;
  the mirror doesn't draw them yet.
- **Format variants.** Arkane modified Source's formats, but the parts read
  so far are stock: MDL headers are version 44 with Episode One offsets,
  sequence descriptors are 212 bytes, and VPKs are versions 1 and 2. The
  runtime differs more than the files: the networkable vtable order, float
  health (`m_fHealth`), a `DPT_VectorXY` send-prop type, and an extended
  `CTakeDamageInfo`. Expect map (BSP) and model-geometry surprises when the
  Import starts.
- **Hidden donor.** `shaderapiempty.dll` ships with the game, which suggests
  it can run without rendering. Unverified. For now the donor needs the
  shared Windows box's console session, and it changes the desktop
  resolution.
- **The last 20%.** Phase 3 onward requires a donor kept consistent with
  Rusty-owned state every tick. The cost grows with every channel handed
  over. Plan for Phase 5 to be a re-implementation, not a handover.
- **Distribution.** Donor assets stay in `local/`, as in the sibling
  products, and so do recordings, which are derived from the donor. The
  bridge needs the user's own installed copy and never ships donor content.

## First steps

1. Confirm a server plugin loads in Dark Messiah and can list entities with
   their model paths (done).
2. Stream transforms and model keys over shared memory to a C# reader
   (done: [wire.md](wire.md)).
3. Record a fight and build a scrubbable timeline, the combat stepper. Done,
   with health, sequences, damage, blocks and parries
   ([recording.md](recording.md)).
4. Stream the overlay animation layers and the edict serial number.
5. Import one map and the characters in that fight to GLB, and mirror the
   recording in a Frontend product with placeholder lighting.
6. Run the combat decisions in Shadow against recorded fights: a C#
   verdict and damage model, scored blow by blow against the donor.
