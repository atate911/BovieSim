# Bovie Sim

[![CI](https://github.com/atate911/boviesim/actions/workflows/ci.yml/badge.svg)](https://github.com/atate911/boviesim/actions/workflows/ci.yml)

A training simulator for monopolar electrosurgery with a Valleylab FT10 generator model. A voxel
tissue bed (skin, subcutaneous fat with septa and perforators, Scarpa's fascia, deep fascia, muscle,
vessels, nerve, lipoma) is driven on the GPU by a quasi-static electrical field solve, Pennes bioheat
conduction, phase change and Arrhenius thermal damage, with bleeding, vessel sealing and tissue
mechanics. Each scenario has a goal, success criteria, failure points, decisions and modelled
uncertainty, and the event feed explains what happened in surgical terms.

> **Training aid only.** Generator and tissue behaviour are modelled approximations, not
> manufacturer specifications. Not a medical device.

## Two clients, one model

| | Unity client (`unity/`) | Browser build (repo root) |
|---|---|---|
| Use it for | Training on Windows: HDRP rendering, pen input, standalone app | Physics reference and quick checks |
| Needs | Unity 6.6 (6000.6), a discrete GPU | Chrome or Edge with WebGPU |
| Start here | [**Instruction manual**](docs/manual.html) (download and open in a browser) and [unity/README.md](unity/README.md) | `npm ci && npm run dev` |

Both read the same `scenarios/*.json` and teaching text; `npm run sync:unity` copies them into the
Unity project (CI fails if the copies drift).

### Quick start on Windows

```
cd E:\claude
git clone https://github.com/atate911/boviesim bovie-sim
```

Then in Unity Hub: *Add project from disk* → `E:\claude\bovie-sim\unity\BovieSim`, open it, and follow
the manual's *Open and set up the project* steps.

## Browser build


```
npm install
npm run dev        # http://localhost:5173  (Chrome / Edge 113+, Safari 18+; WebGPU required)
npm test           # analytic physics tests (vitest)
npm run build
```

URL options: `?scenario=freeplay|hemostasis|lipoma`, `?voxel=0.5` (coarser bed for slower GPUs;
default 0.25 mm), `?seed=3` (re-randomise anatomy), `?cut=1|2|4|8` (pre-carved trench, pit, ring
around the lipoma, undermining plane beneath it; add them), `?debug=1|2|3` (temperature / damage / tissue-type overlay), `?mech=0`
(disable the mechanics lattice).

### Controls (browser build)

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
unity/          Unity 6 HDRP client (same model, HLSL compute port, HDRP Lit SSS materials)
```

## Checks

CI runs on every push (`.github/workflows/ci.yml`):
- **web**: `tsc --noEmit`, `npm test` (vitest physics tests), scenario-copy drift check.
- **unity-check**: compiles every compute kernel and the particle shader (`unity/tools/check/shaders.sh`),
  compile-checks all Unity C# against Unity reference assemblies plus HDRP stubs, and runs the NUnit
  EditMode tests under Mono (`unity/tools/check/check.sh`). The real Unity import is still the final test.

Locally:

- `npm test` checks conduction against the analytic Gaussian, the field solver against a spherical
  electrode, Arrhenius against its closed form, phase-change energy bookkeeping and the FT10 curve.
- `node scripts/shot.mjs name "voxel=0.5&cut=3"` renders headless (needs `npm run dev` on 5199).
- `node scripts/activate.mjs name "auto=cut,30,2.5,-0.3,-0.1,0.3,-0.1"` runs a scripted activation
  (mode, watts, seconds, optional start/end cursor positions) and saves normal, temperature and
  damage views plus the impedance / power trace.
- `npm run sync:unity` copies `scenarios/*.json` (and `teaching.json`) into the Unity project.
- `docs/calibration.md` records simulated vs published thermal spread per mode and setting.

## Docs

- [docs/manual.html](docs/manual.html): installation and operation manual (Unity client)
- [docs/physics.md](docs/physics.md): physics model and provenance
- [docs/calibration.md](docs/calibration.md): simulated vs published values
- [docs/tools.md](docs/tools.md): instrument model
- [docs/visual-spec.md](docs/visual-spec.md): what the surgeon should see
- [docs/status.md](docs/status.md): what is built, verified and still missing
- [unity/BovieSim/Assets/BovieSim/Art/ATTRIBUTION.md](unity/BovieSim/Assets/BovieSim/Art/ATTRIBUTION.md): third-party assets

Problems and realism feedback: open an issue using the templates.

## Licence

All rights reserved; see [LICENSE](LICENSE).
