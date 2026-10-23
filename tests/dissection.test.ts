import { describe, it, expect } from 'vitest';
import { vec3 } from 'gl-matrix';
import { Tissue } from '@/core/tissueProperties';
import { Lattice, Attachment } from '@/mechanics/lattice';
import { Volume, packCell } from '@/core/volume';

/**
 * CPU mirrors of the mechanical-cut rules in sim.wgsl (`mechCut`): what a blunt spread separates and what resists,
 * and how a blade leaves fractional occupancy at its face.
 */
function bluntYields(ty: Tissue, rMm = 0): 'parts' | 'tears' | 'stretched' | 'resists' {
  if (ty === Tissue.Fat) return 'parts';
  if (ty === Tissue.Artery || ty === Tissue.Vein) return rMm <= 0.3 ? 'tears' : 'stretched';
  if (ty === Tissue.Nerve) return 'stretched';
  return 'resists';
}
/** fat next to a vessel over 0.3 mm stays as its cuff: the vessel is tented in the spread rather than bared */
const fatKeepsCuff = (neighbourVesselRMm: number) => neighbourVesselRMm > 0.3;
const sharpKeep = (d: number, h: number) => Math.min(Math.max(0.5 + d / h, 0), 1);
/** rounded-box distance used by both the blade and the spread wedge */
function cutBoxDistance(p: vec3, centre: vec3, axis: vec3, w: vec3, n: vec3, ha: number, hw: number, hn: number, r: number) {
  const rel = vec3.sub(vec3.create(), p, centre);
  const q = [Math.abs(vec3.dot(rel, axis)) - ha, Math.abs(vec3.dot(rel, w)) - hw, Math.abs(vec3.dot(rel, n)) - hn];
  const outside = Math.hypot(...q.map((v) => Math.max(v, 0)));
  return outside + Math.min(Math.max(q[0], q[1], q[2]), 0) - r;
}

describe('blunt dissection rules', () => {
  it('spreading parts fat but not the capsule, so the plane opens on the capsule surface', () => {
    expect(bluntYields(Tissue.Fat)).toBe('parts');
    expect(bluntYields(Tissue.LipomaCapsule)).toBe('resists');
    expect(bluntYields(Tissue.Lipoma)).toBe('resists');
  });
  it('septa, fasciae and skin resist: they have to be cut', () => {
    for (const t of [Tissue.Septum, Tissue.SuperficialFascia, Tissue.DeepFascia, Tissue.Dermis, Tissue.Epidermis, Tissue.Muscle]) expect(bluntYields(t)).toBe('resists');
  });
  it('small vessels tear under blunt force; larger ones and nerves are stretched, not divided', () => {
    expect(bluntYields(Tissue.Vein, 0.25)).toBe('tears');
    expect(bluntYields(Tissue.Artery, 0.8)).toBe('stretched');
    expect(bluntYields(Tissue.Nerve)).toBe('stretched');
  });
  it('a larger vessel keeps its fat cuff in the spread, so it is tented rather than bared and bleeding', () => {
    expect(fatKeepsCuff(0.5)).toBe(true);
    expect(fatKeepsCuff(0.3)).toBe(false);
    expect(fatKeepsCuff(0)).toBe(false);
  });
});

describe('blade geometry', () => {
  it('a blade leaves fractional occupancy at its face and removes the voxels it passes through', () => {
    const h = 0.25;
    expect(sharpKeep(-h, h)).toBe(0);           // a voxel centre a full voxel inside the blade is gone
    expect(sharpKeep(0, h)).toBe(0.5);          // on the face: half
    expect(sharpKeep(h, h)).toBe(1);            // a voxel away: untouched
  });
  it('the swept blade box covers the stroke: points between the start and end poses are inside', () => {
    const axis = vec3.fromValues(0, 1, 0), w = vec3.fromValues(1, 0, 0), n = vec3.fromValues(0, 0, 1);
    const a = vec3.fromValues(0, -1.5, 0), b = vec3.fromValues(6, -1.5, 0);   // 6 mm stroke, blade centre 1.5 mm deep
    const ha = 1.75, hw = 1.6, hn = 0.15, r = 0;
    const sub = 12;
    const dist = (p: vec3) => { let d = 1e9; for (let s = 0; s <= sub; s++) d = Math.min(d, cutBoxDistance(p, vec3.lerp(vec3.create(), a, b, s / sub), axis, w, n, ha, hw, hn, r)); return d; };
    expect(dist(vec3.fromValues(3, -2, 0))).toBeLessThan(0);          // mid-stroke, 2 mm deep: cut
    expect(dist(vec3.fromValues(3, -2, 0.1))).toBeLessThan(0.0 + 1e-6); // within the 0.15 mm half thickness
    expect(dist(vec3.fromValues(3, -2, 0.6))).toBeGreaterThan(0.3);   // 0.6 mm to the side: untouched
    expect(dist(vec3.fromValues(3, -3.6, 0))).toBeGreaterThan(0);     // below the blade tip
  });
});

/** A small synthetic bed: 2 mm skin over fat over muscle. */
function bed(sizeMm = 30, depthMm = 18, h = 1): Volume {
  const airLayers = 2;
  const n = Math.round(sizeMm / h), nz = Math.round(depthMm / h) + airLayers;
  const v = new Volume({ nx: n, ny: n, nz, h, airLayers });
  for (let k = 0; k < nz; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
    const idx = v.index(i, j, k);
    const depth = (v.surfaceK - (k + 0.5)) * h;
    if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
    v.occupancy[idx] = 1;
    v.cell[idx] = packCell(depth < 2 ? Tissue.Dermis : depth < 14 ? Tissue.Fat : Tissue.Muscle);
  }
  return v;
}

describe('two instruments on the lattice', () => {
  it('a latched hook holds its edge open while a second attachment pulls elsewhere', () => {
    const lat = new Lattice(bed(), 3, 0, { along: 0.94, across: 0.97 });
    for (let s = 0; s < 30; s++) lat.step(1 / 60, s / 60);
    // incision along z at x = 1.5 through the skin
    const severed = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length);
    lat.constraints.forEach((c, i) => {
      const xa = lat.mat[c.a * 3] - 1.5, xb = lat.mat[c.b * 3] - 1.5, ya = lat.mat[c.a * 3 + 1], yb = lat.mat[c.b * 3 + 1];
      if (xa * xb < 0 && Math.max(ya, yb) > -4.5) severed[i] = 1;
    });
    lat.applyProbe(severed, om);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    // hand B: hook on the left edge pulled 3 mm away from the cut and held (latched); hand A: forceps lifting the right edge
    const left = lat.nearestNode(vec3.fromValues(0, 0, 0), 2), right = lat.nearestNode(vec3.fromValues(3, 0, 0), 2);
    expect(left).toBeGreaterThanOrEqual(0); expect(right).toBeGreaterThanOrEqual(0);
    // measure the two edge nodes themselves (the nearest-node search would pick other nodes once these are pulled away)
    const gap = () => Math.hypot(lat.pos[left * 3] - lat.pos[right * 3], lat.pos[left * 3 + 1] - lat.pos[right * 3 + 1], lat.pos[left * 3 + 2] - lat.pos[right * 3 + 2]);
    const gap0 = gap();
    const hook: Attachment = { node: left, target: vec3.fromValues(lat.pos[left * 3] - 3, lat.pos[left * 3 + 1] + 1, lat.pos[left * 3 + 2]), strength: 0.25 };
    const forceps: Attachment = { node: right, target: vec3.fromValues(lat.pos[right * 3] + 1, lat.pos[right * 3 + 1] + 3, lat.pos[right * 3 + 2]), strength: 0.2 };
    lat.attachments.push(hook, forceps);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    const gap1 = gap();
    expect(gap1).toBeGreaterThan(gap0 + 1.5);                         // retraction widens the incision well beyond its own gape
    expect(lat.pos[right * 3 + 1]).toBeGreaterThan(lat.mat[right * 3 + 1] + 1.0); // the lifted edge rose
    // letting the forceps go while the hook stays latched: the lifted edge settles, the retracted one stays open
    lat.attachments.length = 0; lat.attachments.push(hook);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    const gap2 = gap();
    expect(gap2).toBeGreaterThan(gap0 + 1.0);
    expect(lat.pos[right * 3 + 1]).toBeLessThan(lat.mat[right * 3 + 1] + 1.0);
  });
});
