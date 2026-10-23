# Changelog

All notable changes. Versions follow `MAJOR.MINOR.PATCH`; each release is a git tag `vX.Y.Z`
(see [docs/upgrading.md](docs/upgrading.md)). "Setup" means *Bovie Sim > Set Up Project* must be re-run
after updating (the editor offers it automatically).

## 0.7.0: Tissue realism (mechanics, blunt dissection, tearing, bleeding, blood) · Setup required

- **Every tissue behaves like itself.** Areolar tissue, viscera and serosa join the table (17 types); each
  tissue carries compliance, a collagen J-curve knee, yield and tear strains, cohesion, coagulation shrink and
  stiffening and a capillary ooze rate. Coagulated collagen firms and contracts, rendered fat softens,
  desiccated and charred tissue turns brittle, muscle is tougher along its fibres, vessel ends recoil.
- **Blunt dissection and tearing.** A Kelly hemostat (Tab) grasps, buzzes, pushes along the loosest planes
  and, with Space, spreads inside the tissue: areolar tissue and fat lobules part, areolar strands draw out,
  thin and snap, vessels and nerves bridge the plane until torn or cut. Links torn by traction carve the
  tissue they ran through; torn vessels bleed.
- **Bleeding that reads like bleeding.** Vessels bleed only from an open lumen; arterioles spasm and venules
  clot within seconds while arteries keep going; every raw perfused surface oozes from its capillaries and
  settles over about a minute unless wiped; char plugs can rebleed. Blood pools, runs downhill, soaks into
  raw tissue, clots with time and heat, boils and spatters under the electrode and cooks into insulating
  coagulum. Suction lifts liquid only and draws the pool toward the tip; a 4x4 gauze (Tab) dabs, tamponades
  and wipes (U swaps a soaked pad). New events and teaching text for all of this.
- New scenario `viscera` (serosal bleeder on an exposed viscus). Areolar planes in every scenario.
- Fixed: the mechanics lattice carried its XPBD multipliers across substeps and could tear a resting lesion
  bed apart; damping and anchoring are now frame-rate independent.
- Unity: full port (kernels, instruments, strands, spatter, 20 material buckets with a viscera class, HUD
  rows); compiled and unit-tested blind, not yet opened in an editor. Setup version 9 bakes the new buckets
  and the strand material.

## 0.6.0: Surgical field (drapes, lights, colour, ray tracing) · Setup required

- **Fenestrated drape with folds.** The flat frame is replaced by a draped sheet: an incise-film margin on the
  skin, an absorbent reinforcement zone, then the loose blue nonwoven drape with swells, pleats radiating from
  the fenestration and fine wrinkles (same geometry in both clients). Surgical-blue fabric with bonding dots
  and a cloth sheen in the browser build; three HDRP materials (film, pad, drape) in Unity.
- **Squared field.** Four folded cotton towels square the window on top of the drape, overlapping windmill-fashion
  at the corners with a little misalignment, held by Backhaus towel clips; the incise film is a narrow clear band
  over the skin edge. Drape folds are seeded per scenario and irregular: domain-warped swells, broad waves across
  the sheet, local bunching, radiating pleats and package creases. Skin shows its polygonal line network, pores at
  the cell corners, vascular mottling and the prep painted on in sponge strokes with a glossy film pooling in the
  furrows (both clients).
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
- **Texture maps.** Both clients take real maps for the drape, reinforcement pad, instrument steel and pencil
  plastic (albedo, normal, roughness; a missing normal map is derived from the colour). CC0 sets download with
  `npm run fetch:textures` (ambientCG, Poly Haven) for the browser build and by Set Up Project for Unity; your
  own maps drop into `public/textures/<slot>/` or `Generated/Downloads/<Slot>_Color.jpg` (docs/textures.md, with
  prompts for making them). Without maps: satin brushed steel that reflects the lit field and the lamp domes
  instead of a dark room, a nonwoven drape with fibre texture, bonding dots and a rolled hem and package
  creases in the folds.
- **Instrument models (Unity).** A purchased pack dropped into `Assets/BovieSim/Models` replaces the procedural
  Adson forceps (the pack's one-piece tweezers are split into two arms, straightened and fitted to the sim's
  frame so they still open and close) and lays its scissors, haemostat and scalpel on the drape beside the
  field; the pack's scratch map drives scratched smoothness and a normal map on its steel. Procedural teeth now
  face each other. Generated models of the pencil (with its two switches), blade, needle and ball electrodes,
  Frazier suction and skin hook (`pencil.fbx`, `blade.fbx`, …) replace the procedural ones, fitted by real length
  with their own PBR maps.
- **Colour rendering.** Browser build: Khronos PBR Neutral tone mapping (blood stays red, fat stays yellow) with a
  proper sRGB transfer curve and a lighter vignette; near-white 4300 K light with the room's cool ambient and
  blue drape bounce. Unity: ambient occlusion at wound scale (1.5 cm, not 25 cm); the scenario skin-tone and
  prep tint is now applied in the right colour space.

## 0.5.0: Beneath the surface (section resolution, depth peel)

- **Section at twice the resolution.** The cross-section is rendered at 1280 × 800 (960 × 600 at Low detail,
  1600 × 1000 at High and Ultra) with bilinear filtering, so layer edges, isotherms and the damage line stay
  sharp on 1440p and 4K screens. **Large** (Shift+C, or the button on the section) floats it over the field at
  800 px.
- **Depth peel (K):** a plane through the 3D field that reveals the architecture under the skin. *Block cut*
  hides everything above the plane: the cut face shows each layer, the vessels in cross-section and what the
  energy did at that depth. *Dissection* strips skin, fat, septa and fasciae and leaves the perforators, the
  nerve, the lipoma and any heated tissue standing in 3D. A ruler marked with the layers drags the plane; `,`
  and `.` step it, Ctrl + wheel scrubs it, Shift+K sweeps it down and up; the section marks the plane with a
  dashed line. Display only: the physics, the pick and the instruments keep the whole bed.
- Browser build: the same depth peel (`K`, `,` `.`, Ctrl + wheel, `?peel=block,6`), and the section is sampled
  trilinearly at 640 × 400 instead of nearest-voxel at 320 × 200.

## 0.4.1: Maintenance scripts

- **`maintain.cmd`** in the repo root (double-click for a menu) keeps a local install healthy: `status`,
  `check [--online] [--all]`, `update` (previews commits, changelog, setup and Unity-version changes and
  the local files it touches, which it backs up first), `rollback list|VERSION|previous|latest`,
  `open`, `setup`, `test`, `build`, `logs`, `doctor`, `clean`, `reset-settings` and `report`.
- **`install.cmd` / `scripts/install.ps1`** clones the project on a new PC (installing Git and Unity Hub
  with winget if missing), or turns a ZIP download into a clone and carries Unity's files across.
- Unity batch entry points (`BovieSim.Bootstrap.BatchEntry`) so setup, tests and builds run without a
  window; setup and the input-handling fix no longer show dialogs in batch mode.
- CI runs the scripts under Windows PowerShell 5.1 and PowerShell 7 on Windows.

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
