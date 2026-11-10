import { describe, it, expect } from 'vitest';
import { Tissue, TISSUE, DISSECT_PUSH_COHESION, DISSECT_SPREAD_COHESION, isBridging } from '@/core/tissueProperties';
import { arrheniusConst } from '@/physics/reference';

/** CPU mirror of the vessel rules in sim.wgsl (bloodSource / bioheat). */
const vesselFlowMlS = (rMm: number) => 0.25 * rMm ** 3;
function sealOutcome(omega: number, coapted: boolean, rMm: number, exposure = 3): 'none' | 'sealed' | 'charPlug' {
  if (omega <= 1) return 'none';
  return coapted || rMm <= 0.5 || exposure !== 3 ? 'sealed' : 'charPlug';
}
const rebleedPerSecond = (rMm: number) => Math.min(Math.max((rMm - 0.5) / 1.5, 0), 0.9) * 0.15;
/** air kinds: 0 tissue, 1 original air, 2 dissected plane, 3 cut surface; a vessel bleeds only onto cut air */
const vesselBleeds = (exposure: number) => exposure === 3;
/** spasm / thrombosis of an open end with the exposure clock (0..31, ~1.5 s per step) */
function spasmFactor(rMm: number, arterial: boolean, clock: number): number {
  if (arterial) {
    if (rMm <= 0.35) return clock >= 20 ? 0 : Math.exp(-clock / 6);
    if (rMm <= 0.5) return Math.max(0.12, Math.exp(-clock / 6));
    return 1 - 0.35 * Math.min(1, clock / 16);
  }
  if (rMm <= 0.5) return clock >= 24 ? 0 : Math.exp(-clock / 10);
  return 1 - 0.2 * Math.min(1, clock / 16);
}
/** capillary ooze (ul/s) from one exposed voxel face */
const oozeUlS = (t: Tissue, hMm: number, clock: number, exposure: number) => TISSUE[t].oozeRate * hMm * hMm * Math.exp(-clock / 7) * (exposure === 2 ? 0.3 : 1);

/** Blood flow step mirror: head-driven exchange with a wetting film and thin-film mobility, on a 1D row of columns. */
const FILM = 0.05;
function flowRate(h: number, clot: number, visc = 0.35): number { const mobile = Math.max(h - FILM, 0); return visc * Math.min(Math.max(mobile / 0.25, 0.15), 1) * (1 - clot); }
/** depth that can flow: above the wetting film and not clotted */
const mobileDepth = (h: number, clot: number) => Math.max(Math.min(h - FILM, h * (1 - clot)), 0);
function flowStep(surf: number[], h: number[], clot: number[] = h.map(() => 0)): number[] {
  const out = h.slice();
  for (let i = 0; i < h.length; i++) {
    const H0 = surf[i] + h[i];
    for (const j of [i - 1, i + 1]) {
      if (j < 0 || j >= h.length) continue;
      const Hj = surf[j] + h[j];
      if (H0 > Hj) out[i] -= Math.min(flowRate(h[i], clot[i]) * 0.25 * (H0 - Hj), mobileDepth(h[i], clot[i]) * 0.25);
      else if (Hj > H0) out[i] += Math.min(flowRate(h[j], clot[j]) * 0.25 * (Hj - H0), mobileDepth(h[j], clot[j]) * 0.25);
    }
  }
  return out.map((v) => Math.max(v, 0));
}
/** suction mirror: liquid leaves, the clot mass stays and the fraction is re-derived */
function suck(h: number, clot: number, dh: number): { h: number; clot: number } {
  const clotMass = h * clot;
  const taken = Math.min(Math.max(h - clotMass, 0), dh);
  const h2 = Math.max(h - taken, clotMass);
  return { h: h2, clot: h2 > 1e-5 ? Math.min(1, clotMass / h2) : 0 };
}
/** fresh blood arriving in a column dilutes the clot fraction (clot mass conserved) */
function addBlood(h: number, clot: number, dh: number): { h: number; clot: number } {
  const h2 = h + dh;
  return { h: h2, clot: h2 > 1e-6 ? clot * h / h2 : 0 };
}
/** blunt dissection rule: separable tissue within reach of the tips */
const dissects = (t: Tissue, spread: boolean) => !isBridging(t) && TISSUE[t].cohesion <= (spread ? DISSECT_SPREAD_COHESION : DISSECT_PUSH_COHESION);

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
    // an intact vessel exposed in a dissected plane coagulates to a seal, not a plug: its lumen was never open
    expect(sealOutcome(1.5, false, 1.0, 2)).toBe('sealed');
  });
  it('char plugs on larger vessels are more likely to rebleed', () => {
    expect(rebleedPerSecond(0.5)).toBe(0);
    expect(rebleedPerSecond(1.0)).toBeGreaterThan(0);
    expect(rebleedPerSecond(2.0)).toBeGreaterThan(rebleedPerSecond(1.0));
  });
  it('a vessel bleeds only when its lumen is opened by cutting or tearing, not when a plane is spread off it', () => {
    expect(vesselBleeds(3)).toBe(true);
    expect(vesselBleeds(2)).toBe(false);
    expect(vesselBleeds(1)).toBe(false);
    expect(vesselBleeds(0)).toBe(false);
  });
  it('small vessels spasm and stop on their own within half a minute; a 1 mm artery keeps bleeding', () => {
    expect(spasmFactor(0.3, true, 0)).toBe(1);
    expect(spasmFactor(0.3, true, 8)).toBeLessThan(0.3);     // ~12 s
    expect(spasmFactor(0.3, true, 20)).toBe(0);              // ~30 s: thrombosed
    expect(spasmFactor(0.5, true, 31)).toBeCloseTo(0.12, 2); // arteriole: a trickle remains
    expect(spasmFactor(1.0, true, 31)).toBeGreaterThan(0.6); // artery: must be sealed or tied
    expect(spasmFactor(0.4, false, 31)).toBe(0);             // venule clots off
    expect(spasmFactor(1.0, false, 31)).toBeCloseTo(0.8, 2);
    // a wipe resets the clock to 6: a stopped arteriole re-bleeds at about a third of its rate
    expect(spasmFactor(0.3, true, 6)).toBeCloseTo(Math.exp(-1), 3);
  });
});

describe('capillary ooze from raw surfaces', () => {
  it('muscle and viscera weep far more than fat; epidermis and vessels do not ooze; the plane weeps less than a cut', () => {
    expect(oozeUlS(Tissue.Muscle, 0.25, 0, 3)).toBeGreaterThan(oozeUlS(Tissue.Fat, 0.25, 0, 3) * 10);
    expect(oozeUlS(Tissue.Viscera, 0.25, 0, 3)).toBeGreaterThan(oozeUlS(Tissue.Dermis, 0.25, 0, 3) * 1.5);
    expect(oozeUlS(Tissue.Epidermis, 0.25, 0, 3)).toBe(0);
    expect(oozeUlS(Tissue.Artery, 0.25, 0, 3)).toBe(0);
    expect(oozeUlS(Tissue.Fat, 0.25, 0, 2)).toBeCloseTo(0.3 * oozeUlS(Tissue.Fat, 0.25, 0, 3), 9);
  });
  it('a 30 mm dermal incision oozes a tenth of a millilitre or so and settles within about a minute', () => {
    // two walls, 2 mm deep, 0.25 mm voxels; the clock steps about every 2 s
    const faces = 2 * (30 / 0.25) * (2 / 0.25);
    let total = 0;
    for (let clock = 0; clock < 31; clock++) total += faces * oozeUlS(Tissue.Dermis, 0.25, clock, 3) * 2.0;
    expect(total / 1000).toBeGreaterThan(0.05);
    expect(total / 1000).toBeLessThan(0.5);
    expect(oozeUlS(Tissue.Dermis, 0.25, 30, 3) / oozeUlS(Tissue.Dermis, 0.25, 0, 3)).toBeLessThan(0.02);
  });
});

describe('blood layer: flow, clotting, suction', () => {
  it('conserves volume and runs downhill into a trench', () => {
    const surf = [0, 0, -4, -8, -8, -4, 0, 0];
    let h = [1.0, 0, 0, 0, 0, 0, 0, 0];
    const total0 = h.reduce((a, b) => a + b, 0);
    for (let s = 0; s < 400; s++) h = flowStep(surf, h);
    const total1 = h.reduce((a, b) => a + b, 0);
    expect(Math.abs(total1 - total0)).toBeLessThan(1e-6);
    // most of the mobile blood ends up in the floor of the trench, only a creeping film stays on the rim
    expect(h[3] + h[4]).toBeGreaterThan(0.6);
    expect(h[0]).toBeLessThan(0.06);
  });
  it('never moves blood uphill against the head', () => {
    const surf = [0, 2, 4];
    let h = [0.5, 0, 0];
    for (let s = 0; s < 50; s++) h = flowStep(surf, h);
    expect(h[2]).toBe(0);
    expect(h[1]).toBe(0);
  });
  it('a deep pool spreads much faster than a thin film, and clotted blood does not move', () => {
    const surf = [0, 0, 0, 0, 0];
    const spread = (h0: number, clot = 0) => { let h = [h0, 0, 0, 0, 0]; const c = [clot, 0, 0, 0, 0]; for (let s = 0; s < 10; s++) h = flowStep(surf, h, c); return (h0 - h[0]) / h0; };
    expect(spread(1.0)).toBeGreaterThan(spread(0.12) * 2);
    expect(spread(0.04)).toBe(0);                 // below the wetting film nothing flows
    expect(spread(1.0, 1.0)).toBe(0);             // a firm clot
    expect(spread(1.0, 0.5)).toBeLessThan(spread(1.0));
  });
  it('blood proteins denature in under a second at 70 C and not within a minute at 45 C', () => {
    const b = TISSUE[Tissue.Blood];
    expect(arrheniusConst(70, 1.0, b.lnA, b.Ea)).toBeGreaterThan(1);
    expect(arrheniusConst(45, 60, b.lnA, b.Ea)).toBeLessThan(0.01);
    expect(arrheniusConst(55, 60, b.lnA, b.Ea)).toBeLessThan(0.3);
  });
  it('suction lifts the liquid and leaves the clot behind', () => {
    const r = suck(1.0, 0.4, 10);
    expect(r.h).toBeCloseTo(0.4, 6);
    expect(r.clot).toBeCloseTo(1.0, 6);
    const partial = suck(1.0, 0.0, 0.3);
    expect(partial.h).toBeCloseTo(0.7, 6); expect(partial.clot).toBe(0);
    expect(suck(0.5, 1.0, 10).h).toBeCloseTo(0.5, 6);
  });
  it('the clot is a mass, not a ratio: fresh blood dilutes it and suction cannot ratchet it up', () => {
    // a trace of clot in a bleeding column under intermittent suction stays a trace
    let c = { h: 0.5, clot: 1e-4 };
    for (let s = 0; s < 200; s++) { c = suck(c.h, c.clot, 0.2); c = addBlood(c.h, c.clot, 0.2); }
    expect(c.h * c.clot).toBeCloseTo(0.5 * 1e-4, 9);   // clot mass conserved
    expect(c.clot).toBeLessThan(1e-3);
    // a real clot concentrates under suction and dilutes again as the vessel refills the column
    const sucked = suck(2.0, 0.25, 10);
    expect(sucked.h).toBeCloseTo(0.5, 6); expect(sucked.clot).toBeCloseTo(1, 6);
    const refilled = addBlood(sucked.h, sucked.clot, 1.5);
    expect(refilled.clot).toBeCloseTo(0.25, 6);
    // clotted blood does not flow away from a column even down a steep head
    const h = flowStep([0, -10], [1.0, 0], [1.0, 0]);
    expect(h[0]).toBeCloseTo(1.0, 9);
  });
});

describe('blunt dissection', () => {
  it('spreading separates areolar tissue, fat lobules, the capsule plane and septa, never dermis, fascia, muscle or vessels', () => {
    for (const t of [Tissue.Areolar, Tissue.Fat, Tissue.LipomaCapsule, Tissue.Septum]) expect(dissects(t, true)).toBe(true);
    // the lipoma itself is a firm mass: it shells out along its capsule plane rather than splitting
    for (const t of [Tissue.Lipoma, Tissue.Dermis, Tissue.Epidermis, Tissue.DeepFascia, Tissue.SuperficialFascia, Tissue.Muscle, Tissue.Artery, Tissue.Vein, Tissue.Nerve, Tissue.Viscera, Tissue.Serosa]) expect(dissects(t, true)).toBe(false);
  });
  it('pushing the closed tips follows only the loosest planes', () => {
    expect(dissects(Tissue.Areolar, false)).toBe(true);
    expect(dissects(Tissue.Fat, false)).toBe(true);
    expect(dissects(Tissue.LipomaCapsule, false)).toBe(true);
    expect(dissects(Tissue.Septum, false)).toBe(false);
    expect(dissects(Tissue.Lipoma, false)).toBe(false);
  });
});
