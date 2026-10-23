import { vec3 } from 'gl-matrix';
import simWgsl from './wgsl/sim.wgsl?raw';
import { Volume, VOLUME_INFO_WGSL } from '@/core/volume';
import { createShader } from '@/render/gpu';
import { packTissueProps, TISSUE, Tissue, diffusivity } from '@/core/tissueProperties';
import { ElectrodeTool } from '@/tools/electrode';
import { FT10 } from '@/core/generator/ft10';

export interface SimReadout {
  zOhm: number; powerW: number; vRms: number; iRms: number;
  hit: vec3 | null; hitCell: number; hitIdx: number;
  maxTempC: number; carbonVoxels: number; newCoagVoxels: number; vapVoxels: number; contactVoxels: number;
  muscleHeatVoxels: number; bloodLostMl: number; exposedArterial: number; exposedVenous: number;
  sealedVoxels: number; charPlugVoxels: number; rebleeds: number; poolMl: number; poolAtTipMm: number;
  /** exposed bleeding voxels this frame: [x, y, z, mlPerS] (negative flow = venous) */
  bleedPoints: number[][];
}

const SIM_BYTES = 21 * 16;
/** section image size; shown at 480 x 300 CSS px, so lines and layer edges stay sharp on high-DPI screens */
export const SLICE_W = 640, SLICE_H = 400;
const STATE_BYTES = 256;
const DROPS_BYTES = 16 + 16 * 16;
export const MAX_DROPS = 16;

export type SecondaryToolMode = 0 | 1 | 2; // none, forceps grasp, suction

/**
 * Orchestrates the electrothermal kernels each frame:
 *   pick -> (active: jacobi x N, heating, generator) -> bioheat x substeps (thermal box)
 * Temperature and potential are ping-ponged; substep counts are kept even so `temp` stays current.
 */
export class PhysicsPipeline {
  readonly simBuf: GPUBuffer;
  readonly stateBuf: GPUBuffer;
  private propsBuf: GPUBuffer;
  private readback: GPUBuffer;
  private pipes: Record<'seed' | 'red' | 'black' | 'heating' | 'generator' | 'bioheat' | 'pick' | 'slice' | 'surfaceHeight' | 'bloodSource' | 'bloodDrops' | 'bloodFlow' | 'bloodCommit' | 'coaptMark' | 'constraintProbe', GPUComputePipeline>;
  private probe: { segs: GPUBuffer; probes: GPUBuffer; readback: GPUBuffer; bg: GPUBindGroup; n: number; severed: Uint8Array; omega: Float32Array; fresh: boolean } | null = null;
  private layoutProbe: GPUPipelineLayout;
  private l2p!: GPUBindGroupLayout;
  readonly bloodBuf: GPUBuffer;
  private dropsBuf: GPUBuffer;
  private g2b!: GPUBindGroup;
  /** wound box: union of every subdomain visited (bleeding and blood flow are scanned here) */
  private wbMin = [1e9, 1e9, 1e9]; private wbMax = [-1, -1, -1];
  /** secondary instrument (forceps jaws a->b, or suction tip a) */
  private tool2 = { a: vec3.create(), b: vec3.create(), radius: 0.6, mode: 0 as SecondaryToolMode, lastMode: 0 as SecondaryToolMode };
  /** droplet landings queued for this frame: [x, y, z, microlitres] */
  readonly drops: number[][] = [];
  readonly sliceBuf: GPUBuffer;
  private sliceReadback: GPUBuffer;
  private g2: GPUBindGroup;
  private g0s!: GPUBindGroup;
  /** slice plane: origin (top centre, mm), horizontal axis, width mm, depth mm, mode; peelY = depth-peel plane (world y, mm; 1e9 none) */
  slice = { o: vec3.create(), u: vec3.fromValues(1, 0, 0), widthMm: 16, depthMm: 10, mode: 0, enabled: true, peelY: 1e9 };
  readonly slicePixels = new Uint8ClampedArray(SLICE_W * SLICE_H * 4);
  private g0: GPUBindGroup;
  private gPot: [GPUBindGroup, GPUBindGroup];
  private gTemp: [GPUBindGroup, GPUBindGroup];
  readonly readout: SimReadout = { zOhm: Infinity, powerW: 0, vRms: 0, iRms: 0, hit: null, hitCell: 0, hitIdx: 0, maxTempC: 37, carbonVoxels: 0, newCoagVoxels: 0, vapVoxels: 0, contactVoxels: 0,
    muscleHeatVoxels: 0, bloodLostMl: 0, exposedArterial: 0, exposedVenous: 0, sealedVoxels: 0, charPlugVoxels: 0, rebleeds: 0, poolMl: 0, poolAtTipMm: 0, bleedPoints: [] };
  /** thermal box in voxel coords (min inclusive, max exclusive); empty when min > max */
  private tbMin = [1e9, 1e9, 1e9]; private tbMax = [-1, -1, -1];
  private hotUntil = 0;
  /** red-black SOR sweeps per frame (each = red + black half-sweep) */
  relaxSweeps = 40;
  subdomain: number;
  readonly dtMax: number;
  simTime = 0;
  /** accumulated stats across the session */
  totals = { carbonVoxels: 0, vapVoxels: 0, activeSeconds: 0 };
  sigmaScale = 1.0;
  private ray = { o: vec3.create(), d: vec3.fromValues(0, -1, 0), max: 600 };

  constructor(private device: GPUDevice, private volume: Volume) {
    const gpu = volume.gpu!;
    const h = volume.dims.h;
    this.subdomain = Math.max(24, Math.min(64, Math.round(14 / h)));
    // explicit stability for the fastest-diffusing tissue, with margin, and a 4 ms cap for the heating-rate limiter
    let aMax = 0;
    for (let t = 1; t < Tissue.COUNT; t++) aMax = Math.max(aMax, diffusivity(TISSUE[t]));
    this.dtMax = Math.min(0.8 * (h * 1e-3) ** 2 / (6 * aMax), 0.004);

    this.simBuf = device.createBuffer({ label: 'simParams', size: SIM_BYTES, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    this.stateBuf = device.createBuffer({ label: 'simState', size: STATE_BYTES, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | GPUBufferUsage.COPY_SRC });
    this.readback = device.createBuffer({ label: 'simStateReadback', size: STATE_BYTES, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    const props = packTissueProps();
    this.propsBuf = device.createBuffer({ label: 'tissueProps', size: props.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(this.propsBuf, 0, props);

    const module = createShader(device, 'sim', VOLUME_INFO_WGSL + simWgsl);
    const l0 = device.createBindGroupLayout({ label: 'sim0', entries: [
      { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      ...[3, 4, 5, 6, 7, 8].map((b) => ({ binding: b, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' as GPUBufferBindingType } })),
    ] });
    const l1 = device.createBindGroupLayout({ label: 'sim1', entries: [
      { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } },
      { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
    ] });
    const l2 = device.createBindGroupLayout({ label: 'sim2', entries: [{ binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } }] });
    const l2b = device.createBindGroupLayout({ label: 'sim2blood', entries: [
      { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
      { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } }] });
    const { nx, ny } = volume.dims;
    this.bloodBuf = device.createBuffer({ label: 'blood', size: nx * ny * 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    this.dropsBuf = device.createBuffer({ label: 'drops', size: DROPS_BYTES, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    // the slice kernel does not use `heat`; a reduced group-0 layout keeps the slice pipeline within 8 storage buffers
    const l0s = device.createBindGroupLayout({ label: 'sim0slice', entries: [
      { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
      ...[3, 4, 5, 6, 7].map((b) => ({ binding: b, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' as GPUBufferBindingType } })),
    ] });
    const layout = device.createPipelineLayout({ label: 'simLayout', bindGroupLayouts: [l0, l1] });
    const layoutSlice = device.createPipelineLayout({ label: 'simSliceLayout', bindGroupLayouts: [l0s, l1, l2] });
    const layoutBlood = device.createPipelineLayout({ label: 'simBloodLayout', bindGroupLayouts: [l0s, l1, l2b] });
    // the probe binds its segment/result buffers in group 1 (same slot types as the ping-pong pair)
    this.layoutProbe = device.createPipelineLayout({ label: 'simProbeLayout', bindGroupLayouts: [l0s, l1] });
    this.l2p = l1;
    const mk = (entryPoint: string, lay = layout) => device.createComputePipeline({ label: entryPoint, layout: lay, compute: { module, entryPoint } });
    this.pipes = { seed: mk('seedPotential'), red: mk('relaxRed'), black: mk('relaxBlack'), heating: mk('heating'), generator: mk('generator'), bioheat: mk('bioheat'), pick: mk('pick'), slice: mk('slice', layoutSlice),
      surfaceHeight: mk('surfaceHeight', layoutBlood), bloodSource: mk('bloodSource', layoutBlood), bloodDrops: mk('bloodDrops', layoutBlood), bloodFlow: mk('bloodFlow', layoutBlood), bloodCommit: mk('bloodCommit', layoutBlood), coaptMark: mk('coaptMark'), constraintProbe: mk('constraintProbe', this.layoutProbe) };
    this.g2b = device.createBindGroup({ layout: l2b, entries: [{ binding: 0, resource: { buffer: this.bloodBuf } }, { binding: 1, resource: { buffer: this.dropsBuf } }] });
    this.g0s = device.createBindGroup({ layout: l0s, entries: [
      { binding: 0, resource: { buffer: gpu.info } }, { binding: 1, resource: { buffer: this.simBuf } }, { binding: 2, resource: { buffer: this.propsBuf } },
      { binding: 3, resource: { buffer: this.stateBuf } }, { binding: 4, resource: { buffer: gpu.cell } }, { binding: 5, resource: { buffer: gpu.occupancy } },
      { binding: 6, resource: { buffer: gpu.water } }, { binding: 7, resource: { buffer: gpu.omega } }] });
    this.sliceBuf = device.createBuffer({ label: 'slice', size: SLICE_W * SLICE_H * 4, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC });
    this.sliceReadback = device.createBuffer({ label: 'sliceReadback', size: SLICE_W * SLICE_H * 4, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    this.g2 = device.createBindGroup({ layout: l2, entries: [{ binding: 0, resource: { buffer: this.sliceBuf } }] });
    this.g0 = device.createBindGroup({ layout: l0, entries: [
      { binding: 0, resource: { buffer: gpu.info } }, { binding: 1, resource: { buffer: this.simBuf } }, { binding: 2, resource: { buffer: this.propsBuf } },
      { binding: 3, resource: { buffer: this.stateBuf } }, { binding: 4, resource: { buffer: gpu.cell } }, { binding: 5, resource: { buffer: gpu.occupancy } },
      { binding: 6, resource: { buffer: gpu.water } }, { binding: 7, resource: { buffer: gpu.omega } }, { binding: 8, resource: { buffer: gpu.heat } }] });
    const pair = (a: GPUBuffer, b: GPUBuffer) => device.createBindGroup({ layout: l1, entries: [{ binding: 0, resource: { buffer: a } }, { binding: 1, resource: { buffer: b } }] });
    this.gPot = [pair(gpu.potential, gpu.potentialNext), pair(gpu.potentialNext, gpu.potential)];
    this.gTemp = [pair(gpu.temp, gpu.tempNext), pair(gpu.tempNext, gpu.temp)];
  }

  setPickRay(origin: vec3, dir: vec3) { vec3.copy(this.ray.o, origin); vec3.copy(this.ray.d, dir); }

  /** Registers lattice constraint segments (material mm): [ax, ay, az, throughTissue, bx, by, bz, 0] per constraint. */
  setConstraintSegments(segs: Float32Array<ArrayBuffer>, n: number) {
    const segBuf = this.device.createBuffer({ label: 'constraintSegs', size: Math.max(32, segs.byteLength), usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    this.device.queue.writeBuffer(segBuf, 0, segs);
    const probeBuf = this.device.createBuffer({ label: 'constraintProbes', size: Math.max(16, n * 16), usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC });
    const readback = this.device.createBuffer({ label: 'constraintProbesRead', size: Math.max(16, n * 16), usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    const bg = this.device.createBindGroup({ layout: this.l2p, entries: [{ binding: 0, resource: { buffer: segBuf } }, { binding: 1, resource: { buffer: probeBuf } }] });
    this.probe = { segs: segBuf, probes: probeBuf, readback, bg, n, severed: new Uint8Array(n), omega: new Float32Array(n), fresh: false };
  }

  /** Latest probe results (severed flags and max omega per constraint); `fresh` is cleared on read. */
  takeProbe(): { severed: Uint8Array; omega: Float32Array } | null {
    if (!this.probe || !this.probe.fresh) return null;
    this.probe.fresh = false;
    return { severed: this.probe.severed, omega: this.probe.omega };
  }

  async readProbe(): Promise<void> {
    const pr = this.probe; if (!pr || pr.readback.mapState !== 'unmapped') return;
    try {
      await pr.readback.mapAsync(GPUMapMode.READ);
      const u = new Uint32Array(pr.readback.getMappedRange().slice(0)); pr.readback.unmap();
      const f = new Float32Array(u.buffer);
      for (let n = 0; n < pr.n; n++) { pr.severed[n] = u[n * 4] & 1; pr.omega[n] = f[n * 4 + 1]; }
      pr.fresh = true;
    } catch { /* in flight */ }
  }

  /** Secondary instrument: forceps jaws (a -> b, grasp when mode 1) or suction tip (a, mode 2). */
  setSecondaryTool(a: vec3, b: vec3, radius: number, mode: SecondaryToolMode) {
    vec3.copy(this.tool2.a, a); vec3.copy(this.tool2.b, b); this.tool2.radius = radius; this.tool2.mode = mode;
  }

  private lastSubsteps = 2;
  private probeEvery = 0;
  private probePending = false;
  private woundBox(): { o: number[]; s: number[] } {
    const { nx, ny, nz } = this.volume.dims;
    if (this.wbMax[0] <= this.wbMin[0]) return { o: [0, 0, 0], s: [0, 0, 0] };
    const o = [Math.max(0, this.wbMin[0]), Math.max(0, this.wbMin[1]), Math.max(0, this.wbMin[2])];
    return { o, s: [Math.min(nx, this.wbMax[0]) - o[0], Math.min(ny, this.wbMax[1]) - o[1], Math.min(nz, this.wbMax[2]) - o[2]] };
  }
  /** Marks a region (voxel coords, min inclusive / max exclusive) as part of the wound: bleeding is scanned there. */
  markWound(min: number[], max: number[]) {
    for (let a = 0; a < 3; a++) { this.wbMin[a] = Math.min(this.wbMin[a], min[a]); this.wbMax[a] = Math.max(this.wbMax[a], max[a]); }
  }

  private writeParams(box0: number[], S: number, tb0: number[], tb1: number[], tool: ElectrodeTool, gen: FT10, dt: number, active: boolean) {
    const buf = new ArrayBuffer(SIM_BYTES);
    const i32 = new Int32Array(buf), f32 = new Float32Array(buf);
    i32.set([box0[0], box0[1], box0[2], S], 0);
    i32.set([tb0[0], tb0[1], tb0[2], 0], 4);
    i32.set([tb1[0], tb1[1], tb1[2], 0], 8);
    const mode = gen.activeMode;
    const reach = mode ? Math.max(0.3 * this.volume.dims.h, (mode.vPeak / 1000) * mode.arcMmPerKv) : 0;
    f32.set([tool.tip[0], tool.tip[1], tool.tip[2], reach], 12);
    f32.set([tool.axis[0], tool.axis[1], tool.axis[2], tool.length], 16);
    f32.set([tool.wdir[0], tool.wdir[1], tool.wdir[2], tool.halfWidth], 20);
    f32.set([tool.ndir[0], tool.ndir[1], tool.ndir[2], tool.halfThickness], 24);
    f32.set(mode ? [gen.activeWatts, mode.zLo, mode.zHi, mode.vPeak / mode.crest] : [0, 100, 1000, 0], 28);
    f32.set([mode ? mode.vapFactor : 0, active ? 1 : 0, tool.type === 'blade' ? 0 : tool.type === 'needle' ? 1 : tool.type === 'ball' ? 2 : 3, this.sigmaScale], 32);
    f32.set([mode ? mode.nearField : 0, 0, 0, 0], 36);
    f32.set([dt, this.simTime, 15, mode ? mode.contactMm : 0], 40);
    f32.set([this.ray.o[0], this.ray.o[1], this.ray.o[2], 0], 44);
    f32.set([this.ray.d[0], this.ray.d[1], this.ray.d[2], this.ray.max], 48);
    const sl = this.slice;
    f32.set([sl.o[0], sl.o[1], sl.o[2], sl.widthMm], 52);
    f32.set([sl.u[0], sl.u[1], sl.u[2], SLICE_W], 56);
    f32.set([0, -1, 0, SLICE_H], 60);
    f32.set([sl.depthMm, sl.mode, sl.peelY, 0], 64);
    const wb = this.woundBox();
    i32.set([wb.o[0], wb.o[1], wb.o[2], this.lastSubsteps], 68);
    i32.set([wb.s[0], wb.s[1], wb.s[2], 0], 72);
    const t2 = this.tool2;
    f32.set([t2.a[0], t2.a[1], t2.a[2], t2.radius], 76);
    f32.set([t2.b[0], t2.b[1], t2.b[2], t2.mode], 80);
    this.device.queue.writeBuffer(this.simBuf, 0, buf);
  }

  /** Encodes one frame of physics. Returns true if tissue state changed (surface needs re-extraction). */
  step(enc: GPUCommandEncoder, tool: ElectrodeTool, gen: FT10, frameDt: number): boolean {
    const { nx, ny, nz } = this.volume.dims;
    const dtFrame = Math.min(frameDt, 0.05);
    const active = gen.activation !== 'none' && !gen.remFault && tool.visible && this.readout.hit !== null;
    if (!active) { this.readout.powerW = 0; this.readout.iRms = 0; this.readout.contactVoxels = 0; }
    const S = this.subdomain;
    let box0 = [0, 0, 0];
    if (active) {
      const [vi, vj, vk] = this.volume.worldToVoxel(tool.tip[0], tool.tip[1], tool.tip[2]);
      box0 = [Math.round(vi) - S / 2, Math.round(vj) - S / 2, Math.round(vk) - S / 2].map(Math.floor);
      // grow the thermal box to cover the subdomain
      const lo = box0, hi = box0.map((v) => v + S);
      for (let a = 0; a < 3; a++) { this.tbMin[a] = Math.min(this.tbMin[a], lo[a]); this.tbMax[a] = Math.max(this.tbMax[a], hi[a]); }
      this.markWound(lo, hi);
      this.hotUntil = this.simTime + 10;
      this.totals.activeSeconds += dtFrame;
    }
    const hot = this.simTime < this.hotUntil && this.tbMax[0] > this.tbMin[0];
    const tb0 = hot ? [Math.max(0, this.tbMin[0]), Math.max(0, this.tbMin[1]), Math.max(0, this.tbMin[2])] : [0, 0, 0];
    const tb1 = hot ? [Math.min(nx, this.tbMax[0]) - tb0[0], Math.min(ny, this.tbMax[1]) - tb0[1], Math.min(nz, this.tbMax[2]) - tb0[2]] : [0, 0, 0];
    if (!hot) { this.tbMin = [1e9, 1e9, 1e9]; this.tbMax = [-1, -1, -1]; }

    let substeps = Math.ceil(dtFrame / this.dtMax);
    if (substeps % 2 === 1) substeps++;
    const dt = dtFrame / substeps;
    this.lastSubsteps = substeps;
    this.writeParams(box0, S, tb0, tb1, tool, gen, dt, active);
    // droplet landings
    const nd = Math.min(this.drops.length, MAX_DROPS);
    const db = new ArrayBuffer(DROPS_BYTES); new Uint32Array(db, 0, 4)[0] = nd;
    const df = new Float32Array(db, 16);
    for (let n = 0; n < nd; n++) df.set(this.drops[n], n * 4);
    this.drops.length = 0;
    this.device.queue.writeBuffer(this.dropsBuf, 0, db);
    // reset per-frame accumulators (cumulative counters at 80, 92, 96, 100 are kept)
    this.device.queue.writeBuffer(this.stateBuf, 0, new Uint32Array([0, 0, 0, 0]));
    this.device.queue.writeBuffer(this.stateBuf, 64, new Uint32Array([0, 0, 0, 0]));
    this.device.queue.writeBuffer(this.stateBuf, 84, new Uint32Array([0, 0]));
    this.device.queue.writeBuffer(this.stateBuf, 104, new Uint32Array([0, 0]));
    this.device.queue.writeBuffer(this.stateBuf, 112, new Uint32Array([0, 0, 0, 0]));
    const wb = this.woundBox();

    const pass = enc.beginComputePass({ label: 'physics' });
    pass.setBindGroup(0, this.g0);
    // cursor pick against the current tissue state
    pass.setPipeline(this.pipes.pick); pass.setBindGroup(1, this.gPot[0]); pass.dispatchWorkgroups(1);
    const wg = Math.ceil(S / 4);
    if (active) {
      // solve in place on `potential` (dstA of gPot[1]); heating then reads it as srcA of gPot[0]
      pass.setBindGroup(1, this.gPot[1]);
      pass.setPipeline(this.pipes.seed); pass.dispatchWorkgroups(wg, wg, wg);
      for (let it = 0; it < this.relaxSweeps; it++) {
        pass.setPipeline(this.pipes.red); pass.dispatchWorkgroups(wg, wg, wg);
        pass.setPipeline(this.pipes.black); pass.dispatchWorkgroups(wg, wg, wg);
      }
      pass.setPipeline(this.pipes.heating); pass.setBindGroup(1, this.gPot[0]); pass.dispatchWorkgroups(wg, wg, wg);
      pass.setPipeline(this.pipes.generator); pass.dispatchWorkgroups(1);
    }
    if (hot) {
      pass.setPipeline(this.pipes.bioheat);
      const gx = Math.ceil(tb1[0] / 4), gy = Math.ceil(tb1[1] / 4), gz = Math.ceil(tb1[2] / 4);
      for (let s = 0; s < substeps; s++) { pass.setBindGroup(1, this.gTemp[s & 1]); pass.dispatchWorkgroups(gx, gy, gz); }
    }
    // forceps coaptation flags: mark while grasping; one clearing pass after release
    if (this.tool2.mode === 1 || this.tool2.lastMode === 1) {
      pass.setPipeline(this.pipes.coaptMark); pass.setBindGroup(0, this.g0); pass.setBindGroup(1, this.gPot[0]); pass.dispatchWorkgroups(3, 3, 3);
    }
    this.tool2.lastMode = this.tool2.mode;
    // bleeding and blood flow over the wound box
    if (wb.s[0] > 0 && wb.s[1] > 0 && wb.s[2] > 0) {
      pass.setBindGroup(0, this.g0s); pass.setBindGroup(1, this.gTemp[0]); pass.setBindGroup(2, this.g2b);
      const cx = Math.ceil(wb.s[0] / 8), cy = Math.ceil(wb.s[1] / 8);
      pass.setPipeline(this.pipes.surfaceHeight); pass.dispatchWorkgroups(cx, cy);
      pass.setPipeline(this.pipes.bloodSource); pass.dispatchWorkgroups(Math.ceil(wb.s[0] / 4), Math.ceil(wb.s[1] / 4), Math.ceil(wb.s[2] / 4));
      if (nd > 0) { pass.setPipeline(this.pipes.bloodDrops); pass.dispatchWorkgroups(1); }
      const fx = Math.ceil((wb.s[0] + 8) / 8), fy = Math.ceil((wb.s[1] + 8) / 8);
      for (let it = 0; it < 2; it++) {
        pass.setPipeline(this.pipes.bloodFlow); pass.dispatchWorkgroups(fx, fy);
        pass.setPipeline(this.pipes.bloodCommit); pass.dispatchWorkgroups(fx, fy);
      }
    }
    if (this.probe && (hot || this.probeEvery++ % 10 === 0)) {
      pass.setPipeline(this.pipes.constraintProbe); pass.setBindGroup(0, this.g0s); pass.setBindGroup(1, this.probe.bg);
      pass.dispatchWorkgroups(Math.ceil(this.probe.n / 64));
      this.probePending = true;
    }
    if (this.slice.enabled) {
      pass.setPipeline(this.pipes.slice); pass.setBindGroup(0, this.g0s); pass.setBindGroup(1, this.slice.mode >= 4 ? this.gPot[0] : this.gTemp[0]); pass.setBindGroup(2, this.g2);
      pass.dispatchWorkgroups(Math.ceil(SLICE_W / 8), Math.ceil(SLICE_H / 8));
    }
    pass.end();
    enc.copyBufferToBuffer(this.stateBuf, 0, this.readback, 0, STATE_BYTES);
    if (this.slice.enabled) enc.copyBufferToBuffer(this.sliceBuf, 0, this.sliceReadback, 0, SLICE_W * SLICE_H * 4);
    if (this.probe && this.probePending && this.probe.readback.mapState === 'unmapped') { enc.copyBufferToBuffer(this.probe.probes, 0, this.probe.readback, 0, this.probe.n * 16); this.probePending = false; }
    this.simTime += dtFrame;
    return hot;
  }

  /** Async readback of the cross-section image into slicePixels. Returns true when updated. */
  async readSlice(): Promise<boolean> {
    if (!this.slice.enabled || this.sliceReadback.mapState !== 'unmapped') return false;
    try {
      await this.sliceReadback.mapAsync(GPUMapMode.READ);
      this.slicePixels.set(new Uint8ClampedArray(this.sliceReadback.getMappedRange()));
      this.sliceReadback.unmap();
      return true;
    } catch { return false; }
  }

  /** Async readback of generator state, pick result and stats (one frame of latency). */
  async read(): Promise<SimReadout> {
    if (this.readback.mapState !== 'unmapped') return this.readout;
    try {
      await this.readback.mapAsync(GPUMapMode.READ);
      const buf = this.readback.getMappedRange().slice(0);
      this.readback.unmap();
      const u = new Uint32Array(buf), f = new Float32Array(buf);
      const r = this.readout;
      r.contactVoxels = u[1]; r.vapVoxels = u[2] / 1000;
      r.zOhm = f[4]; r.powerW = f[5]; r.vRms = Math.sqrt(Math.max(f[6], 0)); r.iRms = f[7];
      r.hit = f[11] > 0.5 ? vec3.fromValues(f[8], f[9], f[10]) : null;
      r.hitCell = u[12]; r.hitIdx = u[13];
      r.maxTempC = u[16] / 100; r.carbonVoxels = u[17]; r.newCoagVoxels = u[18];
      r.muscleHeatVoxels = u[19]; r.bloodLostMl = u[20] / 1000; r.exposedArterial = u[21]; r.exposedVenous = u[22];
      r.sealedVoxels = u[23]; r.charPlugVoxels = u[24]; r.rebleeds = u[25]; r.poolMl = u[26] / 2e6; r.poolAtTipMm = u[27] / 1000; // pool stats are accumulated by both flow commits per frame (nanolitres)
      const nb = Math.min(u[28], 8); r.bleedPoints = [];
      for (let n = 0; n < nb; n++) r.bleedPoints.push([f[32 + n * 4], f[33 + n * 4], f[34 + n * 4], f[35 + n * 4]]);
      this.totals.vapVoxels += r.vapVoxels;
    } catch { /* in flight */ }
    return this.readout;
  }
}
