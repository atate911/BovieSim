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
- **Skin hook**: retract. Built: press attaches the nearest lattice node, drag retracts, release lets go. In the left hand it stays where it is put (below). Senn (two hooks) pending.
- **Allis / Kocher**: grasp (traumatic). Phase 2.
- **Metzenbaum scissors**: cut, blunt dissect. Built. A click snips the sheet of tissue between the open tips along the blades (3 mm wide, as deep as the tips are in) with the `mechCut` kernel: what a septum, a strand or a controlled vessel across a plane needs. Held, the closed tips go into the tissue (pen pressure sets the depth, 1–6 mm) and `Space` opens them to 8 mm: blunt dissection through the hemostat's `dissect` kernel with the spread threshold. Mayo pending.
- **#15 scalpel**: cut. Built. A thin blade in the plane of the handle and the direction of travel; pen pressure sets the depth (0.5–5 mm; the mouse gives about 2.8 mm, through the dermis into fat). Each frame the blade sweeps from its last pose to the current one through the `mechCut` kernel: everything in its path becomes cut air with no thermal effect, the wall keeps fractional occupancy so it extracts smoothly, the lattice probe severs the links it crossed so the incision gapes, and the faces it leaves are raw wound surfaces under the existing rules (capillary ooze, bleeding from transected vessels).
- **Weitlaner**: spread. Phase 2.
- **Needle driver + suture, ties, clips**: occlude; closure. Phase 2.

### Interaction model: two hands on one mouse
The right hand is the pointer and the left button and holds any instrument (`Tab` cycles, `` ` `` goes back).
The left hand is the **right mouse button** (a pen's barrel button) and holds a skin hook, Adson forceps or
Frazier suction of its own (`Shift+Tab` cycles; none by default, and then the right button orbits as before).

- **Place and hold.** A right-button press on the tissue sends the left hand's instrument there and it takes
  hold (the hook catches the nearest lattice node, the forceps close on it and coapt what is between their
  tips); dragging pulls; letting the button go leaves it there, holding its traction. The pointer is the right
  hand's again and both instruments are on the field. This is how the non-dominant hand works at the table:
  it places a retractor or a forceps, holds still while the dominant hand works, and moves now and then.
- **Adjust, move, remove.** Press the right button on the held instrument and drag to change the pull; press
  it somewhere else to move the hold there; click it to take it off. The wheel lifts while the button is down.
- **Trim from the keyboard.** While the left hand holds, `W A S D` slide its pull across the field (as seen on
  screen) and `E` / `F` raise and lower it. The user's own left hand rests on those keys, so traction can
  follow the dissection while the mouse stays on the right hand: both hands move at once.
- **The camera** keeps the right button wherever the left hand does not take it: off the tissue (on the drapes),
  or always when the left hand is empty. `Alt` + left drag orbits anywhere.
- **Suction** in the left hand is the exception to holding: it sucks while the button is down and follows the pointer.

The state machine is `src/tools/hands.ts` (`Core/Hands.cs`), pure and unit-tested. The held instrument is drawn on
the tissue it caught (its node plus the offset of the grasp), not at the pull target: an incised edge follows about
two thirds of a pull, intact skin barely moves, and past about 6 mm the fat under the edge starts to tear
(`tissue.torn`), which is the feedback for traction in place of force in the hand.

**What is under the cursor is what is seen there.** Tissue is drawn displaced by the lattice while the voxel
physics stays in the undeformed bed, so every instrument is posed in world coordinates (where it is seen) and
mapped to material coordinates at the boundary to the kernels. The pick marches the cursor ray through the
field as displayed, taking each sample back through the lattice (`Lattice.worldToMaterial`: Newton's method on
the trilinear displacement field, the same in the WGSL pick and the Unity height-map picker), so under a
retracted edge the pointer reaches the wound floor rather than the skin that used to cover it. Electrode tip,
jaws, suction tip, gauze, dissection entry point and blade are mapped the same way; bleed points and spatter
come back the other way.

**Instruments touching.** The active pencil within a millimetre of forceps held in the left hand makes their jaws
the electrode: buzzing the vessel they hold, as it is done. Touching the hook or the suction instead diverts the
current into the tissue under it: an alternate-site burn (`instrument.contact`).

Every instrument exposes the same interface: `pose`, `activate`, `release`, and a list of capabilities with their
contact geometry, so the physics pipeline never knows which instrument it is dealing with. The
secondary-tool slot of the physics (`setSecondaryTool`) carries one capability at a time: grasp (1),
suction (2), gauze (3), spread (4) or push (5), with the instrument's motion and pressure alongside. With two
hands the right hand's instrument takes the slot when it needs it, otherwise the left hand's grasp or suction:
while the right hand is itself grasping, sucking, dabbing or dissecting, forceps held in the left hand keep
their traction but stop pressing the vessel walls together until the slot is free again.

A pen and a mouse share one cursor on Windows, which is why the second hand is a button and not a second
pointer. Touch (pen plus a finger) or a gamepad stick could drive the left hand directly later.
