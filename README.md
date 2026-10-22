# Bovie Sim

A browser-based training simulator for monopolar electrosurgery. A voxel tissue bed (skin,
subcutaneous fat with septa and perforators, Scarpa's fascia, deep fascia, muscle, vessels, nerve,
lipoma) is driven by a quasi-static electrical field solve, Pennes bioheat conduction, phase change
and Arrhenius thermal damage on the GPU, with a Valleylab FT10 generator model and a surgical-field
renderer. Phase 1 (this repository) is the accurate physics + visuals core; procedure scenarios
follow.

## Run

```
npm install
npm run dev        # http://localhost:5173  (Chrome / Edge 113+, Safari 18+; WebGPU required)
npm test           # analytic physics tests (vitest)
npm run build
```

URL options: `?scenario=freeplay|hemostasis|lipoma`, `?voxel=0.5` (coarser bed for slower GPUs;
default 0.25 mm), `?seed=3` (re-randomise anatomy), `?cut=1|2|4` (pre-carved trench, pit, or ring
around the lipoma), `?debug=1|2|3` (temperature / damage / tissue-type overlay), `?mech=0`
(disable the mechanics lattice).

## Controls

| Action | Input |
|---|---|
| Cut / coag footswitch | `Z` / `X`, or the on-screen pedals |
| Hand switch | left mouse = cut, Ctrl + left = coag (pencil) |
| Instrument | `Tab` or `Q` cycles pencil → Adson forceps → Frazier suction → skin hook; `T` toothed/smooth forceps |
| Forceps | left button closes the jaws (grasp, pinch, lift); pedal with closed jaws buzzes the forceps |
| Suction | left button drains blood and pulls smoke at the tip |
| Skin hook | left-drag attaches to the tissue and retracts it |
| Pen tablet | pressure sets contact depth, tilt sets pencil orientation |
| Cut mode / coag mode | `1-4` (Pure, Blend, Low, Valleylab) / `5-6` (Fulgurate, Spray), or tap the mode on the panel |
| Watts | `[` `]` cut, `;` `'` coag (Shift = ±1) |
| Electrode | `B` blade, `N` needle, `O` ball, `R` roll blade 45° |
| Overlays | `V` surface overlay, `C` cross-section mode, `H` hide section |
| Camera | right-drag orbit, wheel zoom, Shift-drag pan |

## Layout

```
src/core        tissue property table, voxel volume, scenario schema + anatomy generator, FT10 model
src/physics     WGSL kernels (jacobi, heating, generator, bioheat, pick, slice), pipeline, CPU reference
src/render      surface extraction, PBR tissue materials, drapes/instruments, particles, post
src/tools       electrode, forceps, suction, hook poses and geometry (instrument model in docs/tools.md)
src/mechanics   XPBD lattice (gaping, shrinkage, lipoma delivery, muscle twitch)
src/education   event bus (what happened, what it means)
src/ui          FT10 panel, cross-section inspector, audio
scenarios/      scenario JSON (anatomy + goal / success / failure points / decisions / ambiguity)
docs/           physics provenance, visual spec, calibration table, instrument model
scripts/        headless render + activation checks (Chromium + SwiftShader WebGPU)
tests/          analytic tests of the physics kernels
```

## Verification

- `npm test` checks conduction against the analytic Gaussian, the field solver against a spherical
  electrode, Arrhenius against its closed form, phase-change energy bookkeeping and the FT10 curve.
- `node scripts/shot.mjs name "voxel=0.5&cut=3"` renders headless (needs `npm run dev` on 5199).
- `node scripts/activate.mjs name "auto=cut,30,2.5,-0.3,-0.1,0.3,-0.1"` runs a scripted activation
  (mode, watts, seconds, optional start/end cursor positions) and saves normal, temperature and
  damage views plus the impedance / power trace.
- `docs/calibration.md` records simulated vs published thermal spread per mode and setting.
