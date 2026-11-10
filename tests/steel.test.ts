import { describe, it, expect } from 'vitest';
import { vec3 } from 'gl-matrix';
import { Tissue } from '@/core/tissueProperties';
import { Lattice, Attachment } from '@/mechanics/lattice';
import { Volume, packCell } from '@/core/volume';
import { metalDistance } from '@/tools/instrument';

/** CPU mirrors of the blade geometry in sim.wgsl (`mechCut`). */
const sharpKeep = (d: number, h: number) => Math.min(Math.max(0.5 + d / h, 0), 1);
function cutBoxDistance(p: vec3, centre: vec3, axis: vec3, w: vec3, n: vec3, ha: number, hw: number, hn: number) {
  const rel = vec3.sub(vec3.create(), p, centre);
  const q = [Math.abs(vec3.dot(rel, axis)) - ha, Math.abs(vec3.dot(rel, w)) - hw, Math.abs(vec3.dot(rel, n)) - hn];
  return Math.hypot(...q.map((v) => Math.max(v, 0))) + Math.min(Math.max(q[0], q[1], q[2]), 0);
}

describe('steel: the blade', () => {
  it('leaves fractional occupancy at its face and removes the voxels it passes through', () => {
    const h = 0.25;
    expect(sharpKeep(-h, h)).toBe(0);
    expect(sharpKeep(0, h)).toBe(0.5);
    expect(sharpKeep(h, h)).toBe(1);
  });
  it('covers the whole stroke between two frames, and nothing beside or below it', () => {
    const axis = vec3.fromValues(0, 1, 0), w = vec3.fromValues(1, 0, 0), n = vec3.fromValues(0, 0, 1);
    const a = vec3.fromValues(0, -1.5, 0), b = vec3.fromValues(6, -1.5, 0);   // 6 mm stroke, blade centre 1.5 mm deep
    const ha = 1.75, hw = 1.6, hn = 0.15, sub = 16;
    const dist = (p: vec3) => { let d = 1e9; for (let s = 0; s <= sub; s++) d = Math.min(d, cutBoxDistance(p, vec3.lerp(vec3.create(), a, b, s / sub), axis, w, n, ha, hw, hn)); return d; };
    expect(dist(vec3.fromValues(3, -2, 0))).toBeLessThan(0);
    expect(dist(vec3.fromValues(3, -2, 0.1))).toBeLessThan(1e-6);
    expect(dist(vec3.fromValues(3, -2, 0.6))).toBeGreaterThan(0.3);
    expect(dist(vec3.fromValues(3, -3.6, 0))).toBeGreaterThan(0);
  });
});

describe('the pencil against another instrument', () => {
  it('measures the distance to the instrument metal along its shaft', () => {
    const tip = vec3.fromValues(0, 0, 0), axis = vec3.fromValues(0, 1, 0);
    expect(metalDistance(vec3.fromValues(2, 10, 0), tip, axis, 40, 1)).toBeCloseTo(1, 5);    // beside the shaft
    expect(metalDistance(vec3.fromValues(0, -3, 0), tip, axis, 40, 1)).toBeCloseTo(2, 5);    // beyond the tip
    expect(metalDistance(vec3.fromValues(0.5, 20, 0), tip, axis, 40, 1)).toBeLessThan(0);    // touching
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

describe('what is under the cursor is what is seen there', () => {
  it('maps a point on retracted tissue back to where that tissue lies in the undeformed bed', () => {
    const lat = new Lattice(bed(), 3);
    // a skin node dragged 5 mm sideways and 2 mm up, and its neighbour half as far: the cell is stretched to more
    // than twice its size, where the plain fixed-point inverse (gradient above 1) no longer converges
    const node = lat.nearestNode(vec3.fromValues(0, 0, 0), 2);
    expect(node).toBeGreaterThanOrEqual(0);
    lat.pos[node * 3] -= 5; lat.pos[node * 3 + 1] += 2;
    lat.pos[(node - 1) * 3] -= 2.5; lat.pos[(node - 1) * 3 + 1] += 1;
    // round trip at material points around the dragged node: material -> seen -> material
    let worst = 0;
    for (const dx of [-2.5, -1, 0, 1, 2.5]) for (const dy of [-2.5, -1, -0.2]) for (const dz of [-2, 0, 2]) {
      const m = vec3.fromValues(lat.mat[node * 3] + dx, lat.mat[node * 3 + 1] + dy, lat.mat[node * 3 + 2] + dz);
      const back = lat.worldToMaterial(lat.materialToWorld(m));
      worst = Math.max(worst, vec3.distance(m, back));
    }
    expect(worst).toBeLessThan(0.15);   // well under a voxel
    // and the node itself: the point where it is seen maps back to where it belongs
    const seen = vec3.fromValues(lat.pos[node * 3], lat.pos[node * 3 + 1], lat.pos[node * 3 + 2]);
    const home = lat.worldToMaterial(seen);
    expect(vec3.distance(home, vec3.fromValues(lat.mat[node * 3], lat.mat[node * 3 + 1], lat.mat[node * 3 + 2]))).toBeLessThan(0.15);
  });

  it('is the identity where nothing has moved', () => {
    const lat = new Lattice(bed(), 3);
    const far = vec3.fromValues(9, -15, 9);   // deep, firmly anchored
    expect(vec3.distance(lat.worldToMaterial(far), vec3.sub(vec3.create(), far, lat.displacementAt(far)))).toBeLessThan(0.02);
  });
});

describe('two instruments on the lattice', () => {
  it('a hook left holding keeps its edge retracted while a second instrument comes and goes', () => {
    const lat = new Lattice(bed(), 3, 0, { along: 0.94, across: 0.97 });
    // incision along z at x = 1.5 through the skin
    const flags = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length);
    lat.constraints.forEach((c, i) => {
      const xa = lat.mat[c.a * 3] - 1.5, xb = lat.mat[c.b * 3] - 1.5, ya = lat.mat[c.a * 3 + 1], yb = lat.mat[c.b * 3 + 1];
      if (xa * xb < 0 && Math.max(ya, yb) > -4.5) flags[i] = 1;
    });
    lat.applyProbe(flags, om);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    const left = lat.nearestNode(vec3.fromValues(0, 0, 0), 2), right = lat.nearestNode(vec3.fromValues(3, 0, 0), 2);
    expect(left).toBeGreaterThanOrEqual(0); expect(right).toBeGreaterThanOrEqual(0);
    const gap = () => Math.hypot(lat.pos[left * 3] - lat.pos[right * 3], lat.pos[left * 3 + 1] - lat.pos[right * 3 + 1], lat.pos[left * 3 + 2] - lat.pos[right * 3 + 2]);
    const gap0 = gap();
    // left hand: hook on one edge, pulled 4 mm away and left there (an incised edge follows about two thirds of the
    // pull); right hand: forceps lifting the other edge
    const hook: Attachment = { node: left, target: vec3.fromValues(lat.pos[left * 3] - 4, lat.pos[left * 3 + 1] + 1, lat.pos[left * 3 + 2]), strength: 0.4 };
    const forceps: Attachment = { node: right, target: vec3.fromValues(lat.pos[right * 3] + 0.5, lat.pos[right * 3 + 1] + 2.5, lat.pos[right * 3 + 2]), strength: 0.4 };
    lat.attachments.push(hook, forceps);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    const lifted = lat.pos[right * 3 + 1] - lat.mat[right * 3 + 1];
    expect(gap()).toBeGreaterThan(gap0 + 1.5);
    expect(lifted).toBeGreaterThan(0.8);
    // the forceps let go, the hook stays: the lifted edge settles, the retracted one stays open
    lat.attachments.length = 0; lat.attachments.push(hook);
    for (let s = 0; s < 120; s++) lat.step(1 / 60, s / 60);
    expect(gap()).toBeGreaterThan(gap0 + 1.2);
    expect(lat.pos[right * 3 + 1] - lat.mat[right * 3 + 1]).toBeLessThan(lifted * 0.6);
  });
});
