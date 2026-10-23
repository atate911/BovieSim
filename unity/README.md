# Bovie Sim: Unity client (HDRP)

The native desktop client. It runs the same voxel tissue model, FT10 generator model, physics kernels
(ported from WGSL to HLSL compute), bleeding, XPBD mechanics, instruments, events and scenario JSON as
the web build in the repo root, and renders them with Unity's High Definition Render Pipeline:
subsurface-scattering skin and fat, HDRI lighting, dual-head surgical light with soft shadows, SSAO,
depth of field focused at the tip, bloom, TAA and ACES tonemapping.

## Requirements
- Unity **6.6** (6000.6.x, the current release) via Unity Hub, Personal licence is fine. Unity 6.3 LTS
  (6000.3.x) also works. Unity 6.0 LTS reached end of support in October 2026.
- **Windows 10 or 11, 64-bit** (the target platform; macOS/Metal should also work but is untested).
  DirectX 12 or DirectX 11 both work (every compute kernel stays within DX11's 8 writable buffers).
  A discrete GPU (NVIDIA GTX 1060 / AMD RX 580 class or better) is recommended; integrated graphics
  will run at a coarser voxel size.
- Internet on first setup (Unity packages; optional CC0 HDRI and fabric downloads).

The full instruction manual (installation and operation, with a guided first session) is
[docs/manual.html](../docs/manual.html): download it and open it in a browser.

Detail adapts to the PC (see [docs/hardware.md](../docs/hardware.md)); updates and rollbacks are in
[docs/upgrading.md](../docs/upgrading.md).

## Windows notes
- **Clone to a short path**, e.g. `E:\claude\bovie-sim`. Unity's `Library` folder nests deeply and paths
  over 260 characters break package imports (`OneDrive\Documents\...` paths are the usual culprit;
  OneDrive syncing the Library folder also slows the editor badly).
- Use Git for Windows; its default line-ending conversion is fine for Unity files.
- Allow the Unity editor through Windows Firewall when asked (package downloads and the optional
  CC0 HDRI / fabric downloads).
- **Wacom pen:** pressure and tilt reach Unity through Windows Ink. In *Wacom Tablet Properties >
  Mapping* keep **Use Windows Ink** ticked (the default). The project reads the pen through Unity's
  Input System package, which the bootstrap installs; without it a Windows pen behaves as a mouse.
  If leaning the pen toward you tips the instrument away from you, press **Y**.
- The bootstrap sets *Project Settings > Player > Active Input Handling* to **Both** and offers to
  restart the editor; accept it (keys and mouse use the legacy input, the pen uses the Input System).

## First open
`maintain open` (in the repo root) starts the exact editor this project names; `maintain setup` does
steps 2-3 below from the command line with Unity closed. By hand:

1. Unity Hub > Add > Add project from disk > `unity/BovieSim`. Hub only lists the project and asks
   nothing else; then click the project to open it with your 6000.6.x editor. Keep the Console open
   (Window > General > Console) to follow progress.
2. The bootstrap installs **HDRP 17**, the **Input System** and the **Test Framework** (Package Manager).
   If Unity asks to enable the new input backends and restart, choose **Yes**; the bootstrap then sets
   input handling to Both and asks for one more restart. Wait for the
   import to finish; the console says `[Bovie Sim] Installing ...`. If it does not start, use
   `Bovie Sim > Install Required Packages`.
3. When HDRP is in, a dialog offers **Set up the project**. Accept (or run `Bovie Sim > Set Up Project`).
   It configures HDRP and linear colour, bakes tissue textures and materials, downloads the HDRI and
   drape fabric (falls back to procedural ones offline), and creates `Assets/BovieSim/Generated/Main.unity`.
4. If `Window > Rendering > HDRP Wizard` lists problems, press **Fix All**, then re-run Set Up Project.
5. Open `Assets/BovieSim/Generated/Main.unity` and press **Play**.

Everything in `Assets/BovieSim/Generated` is rebuilt by Set Up Project and is not committed. After an
update that changes generated assets, the editor offers to re-run Set Up Project (the registry carries a
setup version); accept it.

After the first successful open, commit the `.meta` files Unity generates next to every asset and
folder (`git add unity/BovieSim/Assets`). Unity uses them as stable IDs; they must not be written by hand.
Scenario JSON lives in the repo `scenarios/` folder; after editing it run `npm run sync:unity`
from the repo root to copy it into `Assets/BovieSim/Resources/BovieSim/Scenarios`.

## Controls
| Input | Action |
|---|---|
| Z / X (or the on-screen CUT / COAG pedals) | Footswitch cut / coag |
| LMB on the pencil | Hand-switch cut; Ctrl+LMB hand-switch coag |
| LMB with forceps / hemostat / suction / gauze / hook | Grasp (and buzz with a pedal) / close and push the jaws in / suck / press the pad / hook and retract |
| Tab or Q | Next instrument: Bovie pencil, Adson forceps, Kelly hemostat, Frazier suction, 4x4 gauze, skin hook |
| Space (hemostat closed) | Spread the jaws inside the tissue: blunt dissection of areolar and fatty planes |
| Gauze | Hold still to dab (soaks liquid blood, tamponades, settles ooze); drag with pressure to wipe (lifts clot and stain, restarts ooze); `U` discards a soaked pad |
| 1-4 / 5-6 | Cut mode Pure, Blend, Low, Valleylab / coag Fulgurate, Spray |
| `[` `]` and `;` `'` | Cut and coag watts -5/+5 (Shift: 1 W); or the FT10 panel buttons |
| B / N / O, R | Blade / needle / ball electrode, roll the blade 45 degrees |
| T | Toothed / smooth forceps |
| Space (forceps closed) | Lift the jaws (traction on a grasped edge, delivering a freed lipoma) |
| C / J / H | Cross-section mode / orientation (across view or across the blade) / hide |
| - / = | Section zoom out / in (16 × 10 mm up to the full 56 × 35 mm) |
| RMB or Alt+LMB, MMB or Shift+LMB, wheel | Orbit, pan, zoom |
| F1 / F2 / F3 | Scenario brief / controls / timing readout (frame, CPU, GPU ms; result ages) |
| F7 / F8 | Depth of field on/off (off by default) / anti-aliasing SMAA, TAA, FXAA, off |
| V / G / P | Teaching view on the tissue (temperature, damage) / tip gauge / pause and replay (Enter continues) |
| F9 | Settings: detail level (Auto, Low 0.35, Medium 0.25, High 0.20, Ultra 0.16 mm), display, teaching |
| F5 / F6 | Restart / next scenario (freeplay, hemostasis, lipoma, viscera) |
| F11 / Esc | Toggle fullscreen / quit (built app only) |
| Pen (Wacom) | Pressure sets electrode depth; tilt sets the instrument angle; Y flips tilt direction |

The scenario can also be chosen on the `Bovie Sim` object (Scenario Id). Voxel Override forces a voxel
size for development; normally choose a detail level in Settings (F9).

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
From the command line, with Unity closed: `maintain test` (runs `-runTests -testPlatform EditMode` and
lists failures).
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
- **Input does nothing / `InvalidOperationException` about the Input class**: Project Settings > Player >
  Active Input Handling must be *Both*; the bootstrap sets it, then the editor must restart.
- **Pen moves the cursor but pressure/tilt do nothing**: check Use Windows Ink in the Wacom settings and
  that `com.unity.inputsystem` is installed (Window > Package Manager).
- **Slow**: set Voxel Override to 0.4 or 0.5 on the `Bovie Sim` object; turn off DoF/SSAO in the volume.
- **Downloads blocked**: put any `.hdr` into `Assets/BovieSim/Generated/Downloads` and re-run setup.
