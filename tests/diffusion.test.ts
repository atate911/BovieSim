import { describe, it, expect } from 'vitest';
import { grid3, gidx, diffuseStep, stableDt } from '@/physics/reference';

describe('bioheat conduction kernel', () => {
  it('spreads a Gaussian hot spot like the analytic solution', () => {
    // alpha of dermis ~ 0.37/(1109*3391) ~ 9.8e-8 m2/s, h = 0.25 mm
    const alpha = 9.8e-8, h = 0.25e-3;
    const n = 41, c = 20;
    const s0 = 1.0e-3; // initial sigma 1 mm
    let T = grid3(n, n, n);
    for (let k = 0; k < n; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
      const r2 = ((i - c) ** 2 + (j - c) ** 2 + (k - c) ** 2) * h * h;
      T.data[gidx(T, i, j, k)] = Math.exp(-r2 / (2 * s0 * s0));
    }
    const dt = stableDt(alpha, h) * 0.5;
    const steps = 40;
    for (let s = 0; s < steps; s++) T = diffuseStep(T, alpha, h, dt);
    const t = steps * dt;
    const s2 = s0 * s0 + 2 * alpha * t;
    const expectedPeak = Math.pow(s0 * s0 / s2, 1.5);
    const peak = T.data[gidx(T, c, c, c)];
    expect(Math.abs(peak - expectedPeak) / expectedPeak).toBeLessThan(0.02);
    // profile at 1 mm off-centre
    const r = 4 * h;
    const expectedOff = expectedPeak * Math.exp(-r * r / (2 * s2));
    expect(Math.abs(T.data[gidx(T, c + 4, c, c)] - expectedOff) / expectedOff).toBeLessThan(0.03);
  });

  it('conserves energy with adiabatic boundaries', () => {
    const n = 12;
    let T = grid3(n, n, n, 37);
    T.data[gidx(T, 6, 6, 6)] = 300;
    const sum0 = T.data.reduce((a, b) => a + b, 0);
    for (let s = 0; s < 30; s++) T = diffuseStep(T, 1e-7, 0.25e-3, stableDt(1e-7, 0.25e-3) * 0.9);
    const sum1 = T.data.reduce((a, b) => a + b, 0);
    expect(Math.abs(sum1 - sum0) / sum0).toBeLessThan(1e-6);
  });
});
