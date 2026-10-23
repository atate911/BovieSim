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
