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
| Muscle, nerve, viscera | 2.6e5 | 2.94e39 / 90.9 | Jacques et al. (muscle, in-vitro) |
| Fat, lipoma | 3.0e5 | ln A set for 1/s at 65 °C | placeholder: fit to porcine bench |
| Collagenous (fascia, septa, areolar, serosa, vessel wall, capsule) | 3.06e5 | ln A set for 1/s at 65 °C | collagen denaturation ~65 °C |
| Blood (clotting of free blood) | 5.0e5 | ln A set for 1/s at 68 °C | albumin/fibrinogen denaturation; negligible below 55 °C |

## Tissue properties (starting values; IT'IS database v4.x, Gabriel 1996 for σ)
| Tissue | σ S/m @450 kHz | k W/mK | ρ kg/m³ | c J/kgK | perfusion ml/min/kg | water |
|---|---|---|---|---|---|---|
| Epidermis | 0.05 | 0.24 | 1190 | 3600 | 0 | 0.50 |
| Dermis | 0.30 | 0.37 | 1109 | 3391 | 106 | 0.65 |
| Fat | 0.04 | 0.21 | 911 | 2348 | 33 | 0.18 |
| Septum / fascia / capsule / serosa | 0.30 | 0.40 | 1100 | 3300 | 10–20 | 0.60 |
| Areolar tissue | 0.35 | 0.45 | 1050 | 3500 | 25 | 0.72 |
| Muscle | 0.55 | 0.49 | 1090 | 3421 | 37 | 0.75 |
| Viscera (bowel wall / parenchyma) | 0.55 | 0.52 | 1079 | 3540 | 800 | 0.76 |
| Blood / vessel | 0.70 | 0.52 | 1050 | 3617 | – | 0.80 |
| Nerve | 0.20 | 0.49 | 1075 | 3613 | 20 | 0.70 |

## Tissue mechanics (Sprint 5)
The XPBD lattice (3 mm material grid, axis links plus face diagonals, edge and bottom nodes fixed) now
behaves per tissue. Each link takes the properties of the softer of its two end layers; strain is
measured from the link's settled in-situ length (the lattice settles for 40 steps at construction and
records that state, so a bed lying there is not injured and pre-tension is not strain). Integration: 8
XPBD substeps per frame with the Lagrange multipliers reset at every substep (carrying them across
substeps let the stiff fascia and the J-curve pump energy into a resting bed until it tore itself apart);
damping, anchoring and attachment rates are defined at a 1/480 s substep and rescaled to the actual
one, so the bed behaves the same at 60 fps and in a 20 Hz headless run. Checked at rest on all four
scenario beds: no link yields or tears within 3 s.

| Tissue | compliance | knee strain | yield strain | tear strain | cohesion | coag shrink | coag stiffen | ooze µl/s/mm² |
|---|---|---|---|---|---|---|---|---|
| Epidermis | 1.0e-6 | 0.30 | 0.5 | 0.9 | 1.0 | 0.12 | 2.5 | 0 |
| Dermis | 2.0e-6 | 0.35 | 0.5 | 1.0 | 1.0 | 0.15 | 2.5 | 0.08 |
| Fat | 4.0e-5 | 0.50 | 0.25 | 0.6 | 0.3 | 0.04 | 0.6 | 0.01 |
| Septum | 5.0e-6 | 0.15 | 0.3 | 0.8 | 0.5 | 0.15 | 2 | 0.02 |
| Scarpa's fascia | 8.0e-7 | 0.05 | 0.15 | 0.3 | 0.9 | 0.18 | 2 | 0.02 |
| Deep fascia | 5.0e-7 | 0.05 | 0.15 | 0.25 | 0.9 | 0.18 | 2 | 0.02 |
| Muscle | 1.0e-5 | 0.25 | 0.35 | 0.7 | 0.7 | 0.10 | 2 | 0.15 |
| Artery / vein | 5–8e-6 | 0.30 | 0.6 | 1.2 | 1.0 | 0.25 | 2 | 0 |
| Nerve | 3.0e-6 | 0.20 | 0.4 | 0.9 | 1.0 | 0.10 | 1.5 | 0 |
| Lipoma | 3.0e-5 | 0.50 | 0.25 | 0.5 | 0.6 | 0.04 | 0.6 | 0.01 |
| Lipoma capsule | 4.0e-6 | 0.15 | 0.3 | 0.7 | 0.2 | 0.15 | 2 | 0.01 |
| Areolar tissue | 6.0e-5 | 0.80 | 0.4 | 1.2 | 0.1 | 0.10 | 1.5 | 0.02 |
| Viscera | 2.0e-5 | 0.30 | 0.3 | 0.4 | 0.8 | 0.08 | 1.5 | 0.15 |
| Serosa | 1.5e-6 | 0.08 | 0.2 | 0.6 | 0.9 | 0.15 | 2 | 0.02 |

- **Elasticity (J-curve).** Effective compliance = compliance × stiffMul / (1 + (ε/knee)²). Fascia stiffens
  at once (knee 0.05), skin after its toe region (0.35), fat and areolar tissue hardly at all. Muscle is
  anisotropic: along the fibre direction (`FLAG_FIBRE_X`) links are 0.6× as compliant and 1.6× as tough as
  across it. Provenance: collagen J-curves (Fung), skin toe region 0.3–0.5 strain; values are calibration
  knobs until bench data replace them.
- **Yield (stringiness).** Past the yield strain a link's rest length creeps toward its current length
  (rate 8 /s) so that its elastic strain relaxes to the yield strain: fat lobules and areolar tissue draw out
  into strands instead of snapping back. Yielded links through low-cohesion tissue (cohesion ≤ 0.5) are drawn
  as translucent strands between their deformed endpoints, thinning as they approach their tear strain.
- **Tearing.** A link whose total elongation (from the original length, however much it yielded) exceeds
  tear strain × tearMul for more than 40 ms breaks. The link's middle third is carved from the voxel volume
  by the GPU `tearCarve` kernel (radius 0.3 × lattice spacing): soft-tissue links carve any soft tissue,
  vessel and nerve links only their own type. The carved voxels are *cut air*, so a torn vessel bleeds and
  torn parenchyma oozes. Boundary links (one end pinned at the bed edge) never yield or tear.
- **Thermal state** (from the GPU probe per link: max Ω, max dryness 1 − w/w₀, any char sample):
  coagulated collagen shrinks by the tissue's coag shrink (dermis 15 %, fasciae 18 %, vessels 25 %, fat 4 %)
  with a smoothstep from Ω 0.5 to 2, and is `coagStiffen` times stiffer (rendered fat 0.6×: softer);
  desiccation stiffens by up to 50 % and cuts the tear strain by 65 % (brittle); char keeps 25 % of the tear
  strain; coagulated tissue 80 %.
- **Vessels** are pre-tensioned to 0.92× their length: a cut end recoils into the fat.
- **Anchoring** to the body scales from 15 % at the skin to 100 % at the bottom of the bed, so the superficial
  layers can be tented up by a hook or forceps while deep tissue stays put.

## Blunt dissection
A hemostat (Kelly) pushed into tissue and opened (`Space`) separates planes bluntly. The GPU `dissect`
kernel converts voxels within the carve radius (jaw radius + 0.35 mm) of the two jaw blades (entry point
to each tip) and of the span between the tips into *dissected air*, provided the voxel's tissue has cohesion
at or below the instrument threshold (spread 0.55: areolar tissue, fat lobules, the capsule plane, septa;
pushing closed tips 0.35: areolar tissue, fat, capsule plane only) and already touches air, so the plane
opens from the wound inward at about one voxel per frame. Vessels and nerves are never divided: they bridge
the opened plane as strands (the lattice pulls the two sides apart with attachments at the tips) until they
are torn or cut. Dissected air differs from cut air: an intact vessel exposed in a dissected plane does not
bleed, and a dissected plane weeps at 30 % of a cut surface's rate.

## Vessels and bleeding
Vessel voxels carry radius r (0.05 mm units). A vessel voxel bleeds only when its lumen is open: it has a
*cut-air* neighbour (cutting, vaporisation, a tear, a pre-made wound), not merely a dissected plane or the
skin surface beside it, and it is neither SEALED, COAPTED nor CHARPLUG. Flow of a fully transected end
Q(r) = 0.25 ml/s · r³ (r in mm), shared across the voxels of its cross-section, arterial pulsatile at 72 bpm
(0.35 + 1.3 sin³), venous steady at 0.3×. An arterial end spurts 70 % of its output as droplets (particles
landing back into the layer, up to 16 mm away), the rest wells into its column; a venous end 15 %.

Each exposed voxel carries a 5-bit **exposure clock** (bits 11–15 of the cell word) that advances about
every 1.5 s (vessels) or 2 s (raw surfaces). Small vessels spasm and thrombose with it: arterioles ≤ 0.35 mm
fall as exp(−clock/6) and stop at clock 20 (~30 s); 0.35–0.5 mm keep a 12 % trickle; arteries > 0.5 mm only
slow to 65 %. Venules ≤ 0.5 mm clot off by clock 24, veins keep 80 %. Gauze pressure over an end stops its
flow while held and runs the clock at 3× (tamponade); a wipe sets the clock back to 6, so a stopped arteriole
bleeds again at a third of its rate. Sealing when the wall collagen is denatured (Ω > 1): SEALED if the
lumen is apposed by forceps (FLAG_COAPTED), r ≤ 0.5 mm, or the vessel is not exposed to cut air; otherwise
CHARPLUG, which rebleeds with probability 0.15 · clamp((r − 0.5)/1.5) per second and resets its clock.

**Capillary ooze.** Every perfused raw surface voxel (tissue with ooze rate > 0, normal state, Ω < 1) with a
cut-air or dissected-air neighbour weeps ooze × h² × exp(−clock/7) µl/s (dissected planes 0.3×). A 30 mm
dermal incision weeps ~0.1 ml and settles within about a minute; muscle and viscera weep an order of
magnitude more. Coagulation (Ω > 1), desiccation and char set the clock to 31 (stopped); gauze tamponade
advances it 5× faster; a wipe turns it back to 6.

## Blood layer
One record per surface column: depth h (mm) above the tissue top, age, tissue top height, flow scratch,
clot fraction, temperature, stain and the top voxel index. Sources add volume to the column above the
bleeding voxel; droplets land back into columns. **Flow** is head-driven (H = surface + h) with thin-film
mobility: each step moves min(0.35 · clamp(mobile/0.25, 0.15, 1) · (1 − clot) · ¼ ΔH, ¼ mobile) to lower
neighbours, mobile = min(h − 0.05 mm (wetting film), liquid h): a deep pool spreads fast, a film creeps,
clot stays. The clot is carried as a mass (depth of clotted blood): only liquid flows, drains or soaks away,
fresh blood arriving in a column dilutes the fraction, and the fraction is re-derived after every transfer,
so neither suction nor refilling can ratchet it. **Clotting**: the fraction grows by the blood Arrhenius rate
(under a second at 70 °C) and, once the blood has stood for 20 s, by 1/45 per second (a firm clot in about a
minute). **Temperature** follows the tissue
top and cools to 37 °C; when the generator is active a 35 % share of the delivered power goes into every
column whose blood body lies within 0.8 mm of the electrode (over a ~2 mm footprint), so blood under or around
the electrode boils (the excess energy boils liquid off the column; 40 % of what leaves is thrown as droplets that
land back in the layer, the rest is steam, so spatter never creates blood) and cooks into
coagulum (clot 1, near-black above 110 °C). **Stain**: blood standing on a fresh raw surface soaks in (0.6/s
toward 1 at 0.2 mm depth), darkening it; suction leaves the stain, a wipe lifts some. A column with h > 0.3
mm marks its top voxel BLOODCOVER: it conducts like blood (0.7 S/m) and couples to the electrode within 3 mm,
so buzzing in a pool spreads current widely and seals nothing; a clotted cover (clot > 0.6, CLOTCOVER)
conducts at 30 %. **Suction** drains 2 ml/s over the tip area and, because a pool levels far faster than the
film model moves it, also draws the mobile blood of the same pool within three tip radii toward the tip (12 mm/s
of depth at the rim, falling to 0 at the capture radius); it lifts liquid only, so the clot fraction of
what remains rises, and the wetting film and stain stay. **Gauze** (4x4, 6 mm footprint, 8 ml capacity): pressed, it absorbs liquid at up to 20 mm
of depth per second × pressure and tamponades the surface and any vessel end beneath it; dragged faster than
15 mm/s with pressure it wipes, taking clot (4 mm/s) and stain with it but restarting the ooze and small
vessels. Blood lost is accumulated in microlitres; ooze is also totalled separately.

## Visceral organ
An optional organ (`organ` in the scenario) replaces the deep muscle: `muscleMm` of muscle under the deep
fascia, then serosa (`serosaMm`, fascia-like) over viscera (soft, friable, perfusion 800 ml/min/kg, low
impedance, heavy ooze). Subserosal vessels run 1 mm under the surface; an optional `bleeder` lies in the
serosa so that a pre-carved pit whose `floor` follows the organ opens it. Energy on the viscus counts
`visceraHeat` (voxels > 45 °C) and vaporised viscera for the perforation objectives.

## Open items
- Digitise FT10 output curves.
- Replace placeholder fat/collagen Arrhenius coefficients with bench-fit values.
- Add arc breakdown field strength measurements in smoke/steam.
- Mechanical constants (compliance, knee, yield, tear, cohesion, ooze rates) are first estimates from the
  soft-tissue literature and operative feel; calibrate against bench measurements and the surgeon's eye.
- Blood on vertical wound walls is assigned to the column above the bleeding voxel (the layer is a height
  field); it runs down into the wound rather than clinging to the wall.
