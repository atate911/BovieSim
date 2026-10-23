# Changelog

All notable changes. Versions follow `MAJOR.MINOR.PATCH`; each release is a git tag `vX.Y.Z`
(see [docs/upgrading.md](docs/upgrading.md)). "Setup" means *Bovie Sim > Set Up Project* must be re-run
after updating (the editor offers it automatically).

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
