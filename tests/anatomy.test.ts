import { describe, it, expect } from 'vitest';
import freeplay from '../scenarios/freeplay.json';
import hemostasis from '../scenarios/hemostasis.json';
import lipoma from '../scenarios/lipoma.json';
import viscera from '../scenarios/viscera.json';
import { validateScenario } from '@/core/scenario/schema';
import { buildAnatomy } from '@/core/scenario/anatomy';
import { cellType, cellState, cellClock, withClock, packCell, CUT_AIR, DISSECTED_AIR } from '@/core/volume';
import { Tissue, TissueState, packTissueProps, PROPS_STRIDE, TISSUE } from '@/core/tissueProperties';

function census(cells: Uint32Array, occ: Float32Array): Map<number, number> {
  const m = new Map<number, number>();
  for (let i = 0; i < cells.length; i++) { if (occ[i] < 0.5) continue; const t = cellType(cells[i]); m.set(t, (m.get(t) ?? 0) + 1); }
  return m;
}

describe('anatomy generator', () => {
  it('lays areolar planes on both faces of Scarpa\'s fascia and above the deep fascia, and a sheath around the lipoma capsule', () => {
    const sc = validateScenario(lipoma);
    const { volume } = buildAnatomy(sc, 0.5);
    const c = census(volume.cell, volume.occupancy);
    expect(c.get(Tissue.Areolar) ?? 0).toBeGreaterThan(0);
    expect(c.get(Tissue.LipomaCapsule) ?? 0).toBeGreaterThan(0);
    // the areolar sheath touches the capsule: some areolar voxel has a capsule neighbour
    const { nx, ny, nz } = volume.dims;
    let touching = 0;
    for (let k = 1; k < nz - 1 && touching === 0; k++) for (let j = 1; j < ny - 1 && touching === 0; j++) for (let i = 1; i < nx - 1; i++) {
      const idx = volume.index(i, j, k);
      if (cellType(volume.cell[idx]) !== Tissue.Areolar) continue;
      if (cellType(volume.cell[volume.index(i + 1, j, k)]) === Tissue.LipomaCapsule || cellType(volume.cell[volume.index(i, j, k + 1)]) === Tissue.LipomaCapsule) { touching++; break; }
    }
    expect(touching).toBeGreaterThan(0);
    // a plane sits immediately above the deep fascia in most columns (septal attachments cross it in the rest)
    let columns = 0, withPlane = 0;
    for (let j0 = 2; j0 < ny - 2; j0 += 7) for (let i0 = 2; i0 < nx - 2; i0 += 7) {
      let sawDeep = false, sawAreolarAbove = false;
      for (let k = 0; k < nz; k++) {
        const t = cellType(volume.cell[volume.index(i0, j0, k)]);
        if (t === Tissue.DeepFascia) sawDeep = true;
        else if (sawDeep && t === Tissue.Areolar) { sawAreolarAbove = true; break; }
        else if (sawDeep) break;
      }
      columns++; if (sawAreolarAbove) withPlane++;
    }
    expect(withPlane / columns).toBeGreaterThan(0.6);
  });

  it('builds the visceral scenario: serosa over parenchyma below a thin muscle layer, exposed in a pit whose floor follows the organ', () => {
    const sc = validateScenario(viscera);
    expect(sc.organ?.kind).toBe('bowel');
    const { volume, report } = buildAnatomy(sc, 0.5);
    expect(report.organDepthMm).toBeGreaterThan(14);
    const c = census(volume.cell, volume.occupancy);
    expect(c.get(Tissue.Viscera) ?? 0).toBeGreaterThan(1000);
    expect(c.get(Tissue.Serosa) ?? 0).toBeGreaterThan(100);
    expect(c.get(Tissue.Muscle) ?? 0).toBeGreaterThan(100);
    // the pit floor at the centre is serosa or the top of the viscera, and it is cut air above
    const { nx, ny, nz } = volume.dims;
    const i = nx >> 1, j = ny >> 1;
    let top = -1;
    for (let k = nz - 1; k >= 0; k--) if (volume.occupancy[volume.index(i, j, k)] >= 0.5) { top = k; break; }
    const floorType = cellType(volume.cell[volume.index(i, j, top)]);
    expect([Tissue.Serosa, Tissue.Viscera]).toContain(floorType);
    const airAbove = volume.cell[volume.index(i, j, top + 1)];
    expect(cellType(airAbove)).toBe(Tissue.Air);
    expect(cellState(airAbove)).toBe(TissueState.Vaporized);
  });

  it('marks pre-carved wounds as cut air and leaves the original air above the skin pristine', () => {
    const sc = validateScenario(hemostasis);
    const { volume } = buildAnatomy(sc, 0.5);
    const { nx, ny, nz } = volume.dims;
    // trench centre column: air inside the trench is cut air, air above the skin is original
    const i = nx >> 1, j = ny >> 1;
    let top = -1;
    for (let k = nz - 1; k >= 0; k--) if (volume.occupancy[volume.index(i, j, k)] >= 0.5) { top = k; break; }
    expect(volume.cell[volume.index(i, j, top + 1)]).toBe(CUT_AIR);
    expect(volume.cell[volume.index(i, j, nz - 1)]).toBe(0);
    expect(cellState(CUT_AIR)).toBe(TissueState.Vaporized);
    expect(cellState(DISSECTED_AIR)).toBe(TissueState.Dissected);
  });

  it('every scenario validates and builds', () => {
    for (const s of [freeplay, hemostasis, lipoma, viscera]) {
      const sc = validateScenario(s);
      const { volume } = buildAnatomy(sc, 1.0);
      expect(volume.count).toBeGreaterThan(0);
    }
  });
});

describe('cell word layout', () => {
  it('keeps type, 3-bit state, 5-bit exposure clock, aux and flags separate', () => {
    const c = packCell(Tissue.Artery, TissueState.Coagulated, 20, 1 << 27);
    expect(cellType(c)).toBe(Tissue.Artery);
    expect(cellState(c)).toBe(TissueState.Coagulated);
    expect(cellClock(c)).toBe(0);
    const k = withClock(c, 31);
    expect(cellClock(k)).toBe(31);
    expect(cellType(k)).toBe(Tissue.Artery); expect(cellState(k)).toBe(TissueState.Coagulated);
    expect((k >>> 16) & 0xff).toBe(20);
    expect((k & (1 << 27)) !== 0).toBe(true);
    expect(cellClock(withClock(k, 40))).toBe(31);
  });
  it('packs 17 tissues with cohesion and ooze for the GPU', () => {
    const p = packTissueProps();
    expect(p.length).toBe(Tissue.COUNT * PROPS_STRIDE);
    expect(p[Tissue.Areolar * PROPS_STRIDE + 8]).toBeCloseTo(TISSUE[Tissue.Areolar].cohesion, 6);
    expect(p[Tissue.Viscera * PROPS_STRIDE + 9]).toBeCloseTo(TISSUE[Tissue.Viscera].oozeRate, 6);
    expect(p[Tissue.Dermis * PROPS_STRIDE + 0]).toBeCloseTo(0.3, 6);
  });
});
