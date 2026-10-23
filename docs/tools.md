# Instrument model

The learner has a full minor-surgery kit. Every instrument is a composition of a small set of
*capabilities* against the tissue volume and the mechanical lattice, so adding an instrument is
a matter of geometry plus a capability list, not new physics.

| Capability | What it does in the sim | Instruments |
|---|---|---|
| **conduct** | Metal in contact with the active electrode (or acting as the electrode) carries current: buzzing forceps/hemostat delivers coaptive energy at the jaws; accidental contact with a retractor diverts current (alternate-site burn event) | all metal instruments |
| **grasp** | Jaw geometry closes on tissue: pinches vessels (occludes flow), lifts tissue (lattice constraint), crushes (traumatic vs atraumatic flag drives injury events) | Adson (toothed / smooth), DeBakey, Allis, Kocher, mosquito, Kelly, needle driver |
| **cut (mechanical)** | Divides tissue along a blade or between scissor jaws with no thermal effect; exposes vessels | #15 / #10 scalpel, Metzenbaum, Mayo, iris scissors |
| **retract** | Positional constraint on the lattice pulling tissue aside; exposure for the learner and lateral tension that changes how cut mode behaves. Tissue pulled past its tear strain tears | Senn, Army-Navy, skin hooks, Ragnell |
| **spread** | Two-point retraction held open without a hand | Weitlaner, Gelpi |
| **blunt dissect** | Separates low-cohesion planes (areolar tissue, fat lobules, the lipoma capsule plane, septa) when spread or pushed; vessels and nerves bridge the plane as strands until torn or cut | closed Metzenbaum, hemostat tips, peanut |
| **occlude** | Permanently closes a vessel of any size | ligature (tie), hemoclip |
| **suction** | Clears liquid blood and smoke from the field; leaves clot behind | Frazier, Yankauer |
| **absorb / tamponade / wipe** | Soaks up blood, presses capillaries and vessel ends closed while held, wipes clot and stain off (and restarts the ooze) when dragged | 4x4 gauze, peanut sponge |
| **irrigate** | Wets the field, lowers tissue impedance locally, clears char | bulb syringe |

### Tool definitions
- **Bovie pencil** (blade, needle, ball, extended): conduct. Built.
- **Adson forceps** toothed and smooth: grasp, conduct, lift. Built: closed jaws coapt tissue (FLAG_COAPTED), pinch vessels, act as the electrode when the generator is activated (buzzing), and carry the nearest lattice node.
- **Kelly hemostat**: grasp (coapt, buzz), blunt dissect, lift. Built. Left button closes the jaws and pushes the tips into the tissue (pen pressure sets the depth, 0.8–3.4 mm). Closed jaws that are moved push along the loosest planes (cohesion ≤ 0.35). Holding `Space` with the jaws closed opens them inside the tissue over half a second to 8 mm: the `dissect` kernel separates everything with cohesion ≤ 0.55 within 1.05 mm of the jaw blades and of the span between the tips, and the lattice pulls the two sides apart so areolar tissue strings out and fat lobules part along their septa. Bridging vessels tear (and bleed) if stretched past 120 %.
- **Frazier suction**: suction. Built: drains liquid blood within 2.5 mm at 2 ml/s and draws the mobile blood of the same pool within 7.5 mm toward the tip; pulls smoke; clot, the wetting film and the stain are left behind.
- **4x4 gauze**: absorb, tamponade, wipe. Built: a 6 mm footprint pressed with the left button (pen pressure sets how hard) soaks up to 20 mm of liquid depth per second until it holds 8 ml (`U` swaps in a fresh one; the pad reddens as it fills). Held still it stops any vessel end beneath it and speeds capillary hemostasis fivefold; dragged faster than 15 mm/s it wipes clot and stain off but sets the exposure clock back so the surface oozes again and spasmed arterioles rebleed. The pad also compresses the lattice under it.
- **Skin hook**: retract. Built: press attaches the nearest lattice node, drag retracts, release lets go. Senn (two hooks) pending.
- **Allis / Kocher**: grasp (traumatic). Phase 2.
- **Metzenbaum / Mayo**: cut, blunt dissect. Phase 2.
- **#15 scalpel**: cut. Phase 2.
- **Weitlaner**: spread. Phase 2.
- **Needle driver + suture, ties, clips**: occlude; closure. Phase 2.

### Interaction model
One tool in the dominant hand (pointer) and one in the non-dominant hand (second pointer on a
tablet, or keyboard-anchored). Instrument choice via radial menu or number keys. Every instrument
exposes the same interface: `pose`, `activate`, `release`, and a list of capabilities with their
contact geometry, so the physics pipeline never knows which instrument it is dealing with. The
secondary-tool slot of the physics (`setSecondaryTool`) carries one capability at a time: grasp (1),
suction (2), gauze (3), spread (4) or push (5), with the instrument's motion and pressure alongside.
