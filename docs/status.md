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
- Instruments: Adson forceps (grasp, buzz, lift), Frazier suction, skin hook; Tab cycles tools.
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

## Known gaps (next)
- Picking and tool placement use material coordinates: with large deformation the instrument
  draws slightly off the displaced surface.
- Blood is shaded on the tissue surface (depth-tinted), not as a separate fluid surface mesh.
- Vessel ends do not retract; ties and clips are not implemented (decision point only).
- Fat lobule separation is approximate (compliance only; septa are not individually modelled).
- Absolute impedance and lateral spread not yet calibrated against published values
  (docs/calibration.md): tune `sigmaScale`, `arcMmPerKv`, `vapFactor`.
- FT10 output curves are approximations until the user guide is digitised.
- No bleeding, forceps or suction yet (Sprint 3); no mechanics/gaping (Sprint 4).
- Smoke/arc visuals are first-pass; no depth of field or screen-space SSS yet.
- Reference imagery folder is empty; visual acceptance needs the surgeon's eye.
- Performance on real GPUs untested from this environment (SwiftShader only).
