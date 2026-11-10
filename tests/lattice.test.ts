import { describe, it, expect } from 'vitest';
import { vec3 } from 'gl-matrix';
import { Lattice, effectiveCompliance, thermalMultipliers } from '@/mechanics/lattice';
import { Volume } from '@/core/volume';
import { Tissue, TISSUE } from '@/core/tissueProperties';
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

/** A bed made of one tissue (18 mm deep). */
function uniformBed(ty: Tissue, sizeMm = 30, depthMm = 18, h = 1): Volume {
  const airLayers = 2;
  const n = Math.round(sizeMm / h), nz = Math.round(depthMm / h) + airLayers;
  const v = new Volume({ nx: n, ny: n, nz, h, airLayers });
  for (let k = 0; k < nz; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
    const idx = v.index(i, j, k);
    const depth = (v.surfaceK - (k + 0.5)) * h;
    if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
    v.occupancy[idx] = 1; v.cell[idx] = packCell(ty);
  }
  return v;
}

/** Hooks the surface node nearest the bed centre and lifts it by `liftMm` over `seconds`. */
function pull(lat: Lattice, liftMm: number, seconds = 2): number {
  const node = lat.nearestNode(vec3.fromValues(0, 0, 0), 3);
  expect(node).toBeGreaterThanOrEqual(0);
  const start = vec3.fromValues(lat.mat[node * 3], lat.mat[node * 3 + 1], lat.mat[node * 3 + 2]);
  const steps = Math.round(seconds * 60);
  for (let s = 0; s < steps; s++) {
    const f = (s + 1) / steps;
    lat.attachments.length = 0;
    lat.attachments.push({ node, target: vec3.fromValues(start[0], start[1] + liftMm * f, start[2]), strength: 0.8 });
    lat.step(1 / 60, s / 60);
  }
  return node;
}

describe('XPBD lattice', () => {
  it('settles under pre-tension without drifting (skin stays near its material position)', () => {
    const lat = new Lattice(bed(), 3);
    for (let s = 0; s < 60; s++) lat.step(1 / 60, s / 60);
    let maxDisp = 0;
    for (let n = 0; n < lat.count; n++) maxDisp = Math.max(maxDisp, Math.hypot(lat.disp[n * 4], lat.disp[n * 4 + 1], lat.disp[n * 4 + 2]));
    expect(maxDisp).toBeLessThan(1.2);
    // the resting pre-tension is not an injury: nothing yields or tears while the bed sits there
    expect(lat.yieldedLinks).toBe(0);
    expect(lat.tornLinks).toBe(0);
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

  it('coagulation shrinks each tissue by its own collagen fraction: dermis 15 %, muscle 10 %, fat 4 %', () => {
    const lat = new Lattice(bed(), 3);
    const severed = new Uint8Array(lat.constraints.length), om = new Float32Array(lat.constraints.length).fill(5);
    lat.applyProbe(severed, om);
    const by = (t: Tissue) => lat.constraints.find((c) => c.tissue === t && c.kind === 0)!;
    expect(by(Tissue.Dermis).rest / by(Tissue.Dermis).rest0).toBeCloseTo(0.85, 2);
    expect(by(Tissue.Muscle).rest / by(Tissue.Muscle).rest0).toBeCloseTo(0.90, 2);
    expect(by(Tissue.Fat).rest / by(Tissue.Fat).rest0).toBeCloseTo(0.96, 2);
    // partial damage shrinks partially
    om.fill(0.9); lat.applyProbe(severed, om);
    const r = by(Tissue.Dermis).rest / by(Tissue.Dermis).rest0;
    expect(r).toBeGreaterThan(0.85); expect(r).toBeLessThan(1.0);
  });
});

describe('tissue mechanics', () => {
  it('collagenous tissue stiffens with strain (J-curve): fascia at once, skin past its toe region, fat and areolar tissue barely', () => {
    const ratio = (t: Tissue) => effectiveCompliance(t, 0) / effectiveCompliance(t, 0.3);
    expect(ratio(Tissue.DeepFascia)).toBeGreaterThan(30);
    expect(ratio(Tissue.Dermis)).toBeGreaterThan(1.5);
    expect(ratio(Tissue.Dermis)).toBeLessThan(ratio(Tissue.LipomaCapsule));
    expect(ratio(Tissue.Fat)).toBeLessThan(1.5);
    expect(ratio(Tissue.Areolar)).toBeLessThan(1.2);
    // at rest: fascia far stiffer than fat, fat stiffer than areolar tissue
    expect(effectiveCompliance(Tissue.DeepFascia, 0)).toBeLessThan(effectiveCompliance(Tissue.Fat, 0) / 20);
    expect(effectiveCompliance(Tissue.Fat, 0)).toBeLessThan(effectiveCompliance(Tissue.Areolar, 0));
  });

  it('tear and yield strains order the tissues the way they behave under traction', () => {
    const T = (t: Tissue) => TISSUE[t].tearStrain;
    // fascia splits at low strain; fat lobules part; dermis and vessels stretch far before giving
    expect(T(Tissue.DeepFascia)).toBeLessThan(T(Tissue.Fat));
    expect(T(Tissue.Fat)).toBeLessThan(T(Tissue.Dermis));
    expect(T(Tissue.Dermis)).toBeLessThanOrEqual(T(Tissue.Artery));
    expect(T(Tissue.Viscera)).toBeLessThan(T(Tissue.Muscle));
    // areolar tissue yields long before it tears: the stringy plane
    expect(TISSUE[Tissue.Areolar].tearStrain / TISSUE[Tissue.Areolar].yieldStrain).toBeGreaterThan(2.5);
    // blunt dissection separates areolar tissue and fat lobules, not dermis or fascia
    const C = (t: Tissue) => TISSUE[t].cohesion;
    expect(C(Tissue.Areolar)).toBeLessThan(C(Tissue.LipomaCapsule));
    expect(C(Tissue.LipomaCapsule)).toBeLessThan(C(Tissue.Fat));
    expect(C(Tissue.Fat)).toBeLessThan(C(Tissue.Septum));
    expect(C(Tissue.Septum)).toBeLessThan(C(Tissue.DeepFascia));
    expect(C(Tissue.Dermis)).toBe(1);
  });

  it('lifting a point 6 mm tears fat but not dermis, and areolar tissue strings out instead', () => {
    const fat = new Lattice(uniformBed(Tissue.Fat), 3);
    pull(fat, 6);
    expect(fat.tornLinks).toBeGreaterThan(0);
    const tears = fat.takeTears();
    expect(tears.length).toBeGreaterThan(0);
    expect(tears[0][7]).toBe(Tissue.Fat);
    expect(fat.takeTears().length).toBe(0); // taken once

    const skin = new Lattice(uniformBed(Tissue.Dermis), 3);
    pull(skin, 6);
    expect(skin.tornLinks).toBe(0);

    const are = new Lattice(uniformBed(Tissue.Areolar), 3);
    pull(are, 4);
    expect(are.tornLinks).toBe(0);
    expect(are.yieldedLinks).toBeGreaterThan(0);
    const buf = new Float32Array(8 * 64);
    expect(are.strandData(buf)).toBeGreaterThan(0);
    // a yielded strand keeps its new length: strain relaxes toward the yield strain
    const strand = are.constraints.find((c) => c.alive && c.plastic > 1.05)!;
    expect(strand).toBeDefined();
    expect(strand.rest).toBeGreaterThan(strand.rest0 * 1.05);
  });

  it('desiccated and charred tissue is brittle: dried dermis tears under a pull healthy dermis survives', () => {
    const skin = new Lattice(uniformBed(Tissue.Dermis), 3);
    const flags = new Uint8Array(skin.constraints.length), om = new Float32Array(skin.constraints.length), dry = new Float32Array(skin.constraints.length).fill(1);
    skin.applyProbe(flags, om, dry);
    pull(skin, 6);
    expect(skin.tornLinks).toBeGreaterThan(0);
    expect(skin.takeTears()[0][7]).toBe(Tissue.Dermis);
    const m = thermalMultipliers(Tissue.Dermis, 0, 1, false);
    expect(m.tearMul).toBeCloseTo(0.35, 2);
    expect(m.stiffMul).toBeCloseTo(0.5, 2);
    expect(thermalMultipliers(Tissue.Dermis, 0, 0, true).tearMul).toBeCloseTo(0.25, 2);
    // coagulated collagen is firmer, rendered fat softer
    expect(thermalMultipliers(Tissue.Dermis, 2, 0, false).stiffMul).toBeCloseTo(1 / 2.5, 3);
    expect(thermalMultipliers(Tissue.Fat, 2, 0, false).stiffMul).toBeGreaterThan(1);
  });

  it('muscle is tougher and stiffer along its fibres than across them', () => {
    const along = thermalMultipliers(Tissue.Muscle, 0, 0, false, 1), across = thermalMultipliers(Tissue.Muscle, 0, 0, false, 0);
    expect(along.tearMul).toBeGreaterThan(across.tearMul * 1.4);
    expect(along.stiffMul).toBeLessThan(across.stiffMul * 0.5);
  });

  it('vessel links are pre-tensioned so a cut end recoils', () => {
    const v = uniformBed(Tissue.Fat);
    // a vertical 1 mm artery down the centre
    const n = v.dims.nx;
    for (let k = 0; k < v.dims.nz; k++) for (let j = n / 2 - 1; j <= n / 2; j++) for (let i = n / 2 - 1; i <= n / 2; i++) {
      const idx = v.index(i, j, k); if (v.occupancy[idx] >= 0.5) v.cell[idx] = packCell(Tissue.Artery, 0, 20);
    }
    const lat = new Lattice(v, 3);
    const vesselLink = lat.constraints.find((c) => c.tissue === Tissue.Artery || c.tissue === Tissue.Vein);
    // the softer end governs, so a fat-vessel link is "fat", but any link touching a vessel node is pre-tensioned
    const touching = lat.constraints.filter((c) => lat.layer[c.a] === Tissue.Artery || lat.layer[c.b] === Tissue.Artery);
    expect(touching.length).toBeGreaterThan(0);
    for (const c of touching) expect(c.rest0 / Math.hypot(lat.mat[c.b * 3] - lat.mat[c.a * 3], lat.mat[c.b * 3 + 1] - lat.mat[c.a * 3 + 1], lat.mat[c.b * 3 + 2] - lat.mat[c.a * 3 + 2])).toBeCloseTo(0.92, 2);
    void vesselLink;
  });
});
