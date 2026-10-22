# Calibration table

Simulated vs published monopolar effects. Fill in as Sprint 1 completes.

| Mode / W | Dwell or speed | Published lateral thermal spread | Published depth | Simulated spread | Simulated depth | Status |
|---|---|---|---|---|---|---|
| Pure cut 20 W | 10 mm/s | ~0.2–0.5 mm | – | | | ☐ |
| Pure cut 30 W | 10 mm/s | ~0.3–0.7 mm | – | ~1.0 mm per side (Ω≥0.53 zone 3.7 mm across a 0.5 mm blade + 1.0 mm groove; one voxel = 0.5 mm) | groove ~1.4 mm, Ω zone 1.65 mm | preliminary, 0.5 mm voxels; removal 7 mm³/s ≈ expected for a 1.4 mm deep stroke |
| Pure cut 40 W | 10 mm/s | ~0.4–0.9 mm | – | | | ☐ |
| Blend 30 W | 10 mm/s | ~0.6–1.2 mm | – | | | ☐ |
| Fulgurate 30 W | 1 s dwell | ~1–2 mm | ~0.5–1 mm | | | ☐ |
| Fulgurate 40 W | 3 s dwell | ~2–3 mm | ~1–2 mm | | | ☐ |
| Contact coag (Fulgurate waveform, blade pressed ~1 mm) 30 W | 2 s dwell | footprint + ~1–2 mm | ~1–2 mm | footprint + ~0.3 mm each side (zone 7.05 mm across a 2.4 mm blade + 2 mm arc reach each side); 8.6 mm before the near-field model | 1.85 mm (3.9 mm before the near-field model); surface desiccated | preliminary, 0.5 mm voxels; Z 360→480 Ω over the dwell |
| Spray 40 W | 1 s | ~2–4 mm, shallow | <0.5 mm | | | ☐ |

Model history for the cut-line case (30 W pure, 26 mm in 2.5 s, 0.5 mm voxels): whole-face
contact → 3.8 mm³ removed, blanch zone 5.5 × 2.35 mm; thin contact front → 10.7 mm³, 5.5 × 2.35;
near-field deposition weighted toward tissue in the electrode's path → 17.7 mm³, 3.7 × 1.65 mm
with a visible groove. The remaining rim width is at the voxel size; repeat at 0.25 mm.

Measurement method: `scripts/activate.mjs` with `slice=perp` runs a scripted activation and reports
the Ω ≥ 0.53 zone extent in the section perpendicular to the blade (`__simControl.measureSlice`).
Runs so far were at 0.5 mm voxels under a software GPU; repeat at 0.25 mm on hardware.

Published ranges are indicative summaries of the monopolar electrosurgery thermal-spread
literature (porcine skin/muscle, blade electrode). Replace with specific citations as they are
added to `docs/reference/`.
