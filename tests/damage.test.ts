import { describe, it, expect } from 'vitest';
import { arrhenius, arrheniusConst, depositWithBoiling } from '@/physics/reference';
import { TISSUE, Tissue } from '@/core/tissueProperties';

describe('Arrhenius thermal damage', () => {
  const skin = TISSUE[Tissue.Dermis];
  it('matches the closed form at constant temperature', () => {
    const T = new Float32Array(1000).fill(60);
    const dt = 0.001;
    const om = arrhenius(T, dt, skin.lnA, skin.Ea);
    expect(Math.abs(om - arrheniusConst(60, 1.0, skin.lnA, skin.Ea)) / om).toBeLessThan(1e-4);
  });
  it('skin coagulates (omega > 1) within seconds at 60 C but not within an hour at 43 C', () => {
    expect(arrheniusConst(60, 1.5, skin.lnA, skin.Ea)).toBeGreaterThan(1);
    expect(arrheniusConst(43, 3600, skin.lnA, skin.Ea)).toBeLessThan(1);
  });
  it('is steep: with Henriques kinetics 70 C damages ~2e4x faster than 55 C', () => {
    // exp(Ea/R (1/328.15 - 1/343.15)) = exp(10.06)
    const r = arrheniusConst(70, 1, skin.lnA, skin.Ea) / arrheniusConst(55, 1, skin.lnA, skin.Ea);
    expect(r).toBeGreaterThan(1e4);
    expect(r).toBeLessThan(5e4);
  });
});

describe('phase change bookkeeping', () => {
  it('holds temperature at 100 C while water boils and conserves energy', () => {
    const rho = 1109, c = 3391, w0 = 0.65;
    const e = 5.0e8; // J/m3: sensible heat to 100 C is 2.37e8, boiling all water needs a further 1.63e9
    const r = depositWithBoiling(37, w0, e, rho, c);
    expect(r.T).toBeCloseTo(100, 5);
    const sensible = (100 - 37) * rho * c;
    expect(Math.abs(sensible + r.latentUsed - e) / e).toBeLessThan(1e-6);
    expect(r.w).toBeLessThan(w0);
    expect(r.w).toBeGreaterThan(0);
  });
  it('rises above 100 C only after all water is gone (char regime)', () => {
    const rho = 1109, c = 3391, w0 = 0.65;
    const eAll = (100 - 37) * rho * c + w0 * rho * 2.26e6;
    const r = depositWithBoiling(37, w0, eAll * 1.2, rho, c);
    expect(r.w).toBeCloseTo(0, 6);
    expect(r.T).toBeGreaterThan(100);
  });
});
