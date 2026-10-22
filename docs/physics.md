# Physics model and parameter provenance

This document is the single source of truth for every physical constant used by the sim.
Shaders read from the tables generated out of `src/core/tissueProperties.ts`; tune here.

## Electrical (quasi-static field)
∇·(σ∇φ) = 0 on a subdomain (~14 mm cube) around the active electrode. Electrode contact set at
V_rms, subdomain boundary 0 V (dispersive pad). Solved every frame: analytic seed
φ₀ = a/(d+a) around the electrode surface (a = 0.35 mm), then 40 red-black Gauss-Seidel sweeps
with over-relaxation ω = 1.5, harmonic-mean face conductances. Seeding each frame avoids the lag a
warm start leaves behind a moving electrode. Joule heating uses the scheme-consistent face
dissipation q = ½ Σ_faces g (Δφ)² / h², so Σ q dV = V·I exactly at convergence. σ(T, w) = σ₀ (1 + 0.02 (T − 37)) g(w) with g(w) → 0 as water fraction → 0.
Joule heating q = |σ∇φ|² / σ. Load impedance Z = V / I with I = ∮ J·dA around the electrode.

Electrosurgical fundamental ≈ 390–500 kHz; conductivities below are for that band.

## Electrode coupling
The contact set (voxels held at the electrode potential) is tissue within `contactMm` of the
electrode surface plus surface voxels the arc can reach across air (`arcMmPerKv` × V_peak).
In cut modes `contactMm` ≈ 0.1 mm: the steam envelope confines conduction to a thin front at the
edge, so each tissue element receives its full vaporisation energy before the blade moves on
(thermal diffusion length over the 50 ms dwell of a 10 mm/s stroke is ~0.07 mm). In coag modes
≈ 0.3 mm: a desiccating layer couples the electrode to the tissue. Both are calibration knobs.

### Unresolved near field
For a thin edge most of the dissipation happens within tens of micrometres of the electrode,
below voxel resolution (for a cylinder of radius a in a medium of outer radius b, the fraction
inside radius r is ln(r/a)/ln(b/a): with a = 50 µm, r = 0.25 mm, b = 7 mm that is ~33 %, more for
a sphere-like tip). The field solve therefore delivers (1 − f_nf) of the power and f_nf is deposited
directly into the contact voxels, weighted by their remaining water (current favours the fresh,
conductive front; desiccated walls insulate). f_nf: cut 0.6, blend/Valleylab 0.5, coag 0.4 —
calibration knobs tied to the cut-rate and thermal-rim measurements.

## FT10 generator
Delivered power P = min(P_set, P_curve(Z, mode)). The power-vs-impedance curves are digitised from
the Valleylab FT10 user guide (to be added under `docs/reference/ft10/`). Until then the model
uses a constant-power plateau between Z_low and Z_high with linear falloff outside, and per-mode
V_peak limits: Pure cut ≈ 1.6 kV, Blend ≈ 2.2 kV, Low ≈ 1.2 kV, Fulgurate ≈ 4.5 kV, Spray ≈ 6 kV
(approximate; verify against the manual).

## Bioheat (Pennes 1948)
ρc ∂T/∂t = ∇·(k∇T) + q − ω_b ρ_b c_b (T − T_a). Explicit 7-point stencil, h = voxel edge.
Stability: dt ≤ h² / (6α). Heating-rate limiter: |ΔT| ≤ 15 K per substep.

## Phase change
Above 100 °C energy goes to latent heat 2.26 MJ/kg × ρ × w until w → 0. Cut-mode arc contact
vaporizes (occupancy → 0). Coag desiccates; carbonization when T > 220 °C sustained.
Fat: lipid melt above ~45 °C (visual liquefaction).

## Thermal damage (Arrhenius)
Ω = ∫ A exp(−Eₐ / (R T)) dt. Ω > 0.53 coagulated (whitening), Ω > 1 necrotic.

| Tissue | Eₐ (J/mol) | A (1/s) / ln A | Source |
|---|---|---|---|
| Skin, dermis | 6.28e5 | 3.1e98 / 226.8 | Henriques 1947 |
| Muscle | 2.6e5 | 2.94e39 / 90.9 | Jacques et al. (muscle, in-vitro) |
| Fat, lipoma | 3.0e5 | ln A set for 1/s at 65 °C | placeholder: fit to porcine bench |
| Collagenous (fascia, septa, vessel wall, capsule) | 3.06e5 | ln A set for 1/s at 65 °C | collagen denaturation ~65 °C |

## Tissue properties (starting values; IT'IS database v4.x, Gabriel 1996 for σ)
| Tissue | σ S/m @450 kHz | k W/mK | ρ kg/m³ | c J/kgK | perfusion ml/min/kg | water |
|---|---|---|---|---|---|---|
| Epidermis | 0.05 | 0.24 | 1190 | 3600 | 0 | 0.50 |
| Dermis | 0.30 | 0.37 | 1109 | 3391 | 106 | 0.65 |
| Fat | 0.04 | 0.21 | 911 | 2348 | 33 | 0.18 |
| Septum / fascia | 0.30 | 0.40 | 1100 | 3300 | 10–20 | 0.60 |
| Muscle | 0.55 | 0.49 | 1090 | 3421 | 37 | 0.75 |
| Blood / vessel | 0.70 | 0.52 | 1050 | 3617 | – | 0.80 |
| Nerve | 0.20 | 0.49 | 1075 | 3613 | 20 | 0.70 |

## Open items
- Digitise FT10 output curves.
- Replace placeholder fat/collagen Arrhenius coefficients with bench-fit values.
- Add arc breakdown field strength measurements in smoke/steam.

## Vessels and bleeding (Sprint 3)
Vessel voxels carry radius r (0.05 mm units). A vessel voxel with an air neighbour and neither
SEALED nor CHARPLUG bleeds. Flow of a fully transected end Q(r) = 0.6 ml/s · r³ (r in mm; a 1 mm
arteriole ≈ 0.6 ml/s, a 0.3 mm one ≈ 0.016 ml/s), shared across the voxels of its cross-section,
arterial pulsatile at 72 bpm (0.35 + 1.3 sin³), venous steady at 0.3×. Sealing when the wall
collagen is denatured (Ω > 1): SEALED if the lumen is apposed by forceps (FLAG_COAPTED) or
r ≤ 0.5 mm; otherwise CHARPLUG, which rebleeds with probability 0.15 · clamp((r − 0.5)/1.5) per
second. Blood lost is accumulated in microlitres.

## Blood layer
One record per surface column: depth h (mm) above the tissue top, age, and the tissue top height.
Sources add volume to the column above the bleeding voxel; droplets (arterial jets at 0.5–1.2 m/s,
venous welling) land back into columns. Flow: head H = surface + h; each step moves
min(0.35·¼·ΔH, ¼·mobile) to lower neighbours, where mobile = h − 0.05 mm (wetting film); blood
older than 60 s is clotted and immobile. A column with h > 0.3 mm marks its top voxel BLOODCOVER:
it conducts like blood (0.7 S/m) and couples to the electrode within 3 mm, so buzzing in a pool
spreads current widely and seals nothing. Suction drains 2 ml/s over the tip area.

## Tissue mechanics (Sprint 4)
XPBD lattice on a 3 mm material grid: axis links plus face diagonals, compliance per layer (dermis
2e-6, fat 4e-5, fascia 5e-7, muscle 1e-5, lipoma 3e-5), edge and bottom nodes fixed, weak anchoring
to the material position (the bed sits on the body). Skin links are pre-tensioned: rest length
0.96× along Langer's lines, 0.985× across, so severed edges gape. A GPU probe samples each link's
material segment: any air sample on a link that ran through tissue severs it; max Ω along the link
shrinks its rest length by up to 12 % (collagen contraction). Lipoma nodes form a cluster linked to
the host by capsule links; when fewer than 40 % survive the cluster is free, gets gravity and can be
lifted by forceps (delivery = cluster centroid above the skin plane). Coag with > 20 heated muscle
voxels in the thermal box triggers a damped 6 Hz twitch of muscle nodes within 12 mm, amplitude
0.5–2 mm with current. Rendering samples the lattice displacement per vertex; the voxel physics
stays in material space.
