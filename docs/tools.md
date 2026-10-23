# Instrument model

The learner has a full minor-surgery kit. Every instrument is a composition of a small set of
*capabilities* against the tissue volume and the mechanical lattice, so adding an instrument is
a matter of geometry plus a capability list, not new physics.

| Capability | What it does in the sim | Instruments |
|---|---|---|
| **conduct** | Metal in contact with the active electrode (or acting as the electrode) carries current: buzzing forceps/hemostat delivers coaptive energy at the jaws; accidental contact with a retractor diverts current (alternate-site burn event) | all metal instruments |
| **grasp** | Jaw geometry closes on tissue: pinches vessels (occludes flow), lifts tissue (lattice constraint), crushes (traumatic vs atraumatic flag drives injury events) | Adson (toothed / smooth), DeBakey, Allis, Kocher, mosquito, Kelly, needle driver |
| **cut (mechanical)** | Divides tissue along a blade or between scissor jaws with no thermal effect; exposes vessels; a fresh dermal edge oozes until it clots or is coagulated | #15 / #10 scalpel, Metzenbaum, Mayo, iris scissors |
| **retract** | Positional constraint on the lattice pulling tissue aside; exposure for the learner and lateral tension that changes how cut mode behaves | Senn, Army-Navy, skin hooks, Ragnell |
| **spread** | Two-point retraction held open without a hand | Weitlaner, Gelpi |
| **blunt dissect** | Separates low-cohesion planes (fat lobules, the plane on a lipoma capsule) when spread; septa, fasciae and the capsule resist, small vessels tear, larger vessels and nerves are tented | closed Metzenbaum, hemostat tips, peanut |
| **occlude** | Permanently closes a vessel of any size | ligature (tie), hemoclip |
| **suction** | Clears blood and smoke from the field | Frazier, Yankauer |
| **irrigate** | Wets the field, lowers tissue impedance locally, clears char | bulb syringe |

### Tool definitions
- **Bovie pencil** (blade, needle, ball): conduct. Built.
- **#15 scalpel**: cut (mechanical). Built: a thin blade in the plane of the handle and the direction of travel,
  pressure sets the depth (0.5 to 5 mm), and each frame the blade sweeps from its last pose to the current one
  through the `mechCut` kernel (sharp mode). Everything in its path is divided with no thermal effect; the wall keeps
  fractional occupancy so it extracts smoothly; the lattice probe severs the links it crossed so the incision gapes;
  transected vessels bleed by the existing rules; a fresh dermal edge oozes from the dermal plexus until it clots
  (two minutes on average) or is coagulated.
- **Metzenbaum scissors**: cut, blunt dissect. Built: a click snips the sheet between the open tips along the blades
  (sharp mode); Ctrl + hold inserts the closed tips and opens them (blunt mode). In the spread wedge fat parts, so
  the plane around a capsule opens on the capsule surface; septa, fasciae, skin, muscle, the capsule and the lipoma
  resist and are counted (`plane.resists`); vessels up to 0.3 mm tear (`vessel.torn`); larger vessels keep their fat
  cuff and are tented (`vessel.stretched`), as are nerves (`nerve.stretched`).
- **Adson forceps** toothed and smooth: grasp, conduct, lift. Built: closed jaws coapt tissue (FLAG_COAPTED), pinch
  vessels, carry the nearest lattice node (the wheel lifts), and conduct: buzzed either by a pedal activation while
  held in the dominant hand, or by touching the active pencil to them while they are held in the other hand.
- **Frazier suction**: suction. Built: drains the blood layer within 2.5 mm at 2 ml/s and pulls smoke.
- **Skin hook**: retract. Built: press attaches the nearest lattice node, drag retracts, release lets go; held in
  the non-dominant hand it latches and keeps its traction. The active pencil touching a hook or suction held in the
  other hand diverts the current into the tissue under it (`instrument.contact`, an alternate-site burn).
- **Hemostat (mosquito/Kelly)**: grasp (locking), conduct, blunt dissect. Pending: the scissors' spread covers the
  blunt capability for now.
- **Allis / Kocher**: grasp (traumatic). Pending.
- **Senn / Weitlaner**: two-point retraction and self-retaining spread. Pending.
- **Needle driver + suture, ties, clips**: occlude; closure. Pending (decision point only).

### Interaction model: two hands on one pointer
A pen and a mouse share one cursor on Windows, so the two hands cannot be two pointers. Instead the
non-dominant hand is *latched*: hold **Space** and the pointer drives it (press to grasp or catch, drag to
pull, wheel to lift); release Space and it stays where it is, keeping its traction, while the pointer
returns to the dominant hand. Hold Space again and it comes back live still holding; a new press re-grasps,
a release lets go. This matches how the non-dominant hand works at the table: it places forceps or a
retractor, holds still while the dominant hand works, and moves now and then. The state machine is
`src/tools/hands.ts` (`Core/Hands.cs` in Unity) with unit tests. Touch (pen plus a finger) or a gamepad stick
can drive hand B directly later; the latch stays as the baseline that needs no second device.

The dominant hand cycles pencil, scalpel, scissors, forceps, suction and hook (Tab); the non-dominant hand
cycles none, forceps, hook and suction (Shift+Tab). The GPU keeps one electrode (the pencil, buzzed forceps,
or a touched retractor's tine), one secondary tool slot (closed jaws for coaptation or a suction tip; the
non-dominant hand's instrument takes the slot when both hands hold one) and one mechanical cut per frame.

### Known gaps
- The pick and the cut act in material space while the rendered tissue follows the lattice: with a strongly
  retracted edge the instrument draws a little off the displaced surface (the same gap the status notes).
- A vessel bared along its side by any means bleeds like a transected end; blunt spreading now keeps larger
  vessels in their cuff, but a cut that opens a vessel's side still bleeds at full flow.
- Lifting a held instrument is a wheel gesture (no third axis on a tablet).
