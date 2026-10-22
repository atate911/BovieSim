import { describe, it, expect } from 'vitest';

/** CPU mirror of the vessel rules in sim.wgsl (bloodSource / bioheat). */
const vesselFlowMlS = (rMm: number) => 0.25 * rMm ** 3;
function sealOutcome(omega: number, coapted: boolean, rMm: number): 'none' | 'sealed' | 'charPlug' {
  if (omega <= 1) return 'none';
  return coapted || rMm <= 0.5 ? 'sealed' : 'charPlug';
}
const rebleedPerSecond = (rMm: number) => Math.min(Math.max((rMm - 0.5) / 1.5, 0), 0.9) * 0.15;

/** Blood flow step mirror: head-driven exchange with a wetting film, on a 1D row of columns. */
function flowStep(surf: number[], h: number[], visc = 0.35, film = 0.05): number[] {
  const out = h.slice();
  for (let i = 0; i < h.length; i++) {
    const H0 = surf[i] + h[i];
    for (const j of [i - 1, i + 1]) {
      if (j < 0 || j >= h.length) continue;
      const Hj = surf[j] + h[j];
      if (H0 > Hj) out[i] -= Math.min(visc * 0.25 * (H0 - Hj), Math.max(h[i] - film, 0) * 0.25);
      else if (Hj > H0) out[i] += Math.min(visc * 0.25 * (Hj - H0), Math.max(h[j] - film, 0) * 0.25);
    }
  }
  return out.map((v) => Math.max(v, 0));
}

describe('vessel bleeding and sealing rules', () => {
  it('flow scales steeply with radius: a 1 mm artery bleeds ~40x a 0.3 mm one', () => {
    expect(vesselFlowMlS(1.0) / vesselFlowMlS(0.3)).toBeCloseTo(37, 0);
    expect(vesselFlowMlS(1.0)).toBeGreaterThan(0.1); // brisk: a few ml per 10 s
    expect(vesselFlowMlS(1.0)).toBeLessThan(0.6);
  });
  it('coaptive buzz seals any vessel the forceps can hold; direct coag seals only small vessels', () => {
    expect(sealOutcome(1.5, true, 1.5)).toBe('sealed');
    expect(sealOutcome(1.5, false, 0.4)).toBe('sealed');
    expect(sealOutcome(1.5, false, 1.0)).toBe('charPlug');
    expect(sealOutcome(0.8, true, 1.0)).toBe('none');
  });
  it('char plugs on larger vessels are more likely to rebleed', () => {
    expect(rebleedPerSecond(0.5)).toBe(0);
    expect(rebleedPerSecond(1.0)).toBeGreaterThan(0);
    expect(rebleedPerSecond(2.0)).toBeGreaterThan(rebleedPerSecond(1.0));
  });
});

describe('blood layer flow', () => {
  it('conserves volume and runs downhill into a trench', () => {
    const surf = [0, 0, -4, -8, -8, -4, 0, 0];
    let h = [1.0, 0, 0, 0, 0, 0, 0, 0];
    const total0 = h.reduce((a, b) => a + b, 0);
    for (let s = 0; s < 400; s++) h = flowStep(surf, h);
    const total1 = h.reduce((a, b) => a + b, 0);
    expect(Math.abs(total1 - total0)).toBeLessThan(1e-6);
    // most of the mobile blood ends up in the floor of the trench, only a film stays on the rim
    expect(h[3] + h[4]).toBeGreaterThan(0.6);
    expect(h[0]).toBeLessThanOrEqual(0.05 + 1e-6);
  });
  it('never moves blood uphill against the head', () => {
    const surf = [0, 2, 4];
    let h = [0.5, 0, 0];
    for (let s = 0; s < 50; s++) h = flowStep(surf, h);
    expect(h[2]).toBe(0);
    expect(h[1]).toBe(0);
  });
});
