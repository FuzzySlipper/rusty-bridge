# Rusty Bridge: handover design

Rusty Bridge runs an old game as the live simulation source while a Rusty
Engine product draws it. Authority then moves from the old game to Rusty one
piece at a time, until nothing is left on the old side. The first donor is
Dark Messiah of Might and Magic.

> Bootstrap from the donor. Hand over by channel. Tune against the stream.

This document covers the structure, the authority model, the handover order,
and what Rusty Engine has to add upstream. It is a design, not a record of
what exists: as of 2026-10-17 nothing here has been built.

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
| Tap | Per donor | Inside the donor process (32-bit C++) | Reads simulation and presentation state every tick and applies input and write-backs from Rusty. |
| Wire | Generic | Shared memory between processes ([wire.md](wire.md)) | Versioned snapshots, events, input and write-back messages, timestamped by donor tick. |
| Frontend | Generic core, per-donor mapping | C# Rusty Engine product | Takes snapshots in, maps donor IDs to admitted assets, presents them, and owns whichever channels have been handed over. |
| Recorder | Generic | Frontend side | Saves Wire traffic to disk for scrubbing, stepping and offline tuning. |

Generic means "has no donor names in it". Everything starts in the Dark
Messiah tree. Code moves into a generic layer only once a second donor needs
it.

### The asset join

A Source entity names its model by an index into the precache string table.
That string is the model's path in the VPK, which is also the source path the
Import records for the GLB. That path is the join key between Wire and the
asset catalog. Brush entities (doors, breakables, movers) refer to sub-models
of the map (`*N`). The Import exports each one as its own mesh under that key.

Animations join the same way: by the Source sequence name, which the Import
keeps as the GLB clip name.

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
   magnitude and direction, time to rest, hit timing, damage values.
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

## Channels

"Donor reads it" asks whether the donor's own simulation depends on the
channel. If it doesn't, handing the channel over needs no write-back. That
answer drives the order in the next section.

| Channel | Source side | Donor reads it | Write-back when Rusty owns it | Engine mechanism today | Phase |
| --- | --- | --- | --- | --- | --- |
| Static world (map geometry, lighting, collision) | Assets | n/a | None | GLB import, bound world collision | 0 |
| Entity transforms | Server | Yes | Per moved entity (only once Rusty moves them) | Object placement, set every update | 0 (Donor) |
| Character animation pose | Server (sequence, cycle, pose params, layers) | Mostly no; hitboxes follow it on the server | None while the donor keeps hit detection | `AnimationPlaybackRequest` with `NormalizedTime`/`Speed`/`Weight`; `JointOverride` | 1 |
| First-person weapon model and camera | Client | No | None | Viewmodel fitting, camera control | 1 |
| Effects, decals, sound, screen feedback | Client (some server events) | No | None | Presentation services, recorded audio | 1 |
| Death ragdolls | Client in stock Source (verify for Dark Messiah) | No | None | Ragdoll layer with blend; `ReadRagdoll` | 2 |
| Physics props (kickable, breakable) | Server (Havok) | Yes (AI, triggers, damage) | Prop transforms and velocities every tick | Dynamics worlds | 3 |
| Player movement | Client prediction plus server | Yes | Player position, velocity and stance every tick | Character controller | 3 |
| Melee hit detection and damage | Server | Yes | Hit and damage events | Collision queries | 4 |
| Kick, adrenaline, combat rules | Server | Yes | Effects on entities | Product code | 4 |
| AI decisions and navigation | Server | Yes | Become Rusty actors; the donor's copies are suppressed | Pathfinding | 5 |
| Scripts, quests, inventory, saves | Server | Yes | Whole-world state | Persistence | 5 |

## Handover order

The order follows dependencies, not difficulty: hand over first what the
donor doesn't read.

**Phase 0: Mirror.** Every channel is in Donor mode. The Import produces GLBs,
the Tap streams entity transforms and model keys, and the Frontend places
static meshes where the donor says. The combat stepper is built here:
Recorder plus a timeline viewer over Wire traffic. This phase alone answers
the "what makes the combat good" question, because everything about a swing
can be watched frame by frame.

**Phase 1: Presentation.** Character animation is driven from the stream
(clip by sequence name, time from cycle). Then the first-person weapon,
camera, effects and sound move to Rusty, because the donor's simulation never
reads them. This is where the combat feel gets rebuilt, measured against
recorded donor presentation.

**Phase 2: Ragdolls.** If Dark Messiah keeps death ragdolls on the client as
stock Source does, the server never reads them. That makes them the first
physics channel that can go to Rusty with no write-back. Shadow mode works
with today's API: run a Rusty ragdoll at blend 0 while the donor's pose is
applied as overrides, and compare. Then raise the blend.

**Phase 3: Write-back channels.** Physics props and player movement. From
here on the donor still reads things Rusty owns, so the Tap has to force
donor state from Rusty every tick: teleport props, set velocities, override
the player's position. This is the first phase where the Tap writes into the
simulation and doesn't just read it.

**Phase 4: Combat rules.** Hit detection, damage, kicks and adrenaline move
to Rusty. The donor keeps AI, which now reacts to hits Rusty decided.

**Phase 5: The rest.** AI, scripts, quests and saves. Expect the hardest
part here. With most of the world owned by Rusty, keeping a mostly-overridden
donor consistent costs more than it gives back. At some point the remaining
channels are re-implemented without shadowing, and the donor is retired from
the live path. It stays as an oracle for recorded segments.

## Time

- Snapshots carry the donor tick number. Read the tick interval from the
  donor; don't assume Source's defaults.
- The Frontend keeps a short buffer of snapshots and interpolates between
  them. Don't copy the stock client's 100 ms interpolation delay; one tick
  plus transport is enough on shared memory. *Measured 2026-10-17:* NPC
  transforms only change every 8 ticks (120 ms think interval) and the
  player's every 2, so NPCs need interpolation across their own update
  cadence, not across one tick.
- The local player is the exception. Take the donor client's predicted state
  for the player, not the server's, or every input lags by a tick or more.
- Stepping in the stepper means fixing the donor's frame rate and holding its
  frame from the Tap. Scrubbing reads the Recorder, not a donor replay: the
  donor's Havok simulation shouldn't be assumed deterministic.

## Upstream asks for Rusty Engine

These are based on reading the current SDK and docs (`skeleton-poses.md`,
`ragdolls.md`, `multiplayer-sessions.md`, and the generated contracts).

### Already enough

- **Clip playback at a given time.** `AnimationPlaybackRequest` takes
  `NormalizedTime`, `Speed`, `Weight` and `Restart`. Speed 0 plus the donor's
  cycle every update pins a clip to the stream.
- **Absolute pose correction.** `JointOverride` in `Local`, `Model` or
  `World` space with its own weight places joints wherever the donor says,
  over or instead of clips.
- **Reading back the drawn pose.** `ReportJoints` with `ReadJointPose` gives
  Rusty's actual pose, for comparing against the donor.
- **A ragdoll authority dial.** The ragdoll layer's blend mixes the
  simulated bodies over the pose below them, and blend 0 leaves the pose
  unchanged while the bodies keep simulating. That's Shadow mode for
  ragdolls, already.
- **Reproducible physics.** Deterministic world stepping makes offline
  fitting against recorded segments possible.

### Needed

1. **Externally driven transforms with interpolation (Phase 0).** The engine
   adds no entity replication (`multiplayer-sessions.md`). The bridge needs a
   timestamped snapshot buffer: transforms sampled at a render time between
   two ticks, with optional smoothing when a correction arrives. This is the
   main ask, and multiplayer could use it too. It belongs upstream, not in
   every product.
2. **Layered or full-pose input (Phase 1).** Source blends several sequences
   at once: base, gesture layers, and aim blends driven by pose parameters.
   That needs either several clips sampled at explicit times and weights in
   one instance, or a single call that sets a whole local pose array. Doing it
   with one `JointOverride` per joint every update works, but costs too much
   per character.
3. **Bodies that follow external targets (Phase 2–3).** A Dynamics body
   (and a ragdoll bone) that tracks a target transform from outside, the way
   a kinematic body would, and reports how far its own simulation would have
   gone. Props and ragdolls need this to run in Shadow mode and to switch
   authority without a pop.
4. **Switching authority without a pop (Phase 2 onward).** Moving a channel
   between modes should blend over a few updates, not snap. The ragdoll layer
   already works this way. The same blend-in, blend-out treatment is wanted
   for object transforms and dynamics bodies.

What stays out of the engine: donor names, the Wire format, divergence
metrics and the Recorder. Those belong to the bridge.

## Risks and open questions

- **Server plugins.** Source supports loading server plugins, and the game
  ships `serverplugin_empty.dll`. Whether Arkane's build still loads them is
  the first thing to test. If it doesn't, the Tap needs DLL injection.
- **Client-side state.** Source 2006 has no supported client plugin
  interface. Reading predicted player state, the weapon model and client
  ragdolls means hooking `client.dll`, which is the most fragile part of the
  Tap.
- **Format variants.** Arkane modified Source's map and model formats. Stock
  Source parsers will need patches, and the size of that job is unknown
  until the first files are parsed.
- **Hidden donor.** `shaderapiempty.dll` ships with the game, which suggests
  it can run without rendering. Unverified.
- **The last 20%.** Phase 3 onward requires a donor kept consistent with
  Rusty-owned state every tick. The cost grows with every channel handed
  over. Plan for Phase 5 to be a re-implementation, not a handover.
- **Distribution.** Donor assets stay in `local/`, as in the sibling
  products. The bridge needs the user's own installed copy and never ships
  donor content.

## First steps

1. Confirm a server plugin loads in Dark Messiah and can list entities with
   their model paths (done).
2. Stream transforms and model keys over shared memory to a C# reader
   (done: [wire.md](wire.md)).
3. Record a fight and build a scrubbable timeline (the combat stepper)
   (first version done: [recording.md](recording.md)).
4. Import one map and the characters in that fight to GLB, and mirror the
   recording in a Frontend product with placeholder lighting.
