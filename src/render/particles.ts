import { vec3 } from 'gl-matrix';
import commonWgsl from './wgsl/common.wgsl?raw';
import { createShader } from './gpu';
import particlesWgsl from './wgsl/particles.wgsl?raw';

const MAX = 4096;
const STRIDE = 12; // floats

interface P { x: number; y: number; z: number; vx: number; vy: number; vz: number; age: number; life: number; size0: number; size1: number; kind: 0 | 1 | 2 | 3; dx: number; dy: number; dz: number; alpha: number; vol: number }

/**
 * Steam (kind 0), smoke (kind 1) and arc filaments (kind 2). Simulated on the CPU (few thousand
 * particles), rendered as instanced billboards after the opaque pass.
 */
export class ParticleSystem {
  private parts: P[] = [];
  private data = new Float32Array(MAX * STRIDE);
  private buffer: GPUBuffer;
  private pipeAlpha: GPURenderPipeline;
  private pipeAdd: GPURenderPipeline;
  private bindGroup: GPUBindGroup;
  private countAlpha = 0; private countAdd = 0;
  private rngState = 12345;
  constructor(device: GPUDevice, frameBuf: GPUBuffer, format: GPUTextureFormat = 'rgba16float') {
    this.buffer = device.createBuffer({ label: 'particles', size: MAX * STRIDE * 4, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    const layout = device.createBindGroupLayout({ entries: [
      { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
      { binding: 1, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } }] });
    const module = createShader(device, 'particles', commonWgsl + particlesWgsl);
    const mk = (blend: GPUBlendState) => device.createRenderPipeline({
      label: 'particles', layout: device.createPipelineLayout({ bindGroupLayouts: [layout] }),
      vertex: { module, entryPoint: 'vs_main' },
      fragment: { module, entryPoint: 'fs_main', targets: [{ format, blend }] },
      primitive: { topology: 'triangle-list', cullMode: 'none' },
      depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less' },
    });
    this.pipeAlpha = mk({ color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' }, alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' } });
    this.pipeAdd = mk({ color: { srcFactor: 'one', dstFactor: 'one', operation: 'add' }, alpha: { srcFactor: 'zero', dstFactor: 'one', operation: 'add' } });
    this.bindGroup = device.createBindGroup({ layout, entries: [{ binding: 0, resource: { buffer: frameBuf } }, { binding: 1, resource: { buffer: this.buffer } }] });
  }
  private rnd() { this.rngState = (this.rngState * 1664525 + 1013904223) >>> 0; return this.rngState / 4294967296; }

  /** Emit steam/smoke at a point. rate in particles per second, dt seconds. */
  emit(kind: 0 | 1, at: vec3, rate: number, dt: number) {
    let n = rate * dt; if (this.rnd() < n - Math.floor(n)) n++; n = Math.floor(n);
    for (let i = 0; i < n && this.parts.length < MAX - 64; i++) {
      const a = this.rnd() * Math.PI * 2, sp = kind === 0 ? 14 + this.rnd() * 12 : 6 + this.rnd() * 8;
      this.parts.push({ x: at[0] + (this.rnd() - 0.5) * 1.2, y: at[1] + 0.3, z: at[2] + (this.rnd() - 0.5) * 1.2,
        vx: Math.cos(a) * sp * 0.35, vy: sp, vz: Math.sin(a) * sp * 0.35,
        age: 0, life: kind === 0 ? 0.9 + this.rnd() * 0.6 : 2.5 + this.rnd() * 2.0,
        size0: kind === 0 ? 0.4 : 0.5, size1: kind === 0 ? 1.8 + this.rnd() * 1.5 : 2.5 + this.rnd() * 2.5, kind, dx: 0, dy: 1, dz: 0, alpha: kind === 0 ? 0.14 : 0.12, vol: 0 });
    }
  }
  /** Arc filament from a to b, lives ~60 ms. */
  arc(a: vec3, b: vec3) {
    if (this.parts.length >= MAX - 1) return;
    const d = vec3.sub(vec3.create(), b, a); const len = vec3.length(d);
    this.parts.push({ x: a[0], y: a[1], z: a[2], vx: 0, vy: 0, vz: 0, age: 0, life: 0.04 + this.rnd() * 0.05, size0: len, size1: len, kind: 2, dx: d[0], dy: d[1], dz: d[2], alpha: 0.6 + this.rnd() * 0.4, vol: 0 });
  }
  /**
   * Blood droplets from an exposed vessel end at `at`. mlPerS > 0 arterial (jet, ~1 m/s, pulsatile
   * through the caller's rate), < 0 venous (welling at a few cm/s). Volume is carried per droplet so
   * landings conserve what was emitted.
   */
  bleed(at: vec3, mlPerS: number, dt: number) {
    const arterial = mlPerS > 0;
    // the kernel keeps the rest of the output in the column at the vessel end (sim.wgsl ART/VEN_JET_FRACTION)
    const volUl = Math.abs(mlPerS) * dt * 1000 * (arterial ? 0.7 : 0.15);
    const perDrop = arterial ? 0.4 : 0.8; // microlitres
    let n = volUl / perDrop; if (this.rnd() < n - Math.floor(n)) n++; n = Math.floor(n);
    for (let i = 0; i < n && this.parts.length < MAX - 64; i++) {
      const a = this.rnd() * Math.PI * 2, el = arterial ? 0.5 + this.rnd() * 0.9 : 0.9 + this.rnd() * 0.5;
      const sp = arterial ? 120 + this.rnd() * 280 : 15 + this.rnd() * 30;
      this.parts.push({ x: at[0], y: at[1] + 0.2, z: at[2],
        vx: Math.cos(a) * Math.cos(el) * sp, vy: Math.sin(el) * sp, vz: Math.sin(a) * Math.cos(el) * sp,
        age: 0, life: 3.0, size0: arterial ? 0.35 : 0.5, size1: arterial ? 0.35 : 0.5, kind: 3, dx: 0, dy: 1, dz: 0, alpha: 0.95, vol: perDrop });
    }
  }
  /** Hot blood thrown from a boiling pool under the electrode: fast fine droplets that land back in the field. */
  spatter(at: vec3, volUl: number) {
    const n = Math.max(1, Math.round(volUl / 0.1));
    for (let i = 0; i < n && this.parts.length < MAX - 64; i++) {
      const a = this.rnd() * Math.PI * 2, el = 0.6 + this.rnd() * 0.7, sp = 150 + this.rnd() * 300;
      this.parts.push({ x: at[0], y: at[1] + 0.1, z: at[2],
        vx: Math.cos(a) * Math.cos(el) * sp, vy: Math.sin(el) * sp, vz: Math.sin(a) * Math.cos(el) * sp,
        age: 0, life: 2.0, size0: 0.22, size1: 0.22, kind: 3, dx: 0, dy: 1, dz: 0, alpha: 0.95, vol: volUl / n });
    }
    // steam puff
    this.emit(0, at, 40, 0.1);
  }
  /** Droplets that reached the landing height this step: [x, y, z, microlitres]. Cleared by the caller. */
  readonly landings: number[][] = [];

  update(dt: number, suctionAt: vec3 | null, landingY: (x: number, z: number) => number = () => 0) {
    const keep: P[] = [];
    for (const p of this.parts) {
      p.age += dt; if (p.age >= p.life) continue;
      if (p.kind === 3) {
        p.vy -= 9810 * dt; p.vx *= 1 - 0.5 * dt; p.vz *= 1 - 0.5 * dt;
        p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
        if (p.y <= landingY(p.x, p.z) && p.vy < 0) { this.landings.push([p.x, p.y, p.z, p.vol]); continue; }
        keep.push(p); continue;
      }
      if (p.kind !== 2) {
        // buoyancy, drag, gentle drift and turbulence
        p.vy += (p.kind === 0 ? 10 : 4) * dt; p.vx *= 1 - 0.8 * dt; p.vz *= 1 - 0.8 * dt; p.vy *= 1 - 0.5 * dt;
        p.vx += (this.rnd() - 0.5) * 20 * dt; p.vz += (this.rnd() - 0.5) * 20 * dt; p.vx += 3 * dt;
        if (suctionAt) { const dx = suctionAt[0] - p.x, dy = suctionAt[1] - p.y, dz = suctionAt[2] - p.z; const d2 = dx * dx + dy * dy + dz * dz; if (d2 < 900) { const f = 400 * dt / (d2 + 20); p.vx += dx * f; p.vy += dy * f; p.vz += dz * f; if (d2 < 9) continue; } }
        p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
      }
      keep.push(p);
    }
    this.parts = keep;
  }
  /** Packs instances (alpha-blended first, then additive) and uploads. */
  upload(device: GPUDevice) {
    const alphaList = this.parts.filter((p) => p.kind !== 2), addList = this.parts.filter((p) => p.kind === 2);
    let o = 0;
    const put = (p: P) => {
      const f = p.age / p.life;
      const size = p.kind >= 2 ? p.size0 : p.size0 + (p.size1 - p.size0) * Math.sqrt(f);
      const alpha = p.kind >= 2 ? p.alpha : p.alpha * (f < 0.1 ? f / 0.1 : 1 - (f - 0.1) / 0.9);
      const col = p.kind === 0 ? [0.92, 0.92, 0.94] : p.kind === 1 ? [0.30, 0.27, 0.24] : p.kind === 3 ? [0.55, 0.04, 0.03] : [0.65, 0.78, 1.0];
      this.data.set([p.x, p.y, p.z, size, col[0], col[1], col[2], alpha, p.dx, p.dy, p.dz, p.kind], o); o += STRIDE;
    };
    alphaList.forEach(put); addList.forEach(put);
    this.countAlpha = alphaList.length; this.countAdd = addList.length;
    if (o > 0) device.queue.writeBuffer(this.buffer, 0, this.data, 0, o);
  }
  draw(pass: GPURenderPassEncoder) {
    if (this.countAlpha + this.countAdd === 0) return;
    pass.setBindGroup(0, this.bindGroup);
    if (this.countAlpha) { pass.setPipeline(this.pipeAlpha); pass.draw(6, this.countAlpha, 0, 0); }
    if (this.countAdd) { pass.setPipeline(this.pipeAdd); pass.draw(6, this.countAdd, 0, this.countAlpha); }
  }
  get count() { return this.parts.length; }
}
