# The Frontend mirror

`mirror/` is a C# Rusty Engine product that replays a recorded Dark Messiah
fight (design Phase 0). It draws l01_a and every entity in the recording
from the assets dm-import made. Characters hold their streamed sequence at
the streamed cycle, and weapons, shields and helmets hang from their
holders' hands. It plays, pauses and steps by donor tick, like the
stepper. What it can't do yet is in the Den document
`rusty-bridge/known-limitations`.

## Running it

The mirror's content is `local/import`, which isn't tracked. Build it
first, as `docs/import.md` describes:

```
dotnet run --project import/DarkMessiah.Import.Tool -- level local/recordings/l01_a-parrybot.rbrec
```

`level` copies the recording into `local/import/recordings/` and names it in
`l01_a.assets.json`, so the folder holds everything the mirror reads.
Staging stops with that command when the folder has no index.

Then, from `mirror/`:

```
rusty install
rusty dev
```

(`rusty dev start --headless --live-debug` runs it in the background for
harnesses.) The pair is pinned in `mirror/Directory.Build.props`, separately
from the Wire and import tools, which don't need the Engine.

**Controls:**

| Key | Does |
| --- | --- |
| Space | Play or pause. Playing from the end starts over. |
| ← / → | Pause and step one donor tick. |
| ↑ / ↓ | Replay speed, ×0.1 to ×4. |
| C | Camera: behind the player, the player's eye, or overhead. |

**Live-debug commands** (`/__rusty/product/runtime/debug/execute`):

| Command | Does |
| --- | --- |
| `mirror.status` | Tick, playback, entities drawn, clips sampled, items held, and any model or clip the import lacks. |
| `mirror.step N` | Step N ticks. |
| `mirror.seek TICK` | Seek to a tick. |
| `mirror.toggle` | Play or pause. |
| `mirror.camera chase\|eye\|overview` | Pick the camera. |
| `mirror.hits [class]` | Damage records as `tick:attacker>victim`, to jump to blows. |
| `mirror.held` | Each live held item, its inferred holder and whether it hangs from it. |

**Smoke run:** `dotnet run --project mirror/tests/DarkMessiah.Mirror.Smoke`.
- The replay checks need nothing.
- When `local/import` exists, it also runs the product on the real pinned
  Engine through `EngineTestHost`: one second of play, a seek into the parry
  bot's fight with the orc's weapons in hand, steps, and twenty seconds of
  play with no refused scene change.
- It exits non-zero on a failed check.

## How it's built

```
mirror/src/DarkMessiah.Mirror/
  MirrorProduct.cs          entry: wires the pieces, input, status projection
  MirrorDebugCommands.cs    live-debug commands
  Replay/                   no Engine types; the smoke run checks these directly
    RecordedFight.cs        recording → frame clock, name tables, one track per entity
    EntityTrack.cs          an entity's updates, sampled across its last two
    ReplayClock.cs          donor-time clock: play, pause, step, speed
    HeldItems.cs            Dark Messiah's classes; who holds each weapon
  Scene/                    Engine resources
    LevelAssets.cs          <map>.assets.json → models opened on first use
    EntityViews.cs          tracks → appearances, sampled clips, joint attachments
    ReplayCamera.cs         chase, eye and overview cameras
mirror/src/ui/main.js       the status line (plain JS, no build step)
mirror/tests/DarkMessiah.Mirror.Smoke/
```

It follows rusty-hotel's shape:
- one product project;
- views that own their Engine resources and release them in `Dispose`;
- a plain JS UI that only draws a status projection;
- debug commands in their own module;
- a smoke exe on `EngineTestHost`.

It deliberately leaves out:
- the multi-project kit and ruleset layering of rusty-dagger;
- rusty-doom's Nx/TypeScript tooling and service wrappers;
- a product-side event or command framework.

Donor knowledge (class names, attachment names) sits in `DonorClasses`
(`Replay/HeldItems.cs`) and nowhere else. It stays there until a second
donor needs the same shape (design "Scope").

### Assets join by Wire string

- **Models:** `LevelAssets` reads the asset index (asset id = the Wire's
  model path) and opens a model the first time an entity draws it.
  - The world (`maps/l01_a.bsp`) is drawn once, at the origin.
  - A brush model (`*N`) is drawn at its entity's transform.
  - Ids listed as invisible, and sprite textures, are known but not drawn.
- **Attachments:** read from the GLB's `asset.extras`. Only its JSON chunk
  is read.
- **Clips:** a sequence index becomes a clip name through the recording's
  own sequence names, which are the clip names the import wrote.

### Time and interpolation

- **The clock:** it runs on donor curtime, not ticks, at a chosen speed.
- **Tracks:** each entity is a track of the states it changed to.
  - NPCs change every 8 ticks and the player every 2.
  - A track is identified by edict index and serial. The same key can come
    back after its entity left, so a key names a track only together with a
    frame.
- **Sampling:**
  - An entity is drawn moving from its second-to-last update to its last,
    over the gap between them. That gap is capped at 0.25 s, so a door that
    sat still for a minute opens at its own pace.
  - This uses only updates already received, so it works on a live Wire
    too, at the cost of one update interval of delay per entity.
  - Within a sequence, the cycle runs forward through a wrap. A new
    sequence is taken as starting at the earlier update.
- **Animation:** each animated entity holds its clip with the Engine's
  `Sample` playback at the interpolated cycle.
- **No slow-motion:** a recording has no wall clock. The donor's hit
  slow-motion shows in the stream as fewer ticks per wall second, so it
  replays at full speed here. Task #10062 covers slow motion and time scale.

### Held items

The Wire doesn't carry owners yet (#10074), so `HeldItems` infers them:
- **From damage:** an item the damage hook named as the weapon belongs to
  that record's attacker.
- **From proximity:** otherwise, an item belongs to the character whose hand
  height it stays within 64 units of for at least half its updates.

An item hangs from its holder while the holder is live and the item is
within 160 units of the holder's origin; otherwise it's drawn where it was
streamed. The attachment comes from the class: `LeftShield` for shields,
`Helmet` for helmets, `RightHand` otherwise. The child is published with
the attachment's local transform and a `MeshJointAttachment` on the
attachment's joint.

### Publishing

Every update sends one `PublishChanges` holding only what changed.

- **Children re-sent whole:** an upserted child carries its attachment
  again. An upsert without one returns the child to its parent's root.
- **Leaving holders:** when a holder leaves, its children are re-published
  loose in the same batch. The Engine refuses a batch that leaves a child
  hanging from a removed parent (`CSHARP_VISUAL_SNAPSHOT: MissingParent`);
  it doesn't tear the child down.
- **New holders:** a holder that appears in this batch takes its items from
  the next one.
- **Refused batches:** a refused batch changes nothing. Views keep their
  state, and leaving views retry their removal next update.
- **Release order:** appearances are disposed only after a batch without
  them was accepted.

## Findings

- **One GLB per model:** since pair 0.1.0-dev.cd66acc30485 (rusty-engine
  #10072) a model's GLB holds all its clips. The clip packs and their
  `@inplace.N` motion-root anchors that earlier pairs needed are gone.
- **Cost:** the parry-bot fight draws about 300 entities and samples about
  230 clips each update, at 60 updates a second. Interpolating in product
  C# was enough for Phase 0; design ask 1 isn't blocking yet.
