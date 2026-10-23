# Status (Phase 1, Sprint 0–1 core)

## Built and verified
- Voxel tissue bed from scenario JSON: epidermis, dermis, fat with septa and lobules, Scarpa's and
  deep fascia, muscle, arterial/venous perforators with fascial branches, cutaneous nerve, lipoma
  with pseudocapsule and feeding vessel. Layer thickness carries the scenario's modeled variation.
- GPU surface extraction (marching tetrahedra, smoothed normals, per-vertex tissue state).
- Renderer: shadowed surgical light, PBR + wrap/translucency SSS, procedural per-tissue materials,
  thermal-state appearance (blanch, desiccate, char, boil glints, incandescence), drapes, prep tint,
  Bovie pencil model, steam/smoke/arc particles, ACES tonemap. Verified headless at 0.5 mm.
- Physics on the GPU: quasi-static field solve (seeded red-black SOR, harmonic-mean conductances)
  with a per-mode contact front and arc footprint; scheme-consistent Joule dissipation; FT10 power-vs-impedance curve; Pennes bioheat with
  heating-rate limiter; latent-heat phase change; vaporisation (cut) vs desiccation (coag);
  Arrhenius damage; state transitions; GPU cross-section inspector; GPU cursor pick.
- Unit tests (19): diffusion vs Gaussian, energy conservation, field solve vs spherical electrode,
  flux/power identity, Arrhenius closed form and steepness, phase-change energy bookkeeping, FT10
  curve limits, layer behaviour orderings.
- Headless activation checks (0.5 mm voxels, software GPU): 30 W contact coag on skin gives
  Z ≈ 360→490 Ω rising with desiccation, 30 W delivered, tissue held at 100 °C, blanched footprint
  ~1 mm beyond the electrode, ~3.9 mm deep; a 30 W pure-cut stroke at 10 mm/s removes ~7 mm³/s,
  forms a ~1.4 mm groove with a ~1 mm (one-voxel) thermal rim, no char; a held blade in cut mode
  desiccates, impedance climbs past 2 kΩ and delivered power collapses (stalling).

## Sprint 3 + 4 (this block)
- Bleeding: exposed vessel ends bleed with radius-scaled, pulsatile flow; coaptive seal vs char
  plug vs rebleed rules; blood height field with downhill flow, clotting, pool conduction penalty;
  droplet jets; suction. Hemostasis drill scenario with a pre-carved trench crossing a 1.0 mm artery
  and a 0.6 mm vein.
- Instruments: Adson forceps (grasp, buzz, lift), Frazier suction, skin hook; Tab cycles tools (0.6.0 adds the scalpel, scissors and a second hand).
- Mechanics: XPBD lattice with per-layer compliance and skin pre-tension; GPU probe severs and
  shrinks links from the voxel state; incisions gape; lipoma cluster frees and delivers; muscle
  twitch on coag; rendered tissue follows the lattice.
- Scenarios: hemostasis, lipoma (mechanics), freeplay. Tests: bleed rules, blood flow, lattice.
- Verified headless (0.5 mm, software GPU): direct coag on the 1 mm artery leaves char plugs on
  the open end that rebleed twice within 5 s while unexposed vessel thromboses; forceps grasp and
  buzz seals the held vessel (51 voxels) and stops its bleeding; coag on exposed muscle fires
  `muscle.stimulated`; buzzing in a pool fires `blood.poolConducts`; a pre-made 30 mm incision
  gapes 2.8 mm; a ring cut plus undermining frees the lipoma (`lesion.freed`) and forceps traction
  lifts it 4 mm out of its bed (`lesion.delivered`).

## Unity HDRP client (unity/BovieSim)
- Port of the core model to C# (tissue table, volume, anatomy, scenario schema, FT10, XPBD lattice,
  event bus, CPU reference kernels) and of all WGSL kernels to HLSL compute (Sim.compute: field solve,
  heating, generator, bioheat, blood, coaptation, probe, pick, slice; Surface.compute: bucketed
  marching-tetrahedra extraction written straight into Mesh vertex buffers with lattice deformation).
- Rendering with stock HDRP Lit: 18 surface buckets (8 tissue classes, coagulated variants,
  desiccated, carbonized) with procedurally baked triplanar textures, diffusion profiles for skin,
  fat and muscle, wet coat; blood pool mesh; dual-head 4300 K surgical light; CC0 HDRI; SSAO, DoF at the
  tip, bloom, TAA, ACES. Steam, smoke, arcs and blood droplets via a custom HDRP particle shader.
- IMGUI FT10 panel with on-screen footswitch, HUD, event feed with teaching text, scenario brief,
  cross-section inspector. Mouse, keys and pen pressure/tilt.
- Teaching text is extracted to `scenarios/teaching.json`, which the Unity client reads (the web build
  still uses its in-code copy in `src/education/events.ts`; keep the two in step).
- Verified here without a Unity editor: every compute kernel compiles (glslang, HLSL mode); all C#
  compiles with mcs against UnityEngine/UnityEditor reference assemblies plus HDRP stubs written
  from the HDRP 17 source; the NUnit EditMode suite (23 physics, generator, bleeding, lattice tests)
  passes under Mono. **Not yet verified: the first real Unity import, HDRP rendering, GPU parity
  with the WebGPU build and performance**; those need the project opened on the user's machine.

## Unity client, round 1 (interface, latency, section, colour)
- UI Toolkit interface replaces IMGUI: status with live Z/P/V, live objectives checklist (OK/NEAR/FAIL/DONE,
  `Core/Objectives.cs`), FT10 touchscreen-style panel, event feed, scenario brief, controls, F3 timing readout.
- Latency: same-frame CPU pick on a GPU-refreshed height map (`SurfacePicker`), one queued frame, SMAA
  instead of TAA, depth of field off by default.
- Section view: 640 × 400, trilinear sampling, zoom to the full bed depth, isotherms (45/60/100 °C),
  dashed Ω = 1 outline, depth scale, layer labels, per-mode legend.
- Skin-edge thermal rim measured on the GPU (`RimMeasure`, 95th percentile) for the rim objectives.
- Tissue surface extracted from a smoothed occupancy field (no voxel stair-steps).
- Colour: one reference palette (`Core/TissuePalette.cs`) for textures, section and legend; scenario skin
  tone and prep applied at runtime; Neutral tonemapping and white balance for the 4300 K light.
- Not yet verified in Unity: all of the above (compiled and unit-tested here only).

## Unity client, round 2 (0.4.0)
- Detail levels from the GPU (`HardwareProfile`), saved user settings (`Core/UserSettings.cs`), F9 panel,
  frame-budget banner (60 fps; 30 fps at Ultra).
- Brick-based surface extraction (32³ cells, dirty queue, nearest the tip first, double-buffered, meshes
  grow to fit); per-vertex temperature and damage feed the teaching overlay (V).
- Tip gauge, pause-and-replay teaching moments (ring of downscaled frames).
- Instrument models at real proportions (pencil switches and cord, Adson 1 x 2 teeth, Frazier thumb vent,
  skin hook); anatomy build parallelised.
- Upgrade path: version constant, changelog, tags, setup/settings/scenario versioning (docs/upgrading.md).
- Not yet verified in Unity.

## Unity client, round 3 (0.5.0: section resolution, depth peel)
- Section rendered at 1280 × 800 (`DetailSettings.sliceScale`: 960 × 600 at Low, 1600 × 1000 at High and Ultra),
  bilinear; large view (Shift+C) floats the card over the field at 800 px.
- Depth peel: a clip plane in the brick extraction (`Surface.compute` `_Clip` / `_ClipMode`). Block cut removes
  every voxel centre above the plane (fractional occupancy for the voxel it crosses, so the face slides smoothly);
  dissection keeps vessels, nerve, lipoma, capsule, blood and any voxel with a changed thermal state or Ω ≥ 0.53,
  holding thin kept structures above the iso level so the smoothing does not erase them. Moving the plane rebuilds
  only the bricks in the band it crossed. Ruler card with layer ticks, keys, Ctrl + wheel, sweep; the section draws
  the plane as a dashed line. Display only: physics, pick and instruments keep the whole bed.
- Browser build: the same peel, verified headless at 0.5 mm: a block cut at 6 mm shows fat lobules, septa and vessel
  cross-sections on the cut face with the strata on the walls; a dissection at 20 mm shows the perforator tree, the
  nerve and the lipoma over the muscle (336 k triangles); a coag activation then a block cut at 1.5 mm shows the
  blanched footprint on the cut face. Section sampled trilinearly at 640 × 400.
- Not yet verified in Unity (compiled and unit-tested here only).

## Two hands and steel (0.6.0)
- Non-dominant hand on the same pointer: hold Space to drive it (forceps, hook or suction), release to latch it
  holding its traction; the pointer returns to the dominant hand (`src/tools/hands.ts`, `Core/Hands.cs`, unit-tested
  state machine). Both instruments stay on the field; the pencil buzzes forceps it touches and diverts current through
  a hook or suction it touches (`instrument.contact`).
- #15 scalpel (sharp mechanical cut, pressure-set depth, swept blade) and Metzenbaum scissors (snip; Ctrl + hold
  spreads) through one `mechCut` kernel in WGSL and HLSL: sharp divides everything with fractional occupancy at the
  face; blunt parts fat, tears vessels up to 0.3 mm, keeps larger vessels in their fat cuff (tented) and is resisted
  by septa, fasciae, skin, muscle and the capsule. Fresh dermal edges ooze from the plexus until they clot (two
  minutes on average) or are coagulated; pre-made wounds start sealed. Events: `incision.made`, `skin.oozing`,
  `plane.developed`, `plane.resists`, `vessel.stretched`, `vessel.torn`, `nerve.stretched`, `instrument.contact`.
- Verified headless (0.5 mm, software GPU): a 30 mm scalpel stroke divides 500 voxels with no heat (max T unchanged,
  nothing vaporised), transects a vein, oozes from 148 dermal edge voxels and gapes 0.7 mm between nodes 3 mm apart
  (2.75 mm deep, the mouse depth); a 7 mm scissors spread in the fat beside the lipoma (trench to the capsule
  pre-carved) separates 270 voxels, cuts 21 of 303 capsule links, tents a vessel (66 voxels, no bleeding from it)
  and reports 594 resisting voxels on the capsule; a skin hook in the non-dominant hand latched on a trench edge
  pulls its node 3.5 mm aside and widens the gap from 7.3 to 8.9 mm while the pencil is free.
- Unity: same kernels (26 entry points compile), C# port compiles against the reference assemblies, 36 EditMode tests
  pass under Mono. Not yet verified in Unity.

## Known gaps (next)
- A vessel bared along its side by a cut bleeds like a transected end (blunt spreading now keeps larger vessels in
  their cuff). Blunt dissection does not yet open a visible cleft on the capsule in the section unless fat is removed.
- The non-dominant hand has no second pointing device yet (latch only); touch and gamepad input are the next step.
- Hemostat, Allis, Senn and ties remain decision points only.
- The depth peel is display-only: an instrument used while peeled still acts on the hidden tissue above the plane,
  so the tip floats over the cut face.
- Picking and tool placement use material coordinates: with large deformation the instrument
  draws slightly off the displaced surface.
- Blood is shaded on the tissue surface (depth-tinted), not as a separate fluid surface mesh.
- Vessel ends do not retract; ties and clips are not implemented (decision point only).
- Fat lobule separation is approximate (compliance only; septa are not individually modelled).
- Absolute impedance and lateral spread not yet calibrated against published values
  (docs/calibration.md): tune `sigmaScale`, `arcMmPerKv`, `vapFactor`.
- FT10 output curves are approximations until the user guide is digitised.
- Smoke/arc visuals are first-pass; no depth of field or screen-space SSS yet.
- Reference imagery folder is empty; visual acceptance needs the surgeon's eye.
- Performance on real GPUs untested from this environment (SwiftShader only).
