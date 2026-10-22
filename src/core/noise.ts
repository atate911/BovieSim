/** Deterministic RNG and cheap noise for anatomy generation and procedural detail. */

export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function hash2(ix: number, iy: number, seed: number): number {
  let h = (ix * 374761393 + iy * 668265263 + seed * 2246822519) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

function smooth(t: number): number { return t * t * (3 - 2 * t); }

/** 2D value noise in [0,1], cell size 1. */
export function valueNoise2(x: number, y: number, seed = 0): number {
  const ix = Math.floor(x), iy = Math.floor(y);
  const fx = smooth(x - ix), fy = smooth(y - iy);
  const a = hash2(ix, iy, seed), b = hash2(ix + 1, iy, seed);
  const c = hash2(ix, iy + 1, seed), d = hash2(ix + 1, iy + 1, seed);
  return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
}

/** Fractal value noise in [0,1]. */
export function fbm2(x: number, y: number, octaves: number, seed = 0): number {
  let sum = 0, amp = 0.5, norm = 0, f = 1;
  for (let o = 0; o < octaves; o++) {
    sum += amp * valueNoise2(x * f, y * f, seed + o * 101);
    norm += amp; amp *= 0.5; f *= 2.03;
  }
  return sum / norm;
}

export interface CellResult { f1: number; f2: number; id: number; cx: number; cy: number }

/**
 * 2D jittered-grid Voronoi: returns distance to nearest and second nearest feature point
 * plus the nearest cell id. Cell size 1.
 */
export function voronoi2(x: number, y: number, seed = 0, out: CellResult = { f1: 0, f2: 0, id: 0, cx: 0, cy: 0 }): CellResult {
  const ix = Math.floor(x), iy = Math.floor(y);
  let f1 = 1e9, f2 = 1e9, id = 0, cx = 0, cy = 0;
  for (let dy = -1; dy <= 1; dy++) {
    for (let dx = -1; dx <= 1; dx++) {
      const gx = ix + dx, gy = iy + dy;
      const px = gx + 0.15 + 0.7 * hash2(gx, gy, seed);
      const py = gy + 0.15 + 0.7 * hash2(gx, gy, seed + 7);
      const d = Math.hypot(px - x, py - y);
      if (d < f1) { f2 = f1; f1 = d; id = ((gx & 0xffff) * 31 + (gy & 0xffff) * 17) & 0xff; cx = px; cy = py; }
      else if (d < f2) { f2 = d; }
    }
  }
  out.f1 = f1; out.f2 = f2; out.id = id; out.cx = cx; out.cy = cy;
  return out;
}
