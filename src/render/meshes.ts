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
  /** round tube of radius r along a polyline (parallel-transport frames), optional point at the end */
  tube(path: vec3[], r: number, seg = 12, pointedEnd = false) {
    const m = path.length;
    const t: vec3[] = [];
    for (let i = 0; i < m; i++) t.push(vec3.normalize(vec3.create(), vec3.sub(vec3.create(), path[Math.min(m - 1, i + 1)], path[Math.max(0, i - 1)])));
    let nrm = vec3.normalize(vec3.create(), vec3.cross(vec3.create(), t[0], Math.abs(t[0][1]) < 0.9 ? [0, 1, 0] : [1, 0, 0]));
    const rings: vec3[][] = [], norms: vec3[][] = [];
    for (let i = 0; i < m; i++) {
      if (i > 0) {
        const axis = vec3.cross(vec3.create(), t[i - 1], t[i]);
        const sl = vec3.length(axis);
        if (sl > 1e-6) {
          const ang = Math.atan2(sl, vec3.dot(t[i - 1], t[i]));
          const q = mat4.fromRotation(mat4.create(), ang, vec3.scale(axis, axis, 1 / sl));
          nrm = vec3.normalize(vec3.create(), vec3.transformMat4(vec3.create(), nrm, q));
        }
      }
      const bin = vec3.normalize(vec3.create(), vec3.cross(vec3.create(), t[i], nrm));
      const rr = pointedEnd && i === m - 1 ? 0.02 : r;
      const ring: vec3[] = [], nn: vec3[] = [];
      for (let k = 0; k < seg; k++) {
        const th = (k / seg) * Math.PI * 2;
        const dir = vec3.scaleAndAdd(vec3.create(), vec3.scale(vec3.create(), nrm, Math.cos(th)), bin, Math.sin(th));
        ring.push(vec3.scaleAndAdd(vec3.create(), path[i], dir, rr)); nn.push(dir);
      }
      rings.push(ring); norms.push(nn);
    }
    for (let i = 0; i < m - 1; i++) for (let k = 0; k < seg; k++) {
      const k1 = (k + 1) % seg;
      this.triN(rings[i][k], rings[i + 1][k1], rings[i][k1], norms[i][k], norms[i + 1][k1], norms[i][k1]);
      this.triN(rings[i][k], rings[i + 1][k], rings[i + 1][k1], norms[i][k], norms[i + 1][k], norms[i + 1][k1]);
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
/** Aperture geometry in mm: half extents and corner radius of the rounded-rectangle fenestration, and a seed for the folds. */
export interface DrapeSpec { ax: number; az: number; rc: number; seed: number }
export const DRAPE_FILM_MM = 2.5; // incise film margin adhered to the skin
export const DRAPE_PAD_MM = 36;   // flat reinforced zone around the fenestration (under the towels)
/** towels: folded edge this far outside the skin edge, folded width (outward), length along the side, folded thickness */
export const TOWEL_GAP_MM = 2.5, TOWEL_W_MM = 55, TOWEL_L_EXTRA_MM = 45, TOWEL_T_MM = 2.4;

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
  const sd = s.seed * 17;
  let y = 0.12 + 0.35 * smoothstep(0, DRAPE_FILM_MM, d) + 0.55 * smoothstep(DRAPE_FILM_MM, DRAPE_PAD_MM, d);
  y += 1.1 * Math.exp(-(((d - 1.2) / 0.9) ** 2));                                   // rolled hem at the aperture edge
  const loose = smoothstep(DRAPE_PAD_MM - 6, 70, d);
  const far = smoothstep(70, 240, d);
  // domain warp so swells and waves are irregular rather than a stationary noise pattern
  const wx = x + 30 * (fbm2(x / 95, z / 95, 2, sd + 3) - 0.5), wz = z + 30 * (fbm2(x / 95 + 7, z / 95, 2, sd + 4) - 0.5);
  const swell = (fbm2(wx / 70, wz / 70, 3, sd + 5) - 0.5) * 2;
  // waves: the sheet lies over the body's contour, so broad parallel waves run across it, bending with the warp
  const wa = 0.5 + 0.8 * (fbm2(x / 300, z / 300, 1, sd + 6) - 0.5);
  const wave = Math.sin((wx * Math.cos(wa) + wz * Math.sin(wa)) / 26 + 2.5 * fbm2(x / 60, z / 60, 2, sd + 7));
  const ang = Math.atan2(z, x);
  const pleat = Math.sin(ang * 9 + (fbm2(x / 40, z / 40, 2, sd + 11) - 0.5) * 6) * 0.5 + 0.5;
  const wrinkle = (fbm2(wx / 14, wz / 14, 2, sd + 17) - 0.5) * 2;
  // local heaps where the fabric bunched when it was laid
  const bunch = Math.max(0, fbm2(x / 48, z / 48, 2, sd + 29) - 0.56) * 22;
  y += loose * (swell * (2.5 + 6 * far) + wave * (0.8 + 2.6 * far) + pleat * pleat * (1.0 + 2.5 * far) + wrinkle * 0.5 + bunch * (0.3 + far));
  // package creases: a slightly skewed grid of sharp ridges (120 x 170 mm fan folds) plus a few random creases
  const ca = 0.14 + (s.seed % 7) * 0.05, u1 = x * Math.cos(ca) + z * Math.sin(ca), v1 = -x * Math.sin(ca) + z * Math.cos(ca);
  const ridge = (t: number, period: number) => { const q = Math.abs((((t / period) % 1) + 1) % 1 - 0.5) * period; return Math.exp(-((q / 2.2) ** 2)); };
  const creaseGrid = ridge(u1 + 37 + s.seed * 13, 120) + ridge(v1 + 61 + s.seed * 7, 170);
  const creaseRand = Math.exp(-((Math.abs(fbm2(wx / 55, wz / 55, 2, sd + 23) - 0.5) / 0.012) ** 2));
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

export interface DrapeSet { film: MeshBuilder; pad: MeshBuilder; drape: MeshBuilder; towels: MeshBuilder[]; clips: { mesh: MeshBuilder; model: mat4 }[]; spec: DrapeSpec }

/** Fenestrated drape around the bed aperture: incise film, reinforcement pad, loose blue drape, four towels squaring the window and their clips. */
export function buildDrapeSet(halfX: number, halfZ: number, seed = 0, margin = 2.5): DrapeSet {
  const spec: DrapeSpec = { ax: halfX - margin, az: halfZ - margin, rc: 6, seed };
  const rings = [0, 0.5, 1.0, 1.5, 2.0, DRAPE_FILM_MM, 3.5, 5, 7, 10, 14, 18, 22, 26, 30, DRAPE_PAD_MM, 40, 44, 48, 52, 56, 60, 65, 70, 76, 82, 88, 95, 102, 110, 118, 127, 137, 148, 160, 175, 190, 210, 235, 260, 290, 330, 400];
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
  return { film, pad, drape, towels: buildTowels(spec), clips: buildTowelClips(spec), spec };
}

// ---- towels and towel clips ---------------------------------------------------------------------------------------
/** Towel k runs along side k of the window (0: +z, 1: +x, 2: -z, 3: -x); u along the side, v outward from its folded edge. */
function towelFrame(k: number, s: DrapeSpec) {
  const wx = s.ax + TOWEL_GAP_MM, wz = s.az + TOWEL_GAP_MM;
  const r = (n: number) => fbm2(k * 3.7 + n, s.seed * 1.3 + n * 0.37, 1, 211 + n) - 0.5;
  const rot = r(1) * 0.09;   // a few degrees of misalignment per towel
  const base: Record<number, { ox: number; oz: number; ux: number; uz: number; half: number }> = {
    0: { ox: 0, oz: wz, ux: 1, uz: 0, half: wx },
    1: { ox: wx, oz: 0, ux: 0, uz: -1, half: wz },
    2: { ox: 0, oz: -wz, ux: -1, uz: 0, half: wx },
    3: { ox: -wx, oz: 0, ux: 0, uz: 1, half: wz },
  };
  const b = base[k];
  // outward (v) direction is the side's normal; rotate the frame slightly and shift the towel along its side
  const c = Math.cos(rot), sn = Math.sin(rot);
  const ux = b.ux * c - b.uz * sn, uz = b.ux * sn + b.uz * c;
  const vx = -uz, vz = ux;   // 90 degrees from u, pointing outward for this side ordering
  const shift = r(2) * 24;
  const ox = b.ox + ux * shift + r(3) * 3 * vx, oz = b.oz + uz * shift + r(3) * 3 * vz;
  return { ox, oz, ux, uz, vx, vz, half: b.half, len: b.half + TOWEL_L_EXTRA_MM, onTop: k % 2 === 0 };
}

function towelPoint(f: ReturnType<typeof towelFrame>, u: number, v: number): [number, number] {
  return [f.ox + f.ux * u + f.vx * v, f.oz + f.uz * u + f.vz * v];
}

/** Is world (x, z) inside towel k's footprint? */
function inTowel(k: number, s: DrapeSpec, x: number, z: number): boolean {
  const f = towelFrame(k, s);
  const dx = x - f.ox, dz = z - f.oz;
  const u = dx * f.ux + dz * f.uz, v = dx * f.vx + dz * f.vz;
  return u > -f.len && u < f.len && v > 0 && v < TOWEL_W_MM;
}

/** Top-surface height of towel k above the skin plane at local (u, v): its base (the drape, or the towel it overlaps), the folded hem and ripples. */
function towelHeight(k: number, s: DrapeSpec, u: number, v: number): number {
  const f = towelFrame(k, s);
  const [x, z] = towelPoint(f, u, v);
  let base = drapeHeight(x, z, s) + 0.15;
  if (f.onTop) { for (const j of [1, 3]) if (inTowel(j, s, x, z)) { base += TOWEL_T_MM; break; } }
  const hem = v < TOWEL_T_MM ? Math.sqrt(Math.max(0, 1 - ((TOWEL_T_MM - v) / TOWEL_T_MM) ** 2)) : 1;
  const sd = s.seed * 17 + k * 101;
  const loose = smoothstep(8, 45, v);
  const ripple = Math.sin(u / 17 + 2.2 * fbm2(u / 40, v / 40, 2, sd + 1)) * (0.35 + 0.5 * loose) * smoothstep(3, 20, v)
    + (fbm2(u / 22, v / 22, 3, sd + 2) - 0.5) * 2 * (0.3 + 0.9 * loose)
    + (fbm2(u / 7, v / 7, 2, sd + 3) - 0.5) * 0.35;
  return base + TOWEL_T_MM * hem + Math.max(ripple, -TOWEL_T_MM * 0.6 * hem);
}

/** Four folded towels on the drape, each along one side of the window, overlapping at the corners in a windmill. */
export function buildTowels(s: DrapeSpec): MeshBuilder[] {
  const out: MeshBuilder[] = [];
  const vs = [0, 0.5, 1.0, 1.6, 2.4, 3.5, 5, 7, 10, 13, 16, 20, 24, 28, 33, 38, 44, 50, 55];
  for (let k = 0; k < 4; k++) {
    const f = towelFrame(k, s);
    const mb = new MeshBuilder();
    const nu = 56;
    const P = (iu: number, iv: number) => { const u = -f.len + (2 * f.len * iu) / nu, v = vs[iv]; const [x, z] = towelPoint(f, u, v); return vec3.fromValues(x, towelHeight(k, s, u, v), z); };
    const N = (iu: number, iv: number) => {
      const u = -f.len + (2 * f.len * iu) / nu, v = vs[iv], e = 0.4;
      const du = (towelHeight(k, s, u + e, v) - towelHeight(k, s, u - e, v)) / (2 * e), dv = (towelHeight(k, s, u, v + e) - towelHeight(k, s, u, Math.max(0, v - e))) / (2 * e);
      // slopes in (u, v) to world: n = up - du * uDir - dv * vDir
      return vec3.normalize(vec3.create(), vec3.fromValues(-du * f.ux - dv * f.vx, 1, -du * f.uz - dv * f.vz));
    };
    for (let iu = 0; iu < nu; iu++) for (let iv = 0; iv < vs.length - 1; iv++) {
      const a = P(iu, iv), b = P(iu + 1, iv), c = P(iu + 1, iv + 1), d = P(iu, iv + 1);
      mb.triN(a, c, b, N(iu, iv), N(iu + 1, iv + 1), N(iu + 1, iv));
      mb.triN(a, d, c, N(iu, iv), N(iu, iv + 1), N(iu + 1, iv + 1));
    }
    // skirts at the two ends and the back edge, down to the base
    const skirt = (p0: vec3, p1: vec3, outward: vec3) => {
      const q0 = vec3.fromValues(p0[0], p0[1] - TOWEL_T_MM - 1.2, p0[2]), q1 = vec3.fromValues(p1[0], p1[1] - TOWEL_T_MM - 1.2, p1[2]);
      mb.triN(p0, p1, q1, outward, outward, outward); mb.triN(p0, q1, q0, outward, outward, outward);
    };
    for (let iv = 0; iv < vs.length - 1; iv++) {
      skirt(P(0, iv), P(0, iv + 1), vec3.fromValues(-f.ux, 0, -f.uz));
      skirt(P(nu, iv + 1), P(nu, iv), vec3.fromValues(f.ux, 0, f.uz));
    }
    for (let iu = 0; iu < nu; iu++) skirt(P(iu + 1, vs.length - 1), P(iu, vs.length - 1), vec3.fromValues(f.vx, 0, f.vz));
    out.push(mb);
  }
  return out;
}

/** Backhaus towel clip in its own frame: jaw tips at the origin, +Y toward the ring handles, lying in the X-Y plane with +Z up. */
export function buildTowelClip(): MeshBuilder {
  const mb = new MeshBuilder();
  for (const side of [-1, 1]) {
    const zo = side * 0.9;  // the two arms pass each other at the box joint
    const path: vec3[] = [];
    // curved jaw from the tip, bowing outward, then the shank crossing to the other side, then the ring
    for (let i = 0; i <= 8; i++) { const t = i / 8, a = t * Math.PI * 0.55; path.push(vec3.fromValues(side * (1 - Math.cos(a)) * 9, Math.sin(a) * 9 * 1.6, zo * t)); }
    path.push(vec3.fromValues(side * 9, 24, zo)); path.push(vec3.fromValues(-side * 7, 62, zo)); path.push(vec3.fromValues(-side * 9, 70, zo));
    mb.tube(path, 1.5, 12);
    // ring handle
    const ring: vec3[] = [];
    for (let i = 0; i <= 28; i++) { const a = (i / 28) * Math.PI * 2; ring.push(vec3.fromValues(-side * (9 + 1.5) + Math.cos(a) * 10.5, 80.5 + Math.sin(a) * 10.5, zo)); }
    mb.tube(ring, 1.6, 10);
  }
  // box joint
  mb.box(vec3.fromValues(0, 34, 0), vec3.fromValues(4.2, 5, 1.6));
  return mb;
}

/** The four clips at the window corners, jaws on the corner where two towels overlap, handles pointing out along the diagonal. */
export function buildTowelClips(s: DrapeSpec): { mesh: MeshBuilder; model: mat4 }[] {
  const out: { mesh: MeshBuilder; model: mat4 }[] = [];
  const wx = s.ax + TOWEL_GAP_MM, wz = s.az + TOWEL_GAP_MM;
  const corners: [number, number][] = [[1, 1], [1, -1], [-1, -1], [-1, 1]];
  corners.forEach(([sx, sz], i) => {
    const jitter = (fbm2(i * 2.3, s.seed * 0.7, 1, 307 + i) - 0.5);
    const ang = Math.atan2(sz, sx) + jitter * 0.5;                   // handles out along the diagonal
    const tipX = sx * wx + Math.cos(ang) * 4, tipZ = sz * wz + Math.sin(ang) * 4;
    const h = towelHeight(0, s, 0, 20) - drapeHeight(0, 0, s);      // typical towel-top height
    const y = drapeHeight(tipX, tipZ, s) + 2 * TOWEL_T_MM + h * 0.3 + 1.2;
    // basis: local +Y (clip axis) along the diagonal, local +Z (the clip's up) to world +Y
    const ey: [number, number, number] = [Math.cos(ang), 0, Math.sin(ang)], ez: [number, number, number] = [0, 1, 0];
    const ex: [number, number, number] = [-Math.sin(ang), 0, Math.cos(ang)];
    const m = mat4.fromValues(ex[0], ex[1], ex[2], 0, ey[0], ey[1], ey[2], 0, ez[0], ez[1], ez[2], 0, tipX, y, tipZ, 1);
    out.push({ mesh: buildTowelClip(), model: m });
  });
  return out;
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
