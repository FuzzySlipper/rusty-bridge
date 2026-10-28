# The Import: donor files to Rusty Engine assets

`dm-import` (`import/DarkMessiah.Import.Tool`) turns Dark Messiah's maps and
studio models into GLBs a Rusty Engine product loads, keyed by the model
path the Wire reports. What it can't read yet is listed in the Den document
`rusty-bridge/known-limitations`.

## Running it

The importer reads the game's VPKs. Copy them once into the gitignored
`local/donor/vpks` (5.5 GB; about 25 s over the LAN):

```
scp -r 'den-win11:C:/Program Files (x86)/Steam/steamapps/common/Dark Messiah Might and Magic Single Player/vpks' local/donor/
```

Then, from the repository:

```
dotnet run --project import/DarkMessiah.Import.Tool -- level local/recordings/l01_a-parrybot.rbrec local/recordings
dotnet run --project import/DarkMessiah.Import.Tool -- model models/npc/orc/npc_orc.mdl
dotnet run --project import/DarkMessiah.Import.Tool -- map maps/l01_a.bsp
dotnet run --project import/DarkMessiah.Import.Tool -- tables
```

`level` imports the recordings' map, every model path in their model
tables, and their sprites, and writes `local/import/<map>.assets.json`.
- **Several recordings:** it takes one or more recordings, or folders of them. They must all be of one map; it refuses a mix. The import covers the union of their model paths, so every recording of the map replays on it. The provenance lists them all (`recordings`).
- **The recording that travels:** the first is copied into `local/import/recordings/` and named as `recording`/`recordingFile`, so the folder is the whole content root for the mirror ([mirror.md](mirror.md)). Put `l01_a-parrybot` first: the mirror smoke seeks into its fight.
- **Clip check:** it checks each model's clip order against the sequence names the recordings carry and reports any difference.
- **Gameplay tables:** it also copies them (below), and indexes the item models.
- **Scale:** for l01_a with the 19 local recordings it finishes in about 11 s. All 358 Wire model paths resolve (158 of them invisible brush models), into 197 GLBs and 321 MB, and 132 of 132 clip lists match. `--vpks DIR` and `--out DIR` override
the defaults (`local/donor/vpks`, falling back to the Steam install, and
`local/import`).

`rusty asset check <file.glb>` runs the Engine's own admission on a file.
Every GLB the level import writes passes it.

## Output

Paths mirror donor paths, lowercased:

| Donor | GLB |
| --- | --- |
| `models/npc/orc/npc_orc.mdl` | `models/npc/orc/npc_orc.glb` |
| `maps/l01_a.bsp`, `*0` | `maps/l01_a/world.glb` |
| `*12` (brush model 12) | `maps/l01_a/brush_12.glb` |
| `materials/sprites/glow_torch.vmt` | `materials/sprites/glow_torch.png` |

`<map>.assets.json` is a [portable asset descriptor](../../rusty-engine/docs/portable-assets.md)
(schemaVersion 1). Each asset id is the Wire's model path exactly as
reported, case included, so a Wire model index joins to its asset by string.

- **Invisible brush models:** brush models with no drawable face (triggers,
  clips, tool brushes) get no GLB. The descriptor's `provenance.invisible`
  lists them; there is nothing to draw.

### Characters

A studio model is one skinned GLB:

- **Skeleton:** every bone is a joint node, holding its rest transform.
- **Mesh:** body part 0 of each body group, one primitive per material.
- **Clips:** every sequence, from the model and its `$includemodel` files,
  becomes a clip named by its Source sequence name.
- **Clip order:** clips follow the numbering `m_nSequence` uses, which is
  the order `StudioModel.SequenceNames` gives (own sequences first, then
  each include depth first, skipping names already present). The primary
  GLB's `asset.extras.sequences` repeats that list.
- **Clip extras:** each clip's `extras` holds the activity, fps, frame
  count, looping, delta, blend grid and fades. When the sequence has
  animation events, `events` lists them as `[cycle, id]` pairs. Those are
  Arkane's ids: 2186 opens a hit window and 2190 closes it, and 2102 and
  2139 are the struck parts. A few events with a non-finite cycle are left out.
- **Blends:** a sequence that blends a grid of animations plays its centre (the neutral pose of an aim matrix, the
  forward one of a `move_yaw` walk). A one-row grid also gets a clip per column, `NAME@COLUMN`, after every sequence's
  (#10394): `player_basic`'s `Walk@0` to `Walk@8` are south, south-east and so on round to south again, `move_yaw` −180
  to 180. Each blended clip's extras name what it blends over (`parameters`: the pose parameter, and its value at the
  first and last column), and each column's which sequence it's of (`cellOf`), its column (`cell`) and its value (`at`).
  Grids of more than one row still play their centre only.
  When the sequence's animation has root motion (Source's `mstudiomovement_t`, which
  it keeps out of the root bone), `movement` lists `[cycle, x, y, angle]` at each
  piece's end, cumulative, in the model's frame (Source units, X forward, Y left;
  degrees). The orc's `attack_Jump` carries it 120 units, 100 by its strike; the
  combat lab moves NPCs by it (#10150).
- **Game data:** the level import writes each model's `.qct` beside its GLB,
  under the same lowercased name, and copies the gameplay tables (below).
  The combat lab reads Arkane's numbers from them at run time ([lab.md](lab.md)).
- **Attachments:** weapon grips and other attachments are in
  `asset.extras.attachments`, each a joint name and a local matrix.
- **Hit boxes:** the first hitbox set (`default`, the one traces test) is
  in `asset.extras.hitboxes`, in its order (a trace's hitbox index counts
  them): each a joint name, its hit group, and a box (`center`,
  `halfExtents`) in the joint's frame, in metres.
- **Collision:** a model whose `.phy` has one solid (a weapon, shield,
  helmet or prop) has it in `asset.extras.collision`: the root joint's name
  and the solid's convex pieces (`points` in that joint's frame, metres;
  `triangles` over them). Traces meet items by these. Characters' ragdoll
  solids, one per bone, aren't written. With them, the solid's `mass` (kg)
  and `surfaceprop` from the `.phy`'s text (studiomdl's `solid {...}`):
  telekinesis sorts bodies by mass (#10164; `barrel01` 80, `jug01` 4,
  `cratea_new` 300).

Every clip goes into the model's one GLB. The Engine bounds what a GLB's
clips decode to, 16,000,000 keyframes and 32,000,000 values (rusty-engine
#10072), not how many there are. l01_a's largest, the guard, is 715 clips
in 24 MB, and the orc 334 clips in 10 MB. Should a model ever exceed the
budget, the importer keeps the clips that fit, in sequence order, and warns
about the rest. A model with no triangles is written as a skeleton with
clips, which the Engine admits without a mesh.

### Gameplay tables

`level` copies the game's tables a product reads at run time, and `tables` refreshes just them in an existing
import (about 3 s), adding the item models to each `<map>.assets.json` there. `provenance.tables` records what was
copied: the scripts, the item folders, how many item tables and models, and anything the donor lacked.
- **Scripts**, under their own paths:

  | Script | What it holds |
  | --- | --- |
  | `mm_game_settings.txt` | melee, stamina, AI and stealth settings |
  | `spells.txt` | `spelldatas`: each spell's mana cost, effect, duration and recovery (`air_1` is telekinesis) |
  | `spells_entityspellcaster.txt` | spell entities, such as traps |
  | `mm_skills_infos.txt` | skills: requirements, maximum level, each level's cost and modules |
  | `mm_relationship.txt` | factions |
  | `propdata.txt`, `surfaceproperties*.txt` | physics and materials |
  | `kb_act.lst` | the bindable actions |
  | `plugin_animations.txt` | the animation plugin table |

- **Item tables:** every `.qct` under `models/items/weapons`, `armors`, `provisions` and `magic` (166 in this
  donor): each item's `entity_data` (`WDmg`, `speed_attack`, `rangeattack`, a bow's `ammo`), shown in the
  inventory as `prop_data`.
- **Item models:** every weapon model (swords, daggers, staffs, axes, clubs, bows, arrows, quivers) and every whole
  shield, as GLBs with their `.qct` beside them: 74 here. Shields' break chunks aren't imported. The four
  `models/items/weapons/animations/*_animations.mdl` are clips only, written as skeletons.

The lab reads them through `DonorContent`: `Tables.Spells`/`Spell`, `Tables.Skills`, and `Number` for an item's
`entity_data` (docs/lab.md).

### Maps

Each BSP model is one static GLB with a primitive per material. Its
material name is the donor material path (`generic/l01/l01_oldstone02`),
and its base texture is embedded at up to 512 px. Displacements are
tessellated. Brush models keep their entity-local space: place them at
the entity's Wire origin and angles.

## Space and scale

glTF is right-handed, Y up, with the front of an asset facing +Z, in metres.
Source is right-handed, Z up, with X forward and Y left, in inches. The
importer maps:

| Source | glTF |
| --- | --- |
| +X (forward) | +Z |
| +Y (left) | +X |
| +Z (up) | +Y |
| 1 unit | 0.0254 m |

This mapping is a rotation, so handedness is kept. Every GLB records it in
`asset.extras.convention`. To place a Wire entity:

- **Position:** `(y, z, x) * 0.0254` from the Wire origin `(x, y, z)`.
- **Rotation:** build the Source rotation from the Wire's pitch, yaw and
  roll (yaw about Z, then pitch about Y, then roll about X). Then swap the
  quaternion's axes the same way: `(qx, qy, qz, qw)` becomes
  `(qy, qz, qx, qw)`. A yaw alone is the same angle about glTF +Y.

A model needs no turn of its own: Source models face +X, so imported ones
face +Z. This was checked by rendering the scratch product's view from the
player's Wire eye position, and an orc placed at a Wire origin and yaw, in
headless Blender and in the Engine.

## Format notes

What the donor's files turned out to be. Offsets are bytes.

**Maps**

- **Version:** l01_a reports VBSP version `0x00040014`, i.e. 20 in the
  low word and 4 in the high word. The high word marks two Arkane changes:
  - **texinfo is 96 bytes, not 72.** Six extra floats (zero in l01_a) sit
    between the texture axes (0–31) and the lightmap axes (now at 56–87).
    Flags are at 88 and texdata at 92.
  - **dmodel_t is 52 bytes, not 48.** An extra int (zero) comes before
    `headnode`, so `firstface` and `numfaces` move to 44 and 48.
- **Unchanged lumps:** faces (56 bytes), texdata (32), dispinfo (176) and
  disp verts (20).
- **Unknown lumps:** lumps 29–31, 36, 37, 39, 41, 42, 45–47, 49, 54 and 55–56
  are filled, and their contents are unknown. The game lump (35, static
  props) is not read.
- **Patched materials:** some material references are `patch` materials
  in the map's PAKFILE (a zip). The importer searches the pak first while a
  map imports.
- **Brush model bounds:** brush models are stored around their own origin,
  e.g. `*1` spans ±70 units.
- **Degenerate triangles:** about 1,700 of the world's 60,000 fan
  triangles are slivers that the Engine's degenerate-triangle test
  refuses, and one refusal fails the whole GLB. They are dropped using the
  same test (`Triangles.IsDegenerate`).

**Models (MDL v44, Episode One layout)**

- **Record sizes:** bones 216 bytes, animdesc 100, seqdesc 212, attachment
  92, model 148, mesh 116, hitbox set 12, hitbox 68 (`numhitboxsets` at
  +172, `hitboxsetindex` +176).
- **Collision (.phy):** a 16-byte header (size, id, solid count at +8,
  checksum), then each solid as its byte size and a `VPHY` header (28 bytes
  from the id) before IVP's compact surface. That surface is 48 bytes, with
  the ledge tree's offset at +32. The tree's leaves (28-byte nodes: right
  child offset, ledge offset) name the compact ledges, one convex piece
  each. A ledge is 16 bytes (point offset, size and flags, triangle count
  at +12), then 16-byte triangles of three edges whose low 16 bits are
  start points. Points are 16 bytes in IVP's metres and axes: Source
  (x, y, z) is IVP (-y, -z, x). A solid is in model space, so an item's is
  turned from its bone 0. VVD is version 4 (48-byte vertices) and VTX is
  version 7: mesh header 9 bytes, strip group 25, strip 27, all triangle
  lists.
- **No external animation:** there are no `.ani` files (no animation
  blocks), and the animdesc section fields are zero (no sections).
- **Animation record flags:** only RAWROT (Quaternion48), RAWROT|RAWPOS,
  ANIMROT, and ANIMROT|ANIMPOS occur. Records are `byte bone, byte flags,
  short next`.
- **Bind pose vs rest pose:** the orc's VVD bind pose is turned 90° from
  its skeleton's rest pose (its arms lie along Source X). `poseToBone`
  accounts for it, so inverse bind matrices come straight from
  `poseToBone`. Never rebuild them from the rest pose.
- **Empty sequences:** some sequences are weighted to no bone and play an
  all-zero animation (`gesture_arms_*`, `posture_*`; 14 of the orc's).
  They still get a clip, so every sequence name resolves: one rotation
  channel holds the first bone at rest, and `extras.empty` is true.
- **Delta sequences** (aim and look layers, `layer_*`) are composed onto
  the rest pose:
  - "post" layers (flag 0x10) apply after the rest pose, as Source's
    `QuaternionMA` does;
  - others apply before it, as `QuaternionSM` does.

  The clip's `extras.delta` says which clips are deltas.
- **Weight lists:** a weight list's zero-weight bones get no channel.
- **Constant rest translations:** a translation channel that stays at the
  bone's rest is dropped. This keeps a 54-bone clip near 57 channels, so
  the 64-clip limit binds before the channel limit does.
- **Texture search:** materials search every `$cdmaterials` folder in
  order. The folder strings carry their own trailing separators
  (`models\NPC_Orc\`), so paths are normalised (`\` to `/`, `//` and
  `./` collapsed).

**Textures**

- **Versions and formats:** VTF 7.0–7.2. Formats seen are DXT1 (2,119),
  DXT5 (1,857), BGR888, RGBA16161616F, BGRA8888, UV88 and UVWQ8888.
- **Decoding:** the importer decodes the colour formats to PNG. The HDR
  format is clamped, not tone-mapped, and cube maps are skipped.
- **Alpha:** alpha becomes glTF `MASK` only for `$alphatest`, and `BLEND`
  only for `$translucent`. Most base textures carry a specular or env-map
  mask in alpha.
