# Instrument model

The learner has a full minor-surgery kit. Every instrument is a composition of a small set of
*capabilities* against the tissue volume and the mechanical lattice, so adding an instrument is
a matter of geometry plus a capability list, not new physics.

| Capability | What it does in the sim | Instruments |
|---|---|---|
| **conduct** | Metal in contact with the active electrode (or acting as the electrode) carries current: buzzing forceps/hemostat delivers coaptive energy at the jaws; accidental contact with a retractor diverts current (alternate-site burn event) | all metal instruments |
| **grasp** | Jaw geometry closes on tissue: pinches vessels (occludes flow), lifts tissue (lattice constraint), crushes (traumatic vs atraumatic flag drives injury events) | Adson (toothed / smooth), DeBakey, Allis, Kocher, mosquito, Kelly, needle driver |
| **cut (mechanical)** | Divides tissue along a blade or between scissor jaws with no thermal effect; exposes vessels | #15 / #10 scalpel, Metzenbaum, Mayo, iris scissors |
| **retract** | Positional constraint on the lattice pulling tissue aside; exposure for the learner and lateral tension that changes how cut mode behaves | Senn, Army-Navy, skin hooks, Ragnell |
| **spread** | Two-point retraction held open without a hand | Weitlaner, Gelpi |
| **blunt dissect** | Separates low-cohesion planes (fat lobules, lipoma capsule) when spread or pushed | closed Metzenbaum, hemostat tips, peanut |
| **occlude** | Permanently closes a vessel of any size | ligature (tie), hemoclip |
| **suction** | Clears blood and smoke from the field | Frazier, Yankauer |
| **irrigate** | Wets the field, lowers tissue impedance locally, clears char | bulb syringe |

### Tool definitions (Phase 1 geometry, Phase 2+ behaviour as noted)
- **Bovie pencil** (blade, needle, ball, extended): conduct. Phase 1.
- **Adson forceps** toothed and smooth: grasp, conduct. Phase 1 grasp + buzz on vessels.
- **Frazier suction**: suction. Phase 1 (blood sprint).
- **Skin hooks / Senn**: retract. Phase 1 (mechanics sprint).
- **Hemostat (mosquito/Kelly)**: grasp (locking), conduct, blunt dissect. Phase 1 mechanics sprint.
- **Allis / Kocher**: grasp (traumatic). Phase 2.
- **Metzenbaum / Mayo**: cut, blunt dissect. Phase 2.
- **#15 scalpel**: cut. Phase 2.
- **Weitlaner**: spread. Phase 2.
- **Needle driver + suture, ties, clips**: occlude; closure. Phase 2.

### Interaction model
One tool in the dominant hand (pointer) and one in the non-dominant hand (second pointer on a
tablet, or keyboard-anchored). Instrument choice via radial menu or number keys. Every instrument
exposes the same interface: `pose`, `activate`, `release`, and a list of capabilities with their
contact geometry, so the physics pipeline never knows which instrument it is dealing with.
