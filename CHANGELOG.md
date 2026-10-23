# Changelog

All notable changes. Versions follow `MAJOR.MINOR.PATCH`; each release is a git tag `vX.Y.Z`
(see [docs/upgrading.md](docs/upgrading.md)). "Setup" means *Bovie Sim > Set Up Project* must be re-run
after updating (the editor offers it automatically).

## 0.5.0: Surgical field (drapes, lights, colour, ray tracing) · Setup required

- **Fenestrated drape with folds.** The flat frame is replaced by a draped sheet: an incise-film margin on the
  skin, an absorbent reinforcement zone, then the loose blue nonwoven drape with swells, pleats radiating from
  the fenestration and fine wrinkles (same geometry in both clients). Surgical-blue fabric with bonding dots
  and a cloth sheen in the browser build; three HDRP materials (film, pad, drape) in Unity.
- **Overhead surgical light.** Two light heads modelled as 50 cm domes: shadows are sharp where the instrument
  touches tissue and widen with distance (PCSS in Unity from the lights' shape radius with High shadow filtering,
  percentage-closer soft shadows with two shadow maps in the browser build), the specular highlight on wet
  tissue broadens to the dome's size, contact shadows ground the tip, and the lamp heads themselves are emissive
  domes seen in reflections on steel and wet tissue.
- **Ray tracing (Unity, DirectX 12 + RTX / RX 6000-class card).** Set Up Project enables DXR on the HDRP asset and
  puts DirectX 12 first. Settings (F9) > *Ray tracing*: Auto by detail level (Medium: ray-traced shadows + ambient
  occlusion; High: + reflections; Ultra: + global illumination and subsurface scattering), or Off / Shadows /
  Reflections / Full. GPU-rebuilt tissue meshes enter the acceleration structure as dynamic geometry. Without
  DXR the raster effects stay (screen-space reflections from High detail up).
- **Colour rendering.** Browser build: Khronos PBR Neutral tone mapping (blood stays red, fat stays yellow) with a
  proper sRGB transfer curve and a lighter vignette; near-white 4300 K light with the room's cool ambient and
  blue drape bounce. Unity: ambient occlusion at wound scale (1.5 cm, not 25 cm); the scenario skin-tone and
  prep tint is now applied in the right colour space.

## 0.4.0: Round 2 (resolution, teaching view, replay, hardware) · Setup required

- **Resolution scales with the PC.** Detail levels Low / Medium / High / Ultra (0.35 / 0.25 / 0.20 / 0.16 mm
  voxels) chosen automatically from the graphics card, or in Settings (F9). The field-solve region, solver
  iterations, ambient occlusion and shadow-map size scale too. Target 60 fps; Ultra accepts 30 fps.
- **Only changed tissue is rebuilt.** The surface is extracted in 32-voxel bricks; only bricks the electrode,
  heat or tissue movement touched are rebuilt, nearest the instrument first.
- **Teaching view (V):** temperature or damage drawn directly on the tissue, with isotherms and the
  irreversible-damage line.
- **Tip gauge (G):** contact, tip temperature, delivered vs set power and impedance beside the instrument.
- **Pause and replay (P):** after char, a rebleed, entering the lipoma or energy next to a nerve, the last
  seconds replay with the teaching point; Continue with Enter.
- **Settings panel (F9)** saved per user (`settings.json`), and a notice when the PC cannot hold its frame
  budget, offering a lower detail level.
- **Instruments modelled on the real ones:** Valleylab-style pencil with yellow CUT / blue COAG rocker
  switches and cord; Adson forceps with 1 x 2 teeth and serrated grips; 10 Fr Frazier with thumb vent;
  single-prong skin hook.
- Anatomy generation uses all CPU cores. Tissue textures baked at 1024 px.
- Scenario files carry `schemaVersion`; settings carry `version` with migration.

## 0.3.0: Round 1 (interface, latency, section view, colour) · Setup required

- UI Toolkit interface: status, live objectives checklist, FT10 touchscreen-style panel, feed, brief.
- Same-frame instrument tracking; one queued frame; SMAA instead of TAA; depth of field off by default.
- Section view at 640 x 400 with zoom to the full bed depth, isotherms, damage outline, depth scale,
  layer labels and legend; skin-edge thermal rim measured on the GPU.
- Smoothed tissue surface; one reference tissue palette; scenario skin tone and prep applied; Neutral
  tonemapping and white balance.

## 0.2.0: Unity HDRP client

- Native Windows client (Unity 6.6, HDRP) with the physics ported to HLSL compute, HDRP Lit materials with
  subsurface scattering, CC0 HDRI, one-click setup, NUnit tests, CI.

## 0.1.0: Browser build

- WebGPU voxel tissue bed, electrothermal physics, FT10 model, bleeding and hemostasis, XPBD mechanics,
  forceps / suction / hook, scenarios with education blocks.
