/** Procedural geometry for drapes and instruments. Interleaved pos(3) nrm(3) uv(2). */
import { mat4, vec3 } from 'gl-matrix';

export class MeshBuilder {
  private data: number[] = [];
  count = 0;
  tri(a: vec3, b: vec3, c: vec3, n?: vec3, uva: [number, number] = [0, 0], uvb: [number, number] = [1, 0], uvc: [number, number] = [1, 1]) {
    let nn = n;
    if (!nn) {
      const ab = vec3.sub(vec3.create(), b, a), ac = vec3.sub(vec3.create(), c, a);
      nn = vec3.normalize(vec3.create(), vec3.cross(vec3.create(), ab, ac));
    }
    for (const [p, uv] of [[a, uva], [b, uvb], [c, uvc]] as [vec3, [number, number]][]) {
      this.data.push(p[0], p[1], p[2], nn[0], nn[1], nn[2], uv[0], uv[1]);
    }
    this.count += 3;
  }
  /** triangle with a normal per vertex (smooth surfaces) */
  triN(a: vec3, b: vec3, c: vec3, na: vec3, nb: vec3, nc: vec3) {
    for (const [p, n] of [[a, na], [b, nb], [c, nc]] as [vec3, vec3][]) {
      this.data.push(p[0], p[1], p[2], n[0], n[1], n[2], p[0] * 0.05, p[2] * 0.05);
    }
    this.count += 3;
  }
  quad(a: vec3, b: vec3, c: vec3, d: vec3, n?: vec3) {
    this.tri(a, b, c, n, [0, 0], [1, 0], [1, 1]);
    this.tri(a, c, d, n, [0, 0], [1, 1], [0, 1]);
  }
  /** axis-aligned box centred at c with half extents e */
  box(c: vec3, e: vec3) {
    const [cx, cy, cz] = c, [ex, ey, ez] = e;
    const P = (sx: number, sy: number, sz: number) => vec3.fromValues(cx + sx * ex, cy + sy * ey, cz + sz * ez);
    this.quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), vec3.fromValues(0, 0, 1));
    this.quad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), vec3.fromValues(0, 0, -1));
    this.quad(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), vec3.fromValues(1, 0, 0));
    this.quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), vec3.fromValues(-1, 0, 0));
    this.quad(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), vec3.fromValues(0, 1, 0));
    this.quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), vec3.fromValues(0, -1, 0));
  }
  /** cylinder along +Y from y0 to y1 with radii r0 (bottom) r1 (top) */
  cylinder(r0: number, r1: number, y0: number, y1: number, seg = 24, caps = true) {
    for (let s = 0; s < seg; s++) {
      const a0 = (s / seg) * Math.PI * 2, a1 = ((s + 1) / seg) * Math.PI * 2;
      const c0 = Math.cos(a0), s0 = Math.sin(a0), c1 = Math.cos(a1), s1 = Math.sin(a1);
      const p00 = vec3.fromValues(c0 * r0, y0, s0 * r0), p10 = vec3.fromValues(c1 * r0, y0, s1 * r0);
      const p01 = vec3.fromValues(c0 * r1, y1, s0 * r1), p11 = vec3.fromValues(c1 * r1, y1, s1 * r1);
      const slope = (r0 - r1) / Math.max(y1 - y0, 1e-6);
      const n0 = vec3.normalize(vec3.create(), vec3.fromValues(c0, slope, s0));
      const n1 = vec3.normalize(vec3.create(), vec3.fromValues(c1, slope, s1));
      this.tri(p00, p10, p11, undefined);
      this.tri(p00, p11, p01, undefined);
      // overwrite normals of the last 6 vertices with smooth normals
      const base = this.data.length - 48;
      const ns = [n0, n1, n1, n0, n1, n0];
      for (let i = 0; i < 6; i++) { this.data[base + i * 8 + 3] = ns[i][0]; this.data[base + i * 8 + 4] = ns[i][1]; this.data[base + i * 8 + 5] = ns[i][2]; }
      if (caps) {
        if (r1 > 0) this.tri(vec3.fromValues(0, y1, 0), p01, p11, vec3.fromValues(0, 1, 0));
        if (r0 > 0) this.tri(vec3.fromValues(0, y0, 0), p10, p00, vec3.fromValues(0, -1, 0));
      }
    }
  }
  transform(m: mat4, from = 0) {
    for (let i = from; i < this.data.length; i += 8) {
      const p = vec3.fromValues(this.data[i], this.data[i + 1], this.data[i + 2]);
      const n = vec3.fromValues(this.data[i + 3], this.data[i + 4], this.data[i + 5]);
      vec3.transformMat4(p, p, m);
      const nm = mat4.clone(m); nm[12] = 0; nm[13] = 0; nm[14] = 0;
      vec3.transformMat4(n, n, nm); vec3.normalize(n, n);
      this.data[i] = p[0]; this.data[i + 1] = p[1]; this.data[i + 2] = p[2];
      this.data[i + 3] = n[0]; this.data[i + 4] = n[1]; this.data[i + 5] = n[2];
    }
  }
  get byteLength() { return this.data.length; }
  toBuffer(device: GPUDevice, label: string): { buffer: GPUBuffer; count: number } {
    const arr = new Float32Array(this.data);
    const buffer = device.createBuffer({ label, size: arr.byteLength, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(buffer, 0, arr);
    return { buffer, count: this.count };
  }
}

// ---- deterministic 2D value noise (same hash as the Unity client's Core/Noise.cs, so both clients fold their drapes alike)
function hash2(ix: number, iy: number, seed: number): number {
  let h = (Math.imul(ix, 374761393) + Math.imul(iy, 668265263) + Math.imul(seed, 2246822519)) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177) >>> 0;
  h = (h ^ (h >>> 16)) >>> 0;
  return h / 4294967296;
}
function value2(x: number, y: number, seed: number): number {
  const ix = Math.floor(x), iy = Math.floor(y);
  const sm = (t: number) => t * t * (3 - 2 * t);
  const fx = sm(x - ix), fy = sm(y - iy);
  const a = hash2(ix, iy, seed), b = hash2(ix + 1, iy, seed), c = hash2(ix, iy + 1, seed), d = hash2(ix + 1, iy + 1, seed);
  return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
}
export function fbm2(x: number, y: number, octaves: number, seed = 0): number {
  let sum = 0, amp = 0.5, norm = 0, f = 1;
  for (let o = 0; o < octaves; o++) { sum += amp * value2(x * f, y * f, seed + o * 101); norm += amp; amp *= 0.5; f *= 2.03; }
  return sum / norm;
}
const smoothstep = (a: number, b: number, x: number) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

// ---- fenestrated drape ------------------------------------------------------------------------------------
/** Aperture geometry in mm: half extents and corner radius of the rounded-rectangle fenestration. */
export interface DrapeSpec { ax: number; az: number; rc: number }
export const DRAPE_FILM_MM = 5;   // incise film margin adhered to the skin
export const DRAPE_PAD_MM = 20;   // absorbent reinforcement around the fenestration

/** Signed distance outward from the aperture edge (negative inside). */
export function apertureDist(x: number, z: number, s: DrapeSpec): number {
  const qx = Math.abs(x) - (s.ax - s.rc), qz = Math.abs(z) - (s.az - s.rc);
  const ox = Math.max(qx, 0), oz = Math.max(qz, 0);
  return Math.hypot(ox, oz) + Math.min(Math.max(qx, qz), 0) - s.rc;
}

/**
 * Drape height above the skin plane (mm). The incise film lies on the skin behind a rolled hem, the reinforcement
 * pad is thicker and flat, and beyond it the loose drape carries broad swells, pleats radiating from the
 * fenestration where it was pulled taut, the sharp creases left by its fan-folded packaging, and fine wrinkles;
 * past the table edge it hangs down.
 */
export function drapeHeight(x: number, z: number, s: DrapeSpec): number {
  const d = apertureDist(x, z, s);
  let y = 0.12 + 0.35 * smoothstep(0, DRAPE_FILM_MM, d) + 0.55 * smoothstep(DRAPE_FILM_MM, DRAPE_PAD_MM, d);
  y += 1.1 * Math.exp(-(((d - 1.2) / 0.9) ** 2));                                   // rolled hem at the aperture edge
  const loose = smoothstep(DRAPE_PAD_MM - 4, 60, d);
  const far = smoothstep(60, 220, d);
  const swell = (fbm2(x / 70, z / 70, 3, 5) - 0.5) * 2;
  const ang = Math.atan2(z, x);
  const pleat = Math.sin(ang * 9 + (fbm2(x / 40, z / 40, 2, 11) - 0.5) * 6) * 0.5 + 0.5;
  const wrinkle = (fbm2(x / 14, z / 14, 2, 17) - 0.5) * 2;
  y += loose * (swell * (2.5 + 6 * far) + pleat * pleat * (1.2 + 3 * far) + wrinkle * 0.5);
  // package creases: a slightly skewed grid of sharp ridges (120 x 170 mm fan folds) plus a few random creases
  const ca = 0.14, u1 = x * Math.cos(ca) + z * Math.sin(ca), v1 = -x * Math.sin(ca) + z * Math.cos(ca);
  const ridge = (t: number, period: number) => { const q = Math.abs((((t / period) % 1) + 1) % 1 - 0.5) * period; return Math.exp(-((q / 2.2) ** 2)); };
  const creaseGrid = ridge(u1 + 37, 120) + ridge(v1 + 61, 170);
  const creaseRand = Math.exp(-((Math.abs(fbm2(x / 55, z / 55, 2, 23) - 0.5) / 0.012) ** 2));
  y += loose * (creaseGrid * 1.8 + creaseRand * 1.2);
  y -= Math.max(0, d - 260) * 0.5;
  return y;
}

function drapeNormal(x: number, z: number, s: DrapeSpec): vec3 {
  const e = 0.5;
  const dx = (drapeHeight(x + e, z, s) - drapeHeight(x - e, z, s)) / (2 * e);
  const dz = (drapeHeight(x, z + e, s) - drapeHeight(x, z - e, s)) / (2 * e);
  return vec3.normalize(vec3.create(), vec3.fromValues(-dx, 1, -dz));
}

/** Point on the rounded rectangle offset by d from the aperture, at perimeter sample k of n (consistent topology per ring). */
function ringPoint(k: number, n: number, d: number, s: DrapeSpec): [number, number] {
  const a = s.ax + d, b = s.az + d, r = s.rc + d;
  const nEdge = Math.round(n / 12), nArc = n / 4 - nEdge;
  const seg = Math.floor(k / (n / 4)), t = k - seg * (n / 4);
  // each quarter: a straight run then a corner arc, going counter-clockwise from (+a, -b+r)
  let px: number, pz: number;
  if (t < nEdge) {
    const u = t / nEdge;
    switch (seg) {
      case 0: px = a; pz = -(b - r) + u * 2 * (b - r); break;
      case 1: px = (a - r) - u * 2 * (a - r); pz = b; break;
      case 2: px = -a; pz = (b - r) - u * 2 * (b - r); break;
      default: px = -(a - r) + u * 2 * (a - r); pz = -b; break;
    }
  } else {
    const u = (t - nEdge) / nArc;
    const base = seg * Math.PI / 2, ang = base + u * Math.PI / 2;
    const cx = seg === 0 || seg === 3 ? a - r : -(a - r), cz = seg === 0 || seg === 1 ? b - r : -(b - r);
    px = cx + Math.cos(ang) * r; pz = cz + Math.sin(ang) * r;
  }
  return [px, pz];
}

export interface DrapeSet { film: MeshBuilder; pad: MeshBuilder; drape: MeshBuilder; spec: DrapeSpec }

/** Fenestrated drape around the bed aperture in three parts: incise film, reinforcement pad, loose blue drape. */
export function buildDrapeSet(halfX: number, halfZ: number, margin = 2.5): DrapeSet {
  const spec: DrapeSpec = { ax: halfX - margin, az: halfZ - margin, rc: 6 };
  const rings = [0, 0.6, 1.2, 1.8, 2.5, 3.5, DRAPE_FILM_MM, 7, 10, 14, 17, DRAPE_PAD_MM, 23, 26, 29, 32, 35, 38, 41, 44, 48, 52, 56, 60, 65, 70, 76, 82, 88, 95, 102, 110, 118, 127, 137, 148, 160, 175, 190, 210, 235, 260, 290, 330, 400];
  const n = 256;
  const film = new MeshBuilder(), pad = new MeshBuilder(), drape = new MeshBuilder();
  const P = (k: number, d: number) => { const [x, z] = ringPoint(k % n, n, d, spec); return vec3.fromValues(x, drapeHeight(x, z, spec), z); };
  const N = (k: number, d: number) => { const [x, z] = ringPoint(k % n, n, d, spec); return drapeNormal(x, z, spec); };
  for (let r = 0; r < rings.length - 1; r++) {
    const d0 = rings[r], d1 = rings[r + 1];
    const mb = d1 <= DRAPE_FILM_MM ? film : d1 <= DRAPE_PAD_MM ? pad : drape;
    for (let k = 0; k < n; k++) {
      const a = P(k, d0), b = P(k + 1, d0), c = P(k + 1, d1), d = P(k, d1);
      const na = N(k, d0), nb = N(k + 1, d0), nc = N(k + 1, d1), nd = N(k, d1);
      mb.triN(a, c, b, na, nc, nb);
      mb.triN(a, d, c, na, nd, nc);
    }
  }
  return { film, pad, drape, spec };
}

/** Bovie pencil along -Y (tip at origin pointing down), length ~150 mm, blade electrode. */
export function buildPencilBody(): MeshBuilder {
  const mb = new MeshBuilder();
  // body: tapered front, cylindrical rear
  mb.cylinder(3.2, 5.2, 22, 60, 28);
  mb.cylinder(5.2, 5.2, 60, 165, 28);
  mb.cylinder(5.2, 4.6, 165, 172, 28);
  // nose cone
  mb.cylinder(1.8, 3.2, 14, 22, 20);
  // rocker switch housing (blue)
  return mb;
}
export function buildPencilButtons(): MeshBuilder {
  const mb = new MeshBuilder();
  mb.box(vec3.fromValues(4.6, 82, 0), vec3.fromValues(1.6, 9, 3.2));   // cut (yellow), on the upper face
  mb.box(vec3.fromValues(4.6, 104, 0), vec3.fromValues(1.6, 9, 3.2));  // coag (blue)
  return mb;
}
export function buildBlade(): MeshBuilder {
  // stainless blade electrode: shaft 2.4 mm dia from y=4..14, blade 2.4 x 0.5 x 12 mm, tip at y=0
  const mb = new MeshBuilder();
  mb.cylinder(1.2, 1.2, 5, 15, 16);
  mb.box(vec3.fromValues(0, 2.5, 0), vec3.fromValues(1.2, 2.5, 0.25));
  // bevelled tip
  mb.tri(vec3.fromValues(-1.2, 0, 0.25), vec3.fromValues(1.2, 0, 0.25), vec3.fromValues(0, -1.2, 0), vec3.fromValues(0, -0.2, 1));
  mb.tri(vec3.fromValues(1.2, 0, -0.25), vec3.fromValues(-1.2, 0, -0.25), vec3.fromValues(0, -1.2, 0), vec3.fromValues(0, -0.2, -1));
  return mb;
}

/** One forceps arm along +Y from the jaw tip (origin) to the pivot: flat tapered bar with serrated tip. */
export function buildForcepsArm(length = 95): MeshBuilder {
  const mb = new MeshBuilder();
  // jaw: 1.2 mm wide, 0.8 mm thick, 8 mm long
  mb.box(vec3.fromValues(0, 4, 0), vec3.fromValues(0.6, 4, 0.4));
  // serrations as small ridges on the inner face
  for (let i = 0; i < 6; i++) mb.box(vec3.fromValues(0, 0.8 + i * 1.1, 0.45), vec3.fromValues(0.6, 0.25, 0.12));
  // shank: widening bar
  mb.box(vec3.fromValues(0, 8 + (length - 8) * 0.5, 0), vec3.fromValues(1.8, (length - 8) * 0.5, 0.7));
  // finger grip ribs
  for (let i = 0; i < 10; i++) mb.box(vec3.fromValues(0, length * 0.45 + i * 3, -0.9), vec3.fromValues(2.4, 0.6, 0.25));
  return mb;
}

/** Frazier suction tip: slim angled tube along +Y with a thumb vent, tip at origin. */
export function buildFrazier(): MeshBuilder {
  const mb = new MeshBuilder();
  mb.cylinder(1.5, 1.5, 0, 40, 16);
  // bend: second segment angled 30 deg
  const bent = new MeshBuilder();
  bent.cylinder(1.5, 1.5, 0, 80, 16);
  bent.cylinder(4.5, 4.5, 60, 70, 16);                       // handle bulge
  bent.box(vec3.fromValues(0, 66, 4.6), vec3.fromValues(2, 3, 0.6)); // thumb vent plate
  const m = mat4.create();
  mat4.fromTranslation(m, [0, 40, 0]);
  mat4.rotateZ(m, m, -30 * Math.PI / 180);
  bent.transform(m);
  // append bent into mb
  (mb as unknown as { data: number[] }).data.push(...(bent as unknown as { data: number[] }).data);
  mb.count += bent.count;
  return mb;
}

/** Skin hook: thin shaft along +Y with a 3 mm curved tine at the origin. */
export function buildSkinHook(): MeshBuilder {
  const mb = new MeshBuilder();
  mb.cylinder(1.2, 1.2, 4, 110, 12);
  mb.cylinder(0.45, 0.45, 0, 4.5, 8);
  // tine: short cylinder bent forward
  const tine = new MeshBuilder();
  tine.cylinder(0.4, 0.3, 0, 3.2, 8);
  const m = mat4.create(); mat4.rotateX(m, m, 110 * Math.PI / 180); tine.transform(m);
  (mb as unknown as { data: number[] }).data.push(...(tine as unknown as { data: number[] }).data);
  mb.count += tine.count;
  return mb;
}
