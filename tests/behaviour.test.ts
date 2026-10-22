/**
 * Behaviour acceptance: the layer table in the plan, checked on the CPU reference kernels.
 * These assert qualitative orderings the surgeon expects, not absolute calibration.
 */
import { describe, it, expect } from 'vitest';
import { TISSUE, Tissue, LATENT_HEAT_WATER } from '@/core/tissueProperties';
import { grid3, gidx, jacobiSolve, joulePower, arrheniusConst, depositWithBoiling } from '@/physics/reference';
import { FT10_MODES, generatorOutput } from '@/core/generator/ft10';

function loadImpedance(sigma: number): number {
  const n = 25, c = 12, h = 0.25e-3;
  const sg = grid3(n, n, n, sigma);
  const fixed = new Uint8Array(n ** 3), phiF = new Float32Array(n ** 3);
  for (let k = 0; k < n; k++) for (let j = 0; j < n; j++) for (let i = 0; i < n; i++) {
    const idx = gidx(sg, i, j, k);
    if (Math.hypot(i - c, j - c, k - c) <= 1.5) { fixed[idx] = 1; phiF[idx] = 1; }
    if (i === 0 || j === 0 || k === 0 || i === n - 1 || j === n - 1 || k === n - 1) fixed[idx] = 1;
  }
  return 1 / joulePower(jacobiSolve(sg, fixed, phiF, 400), sg, h);
}

describe('layer behaviour', () => {
  it('fat presents a much higher load than dermis or muscle, so cut mode delivers less power into it', () => {
    const zFat = loadImpedance(TISSUE[Tissue.Fat].sigma);
    const zDermis = loadImpedance(TISSUE[Tissue.Dermis].sigma);
    const zMuscle = loadImpedance(TISSUE[Tissue.Muscle].sigma);
    expect(zFat).toBeGreaterThan(zDermis * 5);
    expect(zMuscle).toBeLessThan(zDermis);
    // at low watts into fat the FT10 pure-cut output is voltage-limited (the "sputter and stall")
    const pure = FT10_MODES.pure;
    const pFat = generatorOutput(pure, 30, zFat * 0.5).power; // half: real contact is larger than a point
    const pDermis = generatorOutput(pure, 30, zDermis * 0.5).power;
    expect(pFat).toBeLessThanOrEqual(pDermis);
  });

  it('fat needs far less energy per volume to remove than dermis (low water), but muscle the most', () => {
    const eVap = (t: Tissue) => { const p = TISSUE[t]; return p.rho * (p.c * 63 + p.water * LATENT_HEAT_WATER); };
    expect(eVap(Tissue.Fat)).toBeLessThan(eVap(Tissue.Dermis) * 0.5);
    expect(eVap(Tissue.Muscle)).toBeGreaterThan(eVap(Tissue.Dermis));
  });

  it('held coag desiccates: all water leaves before the temperature can exceed 100 C', () => {
    const p = TISSUE[Tissue.Dermis];
    let T = 37, w = p.water, steps = 0;
    while (w > 0 && steps < 10000) { const r = depositWithBoiling(T, w, 2e7, p.rho, p.c); T = r.T; w = r.w; if (w > 0) expect(T).toBeLessThanOrEqual(100 + 1e-6); steps++; }
    expect(w).toBe(0);
    const r = depositWithBoiling(T, w, 2e8, p.rho, p.c);
    expect(r.T).toBeGreaterThan(100);
  });

  it('whitening appears at the coag temperature (60-70 C within a second) for every soft tissue', () => {
    for (const t of [Tissue.Dermis, Tissue.Fat, Tissue.Muscle, Tissue.DeepFascia, Tissue.Artery]) {
      const p = TISSUE[t];
      expect(arrheniusConst(70, 1.0, p.lnA, p.Ea)).toBeGreaterThan(0.53);
      expect(arrheniusConst(45, 1.0, p.lnA, p.Ea)).toBeLessThan(0.05);
    }
  });

  it('coag modes bridge air gaps that cut modes cannot (fulguration reach)', () => {
    const reach = (m: keyof typeof FT10_MODES) => (FT10_MODES[m].vPeak / 1000) * FT10_MODES[m].arcMmPerKv;
    expect(reach('pure')).toBeLessThan(0.2);
    expect(reach('fulgurate')).toBeGreaterThan(1.5);
    expect(reach('spray')).toBeGreaterThan(reach('fulgurate'));
  });
});
