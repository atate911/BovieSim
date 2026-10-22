import { vec3 } from 'gl-matrix';
import { Volume, cellType } from '@/core/volume';
import { Tissue } from '@/core/tissueProperties';

/**
 * XPBD mechanics lattice. Nodes sit on a coarse grid in material space (the undeformed bed);
 * `pos` is the deformed world position. Rendering and picking sample the displacement field;
 * the voxel physics stays in material space.
 */
export interface Constraint {
  a: number; b: number;
  rest: number;          // current rest length (mm), after shrinkage
  rest0: number;         // original rest length
  compliance: number;    // XPBD compliance (1/stiffness), mm/N-ish units
  lambda: number;
  alive: boolean;
  /** 0 ordinary, 1 lipoma capsule link (host <-> cluster), 2 internal lipoma */
  kind: 0 | 1 | 2;
}

export interface Attachment { node: number; target: vec3; strength: number }

/** per-layer compliance: lower = stiffer */
const COMPLIANCE: Partial<Record<number, number>> = {
  [Tissue.Epidermis]: 1.0e-6, [Tissue.Dermis]: 2.0e-6, [Tissue.Fat]: 4.0e-5, [Tissue.Septum]: 5.0e-6,
  [Tissue.SuperficialFascia]: 8.0e-7, [Tissue.DeepFascia]: 5.0e-7, [Tissue.Muscle]: 1.0e-5,
  [Tissue.Artery]: 5.0e-6, [Tissue.Vein]: 8.0e-6, [Tissue.Nerve]: 3.0e-6, [Tissue.Lipoma]: 3.0e-5, [Tissue.LipomaCapsule]: 4.0e-6,
};

export class Lattice {
  readonly nx: number; readonly ny: number; readonly nz: number;
  readonly spacing: number;
  readonly count: number;
  /** material positions (mm, world axes: x, y up, z) */
  readonly mat: Float32Array;
  /** deformed positions */
  readonly pos: Float32Array;
  readonly prev: Float32Array;
  readonly vel: Float32Array;
  readonly invMass: Float32Array;
  readonly layer: Uint8Array;
  /** 0 = host tissue, 1 = lipoma cluster */
  readonly cluster: Uint8Array;
  readonly constraints: Constraint[] = [];
  readonly attachments: Attachment[] = [];
  /** displacement = pos - mat, uploaded for rendering */
  readonly disp: Float32Array<ArrayBuffer>;
  substeps = 8;
  damping = 0.12;
  gravityOnFree = 0.004; // mm/ms^2 scaled: freed lipoma sags gently
  lipomaFree = false;
  capsuleTotal = 0; capsuleAlive = 0;
  private origin: vec3;
  private twitch = { amp: 0, phase: 0, centre: vec3.create(), until: -1 };

  constructor(volume: Volume, spacingMm = 3, langerRad = 0, preTension = { along: 0.96, across: 0.985 }) {
    const [sx, sy, sz] = volume.sizeMm; // x, depth, z
    this.spacing = spacingMm;
    this.nx = Math.round(sx / spacingMm) + 1; this.nz = Math.round(sz / spacingMm) + 1; this.ny = Math.round(sy / spacingMm) + 1;
    this.count = this.nx * this.ny * this.nz;
    this.origin = vec3.fromValues(-sx / 2, -sy, -sz / 2);
    this.mat = new Float32Array(this.count * 3); this.pos = new Float32Array(this.count * 3); this.prev = new Float32Array(this.count * 3);
    this.vel = new Float32Array(this.count * 3); this.invMass = new Float32Array(this.count); this.layer = new Uint8Array(this.count); this.cluster = new Uint8Array(this.count);
    this.disp = new Float32Array(new ArrayBuffer(this.count * 16));
    for (let k = 0; k < this.nz; k++) for (let j = 0; j < this.ny; j++) for (let i = 0; i < this.nx; i++) {
      const n = this.index(i, j, k);
      const x = this.origin[0] + i * spacingMm, y = this.origin[1] + j * spacingMm, z = this.origin[2] + k * spacingMm;
      this.mat.set([x, y, z], n * 3); this.pos.set([x, y, z], n * 3); this.prev.set([x, y, z], n * 3);
      // sample the tissue type at (or just below) the node; air above the surface -> skin node pinned to the surface layer
      const [vi, vj, vk] = volume.worldToVoxel(x, Math.min(y, -0.2), z);
      const ci = Math.min(Math.max(Math.round(vi), 0), volume.dims.nx - 1), cj = Math.min(Math.max(Math.round(vj), 0), volume.dims.ny - 1);
      let ck = Math.min(Math.max(Math.round(vk), 0), volume.dims.nz - 1);
      while (ck > 0 && volume.occupancy[volume.index(ci, cj, ck)] < 0.5) ck--;
      const ty = cellType(volume.cell[volume.index(ci, cj, ck)]);
      this.layer[n] = ty;
      this.cluster[n] = ty === Tissue.Lipoma || ty === Tissue.LipomaCapsule ? 1 : 0;
      const edge = i === 0 || k === 0 || i === this.nx - 1 || k === this.nz - 1 || j === 0;
      this.invMass[n] = edge ? 0 : 1;
    }
    const ca = Math.cos(langerRad), sa = Math.sin(langerRad);
    const add = (a: number, b: number) => {
      const la = this.layer[a], lb = this.layer[b];
      const comp = Math.max(COMPLIANCE[la] ?? 1e-5, COMPLIANCE[lb] ?? 1e-5);
      const dx = this.mat[b * 3] - this.mat[a * 3], dy = this.mat[b * 3 + 1] - this.mat[a * 3 + 1], dz = this.mat[b * 3 + 2] - this.mat[a * 3 + 2];
      let rest = Math.hypot(dx, dy, dz);
      const skin = (la === Tissue.Epidermis || la === Tissue.Dermis) && (lb === Tissue.Epidermis || lb === Tissue.Dermis);
      if (skin && Math.abs(dy) < 1e-3) {
        // horizontal skin link: pre-tension along Langer's lines
        const along = Math.abs((dx * ca + dz * sa) / rest);
        rest *= preTension.along * along + preTension.across * (1 - along);
      }
      const ka = this.cluster[a], kb = this.cluster[b];
      const kind: 0 | 1 | 2 = ka !== kb ? 1 : ka === 1 ? 2 : 0;
      if (kind === 1) this.capsuleTotal++;
      this.constraints.push({ a, b, rest, rest0: rest, compliance: kind === 2 ? 2e-6 : comp, lambda: 0, alive: true, kind });
    };
    for (let k = 0; k < this.nz; k++) for (let j = 0; j < this.ny; j++) for (let i = 0; i < this.nx; i++) {
      const n = this.index(i, j, k);
      if (i + 1 < this.nx) add(n, this.index(i + 1, j, k));
      if (j + 1 < this.ny) add(n, this.index(i, j + 1, k));
      if (k + 1 < this.nz) add(n, this.index(i, j, k + 1));
      // face diagonals for shear
      if (i + 1 < this.nx && j + 1 < this.ny) { add(n, this.index(i + 1, j + 1, k)); add(this.index(i + 1, j, k), this.index(i, j + 1, k)); }
      if (i + 1 < this.nx && k + 1 < this.nz) { add(n, this.index(i + 1, j, k + 1)); add(this.index(i + 1, j, k), this.index(i, j, k + 1)); }
      if (j + 1 < this.ny && k + 1 < this.nz) { add(n, this.index(i, j + 1, k + 1)); add(this.index(i, j + 1, k), this.index(i, j, k + 1)); }
    }
    this.capsuleAlive = this.capsuleTotal;
  }

  index(i: number, j: number, k: number) { return i + this.nx * (j + this.ny * k); }

  /** material position -> fractional lattice coords */
  private cellCoords(p: vec3): [number, number, number] {
    return [(p[0] - this.origin[0]) / this.spacing, (p[1] - this.origin[1]) / this.spacing, (p[2] - this.origin[2]) / this.spacing];
  }

  /** Trilinear displacement at a material point. */
  displacementAt(p: vec3, out = vec3.create()): vec3 {
    const [fx, fy, fz] = this.cellCoords(p);
    const i0 = Math.min(Math.max(Math.floor(fx), 0), this.nx - 2), j0 = Math.min(Math.max(Math.floor(fy), 0), this.ny - 2), k0 = Math.min(Math.max(Math.floor(fz), 0), this.nz - 2);
    const tx = Math.min(Math.max(fx - i0, 0), 1), ty = Math.min(Math.max(fy - j0, 0), 1), tz = Math.min(Math.max(fz - k0, 0), 1);
    vec3.set(out, 0, 0, 0);
    for (let dk = 0; dk < 2; dk++) for (let dj = 0; dj < 2; dj++) for (let di = 0; di < 2; di++) {
      const n = this.index(i0 + di, j0 + dj, k0 + dk);
      const w = (di ? tx : 1 - tx) * (dj ? ty : 1 - ty) * (dk ? tz : 1 - tz);
      out[0] += w * (this.pos[n * 3] - this.mat[n * 3]); out[1] += w * (this.pos[n * 3 + 1] - this.mat[n * 3 + 1]); out[2] += w * (this.pos[n * 3 + 2] - this.mat[n * 3 + 2]);
    }
    return out;
  }

  /** world -> material by fixed-point inversion of p_mat = p_world - disp(p_mat). */
  worldToMaterial(pw: vec3, out = vec3.create()): vec3 {
    vec3.copy(out, pw);
    const d = vec3.create();
    for (let it = 0; it < 3; it++) { this.displacementAt(out, d); vec3.sub(out, pw, d); }
    return out;
  }

  /** nearest node (by deformed position) within radius, or -1 */
  nearestNode(pw: vec3, radius = 4): number {
    let best = -1, bd = radius * radius;
    for (let n = 0; n < this.count; n++) {
      if (this.invMass[n] === 0) continue;
      const dx = this.pos[n * 3] - pw[0], dy = this.pos[n * 3 + 1] - pw[1], dz = this.pos[n * 3 + 2] - pw[2];
      const d2 = dx * dx + dy * dy + dz * dz;
      if (d2 < bd) { bd = d2; best = n; }
    }
    return best;
  }

  /** Sever / shrink constraints from the GPU probe results (per constraint: severed flag, max omega). */
  applyProbe(severed: Uint8Array, omegaMax: Float32Array) {
    const cs = this.constraints;
    for (let c = 0; c < cs.length; c++) {
      const k = cs[c];
      if (!k.alive) continue;
      if (severed[c]) { k.alive = false; if (k.kind === 1) this.capsuleAlive--; continue; }
      const om = omegaMax[c];
      if (om > 0.5) {
        const sh = Math.min(Math.max((om - 0.5) / 1.5, 0), 1);
        k.rest = k.rest0 * (1 - 0.12 * sh * sh * (3 - 2 * sh));
      }
    }
    if (!this.lipomaFree && this.capsuleTotal > 0 && this.capsuleAlive < 0.5 * this.capsuleTotal) this.lipomaFree = true;
  }

  /** Muscle twitch impulse near a world point: damped 6 Hz oscillation, amplitude in mm. */
  stimulate(centre: vec3, amplitudeMm: number, now: number) {
    vec3.copy(this.twitch.centre, centre); this.twitch.amp = amplitudeMm; this.twitch.phase = now; this.twitch.until = now + 0.5;
  }

  step(dtSeconds: number, now: number) {
    const dt = Math.min(dtSeconds, 0.05) / this.substeps;
    const cs = this.constraints;
    for (const c of cs) c.lambda = 0;
    const twitchActive = now < this.twitch.until;
    for (let s = 0; s < this.substeps; s++) {
      // integrate
      for (let n = 0; n < this.count; n++) {
        if (this.invMass[n] === 0) continue;
        const o = n * 3;
        this.prev[o] = this.pos[o]; this.prev[o + 1] = this.pos[o + 1]; this.prev[o + 2] = this.pos[o + 2];
        const damp = 1 - this.damping;
        let gy = 0;
        if (this.cluster[n] === 1 && this.lipomaFree) gy = -this.gravityOnFree * 1000;
        if (twitchActive && this.layer[n] === Tissue.Muscle) {
          const dx = this.pos[o] - this.twitch.centre[0], dz = this.pos[o + 2] - this.twitch.centre[2];
          const r2 = dx * dx + dz * dz;
          if (r2 < 144) {
            const tau = now - this.twitch.phase;
            gy += this.twitch.amp * Math.exp(-tau * 6) * Math.cos(tau * 2 * Math.PI * 6) * 4000 * (1 - r2 / 144);
          }
        }
        this.vel[o] *= damp; this.vel[o + 1] = this.vel[o + 1] * damp + gy * dt; this.vel[o + 2] *= damp;
        this.pos[o] += this.vel[o] * dt; this.pos[o + 1] += this.vel[o + 1] * dt; this.pos[o + 2] += this.vel[o + 2] * dt;
        // spring back toward the material position (tissue is anchored to the body below the bed)
        const kb = this.cluster[n] === 1 && this.lipomaFree ? 0 : 0.004;
        this.pos[o] += (this.mat[o] - this.pos[o]) * kb; this.pos[o + 1] += (this.mat[o + 1] - this.pos[o + 1]) * kb; this.pos[o + 2] += (this.mat[o + 2] - this.pos[o + 2]) * kb;
      }
      // attachments (hooks, forceps)
      for (const at of this.attachments) {
        const o = at.node * 3;
        this.pos[o] += (at.target[0] - this.pos[o]) * at.strength; this.pos[o + 1] += (at.target[1] - this.pos[o + 1]) * at.strength; this.pos[o + 2] += (at.target[2] - this.pos[o + 2]) * at.strength;
      }
      // constraints (XPBD)
      const dt2 = dt * dt;
      for (const c of cs) {
        if (!c.alive) continue;
        const oa = c.a * 3, ob = c.b * 3;
        const wa = this.invMass[c.a], wb = this.invMass[c.b];
        if (wa + wb === 0) continue;
        const dx = this.pos[ob] - this.pos[oa], dy = this.pos[ob + 1] - this.pos[oa + 1], dz = this.pos[ob + 2] - this.pos[oa + 2];
        const len = Math.hypot(dx, dy, dz); if (len < 1e-6) continue;
        const C = len - c.rest;
        const alpha = c.compliance / dt2;
        const dl = (-C - alpha * c.lambda) / (wa + wb + alpha);
        c.lambda += dl;
        const sx = dx / len * dl, sy = dy / len * dl, sz = dz / len * dl;
        this.pos[oa] -= wa * sx; this.pos[oa + 1] -= wa * sy; this.pos[oa + 2] -= wa * sz;
        this.pos[ob] += wb * sx; this.pos[ob + 1] += wb * sy; this.pos[ob + 2] += wb * sz;
      }
      // velocities
      for (let n = 0; n < this.count; n++) {
        if (this.invMass[n] === 0) continue;
        const o = n * 3;
        this.vel[o] = (this.pos[o] - this.prev[o]) / dt; this.vel[o + 1] = (this.pos[o + 1] - this.prev[o + 1]) / dt; this.vel[o + 2] = (this.pos[o + 2] - this.prev[o + 2]) / dt;
      }
    }
    for (let n = 0; n < this.count; n++) {
      const o = n * 3;
      this.disp[n * 4] = this.pos[o] - this.mat[o]; this.disp[n * 4 + 1] = this.pos[o + 1] - this.mat[o + 1]; this.disp[n * 4 + 2] = this.pos[o + 2] - this.mat[o + 2]; this.disp[n * 4 + 3] = 0;
    }
  }

  /** Constraint segments for the GPU probe: [ax, ay, az, throughTissue, bx, by, bz, 0] per constraint (material mm). */
  segmentData(volume: Volume): Float32Array<ArrayBuffer> {
    const out = new Float32Array(new ArrayBuffer(this.constraints.length * 32));
    this.constraints.forEach((c, n) => {
      const a = c.a * 3, b = c.b * 3;
      // through tissue if the midpoint is below the surface
      const mx = (this.mat[a] + this.mat[b]) / 2, my = (this.mat[a + 1] + this.mat[b + 1]) / 2, mz = (this.mat[a + 2] + this.mat[b + 2]) / 2;
      const through = my < volume.surfaceY(mx, mz) - 0.1 ? 1 : 0;
      out.set([this.mat[a], this.mat[a + 1], this.mat[a + 2], through, this.mat[b], this.mat[b + 1], this.mat[b + 2], 0], n * 8);
    });
    return out;
  }

  /** LatticeInfo uniform: nx, ny, nz, 0, origin.xyz, spacing */
  infoData(): ArrayBuffer {
    const b = new ArrayBuffer(32);
    new Uint32Array(b, 0, 4).set([this.nx, this.ny, this.nz, 0]);
    new Float32Array(b, 16, 4).set([this.origin[0], this.origin[1], this.origin[2], this.spacing]);
    return b;
  }

  /** Gap (mm) between the two nodes nearest two world points: used by tests and the gaping metric. */
  distanceBetween(a: vec3, b: vec3): number {
    const na = this.nearestNode(a, 10), nb = this.nearestNode(b, 10);
    if (na < 0 || nb < 0) return 0;
    return Math.hypot(this.pos[na * 3] - this.pos[nb * 3], this.pos[na * 3 + 1] - this.pos[nb * 3 + 1], this.pos[na * 3 + 2] - this.pos[nb * 3 + 2]);
  }
}
