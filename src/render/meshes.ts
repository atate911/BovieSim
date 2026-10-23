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
  /** torus in the XY plane (axis along Z) centred at c: major radius R, tube radius r */
  torus(c: vec3, R: number, r: number, seg = 24, tube = 10) {
    const P = (a: number, b: number) => vec3.fromValues(c[0] + (R + r * Math.cos(b)) * Math.cos(a), c[1] + (R + r * Math.cos(b)) * Math.sin(a), c[2] + r * Math.sin(b));
    const N = (a: number, b: number) => vec3.fromValues(Math.cos(b) * Math.cos(a), Math.cos(b) * Math.sin(a), Math.sin(b));
    for (let i = 0; i < seg; i++) for (let j = 0; j < tube; j++) {
      const a0 = (i / seg) * Math.PI * 2, a1 = ((i + 1) / seg) * Math.PI * 2, b0 = (j / tube) * Math.PI * 2, b1 = ((j + 1) / tube) * Math.PI * 2;
      this.tri(P(a0, b0), P(a1, b0), P(a1, b1), N(a0, b0)); this.tri(P(a0, b0), P(a1, b1), P(a0, b1), N(a0, b1));
    }
  }
  append(other: MeshBuilder) { this.data.push(...other.data); this.count += other.count; }
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

/** Drape frame around the bed aperture, lying just above the skin, with a soft lip. */
export function buildDrapes(halfX: number, halfZ: number, margin = 2.5): MeshBuilder {
  const mb = new MeshBuilder();
  const ax = halfX - margin, az = halfZ - margin; // aperture half extents
  const R = 400; // drape outer extent
  const y = 0.6; // mm above skin
  const lip = 3.0;
  const q = (x0: number, z0: number, x1: number, z1: number, yy = y) =>
    mb.quad(vec3.fromValues(x0, yy, z1), vec3.fromValues(x1, yy, z1), vec3.fromValues(x1, yy, z0), vec3.fromValues(x0, yy, z0), vec3.fromValues(0, 1, 0));
  // four flat panels around the aperture
  q(-R, -R, R, -az - lip); q(-R, az + lip, R, R); q(-R, -az - lip, -ax - lip, az + lip); q(ax + lip, -az - lip, R, az + lip);
  // sloped lip down to the skin
  const slope = (x0: number, z0: number, x1: number, z1: number, x2: number, z2: number, x3: number, z3: number) => {
    mb.quad(vec3.fromValues(x0, y, z0), vec3.fromValues(x1, y, z1), vec3.fromValues(x2, 0.15, z2), vec3.fromValues(x3, 0.15, z3));
  };
  slope(ax + lip, -az - lip, -ax - lip, -az - lip, -ax, -az, ax, -az);          // front
  slope(-ax - lip, az + lip, ax + lip, az + lip, ax, az, -ax, az);              // back
  slope(-ax - lip, -az - lip, -ax - lip, az + lip, -ax, az, -ax, -az);          // left
  slope(ax + lip, az + lip, ax + lip, -az - lip, ax, -az, ax, az);              // right
  return mb;
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

/**
 * #15 scalpel on a #3 handle, tip at the origin, +Y along the handle, blade face normal +Z, cutting edge in the
 * XY plane. The small curved #15 blade is approximated by a tapered plate with a bevelled point.
 */
export function buildScalpel(): MeshBuilder {
  const mb = new MeshBuilder();
  // blade plate: 0.3 mm thick, belly 3.2 mm wide, 9 mm from the point to the neck
  mb.box(vec3.fromValues(0, 5.5, 0), vec3.fromValues(1.6, 3.5, 0.15));
  mb.tri(vec3.fromValues(-1.6, 2, 0.15), vec3.fromValues(1.6, 2, 0.15), vec3.fromValues(0.4, 0, 0), vec3.fromValues(0, -0.2, 1));
  mb.tri(vec3.fromValues(1.6, 2, -0.15), vec3.fromValues(-1.6, 2, -0.15), vec3.fromValues(0.4, 0, 0), vec3.fromValues(0, -0.2, -1));
  // blade slot / neck and the flat #3 handle (125 mm, 8 mm wide, 3 mm thick) with grip ridges
  mb.box(vec3.fromValues(0, 11, 0), vec3.fromValues(1.4, 2.5, 0.6));
  mb.box(vec3.fromValues(0, 13.5 + 20, 0), vec3.fromValues(2.4, 20, 1.2));
  mb.box(vec3.fromValues(0, 53.5 + 42, 0), vec3.fromValues(4.0, 42, 1.5));
  for (let i = 0; i < 8; i++) mb.box(vec3.fromValues(0, 100 + i * 4, 1.5), vec3.fromValues(3.6, 0.5, 0.15));
  return mb;
}

/**
 * One Metzenbaum blade with its shank and ring, +Y from the tip (origin) to the pivot at `jawLength`, blade normal +Z.
 * The two blades are posed converging at the pivot (like the forceps arms).
 */
export function buildScissorsArm(jawLength = 20, shankLength = 95): MeshBuilder {
  const mb = new MeshBuilder();
  // slender curved-pattern blade: narrow at the tip, 2.4 mm at the pivot, 0.8 mm thick
  mb.box(vec3.fromValues(0, jawLength * 0.25, 0), vec3.fromValues(0.55, jawLength * 0.25, 0.3));
  mb.box(vec3.fromValues(0, jawLength * 0.7, 0), vec3.fromValues(0.95, jawLength * 0.2, 0.4));
  mb.tri(vec3.fromValues(-0.55, 0.6, 0.3), vec3.fromValues(0.55, 0.6, 0.3), vec3.fromValues(0, 0, 0), vec3.fromValues(0, -0.3, 1));
  mb.tri(vec3.fromValues(0.55, 0.6, -0.3), vec3.fromValues(-0.55, 0.6, -0.3), vec3.fromValues(0, 0, 0), vec3.fromValues(0, -0.3, -1));
  // pivot boss and shank
  mb.cylinder(2.2, 2.2, jawLength - 1.5, jawLength + 1.5, 16);
  mb.box(vec3.fromValues(0, jawLength + shankLength * 0.45, 0), vec3.fromValues(1.7, shankLength * 0.45, 1.0));
  // ring handle in the blade plane
  const ring = new MeshBuilder();
  ring.torus(vec3.fromValues(0, jawLength + shankLength * 0.9 + 7, 0), 7, 1.3, 24, 10);
  mb.append(ring);
  return mb;
}
