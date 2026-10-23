# Texture maps

Both clients render the drapes and instruments with procedural materials. Real texture maps make the fabric
read as fabric and the steel as satin-finished steel. There are two ways to get them: download CC0 sets, or
make your own (for example with an AI image generator) and drop them in. Nothing under these folders is
committed; keep the licence notes with them.

## Slots

| Slot | What it is on screen | Physical size of one tile | Maps |
|---|---|---|---|
| `drape` | the loose blue nonwoven drape | 30 mm | albedo, normal, roughness |
| `pad` | absorbent reinforcement around the fenestration (falls back to `drape`) | 30 mm | albedo, normal, roughness |
| `steel` | forceps, Frazier suction, skin hook, blade electrode (satin stainless) | 12 mm | albedo, normal, roughness |
| `plastic` | Bovie pencil body (off-white moulded plastic) | 20 mm | albedo, normal, roughness |

Only the **albedo** (colour) map is required. A missing normal map is derived from the albedo's luminance;
a missing roughness map leaves the material's roughness in place. Maps must tile seamlessly (the renderer
repeats them triplanar in the mesh's own coordinates) and be square, 1024 or 2048 px. A grey albedo is
tinted to the slot's colour (surgical blue, off-white); a coloured one is used as is.

## Browser build

Files go under `public/textures/<slot>/` as `albedo.jpg`, `normal.jpg`, `roughness.jpg` (PNG works too; name
them in `public/textures/manifest.json` if so). Restart `npm run dev` or reload; the HUD's `textures` field in
`window.__sim` lists the slots that loaded.

- **CC0 download:** `node scripts/fetch-textures.mjs` (or one slot: `node scripts/fetch-textures.mjs steel`)
  fetches sets from ambientCG and Poly Haven (both CC0 1.0) through their public APIs, writes the manifest and
  `public/textures/DOWNLOADED.md` with the asset ids. Needs internet access on the PC that runs it.
- **Your own maps:** copy them into the slot folders with the file names above. No manifest needed.

## Unity client

Put the files in `Assets/BovieSim/Generated/Downloads/` named `<Slot>_Color.jpg`, `<Slot>_NormalGL.jpg`,
`<Slot>_Roughness.jpg` with `Slot` one of `Drape`, `Pad`, `Steel`, `Plastic` (PNG works too), then run
*Bovie Sim > Set Up Project*. Your files win over the CC0 downloads the setup would otherwise fetch (fabric from
ambientCG for the drape, brushed metal for the instruments); a missing normal map is derived from the colour.

## Making maps with an image generator

Ask for a **flat, evenly lit, top-down close-up of the material, filling the frame, seamlessly tileable, no
shadows, no highlights, no perspective, no text** (that is an albedo map: the colour of the surface without
lighting). Square, 2048 x 2048. One image per slot; state the physical size so the scale matches the sim.

Prompts that work as a starting point:

- **drape** — "Seamless tileable texture, flat top-down macro photograph of blue disposable surgical drape
  fabric, SMS spunbond-meltblown nonwoven polypropylene, medium surgical blue, fine random fibre texture with a
  subtle regular pattern of small embossed bonding dots about 1.5 mm apart, matte, evenly lit, no shadows, no
  folds, no highlights, no perspective, no text, area shown about 3 x 3 cm, 2048 x 2048, photorealistic
  material scan, albedo map"
- **pad** — "Seamless tileable texture, flat top-down macro photograph of the absorbent reinforced zone of a
  blue surgical drape: light blue fleece-like nonwoven with soft quilted dimples, matte, evenly lit, no shadows,
  no highlights, no perspective, no text, area about 3 x 3 cm, 2048 x 2048, material scan, albedo map"
- **steel** — "Seamless tileable texture, flat top-down macro photograph of satin-finished bead-blasted
  surgical stainless steel with faint fine brushing lines running horizontally, uniform light grey, no
  reflections of surroundings, no highlights, no scratches larger than hairlines, evenly lit, no perspective,
  no text, area about 12 x 12 mm, 2048 x 2048, material scan, albedo map"
- **plastic** — "Seamless tileable texture, flat top-down macro photograph of off-white lightly textured
  moulded ABS plastic, very fine matte grain, evenly lit, no shadows, no highlights, no perspective, no text,
  area about 2 x 2 cm, 2048 x 2048, material scan, albedo map"

If the generator offers them, also ask for a matching **normal map** ("tangent-space normal map, OpenGL
convention, of the same surface") and a **roughness map** ("greyscale roughness map, white = rough"); otherwise
the sim derives a normal map itself. Check tiling by viewing the image repeated 2 x 2; most generators need a
"seamless" or "tileable" mode, or a tool such as Materialize or ImageMagick's `-virtual-pixel tile` to fix the
edges.

## Instrument models (purchased packs)

Real instrument models replace the procedural ones. Put the pack's **FBX** (or OBJ) and its textures into
`unity/BovieSim/Assets/BovieSim/Models/` (any sub-folder; the folder is git-ignored because a purchased pack
may not be redistributed) and run *Bovie Sim > Set Up Project*. The setup matches meshes by name:

| Mesh name | Used as | Fit |
|---|---|---|
| `tweezers` | Adson forceps | split into its two arms at the splay axis, each arm straightened, tip at the origin, scaled to 114.7 mm; the sim opens and closes them |
| `scissors`, `scissor_tweezers`, `scalpel` | instruments resting on the drape beside the field | laid flat, scaled to their real length |
| `items_scratches` (texture) | scratched smoothness and a derived normal map on all pack steel, in the pack's own UVs | |
| anything ending in `_low` | skipped (LOD meshes) | |

Scale is taken from each item's real length, so it does not matter whether the file is in metres,
centimetres or Blender units.

### The modelled instruments (generated asset pack)

One file per instrument, tip at the origin, handle along +Y, buttons or vent on +Z, with maps in
`models/textures/<material>_albedo|normal|roughness|metallic.png` for the materials `steel`, `plastic`,
`cable`, `yellow`, `blue`. Put the whole `models/` folder (FBX files and the `textures` sub-folder) under
`Assets/BovieSim/Models/` and run Set Up Project. Only the FBX files are read; Unity does not import GLB.

| File | Used as | Fit |
|---|---|---|
| `pencil.fbx` (nodes `pencil`, `pencil_cut_button`, `pencil_coag_button`) | Bovie pencil body and switches | scaled to 224.5 mm; its built-in electrode (`steel` submesh below 26 mm) is cropped off so the blade, needle and ball swap in; the switches glow yellow and blue when pressed |
| `blade.fbx`, `needle.fbx`, `ball.fbx` | electrodes (B, N, O) | scaled to 60 / 45 / 43 mm, shaft into the pencil nose |
| `frazier.fbx` | Frazier suction | scaled to 115 mm, mirrored in X so the handle bends to the same side as the procedural one |
| `skin_hook.fbx` | skin hook | scaled to 160 mm, hook's lowest point at the origin |

Each material becomes an HDRP Lit material from the pack's maps in the model's own UVs (roughness and
metallic packed into the mask map). The `textures/` tiles and `unity-downloads/` copies install as described
above; the `coated_blade`, `cable` and `tubing` tiles are not used by a slot yet. The browser build does not
load models.
