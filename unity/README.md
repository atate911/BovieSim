# Bovie Sim: Unity client (HDRP)

The native desktop client. It runs the same voxel tissue model, FT10 generator model, physics kernels
(ported from WGSL to HLSL compute), bleeding, XPBD mechanics, instruments, events and scenario JSON as
the web build in the repo root, and renders them with Unity's High Definition Render Pipeline:
subsurface-scattering skin and fat, HDRI lighting, dual-head surgical light with soft shadows, SSAO,
depth of field focused at the tip, bloom, TAA and ACES tonemapping.

## Requirements
- Unity **6000.0 LTS** (6000.0.58f1 or later 6000.0.x) via Unity Hub, Personal licence is fine.
- Windows (DX12 or Vulkan) or macOS (Metal), discrete GPU recommended. HDRP needs compute shaders.
- Internet on first setup (Unity packages; optional CC0 HDRI and fabric downloads).

## First open
1. Unity Hub > Add > Add project from disk > `unity/BovieSim`. Open with 6000.0.x.
2. The bootstrap installs **HDRP 17** and the **Test Framework** (Package Manager). Wait for the
   import to finish; the console says `[Bovie Sim] Installing ...`. If it does not start, use
   `Bovie Sim > Install Required Packages`.
3. When HDRP is in, a dialog offers **Set up the project**. Accept (or run `Bovie Sim > Set Up Project`).
   It configures HDRP and linear colour, bakes tissue textures and materials, downloads the HDRI and
   drape fabric (falls back to procedural ones offline), and creates `Assets/BovieSim/Generated/Main.unity`.
4. If `Window > Rendering > HDRP Wizard` lists problems, press **Fix All**, then re-run Set Up Project.
5. Open `Assets/BovieSim/Generated/Main.unity` and press **Play**.

Everything in `Assets/BovieSim/Generated` is rebuilt by Set Up Project and is not committed.
Scenario JSON lives in the repo `scenarios/` folder; after editing it run `npm run sync:unity`
from the repo root to copy it into `Assets/BovieSim/Resources/BovieSim/Scenarios`.

## Controls
| Input | Action |
|---|---|
| Z / X (or the on-screen CUT / COAG pedals) | Footswitch cut / coag |
| LMB on the pencil | Hand-switch cut; Ctrl+LMB hand-switch coag |
| LMB with forceps / suction / hook | Grasp (and buzz with a pedal) / suck / hook and retract |
| Tab or Q | Next instrument: Bovie pencil, Adson forceps, Frazier suction, skin hook |
| 1-4 / 5-6 | Cut mode Pure, Blend, Low, Valleylab / coag Fulgurate, Spray |
| `[` `]` and `;` `'` | Cut and coag watts -5/+5 (Shift: 1 W); or the FT10 panel buttons |
| B / N / O, R | Blade / needle / ball electrode, roll the blade 45 degrees |
| T | Toothed / smooth forceps |
| C / J / H | Cross-section mode / orientation (across view or across the blade) / hide |
| RMB or Alt+LMB, MMB or Shift+LMB, wheel | Orbit, pan, zoom |
| F1 / F2 | Scenario brief (goal, success, failure points, decisions, uncertainties) / controls |
| F5 / F6 | Restart / next scenario (freeplay, hemostasis, lipoma) |
| Pen (Wacom) | Pressure sets electrode depth; tilt sets the instrument angle |

The scenario can also be chosen on the `Bovie Sim` object (Scenario Id). Voxel Override (e.g. 0.4 mm)
trades detail for speed on smaller GPUs.

## Layout
```
Assets/BovieSim/
  Bootstrap/Editor   package installer (compiles without HDRP)
  Core               engine-independent model: tissue table, volume, anatomy, scenario schema, FT10,
                     XPBD lattice, event bus, CPU reference kernels (ports of src/core, src/mechanics, ...)
  Runtime            GPU pipeline (PhysicsPipeline), surface extraction into Mesh vertex buffers drawn
                     with HDRP Lit per material bucket, blood mesh, particles, instruments, input, IMGUI HUD,
                     SimulationRunner (frame order identical to src/main.ts)
  Editor             ProjectSetup (HDRP, materials, profiles, scene), TissueTextures (procedural bakes)
  Resources/BovieSim Shaders (Sim.compute, Surface.compute, Particles.shader), Scenarios (synced JSON)
  Tests/EditMode     NUnit ports of the web vitest suite
  Art/ATTRIBUTION.md third-party asset log
tools/check          blind compile / test harness used when no Unity editor is available
```

## Tests
`Window > General > Test Runner > EditMode > Run All`: diffusion against the Gaussian solution, field
solve against a spherical electrode, Arrhenius and boiling bookkeeping, FT10 limits, bleed rules, blood
flow, lattice gaping and lipoma freeing, scenario parsing, anatomy determinism and teaching coverage.

## Troubleshooting
- **Compile errors after opening**: copy the first error from the Console verbatim (file, line, message)
  and send it back; the client was written without access to a Unity editor, so the first open is the
  first real compile.
- **Pink or missing tissue / "SimAssets not found"**: run `Bovie Sim > Set Up Project`.
- **Black or very dark scene**: the exposure is fixed for an operating light (EV 15). Check the two
  `Surgical light` objects exist; adjust `Exposure` in `Generated/BovieVolume.asset` if needed.
- **Input does nothing**: Project Settings > Player > Active Input Handling must be *Both* or *Input
  Manager (Old)*; setup switches it from *Input System Package (New)* automatically (editor restart).
- **Slow**: set Voxel Override to 0.4 or 0.5 on the `Bovie Sim` object; turn off DoF/SSAO in the volume.
- **Downloads blocked**: put any `.hdr` into `Assets/BovieSim/Generated/Downloads` and re-run setup.
