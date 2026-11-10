import { vec3 } from 'gl-matrix';
import { Volume, cellType, FLAG_FIBRE_X } from '@/core/volume';
import { Tissue, TISSUE, isVessel, CHAR_TEAR_FACTOR, COAG_TEAR_FACTOR, DRY_STIFFEN, DRY_TEAR_FACTOR } from '@/core/tissueProperties';

/**
 * XPBD mechanics lattice. Nodes sit on a coarse grid in material space (the undeformed bed);
 * `pos` is the deformed world position. Rendering and picking sample the displacement field;
 * the voxel physics stays in material space.
 *
 * Each link behaves like the tissue it runs through (src/core/tissueProperties.ts):
 *  - nonlinear elasticity: compliance falls with strain beyond a per-tissue knee (collagen J-curve);
 *  - yield: beyond the tissue's yield strain the link lengthens permanently and is drawn as a strand
 *    (the stringiness of areolar tissue and fat lobules pulled apart);
 *  - tearing: a link held past its tear strain for a few hundredths of a second breaks, and the voxel
 *    tissue it ran through is carved (a tear record is handed to the GPU);
 *  - thermal state from the GPU probe: coagulated collagen shrinks (per tissue) and stiffens, desiccated
 *    and charred tissue is stiffer and brittle, rendered fat softens;
 *  - muscle is anisotropic (stiffer and tougher along the fibre direction), vessels recoil when cut.
 */
export interface Constraint {
  a: number; b: number;
  rest: number;          // current rest length (mm), after shrinkage and plastic yield
  rest0: number;         // original rest length
  compliance: number;    // base XPBD compliance (1/stiffness) of the governing tissue
  lambda: number;
  alive: boolean;
  /** 0 ordinary, 1 lipoma capsule link (host <-> cluster), 2 internal lipoma */
  kind: 0 | 1 | 2;
  /** governing tissue: the softer of the two end layers */
  tissue: Tissue;
  /** |cos| between the link and the muscle fibre direction (muscle links only) */
  along: number;
  /** permanent elongation factor from yield (>= 1) */
  plastic: number;
  /** fractional thermal contraction (0 .. coagShrink) */
  shrink: number;
  /** last probe: max Arrhenius damage, max dryness (1 - water/water0) and whether any sample is char */
  omega: number; dry: number; charred: boolean;
  /** precomputed from the thermal state: multipliers on tear strain and compliance */
  tearMul: number; stiffMul: number;
  /** seconds the link has spent above its tear strain */
  over: number;
  /** in-situ strain after the lattice settled (pre-tension and boundary effects): yield and tearing count elongation beyond it */
  base: number;
  /** 1 if the segment initially ran through tissue (its tear carves the volume) */
  through: boolean;
}

export interface Attachment { node: number; target: vec3; strength: number }

/** A link that tore this frame: material-space endpoints, carve radius and governing tissue, for the GPU tear kernel. */
export type TearRecord = [number, number, number, number, number, number, number, number];

/** seconds a link must stay above its tear strain before it breaks (filters single-frame noise) */
export const TEAR_DWELL = 0.04;
/** rate (1/s) at which a yielded link's rest length creeps toward its current length */
export const YIELD_RATE = 8;
/** cut vessel ends recoil: vessel links are pre-tensioned to this fraction of their length */
export const VESSEL_PRETENSION = 0.92;
/** strands are drawn for yielded links through low-cohesion tissue */
export const STRAND_COHESION = 0.5;
/** reference substep (60 fps, 8 substeps): the per-substep damping, anchoring and attachment rates are defined at this step and rescaled to the actual one, so the bed behaves the same at any frame rate */
const DT_REF = 1 / 480;

const COUNT = Tissue.COUNT;
const M_COMPLIANCE = new Float64Array(COUNT), M_KNEE = new Float64Array(COUNT), M_YIELD = new Float64Array(COUNT), M_TEAR = new Float64Array(COUNT);
const M_COHESION = new Float64Array(COUNT), M_SHRINK = new Float64Array(COUNT), M_COAGSTIFF = new Float64Array(COUNT);
for (let t = 0; t < COUNT; t++) {
  const p = TISSUE[t];
  M_COMPLIANCE[t] = p.compliance; M_KNEE[t] = p.kneeStrain; M_YIELD[t] = p.yieldStrain; M_TEAR[t] = p.tearStrain;
  M_COHESION[t] = p.cohesion; M_SHRINK[t] = p.coagShrink; M_COAGSTIFF[t] = p.coagStiffen;
}
const smooth01 = (x: number) => { const t = Math.min(Math.max(x, 0), 1); return t * t * (3 - 2 * t); };

/** Effective XPBD compliance of a link through `tissue` at a given elastic strain (>= 0) and thermal multiplier: the J-curve. */
export function effectiveCompliance(tissue: Tissue, strain: number, stiffMul = 1): number {
  const sn = Math.max(strain, 0) / M_KNEE[tissue];
  return M_COMPLIANCE[tissue] * stiffMul / (1 + sn * sn);
}
/** Thermal multipliers on tear strain and compliance for a link: omega (Arrhenius), dryness 0..1, char, muscle fibre alignment. */
export function thermalMultipliers(tissue: Tissue, omega: number, dry: number, charred: boolean, along = 0): { tearMul: number; stiffMul: number; shrink: number } {
  const coag = omega > 1;
  const shrink = M_SHRINK[tissue] * smooth01((omega - 0.5) / 1.5);
  let tearMul = (1 - DRY_TEAR_FACTOR * Math.min(Math.max(dry, 0), 1)) * (coag ? COAG_TEAR_FACTOR : 1) * (charred ? CHAR_TEAR_FACTOR : 1);
  let stiffMul = (1 - DRY_STIFFEN * Math.min(Math.max(dry, 0), 1)) * (coag ? 1 / M_COAGSTIFF[tissue] : 1);
  if (tissue === Tissue.Muscle) { tearMul *= 1 + 0.6 * along; stiffMul *= 1.4 - 0.8 * along; }
  return { tearMul, stiffMul, shrink };
}

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
  /** 1 when the node's muscle fibres run along x (FLAG_FIBRE_X), else along z */
  readonly fibreX: Uint8Array;
  /** 0.15 at the skin to 1 at the bottom of the bed: how firmly the node is anchored to the body */
  readonly anchor: Float32Array;
  /** 0 = host tissue, 1 = lipoma cluster */
  readonly cluster: Uint8Array;
  readonly constraints: Constraint[] = [];
  readonly attachments: Attachment[] = [];
  /** displacement = pos - mat, uploaded for rendering */
  readonly disp: Float32Array<ArrayBuffer>;
  /** links torn since the last `takeTears()` */
  readonly tears: TearRecord[] = [];
  /** cumulative counts for the HUD and events */
  tornLinks = 0; yieldedLinks = 0;
  /** > 0 while the lattice settles into its pre-tensioned rest state: no yield or tearing yet */
  private settling = 0;
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
    this.fibreX = new Uint8Array(this.count); this.anchor = new Float32Array(this.count);
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
      const c = volume.cell[volume.index(ci, cj, ck)];
      const ty = cellType(c);
      this.layer[n] = ty;
      this.fibreX[n] = (c & FLAG_FIBRE_X) !== 0 ? 1 : 0;
      this.anchor[n] = 0.15 + 0.85 * Math.min(Math.max(-y / sy, 0), 1);
      this.cluster[n] = ty === Tissue.Lipoma || ty === Tissue.LipomaCapsule ? 1 : 0;
      const edge = i === 0 || k === 0 || i === this.nx - 1 || k === this.nz - 1 || j === 0;
      // nodes whose material position lies in air (above the skin or inside a pre-made wound) are pinned
      const inAir = y > volume.surfaceY(x, z) + 0.01;
      this.invMass[n] = edge || inAir ? 0 : 1;
    }
    const ca = Math.cos(langerRad), sa = Math.sin(langerRad);
    const add = (a: number, b: number) => {
      const la = this.layer[a] as Tissue, lb = this.layer[b] as Tissue;
      // the softer layer governs the link (a link half in fat and half in dermis stretches like fat)
      const tissue = M_COMPLIANCE[la] >= M_COMPLIANCE[lb] ? la : lb;
      const dx = this.mat[b * 3] - this.mat[a * 3], dy = this.mat[b * 3 + 1] - this.mat[a * 3 + 1], dz = this.mat[b * 3 + 2] - this.mat[a * 3 + 2];
      const len = Math.hypot(dx, dy, dz);
      let rest = len;
      const skin = (la === Tissue.Epidermis || la === Tissue.Dermis) && (lb === Tissue.Epidermis || lb === Tissue.Dermis);
      if (skin && Math.abs(dy) < 1e-3) {
        // horizontal skin link: pre-tension along Langer's lines
        const along = Math.abs((dx * ca + dz * sa) / rest);
        rest *= preTension.along * along + preTension.across * (1 - along);
      }
      // vessels are under tension in situ: a cut end recoils into the fat
      if (isVessel(la) || isVessel(lb)) rest *= VESSEL_PRETENSION;
      const ka = this.cluster[a], kb = this.cluster[b];
      const kind: 0 | 1 | 2 = ka !== kb ? 1 : ka === 1 ? 2 : 0;
      // a link that already runs through air (pre-made wound) does not exist; links fully above the skin neither
      let alive = true;
      for (let t = 1; t <= 3; t++) {
        const f = t / 4, px = this.mat[a * 3] + dx * f, py = this.mat[a * 3 + 1] + dy * f, pz = this.mat[a * 3 + 2] + dz * f;
        if (py > volume.surfaceY(px, pz) + 0.01) { alive = false; break; }
      }
      const my = (this.mat[a * 3 + 1] + this.mat[b * 3 + 1]) / 2;
      const through = my < volume.surfaceY((this.mat[a * 3] + this.mat[b * 3]) / 2, (this.mat[a * 3 + 2] + this.mat[b * 3 + 2]) / 2) - 0.1;
      // muscle anisotropy: alignment with the fibre direction of the muscle end
      let along = 0;
      if (tissue === Tissue.Muscle) {
        const fx = this.layer[a] === Tissue.Muscle ? this.fibreX[a] : this.fibreX[b];
        along = len > 1e-6 ? Math.abs(fx ? dx : dz) / len : 0;
      }
      if (kind === 1 && alive) this.capsuleTotal++;
      const c: Constraint = { a, b, rest, rest0: rest, compliance: kind === 2 ? 2e-6 : M_COMPLIANCE[tissue], lambda: 0, alive, kind, tissue, along,
        plastic: 1, shrink: 0, omega: 0, dry: 0, charred: false, tearMul: 1, stiffMul: 1, over: 0, base: 0, through };
      this.updateThermal(c);
      this.constraints.push(c);
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
    // settle into the pre-tensioned rest state before any yield or tearing can happen
    this.settling = 40;
    for (let s = 0; s < 40; s++) this.step(1 / 60, 0);
    // the settled state is the in-situ reference: a bed lying there is not injured
    for (const c of this.constraints) {
      if (!c.alive) continue;
      const oa = c.a * 3, ob = c.b * 3;
      const len = Math.hypot(this.pos[ob] - this.pos[oa], this.pos[ob + 1] - this.pos[oa + 1], this.pos[ob + 2] - this.pos[oa + 2]);
      c.base = Math.max(0, len / c.rest0 - 1);
    }
  }

  index(i: number, j: number, k: number) { return i + this.nx * (j + this.ny * k); }

  /** Thermal-state multipliers and rest length of a link from its probe values (omega, dry, charred) and plastic yield. */
  private updateThermal(c: Constraint) {
    // collagen contraction with Arrhenius damage, per tissue (dermis and fascia 15-18 %, vessels 25 %, fat ~4 %);
    // dried and charred tissue stiff and brittle; coagulated collagen firmer, rendered fat softer
    const m = thermalMultipliers(c.tissue, c.omega, c.dry, c.charred, c.along);
    c.shrink = m.shrink; c.tearMul = m.tearMul; c.stiffMul = m.stiffMul;
    c.rest = c.rest0 * (1 - c.shrink) * c.plastic;
  }

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

  /** all free nodes (by deformed position) within radius */
  nodesWithin(pw: vec3, radius: number): number[] {
    const out: number[] = []; const r2 = radius * radius;
    for (let n = 0; n < this.count; n++) {
      if (this.invMass[n] === 0) continue;
      const dx = this.pos[n * 3] - pw[0], dy = this.pos[n * 3 + 1] - pw[1], dz = this.pos[n * 3 + 2] - pw[2];
      if (dx * dx + dy * dy + dz * dz < r2) out.push(n);
    }
    return out;
  }

  /**
   * Sever / shrink / stiffen constraints from the GPU probe results. Per constraint: flags (bit 0 severed by
   * cutting, bit 2 some sample is char), max omega, and optionally the max dryness (1 - water / water0).
   */
  applyProbe(flags: Uint8Array, omegaMax: Float32Array, dry?: Float32Array) {
    const cs = this.constraints;
    for (let c = 0; c < cs.length; c++) {
      const k = cs[c];
      if (!k.alive) continue;
      if (flags[c] & 1) { this.sever(k, false); continue; }
      const om = omegaMax[c];
      const dr = dry ? Math.min(Math.max(dry[c], 0), 1) : 0;
      const ch = (flags[c] & 4) !== 0;
      if (om !== k.omega || dr !== k.dry || ch !== k.charred) { k.omega = om; k.dry = dr; k.charred = ch; this.updateThermal(k); }
    }
    // lateral capsule freed (the base pedicle may remain): deliverable with traction once ~40 % of links are cut
    if (!this.lipomaFree && this.capsuleTotal > 0 && this.capsuleAlive < 0.62 * this.capsuleTotal) this.lipomaFree = true;
  }

  private sever(c: Constraint, mechanical: boolean) {
    c.alive = false;
    if (c.kind === 1) this.capsuleAlive--;
    if (mechanical) {
      this.tornLinks++;
      if (c.through) {
        const a = c.a * 3, b = c.b * 3;
        this.tears.push([this.mat[a], this.mat[a + 1], this.mat[a + 2], this.spacing * 0.3, this.mat[b], this.mat[b + 1], this.mat[b + 2], c.tissue]);
      }
    }
  }

  /** Tear records since the last call (material mm): [ax, ay, az, radius, bx, by, bz, tissue]. */
  takeTears(): TearRecord[] { const t = this.tears.splice(0, this.tears.length); return t; }

  /** Muscle twitch impulse near a world point: damped 6 Hz oscillation, amplitude in mm. */
  stimulate(centre: vec3, amplitudeMm: number, now: number) {
    vec3.copy(this.twitch.centre, centre); this.twitch.amp = amplitudeMm; this.twitch.phase = now; this.twitch.until = now + 0.5;
  }

  step(dtSeconds: number, now: number) {
    const dt = Math.min(dtSeconds, 0.05) / this.substeps;
    const cs = this.constraints;
    const twitchActive = now < this.twitch.until;
    const yieldK = Math.min(1, YIELD_RATE * dt);
    const rate = dt / DT_REF;
    const damp = Math.pow(1 - this.damping, rate);
    const blend = (k: number) => 1 - Math.pow(1 - Math.min(k, 0.999), rate);
    for (let s = 0; s < this.substeps; s++) {
      // XPBD: the Lagrange multipliers belong to one time step; each substep is a new step (carrying them over makes
      // stiff links, fascia and the J-curve, pump energy into the bed until it tears itself apart at rest)
      for (const c of cs) c.lambda = 0;
      // integrate
      for (let n = 0; n < this.count; n++) {
        if (this.invMass[n] === 0) continue;
        const o = n * 3;
        this.prev[o] = this.pos[o]; this.prev[o + 1] = this.pos[o + 1]; this.prev[o + 2] = this.pos[o + 2];
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
        // spring back toward the material position: the bed is anchored to the body below it, so deep tissue is held
        // firmly and the superficial layers can be tented up by a hook or forceps
        const kb = this.cluster[n] === 1 && this.lipomaFree ? 0 : blend(0.004 * this.anchor[n]);
        this.pos[o] += (this.mat[o] - this.pos[o]) * kb; this.pos[o + 1] += (this.mat[o + 1] - this.pos[o + 1]) * kb; this.pos[o + 2] += (this.mat[o + 2] - this.pos[o + 2]) * kb;
      }
      // attachments (hooks, forceps, hemostat tips, gauze pressure)
      for (const at of this.attachments) {
        const o = at.node * 3, k = blend(at.strength);
        this.pos[o] += (at.target[0] - this.pos[o]) * k; this.pos[o + 1] += (at.target[1] - this.pos[o + 1]) * k; this.pos[o + 2] += (at.target[2] - this.pos[o + 2]) * k;
      }
      // constraints (XPBD with tissue-dependent nonlinear compliance, yield and tearing)
      const dt2 = dt * dt;
      for (const c of cs) {
        if (!c.alive) continue;
        const oa = c.a * 3, ob = c.b * 3;
        const wa = this.invMass[c.a], wb = this.invMass[c.b];
        if (wa + wb === 0) continue;
        const dx = this.pos[ob] - this.pos[oa], dy = this.pos[ob + 1] - this.pos[oa + 1], dz = this.pos[ob + 2] - this.pos[oa + 2];
        const len = Math.hypot(dx, dy, dz); if (len < 1e-6) continue;
        const t = c.tissue;
        // elongation beyond the in-situ length (natural length plus the settled pre-tension): the elastic part is
        // measured from the yielded length (J-curve, yield), the total from the original length (a strand that has
        // been drawn out far enough snaps however much it has yielded)
        const strain = len / (c.rest0 * c.plastic) - 1 - c.base;
        const total = len / c.rest0 - 1 - c.base;
        // boundary links (one end pinned at the bed edge) stand for tissue that continues beyond the bed: they
        // neither yield nor tear
        if (total > 0 && this.settling === 0 && wa + wb > 1.5) {
          // tearing: sustained elongation beyond the (thermally modified) tear strain breaks the link
          if (total > M_TEAR[t] * c.tearMul) {
            c.over += dt;
            if (c.over > TEAR_DWELL) { this.sever(c, true); continue; }
          } else if (c.over > 0) c.over = Math.max(0, c.over - dt);
          // yield: the strand lengthens permanently so that its elastic strain relaxes toward the yield strain
          const ys = M_YIELD[t];
          if (strain > ys && ys < 5) {
            const target = len / (1 + ys + c.base) / c.rest0;
            if (target > c.plastic) {
              const before = c.plastic;
              c.plastic += (target - c.plastic) * yieldK;
              if (before <= 1.02 && c.plastic > 1.02) this.yieldedLinks++;
              c.rest = c.rest0 * (1 - c.shrink) * c.plastic;
            }
          }
        }
        // strain stiffening (J-curve) and thermal state
        const sn = Math.max(strain, 0) / M_KNEE[t];
        const comp = c.compliance * c.stiffMul / (1 + sn * sn);
        const C = len - c.rest;
        const alpha = comp / dt2;
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
    if (this.settling > 0) this.settling--;
  }

  /** True when the link is drawn as a strand: yielded, still attached, through low-cohesion tissue and under tension. */
  isStrand(c: Constraint): boolean {
    if (!c.alive || c.plastic <= 1.02 || M_COHESION[c.tissue] > STRAND_COHESION) return false;
    const oa = c.a * 3, ob = c.b * 3;
    const len = Math.hypot(this.pos[ob] - this.pos[oa], this.pos[ob + 1] - this.pos[oa + 1], this.pos[ob + 2] - this.pos[oa + 2]);
    return len > c.rest0 * (1 + c.base + 0.5 * M_YIELD[c.tissue]);
  }

  /**
   * Strand segments for rendering: per strand [ax, ay, az, w, bx, by, bz, w] in deformed world mm, where w is the
   * strand's thinness (0 fresh, 1 about to snap). Writes into `out` (8 floats per strand) and returns the count.
   */
  strandData(out: Float32Array): number {
    let n = 0;
    const cap = Math.floor(out.length / 8);
    for (const c of this.constraints) {
      if (n >= cap) break;
      if (!this.isStrand(c)) continue;
      const oa = c.a * 3, ob = c.b * 3;
      const len = Math.hypot(this.pos[ob] - this.pos[oa], this.pos[ob + 1] - this.pos[oa + 1], this.pos[ob + 2] - this.pos[oa + 2]);
      const w = Math.min(Math.max((len / c.rest0 - 1 - c.base) / (M_TEAR[c.tissue] * c.tearMul), 0), 1);
      out.set([this.pos[oa], this.pos[oa + 1], this.pos[oa + 2], w, this.pos[ob], this.pos[ob + 1], this.pos[ob + 2], w], n * 8);
      n++;
    }
    return n;
  }

  /** Constraint segments for the GPU probe: [ax, ay, az, throughTissue, bx, by, bz, 0] per constraint (material mm). */
  segmentData(_volume: Volume): Float32Array<ArrayBuffer> {
    const out = new Float32Array(new ArrayBuffer(this.constraints.length * 32));
    this.constraints.forEach((c, n) => {
      const a = c.a * 3, b = c.b * 3;
      out.set([this.mat[a], this.mat[a + 1], this.mat[a + 2], c.through ? 1 : 0, this.mat[b], this.mat[b + 1], this.mat[b + 2], 0], n * 8);
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
