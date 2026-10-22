/**
 * CPU reference implementations of the physics kernels, used by the analytic unit tests and as
 * the readable specification of what the WGSL kernels compute.
 */
import { R_GAS } from '@/core/tissueProperties';

export interface Grid3 { nx: number; ny: number; nz: number; data: Float32Array }

export function grid3(nx: number, ny: number, nz: number, fill = 0): Grid3 {
  return { nx, ny, nz, data: new Float32Array(nx * ny * nz).fill(fill) };
}
export const gidx = (g: Grid3, i: number, j: number, k: number) => i + g.nx * (j + g.ny * k);

/** One explicit step of the heat equation dT/dt = alpha lap T, adiabatic boundaries. h in metres. */
export function diffuseStep(T: Grid3, alpha: number, h: number, dt: number): Grid3 {
  const out = grid3(T.nx, T.ny, T.nz);
  const r = alpha * dt / (h * h);
  for (let k = 0; k < T.nz; k++) for (let j = 0; j < T.ny; j++) for (let i = 0; i < T.nx; i++) {
    const c = T.data[gidx(T, i, j, k)];
    let lap = 0;
    const nb: [number, number, number][] = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];
    for (const [di, dj, dk] of nb) {
      const ii = i + di, jj = j + dj, kk = k + dk;
      if (ii < 0 || jj < 0 || kk < 0 || ii >= T.nx || jj >= T.ny || kk >= T.nz) continue;
      lap += T.data[gidx(T, ii, jj, kk)] - c;
    }
    out.data[gidx(T, i, j, k)] = c + r * lap;
  }
  return out;
}

/** Stable explicit time step for the 7-point stencil. */
export const stableDt = (alpha: number, h: number) => (h * h) / (6 * alpha);

/**
 * Jacobi relaxation of div(sigma grad phi) = 0 with Dirichlet voxels (mask 1 = fixed at phi).
 * Harmonic-mean face conductances, exactly as the WGSL kernel.
 */
export function jacobiSolve(sigma: Grid3, fixed: Uint8Array, phiFixed: Float32Array, iters: number): Grid3 {
  let a = grid3(sigma.nx, sigma.ny, sigma.nz);
  let b = grid3(sigma.nx, sigma.ny, sigma.nz);
  for (let n = 0; n < sigma.data.length; n++) if (fixed[n]) a.data[n] = phiFixed[n];
  const harmonic = (x: number, y: number) => (x + y > 0 ? (2 * x * y) / (x + y) : 0);
  for (let it = 0; it < iters; it++) {
    for (let k = 0; k < sigma.nz; k++) for (let j = 0; j < sigma.ny; j++) for (let i = 0; i < sigma.nx; i++) {
      const idx = gidx(sigma, i, j, k);
      if (fixed[idx]) { b.data[idx] = phiFixed[idx]; continue; }
      const s0 = sigma.data[idx];
      if (s0 <= 0) { b.data[idx] = 0; continue; }
      let num = 0, den = 0;
      const nb: [number, number, number][] = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];
      for (const [di, dj, dk] of nb) {
        const ii = i + di, jj = j + dj, kk = k + dk;
        if (ii < 0 || jj < 0 || kk < 0 || ii >= sigma.nx || jj >= sigma.ny || kk >= sigma.nz) continue;
        const nidx = gidx(sigma, ii, jj, kk);
        const g = harmonic(s0, sigma.data[nidx]);
        num += g * a.data[nidx]; den += g;
      }
      b.data[idx] = den > 0 ? num / den : 0;
    }
    [a, b] = [b, a];
  }
  return a;
}

/**
 * Total Joule power (W) with the discrete dissipation of the harmonic-mean scheme:
 * P = sum over faces g_f (dphi)^2 h, which equals V * I exactly for the converged solution.
 * Per voxel: q = 1/2 sum_faces g_f (dphi)^2 / h^2  (W/m^3), as in the WGSL heating kernel.
 */
export function joulePower(phi: Grid3, sigma: Grid3, h: number): number {
  const harmonic = (x: number, y: number) => (x + y > 0 ? (2 * x * y) / (x + y) : 0);
  let sum = 0;
  for (let k = 0; k < phi.nz; k++) for (let j = 0; j < phi.ny; j++) for (let i = 0; i < phi.nx; i++) {
    const idx = gidx(phi, i, j, k);
    const s0 = sigma.data[idx];
    if (s0 <= 0) continue;
    const nb: [number, number, number][] = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];
    let q = 0;
    for (const [di, dj, dk] of nb) {
      const ii = i + di, jj = j + dj, kk = k + dk;
      if (ii < 0 || jj < 0 || kk < 0 || ii >= phi.nx || jj >= phi.ny || kk >= phi.nz) continue;
      const nidx = gidx(phi, ii, jj, kk);
      const d = phi.data[nidx] - phi.data[idx];
      q += 0.5 * harmonic(s0, sigma.data[nidx]) * d * d / (h * h);
    }
    sum += q * h * h * h;
  }
  return sum;
}

/** Arrhenius damage integral for a temperature history (deg C) sampled at dt. */
export function arrhenius(tempC: ArrayLike<number>, dt: number, lnA: number, Ea: number): number {
  let om = 0;
  for (let n = 0; n < tempC.length; n++) om += dt * Math.exp(lnA - Ea / (R_GAS * (tempC[n] + 273.15)));
  return om;
}

/** Closed form for constant temperature. */
export const arrheniusConst = (tempC: number, t: number, lnA: number, Ea: number) => t * Math.exp(lnA - Ea / (R_GAS * (tempC + 273.15)));

/**
 * Phase-change bookkeeping for one voxel, mirroring the WGSL bioheat kernel.
 * Returns the new temperature and water fraction after depositing energy e (J/m3) into a voxel
 * at temperature T0 with water fraction w0 (mass fraction), density rho, specific heat c.
 */
export function depositWithBoiling(T0: number, w0: number, e: number, rho: number, c: number, L = 2.26e6, tBoil = 100): { T: number; w: number; latentUsed: number } {
  const rhoc = rho * c;
  let T = T0 + e / rhoc;
  let w = w0, latentUsed = 0;
  if (T > tBoil && w > 0) {
    const excess = (T - tBoil) * rhoc;
    const eBoilAll = w * rho * L;
    latentUsed = Math.min(excess, eBoilAll);
    w = w - w * (latentUsed / eBoilAll);
    T = tBoil + (excess - latentUsed) / rhoc;
  }
  return { T, w, latentUsed };
}
