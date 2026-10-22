import { describe, it, expect } from 'vitest';
import { grid3, gidx, jacobiSolve, joulePower } from '@/physics/reference';

describe('quasi-static field solver', () => {
  it('spherical electrode in a uniform medium gives phi ~ 1/r and the analytic resistance', () => {
    const n = 41, c = 20, h = 0.25e-3, sigma = 0.3;
    const sg = grid3(n, n, n, sigma);
    const fixed = new Uint8Array(n * n * n), phiF = new Float32Array(n * n * n);
    const a = 2.0 * h; // electrode radius: 0.5 mm (2 voxels)
    for (let k = 0; k < n; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
      const idx = gidx(sg, i, j, k);
      const r = Math.hypot(i - c, j - c, k - c) * h;
      if (r <= a) { fixed[idx] = 1; phiF[idx] = 1; }
      if (i === 0 || j === 0 || k === 0 || i === n - 1 || j === n - 1 || k === n - 1) { fixed[idx] = 1; phiF[idx] = 0; }
    }
    const phi = jacobiSolve(sg, fixed, phiF, 900);
    // phi(r) for a sphere between radius a and outer radius b (box ~ sphere of radius b ~ 30 h):
    // phi = (1/r - 1/b) / (1/a - 1/b)
    const b = c * h;
    const model = (r: number) => (1 / r - 1 / b) / (1 / a - 1 / b);
    // the cubic outer boundary is not a sphere, so compare well inside it (r <= b/2)
    for (const dr of [4, 7, 10]) {
      const r = dr * h;
      const got = phi.data[gidx(sg, c + dr, c, c)];
      expect(Math.abs(got - model(r)) / model(r)).toBeLessThan(0.15);
    }
    // resistance between concentric spheres: R = (1/a - 1/b) / (4 pi sigma). A 2-voxel electrode
    // in a cubic box is a coarse discretisation, so this is a sanity bound; absolute impedance is
    // calibrated empirically through sigmaScale (docs/calibration.md).
    const Ranalytic = (1 / a - 1 / b) / (4 * Math.PI * sigma);
    const P = joulePower(phi, sg, h); // at 1 V -> P = 1/R
    const Rnum = 1 / P;
    expect(Math.abs(Rnum - Ranalytic) / Ranalytic).toBeLessThan(0.5);
    // the Joule power must equal the current through any closed shell around the electrode (V = 1):
    // I = sum over the faces of a cube of half-size s of sigma * dphi/dn * h^2
    const s = 8;
    let I = 0;
    for (let u = -s; u <= s; u++) for (let v = -s; v <= s; v++) {
      const faces: [number, number, number, number, number, number][] = [
        [c + s, c + u, c + v, c + s + 1, c + u, c + v], [c - s, c + u, c + v, c - s - 1, c + u, c + v],
        [c + u, c + s, c + v, c + u, c + s + 1, c + v], [c + u, c - s, c + v, c + u, c - s - 1, c + v],
        [c + u, c + v, c + s, c + u, c + v, c + s + 1], [c + u, c + v, c - s, c + u, c + v, c - s - 1]];
      for (const [i0, j0, k0, i1, j1, k1] of faces) I += sigma * (phi.data[gidx(sg, i0, j0, k0)] - phi.data[gidx(sg, i1, j1, k1)]) / h * h * h;
    }
    expect(Math.abs(I - P) / P).toBeLessThan(0.08);
  });

  it('fat carries far less current than dermis at the same voltage', () => {
    const n = 25, c = 12, h = 0.25e-3;
    const run = (sigma: number) => {
      const sg = grid3(n, n, n, sigma);
      const fixed = new Uint8Array(n ** 3), phiF = new Float32Array(n ** 3);
      for (let k = 0; k < n; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
        const idx = gidx(sg, i, j, k);
        if (Math.hypot(i - c, j - c, k - c) <= 1.5) { fixed[idx] = 1; phiF[idx] = 1; }
        if (i === 0 || j === 0 || k === 0 || i === n - 1 || j === n - 1 || k === n - 1) fixed[idx] = 1;
      }
      return joulePower(jacobiSolve(sg, fixed, phiF, 400), sg, h);
    };
    const pDermis = run(0.3), pFat = run(0.04);
    expect(pDermis / pFat).toBeGreaterThan(6);
    expect(pDermis / pFat).toBeLessThan(9);
  });
});
