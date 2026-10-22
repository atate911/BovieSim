import { describe, it, expect } from 'vitest';
import { FT10_MODES, generatorOutput } from '@/core/generator/ft10';

describe('FT10 output model', () => {
  it('delivers the set power across the constant-power band', () => {
    const m = FT10_MODES.pure;
    for (const z of [m.zLo, 300, 800, m.zHi]) expect(generatorOutput(m, 30, z).power).toBeCloseTo(30, 6);
  });
  it('is current-limited into low impedance (blood, wet contact)', () => {
    const m = FT10_MODES.pure;
    const o = generatorOutput(m, 30, 50);
    expect(o.power).toBeLessThan(30);
    expect(o.power).toBeCloseTo(30 * 50 / m.zLo, 6);
  });
  it('is voltage-limited into high impedance (desiccated tissue, fat)', () => {
    const m = FT10_MODES.pure;
    const o = generatorOutput(m, 30, 8000);
    expect(o.power).toBeLessThan(30);
    expect(o.vRms).toBeLessThanOrEqual(m.vPeak / m.crest + 1e-6);
  });
  it('coag modes bridge a larger gap than cut modes (higher peak voltage)', () => {
    const reach = (id: keyof typeof FT10_MODES) => FT10_MODES[id].vPeak / 1000 * FT10_MODES[id].arcMmPerKv;
    expect(reach('fulgurate')).toBeGreaterThan(reach('pure') * 5);
    expect(reach('spray')).toBeGreaterThan(reach('fulgurate'));
  });
  it('never exceeds the mode maximum', () => {
    expect(generatorOutput(FT10_MODES.fulgurate, 300, 500).power).toBeLessThanOrEqual(120);
  });
});
