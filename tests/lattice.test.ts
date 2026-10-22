import { describe, it, expect } from 'vitest';
import { vec3 } from 'gl-matrix';
import { Lattice } from '@/mechanics/lattice';
import { Volume } from '@/core/volume';
import { Tissue } from '@/core/tissueProperties';
import { packCell } from '@/core/volume';

/** A small synthetic bed: 2 mm skin over fat over muscle, optional lipoma ball. */
function bed(sizeMm = 30, depthMm = 18, h = 1, lipoma = false): Volume {
  const airLayers = 2;
  const n = Math.round(sizeMm / h), nz = Math.round(depthMm / h) + airLayers;
  const v = new Volume({ nx: n, ny: n, nz, h, airLayers });
  for (let k = 0; k < nz; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
    const idx = v.index(i, j, k);
    const depth = (v.surfaceK - (k + 0.5)) * h;
    if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
    v.occupancy[idx] = 1;
    let ty = depth < 2 ? Tissue.Dermis : depth < 14 ? Tissue.Fat : Tissue.Muscle;
    if (lipoma) {
      const x = (i + 0.5 - n / 2) * h, y = (j + 0.5 - n / 2) * h;
      const r = Math.hypot(x, y, depth - 7);
      if (r < 4) ty = Tissue.Lipoma; else if (r < 4.8) ty = Tissue.LipomaCapsule;
    }
    v.cell[idx] = packCell(ty);
  }
  return v;
}

describe('XPBD lattice', () => {
  it('settles under pre-tension without drifting (skin stays near its material position)', () => {
    const lat = new Lattice(bed(), 3);
    for (let s = 0; s < 60; s++) lat.step(1 / 60, s / 60);
    let maxDisp = 0;
    for (let n = 0; n < lat.count; n++) maxDisp = Math.max(maxDisp, Math.hypot(lat.disp[n * 4], lat.disp[n * 4 + 1], lat.disp[n * 4 + 2]));
    expect(maxDisp).toBeLessThan(1.0);
  });

  it('a severed skin line gapes: edges retract 1-3 mm apart', () => {
    const lat = new Lattice(bed(), 3, 0, { along: 0.94, across: 0.97 });
    for (let s = 0; s < 30; s++) lat.step(1 / 60, s / 60);
    // sever every constraint crossing the plane x = 1.5 within the top 4 mm (an incision along z)
    const severed = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length);
    lat.constraints.forEach((c, i) => {
      const xa = lat.mat[c.a * 3] - 1.5, xb = lat.mat[c.b * 3] - 1.5, ya = lat.mat[c.a * 3 + 1], yb = lat.mat[c.b * 3 + 1];
      if (xa * xb < 0 && Math.max(ya, yb) > -4.5) severed[i] = 1;
    });
    lat.applyProbe(severed, om);
    for (let s = 0; s < 180; s++) lat.step(1 / 60, s / 60);
    const gap = lat.distanceBetween(vec3.fromValues(0, 0, 0), vec3.fromValues(3, 0, 0));
    expect(gap).toBeGreaterThan(3.0 + 0.6);  // the two nodes started 3 mm apart
    expect(gap).toBeLessThan(3.0 + 4.0);
  });

  it('frees the lipoma cluster once most capsule links are cut', () => {
    const lat = new Lattice(bed(30, 18, 1, true), 3);
    expect(lat.capsuleTotal).toBeGreaterThan(10);
    const severed = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length);
    let cut = 0;
    lat.constraints.forEach((c, i) => { if (c.kind === 1 && cut < lat.capsuleTotal * 0.3) { severed[i] = 1; cut++; } });
    lat.applyProbe(severed, om);
    expect(lat.lipomaFree).toBe(false);
    lat.constraints.forEach((c, i) => { if (c.kind === 1 && c.alive && cut < lat.capsuleTotal * 0.5) { severed[i] = 1; cut++; } });
    lat.applyProbe(severed, om);
    expect(lat.lipomaFree).toBe(true);
  });

  it('coagulation shrinks rest lengths by up to 12 %', () => {
    const lat = new Lattice(bed(), 3);
    const severed = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length).fill(5);
    lat.applyProbe(severed, om);
    const c = lat.constraints[0];
    expect(c.rest / c.rest0).toBeCloseTo(0.88, 2);
  });
});
