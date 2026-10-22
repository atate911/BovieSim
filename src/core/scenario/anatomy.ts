import { Scenario } from './schema';
import { Volume, VolumeDims, packCell, FLAG_DERMAL_PLEXUS, FLAG_FIBRE_X } from '../volume';
import { Tissue, TISSUE } from '../tissueProperties';
import { mulberry32, fbm2, voronoi2 } from '../noise';

export interface AnatomyReport {
  dims: VolumeDims;
  vessels: { x: number; y: number; r: number; arterial: boolean }[];
  fatMmActual: number;
  deepFasciaDepthMm: number;
  lesionCenter?: [number, number, number];
  fillMs: number;
}

/**
 * Builds the voxel volume for a scenario. Layer thicknesses carry the scenario's modeled
 * variation so the learner cannot know exact depths in advance.
 */
export function buildAnatomy(sc: Scenario, voxelOverride?: number, seedOverride?: number): { volume: Volume; report: AnatomyReport } {
  const t0 = performance.now();
  const h = voxelOverride ?? sc.bed.voxelMm;
  const airLayers = Math.max(4, Math.round(1.5 / h));
  const nx = Math.round(sc.bed.sizeMm[0] / h);
  const ny = Math.round(sc.bed.sizeMm[1] / h);
  const nz = Math.round(sc.bed.sizeMm[2] / h) + airLayers;
  const dims: VolumeDims = { nx, ny, nz, h, airLayers };
  const vol = new Volume(dims);
  const seed = seedOverride ?? sc.vessels.seed;
  const rng = mulberry32(seed * 7919 + 13);
  const L = sc.layers;
  const variation = L.variation ?? 0;
  const fatScale = 1 + variation * (rng() * 2 - 1);
  const fatMm = L.fatMm * fatScale;

  const { cell, occupancy, water } = vol;
  const sx = nx * h, sy = ny * h;
  const surfK = vol.surfaceK; // nominal surface in voxel units

  // ---- per-column precompute -------------------------------------------------------------
  const colSurf = new Float32Array(nx * ny);     // surface height (voxel units, k)
  const colDermis = new Float32Array(nx * ny);   // depth (mm) of dermis bottom
  const colScarpaTop = new Float32Array(nx * ny);
  const colScarpaBot = new Float32Array(nx * ny);
  const colDeepTop = new Float32Array(nx * ny);
  const colDeepBot = new Float32Array(nx * ny);
  const colSeptum = new Uint8Array(nx * ny);      // 1 if the column lies in a septum sheet
  const colLobule = new Uint8Array(nx * ny);
  const vor = { f1: 0, f2: 0, id: 0, cx: 0, cy: 0 };
  const lobule = sc.septa.lobuleMm;
  const septHalf = sc.septa.thicknessMm * 0.5;
  for (let j = 0; j < ny; j++) {
    for (let i = 0; i < nx; i++) {
      const x = (i + 0.5) * h, y = (j + 0.5) * h;
      const c = i + nx * j;
      // skin surface: gentle undulation + fine texture, in mm
      const undul = (fbm2(x / 18, y / 18, 3, seed) - 0.5) * 0.5 + (fbm2(x / 2.5, y / 2.5, 2, seed + 3) - 0.5) * 0.12;
      colSurf[c] = surfK + undul / h;
      const dermBot = L.epidermisMm + L.dermisMm * (0.9 + 0.2 * fbm2(x / 12, y / 12, 2, seed + 11));
      colDermis[c] = dermBot;
      const fatHere = fatMm * (0.85 + 0.3 * fbm2(x / 25, y / 25, 3, seed + 17));
      const scarpaDepth = dermBot + fatHere * L.superficialFasciaAt;
      colScarpaTop[c] = scarpaDepth;
      colScarpaBot[c] = scarpaDepth + L.superficialFasciaMm;
      const deepTop = dermBot + fatHere;
      colDeepTop[c] = deepTop;
      colDeepBot[c] = deepTop + L.deepFasciaMm;
      // septa: boundaries of a jittered Voronoi tiling, slightly warped
      const wx = x + (fbm2(x / 9, y / 9, 2, seed + 23) - 0.5) * 2.5;
      const wy = y + (fbm2(x / 9, y / 9, 2, seed + 29) - 0.5) * 2.5;
      voronoi2(wx / lobule, wy / lobule, seed, vor);
      const edgeDist = (vor.f2 - vor.f1) * lobule * 0.5;
      colSeptum[c] = edgeDist < septHalf ? 1 : 0;
      colLobule[c] = vor.id;
    }
  }

  // ---- layer fill -------------------------------------------------------------------------
  const wEpi = TISSUE[Tissue.Epidermis].water, wDerm = TISSUE[Tissue.Dermis].water, wFat = TISSUE[Tissue.Fat].water;
  const wSept = TISSUE[Tissue.Septum].water, wFasc = TISSUE[Tissue.DeepFascia].water, wMus = TISSUE[Tissue.Muscle].water;
  const cEpi = packCell(Tissue.Epidermis), cMus = packCell(Tissue.Muscle, 0, 0, FLAG_FIBRE_X);
  const cScarpa = packCell(Tissue.SuperficialFascia), cDeep = packCell(Tissue.DeepFascia);
  for (let j = 0; j < ny; j++) {
    for (let i = 0; i < nx; i++) {
      const c = i + nx * j;
      const surf = colSurf[c];
      const dermBot = colDermis[c], sTop = colScarpaTop[c], sBot = colScarpaBot[c], dTop = colDeepTop[c], dBot = colDeepBot[c];
      const sept = colSeptum[c] === 1;
      const lob = colLobule[c];
      const cFat = packCell(Tissue.Fat, 0, lob);
      const cSept = packCell(Tissue.Septum, 0, lob);
      const cDermDeep = packCell(Tissue.Dermis, 0, 0, FLAG_DERMAL_PLEXUS);
      const cDerm = packCell(Tissue.Dermis);
      const topK = Math.floor(surf - 0.5); // topmost voxel with occupancy >= 0.5 (the one the surface mesh samples)
      for (let k = 0; k < nz; k++) {
        const idx = i + nx * (j + ny * k);
        const occ = Math.min(1, Math.max(0, surf - k)); // fraction of voxel below surface
        occupancy[idx] = occ;
        if (occ <= 0) { cell[idx] = 0; water[idx] = 0; continue; }
        const depth = (surf - (k + 0.5)) * h; // mm below surface at voxel centre
        let cc: number, w: number;
        if (k >= topK || depth < L.epidermisMm) { cc = cEpi; w = wEpi; }
        else if (depth < dermBot) { cc = depth > dermBot - 0.6 ? cDermDeep : cDerm; w = wDerm; }
        else if (depth < sTop) { if (sept) { cc = cSept; w = wSept; } else { cc = cFat; w = wFat; } }
        else if (depth < sBot) { cc = cScarpa; w = wFasc; }
        else if (depth < dTop) { if (sept) { cc = cSept; w = wSept; } else { cc = cFat; w = wFat; } }
        else if (depth < dBot) { cc = cDeep; w = wFasc; }
        else { cc = cMus; w = wMus; }
        cell[idx] = cc; water[idx] = w;
      }
    }
  }

  // ---- vessels ------------------------------------------------------------------------------
  const report: AnatomyReport = { dims, vessels: [], fatMmActual: fatMm, deepFasciaDepthMm: L.epidermisMm + L.dermisMm + fatMm, fillMs: 0 };
  const areaCm2 = (sx / 10) * (sy / 10);
  const nPerf = Math.round(sc.vessels.perforatorsPerCm2 * areaCm2);
  const [rMin, rMax] = sc.vessels.radiusMm;
  const paintCylinderZ = (x0: number, y0: number, r: number, depthFrom: number, depthTo: number, type: Tissue, meander: number, phase: number) => {
    // vertical-ish cylinder from depthFrom (shallow) to depthTo (deep), with sinusoidal meander
    const rv = Math.max(r, h * 0.6);
    const auxR = Math.min(255, Math.round(r / 0.05));
    const cc = packCell(type, 0, auxR);
    const wv = TISSUE[type].water;
    const iMin = Math.max(0, Math.floor((x0 - rv - meander) / h)), iMax = Math.min(nx - 1, Math.ceil((x0 + rv + meander) / h));
    const jMin = Math.max(0, Math.floor((y0 - rv - meander) / h)), jMax = Math.min(ny - 1, Math.ceil((y0 + rv + meander) / h));
    for (let k = 0; k < nz; k++) {
      for (let j = jMin; j <= jMax; j++) {
        for (let i = iMin; i <= iMax; i++) {
          const c = i + nx * j;
          const depth = (colSurf[c] - (k + 0.5)) * h;
          if (depth < depthFrom || depth > depthTo) continue;
          const mx = x0 + meander * Math.sin(depth * 0.9 + phase), my = y0 + meander * Math.cos(depth * 0.7 + phase * 1.3);
          const dx = (i + 0.5) * h - mx, dy = (j + 0.5) * h - my;
          if (dx * dx + dy * dy <= rv * rv) {
            const idx = i + nx * (j + ny * k);
            if (occupancy[idx] > 0) { cell[idx] = cc; water[idx] = wv; }
          }
        }
      }
    }
  };
  const paintSegment = (ax: number, ay: number, az: number, bx: number, by: number, bz: number, r: number, type: Tissue, aux: number) => {
    // segment in (x mm, y mm, depth mm) coordinates
    const rv = Math.max(r, h * 0.6);
    const cc = packCell(type, 0, aux);
    const wv = TISSUE[type].water;
    const iMin = Math.max(0, Math.floor((Math.min(ax, bx) - rv) / h)), iMax = Math.min(nx - 1, Math.ceil((Math.max(ax, bx) + rv) / h));
    const jMin = Math.max(0, Math.floor((Math.min(ay, by) - rv) / h)), jMax = Math.min(ny - 1, Math.ceil((Math.max(ay, by) + rv) / h));
    const dMin = Math.min(az, bz) - rv, dMax = Math.max(az, bz) + rv;
    const abx = bx - ax, aby = by - ay, abz = bz - az;
    const ab2 = abx * abx + aby * aby + abz * abz || 1;
    for (let j = jMin; j <= jMax; j++) {
      for (let i = iMin; i <= iMax; i++) {
        const c = i + nx * j;
        const surf = colSurf[c];
        const kHi = Math.min(nz - 1, Math.ceil(surf - dMin / h)), kLo = Math.max(0, Math.floor(surf - dMax / h) - 1);
        const px = (i + 0.5) * h, py = (j + 0.5) * h;
        for (let k = kLo; k <= kHi; k++) {
          const pz = (surf - (k + 0.5)) * h;
          let t = ((px - ax) * abx + (py - ay) * aby + (pz - az) * abz) / ab2;
          t = Math.max(0, Math.min(1, t));
          const qx = ax + abx * t - px, qy = ay + aby * t - py, qz = az + abz * t - pz;
          if (qx * qx + qy * qy + qz * qz <= rv * rv) {
            const idx = i + nx * (j + ny * k);
            if (occupancy[idx] > 0) { cell[idx] = cc; water[idx] = wv; }
          }
        }
      }
    }
  };

  const placeVessel = (x: number, y: number, r: number, arterial: boolean) => {
    const c = Math.floor(x / h) + nx * Math.floor(y / h);
    const type = arterial ? Tissue.Artery : Tissue.Vein;
    paintCylinderZ(x, y, r, colDermis[c] - 0.3, colDeepBot[c] + 1.5, type, 0.6 + rng() * 0.8, rng() * 6.28);
    const ang = rng() * 6.28, len = 6 + rng() * 10;
    paintSegment(x, y, colDeepTop[c] - r, x + Math.cos(ang) * len, y + Math.sin(ang) * len, colDeepTop[c] - r, r * 1.1, type, Math.round(r * 1.1 / 0.05));
    report.vessels.push({ x: x - sx / 2, y: y - sy / 2, r, arterial });
  };
  for (const v of sc.vessels.explicit ?? []) placeVessel(v.xMm + sx / 2, v.yMm + sy / 2, v.rMm, v.arterial);
  for (let n = 0; n < nPerf; n++) {
    // place on a septum: sample until we hit one (septa are where perforators run)
    let x = 0, y = 0;
    for (let tries = 0; tries < 40; tries++) {
      x = (0.08 + 0.84 * rng()) * sx; y = (0.08 + 0.84 * rng()) * sy;
      const c = Math.floor(x / h) + nx * Math.floor(y / h);
      if (colSeptum[c]) break;
    }
    const r = rMin + (rMax - rMin) * Math.pow(rng(), 1.6);
    placeVessel(x, y, r, rng() < sc.vessels.arterialFraction);
  }

  // ---- nerves ---------------------------------------------------------------------------------
  for (const nv of sc.nerves) {
    const ax = nv.fromMm[0] + sx / 2, ay = nv.fromMm[1] + sy / 2, bx = nv.toMm[0] + sx / 2, by = nv.toMm[1] + sy / 2;
    const steps = 12;
    for (let s = 0; s < steps; s++) {
      const t0 = s / steps, t1 = (s + 1) / steps;
      const wob = (t: number) => Math.sin(t * 9.1) * 1.2;
      paintSegment(ax + (bx - ax) * t0, ay + (by - ay) * t0 + wob(t0), nv.depthMm, ax + (bx - ax) * t1, ay + (by - ay) * t1 + wob(t1), nv.depthMm, nv.radiusMm, Tissue.Nerve, 0);
    }
  }

  // ---- lesion -----------------------------------------------------------------------------------
  if (sc.lesion && sc.lesion.type === 'lipoma') {
    const le = sc.lesion;
    const cx = le.centerMm[0] + sx / 2, cy = le.centerMm[1] + sy / 2;
    const [a, b, cz] = le.sizeMm;
    const cc0 = Math.floor(cx / h) + nx * Math.floor(cy / h);
    let topDepth = le.topDepthMm;
    if (le.plane === 'subfascial') topDepth = colDeepBot[cc0] + 0.5;
    if (le.plane === 'intramuscular') topDepth = colDeepBot[cc0] + 3;
    const cdz = topDepth + cz; // centre depth
    const cap = le.capsuleMm;
    const wLip = TISSUE[Tissue.Lipoma].water, wCap = TISSUE[Tissue.LipomaCapsule].water;
    const cLip = packCell(Tissue.Lipoma, 0, 0), cCap = packCell(Tissue.LipomaCapsule);
    const iMin = Math.max(0, Math.floor((cx - a - cap) / h)), iMax = Math.min(nx - 1, Math.ceil((cx + a + cap) / h));
    const jMin = Math.max(0, Math.floor((cy - b - cap) / h)), jMax = Math.min(ny - 1, Math.ceil((cy + b + cap) / h));
    for (let j = jMin; j <= jMax; j++) {
      for (let i = iMin; i <= iMax; i++) {
        const c = i + nx * j;
        const surf = colSurf[c];
        const px = (i + 0.5) * h, py = (j + 0.5) * h;
        // lobulated boundary
        const lobN = 1 + 0.08 * (fbm2(px / 3, py / 3, 2, seed + 41) - 0.5) * 2;
        for (let k = 0; k < nz; k++) {
          const pz = (surf - (k + 0.5)) * h;
          if (pz < topDepth - cap - h || pz > cdz + cz + cap + h) continue;
          const ex = (px - cx) / a, ey = (py - cy) / b, ez = (pz - cdz) / cz;
          const rr = Math.sqrt(ex * ex + ey * ey + ez * ez) * lobN;
          const idx = i + nx * (j + ny * k);
          if (occupancy[idx] <= 0) continue;
          // normalised shell thickness
          const shell = cap / Math.min(a, b, cz);
          if (rr <= 1 - shell) { cell[idx] = cLip; water[idx] = wLip; }
          else if (rr <= 1) { cell[idx] = cCap; water[idx] = wCap; }
        }
      }
    }
    // feeding vessel from the deep fascia to the lesion base
    paintSegment(cx + a * 0.3, cy, colDeepTop[cc0] - 0.2, cx + a * 0.2, cy, cdz + cz * 0.9, 0.5, Tissue.Artery, Math.round(0.5 / 0.05));
    report.lesionCenter = [cx - sx / 2, -cdz, cy - sy / 2];
  }

  // ---- pre-existing wound -----------------------------------------------------------------
  for (const pc of sc.preCarve ?? []) carveSdf(vol, (x, y, d) => {
    const cx = pc.centerMm[0], cy = pc.centerMm[1];
    if (pc.kind === 'pit') return Math.max(Math.hypot(x - cx, y - cy) - (pc.lengthMm - d * (pc.slope ?? 0.4)), d - pc.depthMm);
    const a = (pc.angleDeg ?? 0) * Math.PI / 180, ca = Math.cos(a), sa = Math.sin(a);
    const u = (x - cx) * ca + (y - cy) * sa, v = -(x - cx) * sa + (y - cy) * ca;
    return Math.max(Math.abs(u) - pc.lengthMm / 2, Math.abs(v) - (pc.widthMm + d * (pc.slope ?? 0.3)), d - pc.depthMm);
  });

  report.fillMs = performance.now() - t0;
  return { volume: vol, report };
}

/** Carves a region given by a signed distance (mm, negative inside) in bed coordinates (x, y from the bed centre, depth below the nominal surface). */
export function carveSdf(volume: Volume, sdf: (x: number, y: number, depth: number) => number) {
  const { nx, ny, nz, h } = volume.dims;
  const surfK = volume.surfaceK;
  for (let k = 0; k < nz; k++) for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) {
    const x = (i + 0.5 - nx / 2) * h, y = (j + 0.5 - ny / 2) * h, depth = (surfK - (k + 0.5)) * h;
    const d = sdf(x, y, depth);
    if (d < h) {
      const idx = volume.index(i, j, k);
      const keep = Math.min(1, Math.max(0, 0.5 + d / h));
      volume.occupancy[idx] = Math.min(volume.occupancy[idx], keep);
      if (volume.occupancy[idx] < 0.5) volume.cell[idx] = 0;
    }
  }
}
