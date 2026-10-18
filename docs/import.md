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
dotnet run --project import/DarkMessiah.Import.Tool -- level local/recordings/l01_a-parrybot.rbrec
dotnet run --project import/DarkMessiah.Import.Tool -- model models/npc/orc/npc_orc.mdl
dotnet run --project import/DarkMessiah.Import.Tool -- map maps/l01_a.bsp
```

`level` imports the recording's map, every model path in the recording's
model table, and its sprites, and writes `local/import/<map>.assets.json`.
It checks each model's clip order against the sequence names the
recording carries and reports any difference. For l01_a-parrybot it
finishes in about 10 s: 337 of 337 Wire model paths resolve, 231 GLBs and
284 MB, and 112 of 112 clip lists match. `--vpks DIR` and `--out DIR` override
the defaults (`local/donor/vpks`, falling back to the Steam install, and
`local/import`).

`rusty asset check <file.glb>` runs the Engine's own admission on a file.
Every GLB the level import writes passes it.

## Output

Paths mirror donor paths, lowercased:

| Donor | GLB |
| --- | --- |
| `models/npc/orc/npc_orc.mdl` | `models/npc/orc/npc_orc.glb`, then clip packs `npc_orc.clips1.glb`, `npc_orc.clips2.glb`, ... |
| `maps/l01_a.bsp`, `*0` | `maps/l01_a/world.glb` |
| `*12` (brush model 12) | `maps/l01_a/brush_12.glb` |
| `materials/sprites/glow_torch.vmt` | `materials/sprites/glow_torch.png` |

`<map>.assets.json` is a [portable asset descriptor](../../rusty-engine/docs/portable-assets.md)
(schemaVersion 1). Each asset id is the Wire's model path exactly as
reported, case included, so a Wire model index joins to its asset by string.

- **Clip packs:** an id ending `#clipsN` is clip pack N of the model whose id
  comes before the `#`.
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
  count, looping, delta, blend grid and fades.
- **Attachments:** weapon grips and other attachments are in
  `asset.extras.attachments`, each a joint name and a local matrix.

The Engine admits at most 64 clips, 4,096 channels, 1,000,000 keyframes
and 4,000,000 values per GLB. A character has 120 to 715 sequences, so
clips go in sequence order into the primary GLB, and the rest into clip
packs under the same limits:

- **Pack contents:** a pack holds the same skeleton and a one-triangle
  stand-in mesh, because the Engine's animated admission needs a skinned
  mesh to find the rig.
- **Loading:** a product opens the model, then each pack
  (`Animation.OpenAnimationClipPackFromContent`), and associates each pack
  (`AssociateAnimationClipPack`) before it creates the model's appearance.
  After that, clips from any pack play by name.
- **`ReadClips`:** lists only the primary GLB's clips.

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
  92, model 148, mesh 116. VVD is version 4 (48-byte vertices) and VTX is
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
