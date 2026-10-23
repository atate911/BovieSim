import { mat4, vec3 } from 'gl-matrix';
import commonWgsl from './wgsl/common.wgsl?raw';
import lightingWgsl from './wgsl/lighting.wgsl?raw';
import tissueWgsl from './wgsl/tissue.wgsl?raw';
import simpleWgsl from './wgsl/simple.wgsl?raw';
import postWgsl from './wgsl/post.wgsl?raw';
import { GpuContext, createShader } from './gpu';
import { SurfaceExtractor } from './surface';
import { OrbitCamera } from './camera';
import { MeshBuilder } from './meshes';
import { VOLUME_INFO_WGSL } from '@/core/volume';
import { ParticleSystem } from './particles';

export interface SceneParams {
  skinTone: number;
  prep: 0 | 1 | 2;
  langerRad: number;
  exposure: number;
  debugMode: number;
  bedHalfX: number;
  bedHalfZ: number;
  arcLight: { x: number; y: number; z: number; intensity: number };
}

export interface SimpleDraw {
  buffer: GPUBuffer;
  count: number;
  material: GPUBuffer;
  bindGroup: GPUBindGroup;
  castsShadow: boolean;
}

/** One head of the overhead surgical light: a dome fixed in the room, ~0.85 m above the field. */
export interface LightHead {
  /** position in field millimetres */
  position: vec3;
  intensity: number;
  shadowSize: number;
}

const FRAME_BYTES = 64 * 4 + 16 * 9;
/** half-angle of the light dome seen from the field (radius ~0.25 m at ~0.85 m): sets penumbra width and highlight size */
const DOME_TAN = 0.22;
const SHADOW_NEAR = 100, SHADOW_FAR = 500;

/** Raw WebGPU forward renderer: two shadow passes (one per light head), HDR main pass, tonemap pass, optional readback. */
export class Renderer {
  readonly device: GPUDevice;
  private frameBuf: GPUBuffer;
  private shadowTex: GPUTexture[] = [];
  private shadowView: GPUTextureView[] = [];
  private shadowSel: GPUBuffer[] = [];
  private shadowSampler: GPUSampler;
  private hdr!: GPUTexture; private hdrView!: GPUTextureView;
  private depth!: GPUTexture; private depthView!: GPUTextureView;
  private out!: GPUTexture; private outView!: GPUTextureView; // headless target
  private width = 0; private height = 0;
  private tissuePipe: GPURenderPipeline; private tissueShadowPipe: GPURenderPipeline;
  private simplePipe: GPURenderPipeline; private simpleShadowPipe: GPURenderPipeline;
  private postPipe: GPURenderPipeline;
  private tissueBG!: GPUBindGroup; private simpleFrameBG: GPUBindGroup;
  private tissueShadowBG: GPUBindGroup[] = []; private simpleShadowBG: GPUBindGroup[] = [];
  private shadowFrameLayout: GPUBindGroupLayout;
  private postBG!: GPUBindGroup;
  private simpleMatLayout: GPUBindGroupLayout;
  private frameLayout: GPUBindGroupLayout;
  private linearSampler: GPUSampler;
  readonly draws: SimpleDraw[] = [];
  readonly particles: ParticleSystem;
  private latInfo: GPUBuffer; private latDisp: GPUBuffer;
  private readback: GPUBuffer | null = null;
  /** dual-head surgical light: the main head over the surgeon's left shoulder, a fill head opposite, as in theatre */
  readonly heads: LightHead[] = [
    { position: vec3.scale(vec3.create(), vec3.normalize(vec3.create(), [-0.35, 0.85, 0.3]), 850), intensity: 1.7, shadowSize: 2048 },
    { position: vec3.scale(vec3.create(), vec3.normalize(vec3.create(), [0.4, 0.8, -0.25]), 800), intensity: 1.0, shadowSize: 1024 },
  ];
  /** 4300 K heads after the eye's white balance: near white with a trace of warmth */
  lightColor: [number, number, number] = [1.0, 0.965, 0.91];
  ambient = 0.22;
  time = 0;

  constructor(private gpu: GpuContext, private surface: SurfaceExtractor, private volumeBufs: { blood: GPUBuffer; info: GPUBuffer }) {
    const d = gpu.device; this.device = d;
    this.frameBuf = d.createBuffer({ label: 'frame', size: FRAME_BYTES, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    for (let i = 0; i < 2; i++) {
      const size = this.heads[i].shadowSize;
      this.shadowTex[i] = d.createTexture({ label: 'shadow' + i, size: [size, size], format: 'depth32float', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING });
      this.shadowView[i] = this.shadowTex[i].createView();
      this.shadowSel[i] = d.createBuffer({ label: 'shadowSel' + i, size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
      d.queue.writeBuffer(this.shadowSel[i], 0, new Uint32Array([i, 0, 0, 0]));
    }
    this.shadowSampler = d.createSampler({ compare: 'less', magFilter: 'linear', minFilter: 'linear' });
    this.linearSampler = d.createSampler({ magFilter: 'linear', minFilter: 'linear' });

    this.frameLayout = d.createBindGroupLayout({
      label: 'frameLayout',
      entries: [
        { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
        { binding: 1, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
        { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
        { binding: 3, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'comparison' } },
        { binding: 4, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'read-only-storage' } },
        { binding: 5, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
        { binding: 6, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
        { binding: 7, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
        { binding: 8, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
      ],
    });
    const simpleFrameLayout = d.createBindGroupLayout({
      label: 'simpleFrameLayout',
      entries: [
        { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
        { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
        { binding: 3, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'comparison' } },
        { binding: 8, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
      ],
    });
    this.shadowFrameLayout = d.createBindGroupLayout({
      label: 'shadowFrameLayout',
      entries: [
        { binding: 0, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
        { binding: 1, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
        { binding: 6, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
        { binding: 7, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
        { binding: 9, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
      ],
    });
    // lattice displacement (identity until a lattice is attached)
    this.latInfo = d.createBuffer({ label: 'latticeInfo', size: 32, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    d.queue.writeBuffer(this.latInfo, 0, new Uint32Array([0, 0, 0, 0, 0, 0, 0, 0]));
    this.latDisp = d.createBuffer({ label: 'latticeDisp', size: 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    const simpleShadowFrameLayout = d.createBindGroupLayout({
      label: 'simpleShadowFrameLayout',
      entries: [
        { binding: 0, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
        { binding: 9, visibility: GPUShaderStage.VERTEX, buffer: { type: 'uniform' } },
      ],
    });
    this.simpleMatLayout = d.createBindGroupLayout({ label: 'simpleMat', entries: [{ binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } }] });

    const tissueMod = createShader(d, 'tissue', commonWgsl + lightingWgsl + VOLUME_INFO_WGSL + tissueWgsl);
    const simpleMod = createShader(d, 'simple', commonWgsl + lightingWgsl + simpleWgsl);
    const postMod = createShader(d, 'post', commonWgsl + postWgsl);

    const tissueLayout = d.createPipelineLayout({ bindGroupLayouts: [this.frameLayout] });
    this.tissuePipe = d.createRenderPipeline({
      label: 'tissue', layout: tissueLayout,
      vertex: { module: tissueMod, entryPoint: 'vs_main' },
      fragment: { module: tissueMod, entryPoint: 'fs_main', targets: [{ format: 'rgba16float' }] },
      primitive: { topology: 'triangle-list', cullMode: 'back' },
      depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
    });
    this.tissueShadowPipe = d.createRenderPipeline({
      label: 'tissueShadow', layout: d.createPipelineLayout({ bindGroupLayouts: [this.shadowFrameLayout] }),
      vertex: { module: tissueMod, entryPoint: 'vs_shadow' },
      primitive: { topology: 'triangle-list', cullMode: 'none' },
      depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less', depthBias: 2, depthBiasSlopeScale: 2 },
    });
    const simpleLayout = d.createPipelineLayout({ bindGroupLayouts: [simpleFrameLayout, this.simpleMatLayout] });
    const vbuf: GPUVertexBufferLayout = { arrayStride: 32, attributes: [
      { shaderLocation: 0, offset: 0, format: 'float32x3' }, { shaderLocation: 1, offset: 12, format: 'float32x3' }, { shaderLocation: 2, offset: 24, format: 'float32x2' }] };
    this.simplePipe = d.createRenderPipeline({
      label: 'simple', layout: simpleLayout,
      vertex: { module: simpleMod, entryPoint: 'vs_main', buffers: [vbuf] },
      fragment: { module: simpleMod, entryPoint: 'fs_main', targets: [{ format: 'rgba16float' }] },
      primitive: { topology: 'triangle-list', cullMode: 'none' },
      depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
    });
    this.simpleShadowPipe = d.createRenderPipeline({
      label: 'simpleShadow', layout: d.createPipelineLayout({ bindGroupLayouts: [simpleShadowFrameLayout, this.simpleMatLayout] }),
      vertex: { module: simpleMod, entryPoint: 'vs_shadow', buffers: [vbuf] },
      primitive: { topology: 'triangle-list', cullMode: 'none' },
      depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less', depthBias: 2, depthBiasSlopeScale: 2 },
    });
    this.postPipe = d.createRenderPipeline({
      label: 'post', layout: 'auto',
      vertex: { module: postMod, entryPoint: 'vs_main' },
      fragment: { module: postMod, entryPoint: 'fs_main', targets: [{ format: gpu.format }] },
      primitive: { topology: 'triangle-list' },
    });
    this.simpleFrameBG = d.createBindGroup({ layout: simpleFrameLayout, entries: [
      { binding: 0, resource: { buffer: this.frameBuf } }, { binding: 2, resource: this.shadowView[0] }, { binding: 3, resource: this.shadowSampler },
      { binding: 8, resource: this.shadowView[1] }] });
    for (let i = 0; i < 2; i++) {
      this.simpleShadowBG[i] = d.createBindGroup({ layout: simpleShadowFrameLayout, entries: [
        { binding: 0, resource: { buffer: this.frameBuf } }, { binding: 9, resource: { buffer: this.shadowSel[i] } }] });
    }
    this.particles = new ParticleSystem(d, this.frameBuf);
    this.rebuildTissueBG();
  }

  rebuildTissueBG() {
    this.tissueBG = this.device.createBindGroup({ layout: this.frameLayout, entries: [
      { binding: 0, resource: { buffer: this.frameBuf } }, { binding: 1, resource: { buffer: this.surface.vertexBuffer } },
      { binding: 2, resource: this.shadowView[0] }, { binding: 3, resource: this.shadowSampler },
      { binding: 4, resource: { buffer: this.volumeBufs.blood } }, { binding: 5, resource: { buffer: this.volumeBufs.info } },
      { binding: 6, resource: { buffer: this.latInfo } }, { binding: 7, resource: { buffer: this.latDisp } },
      { binding: 8, resource: this.shadowView[1] }] });
    for (let i = 0; i < 2; i++) {
      this.tissueShadowBG[i] = this.device.createBindGroup({ layout: this.shadowFrameLayout, entries: [
        { binding: 0, resource: { buffer: this.frameBuf } }, { binding: 1, resource: { buffer: this.surface.vertexBuffer } },
        { binding: 6, resource: { buffer: this.latInfo } }, { binding: 7, resource: { buffer: this.latDisp } },
        { binding: 9, resource: { buffer: this.shadowSel[i] } }] });
    }
  }

  /** Attaches a mechanics lattice: allocates the displacement buffer and writes its info. */
  attachLattice(info: ArrayBuffer, nodeCount: number) {
    this.latDisp.destroy();
    this.latDisp = this.device.createBuffer({ label: 'latticeDisp', size: nodeCount * 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    this.device.queue.writeBuffer(this.latInfo, 0, info);
    this.rebuildTissueBG();
  }
  uploadLattice(disp: Float32Array<ArrayBuffer>) { this.device.queue.writeBuffer(this.latDisp, 0, disp); }

  addMesh(mb: MeshBuilder, label: string, color: [number, number, number], opts: { metallic?: number; rough?: number; kind?: number; emissive?: number; castsShadow?: boolean; model?: mat4 } = {}): SimpleDraw {
    const { buffer, count } = mb.toBuffer(this.device, label);
    const material = this.device.createBuffer({ label: label + 'Mat', size: 32 + 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const draw: SimpleDraw = { buffer, count, material, castsShadow: opts.castsShadow ?? true,
      bindGroup: this.device.createBindGroup({ layout: this.simpleMatLayout, entries: [{ binding: 0, resource: { buffer: material } }] }) };
    this.setMaterial(draw, color, opts);
    this.draws.push(draw);
    return draw;
  }

  setMaterial(draw: SimpleDraw, color: [number, number, number], opts: { metallic?: number; rough?: number; kind?: number; emissive?: number; model?: mat4 }) {
    const f = new Float32Array(24);
    f.set([color[0], color[1], color[2], opts.metallic ?? 0, opts.rough ?? 0.5, opts.kind ?? 0, opts.emissive ?? 0, 0]);
    f.set(opts.model ?? mat4.create(), 8);
    this.device.queue.writeBuffer(draw.material, 0, f);
  }

  setModel(draw: SimpleDraw, model: mat4) {
    this.device.queue.writeBuffer(draw.material, 32, new Float32Array(model));
  }

  resize(width: number, height: number) {
    if (width === this.width && height === this.height) return;
    this.width = width; this.height = height;
    const d = this.device;
    this.hdr?.destroy(); this.depth?.destroy(); this.out?.destroy();
    this.hdr = d.createTexture({ label: 'hdr', size: [width, height], format: 'rgba16float', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING });
    this.hdrView = this.hdr.createView();
    this.depth = d.createTexture({ label: 'depth', size: [width, height], format: 'depth24plus', usage: GPUTextureUsage.RENDER_ATTACHMENT });
    this.depthView = this.depth.createView();
    if (this.gpu.headless) {
      this.out = d.createTexture({ label: 'out', size: [width, height], format: 'rgba8unorm', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC });
      this.outView = this.out.createView();
      this.readback?.destroy();
      this.readback = d.createBuffer({ label: 'readback', size: this.paddedRow() * height, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    }
    this.postBG = d.createBindGroup({ layout: this.postPipe.getBindGroupLayout(0), entries: [
      { binding: 0, resource: this.hdrView }, { binding: 1, resource: this.linearSampler }, { binding: 2, resource: { buffer: this.frameBuf } }] });
  }

  private paddedRow() { return Math.ceil((this.width * 4) / 256) * 256; }

  /** Orthographic shadow projection of one head over the field around the camera target; returns the matrix and its half extent (mm). */
  private lightViewProj(cam: OrbitCamera, bedHalf: number, head: LightHead): { lvp: mat4; r: number } {
    const center = vec3.fromValues(cam.target[0], -8, cam.target[2]);
    const dir = vec3.normalize(vec3.create(), head.position);
    const eye = vec3.scaleAndAdd(vec3.create(), center, dir, 300);
    const view = mat4.lookAt(mat4.create(), eye, center, [0, 0, -1]);
    const r = Math.max(bedHalf * 1.6, 50);
    const proj = mat4.orthoZO(mat4.create(), -r, r, -r, r, SHADOW_NEAR, SHADOW_FAR);
    return { lvp: mat4.multiply(mat4.create(), proj, view), r };
  }

  writeFrame(cam: OrbitCamera, p: SceneParams) {
    const buf = new ArrayBuffer(FRAME_BYTES);
    const f = new Float32Array(buf);
    const bedHalf = Math.max(p.bedHalfX, p.bedHalfZ);
    const a = this.lightViewProj(cam, bedHalf, this.heads[0]), b = this.lightViewProj(cam, bedHalf, this.heads[1]);
    f.set(cam.viewProj, 0); f.set(cam.view, 16); f.set(a.lvp, 32); f.set(b.lvp, 48);
    let o = 64;
    f.set([cam.position[0], cam.position[1], cam.position[2], this.time], o); o += 4;
    f.set([this.heads[0].position[0], this.heads[0].position[1], this.heads[0].position[2], this.heads[0].intensity], o); o += 4;
    f.set([this.heads[1].position[0], this.heads[1].position[1], this.heads[1].position[2], this.heads[1].intensity], o); o += 4;
    f.set([this.lightColor[0], this.lightColor[1], this.lightColor[2], this.ambient], o); o += 4;
    f.set([p.arcLight.x, p.arcLight.y, p.arcLight.z, p.arcLight.intensity], o); o += 4;
    f.set([p.skinTone, p.prep, p.langerRad, p.exposure], o); o += 4;
    f.set([p.debugMode, p.bedHalfX, p.bedHalfZ, 0.25], o); o += 4;
    f.set([DOME_TAN, 2 * a.r / this.heads[0].shadowSize, 2 * b.r / this.heads[1].shadowSize, SHADOW_FAR - SHADOW_NEAR], o); o += 4;
    f.set([this.width, this.height, 0, 0], o);
    this.device.queue.writeBuffer(this.frameBuf, 0, buf);
  }

  /** Encodes one frame. Returns the command encoder for additional passes. */
  render(enc: GPUCommandEncoder) {
    // shadow passes, one per light head
    for (let i = 0; i < 2; i++) {
      const pass = enc.beginRenderPass({ label: 'shadow' + i, colorAttachments: [], depthStencilAttachment: { view: this.shadowView[i], depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'store' } });
      pass.setPipeline(this.tissueShadowPipe); pass.setBindGroup(0, this.tissueShadowBG[i]);
      pass.drawIndirect(this.surface.indirect, 0);
      pass.setPipeline(this.simpleShadowPipe); pass.setBindGroup(0, this.simpleShadowBG[i]);
      for (const dr of this.draws) { if (!dr.castsShadow) continue; pass.setBindGroup(1, dr.bindGroup); pass.setVertexBuffer(0, dr.buffer); pass.draw(dr.count); }
      pass.end();
    }
    // main pass
    {
      const pass = enc.beginRenderPass({ label: 'main',
        colorAttachments: [{ view: this.hdrView, clearValue: { r: 0.02, g: 0.025, b: 0.03, a: 1 }, loadOp: 'clear', storeOp: 'store' }],
        depthStencilAttachment: { view: this.depthView, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'store' } });
      pass.setPipeline(this.tissuePipe); pass.setBindGroup(0, this.tissueBG);
      pass.drawIndirect(this.surface.indirect, 0);
      pass.setPipeline(this.simplePipe); pass.setBindGroup(0, this.simpleFrameBG);
      for (const dr of this.draws) { pass.setBindGroup(1, dr.bindGroup); pass.setVertexBuffer(0, dr.buffer); pass.draw(dr.count); }
      this.particles.draw(pass);
      pass.end();
    }
    // post
    {
      const target = this.gpu.headless ? this.outView : this.gpu.context!.getCurrentTexture().createView();
      const pass = enc.beginRenderPass({ label: 'post', colorAttachments: [{ view: target, loadOp: 'clear', clearValue: { r: 0, g: 0, b: 0, a: 1 }, storeOp: 'store' }] });
      pass.setPipeline(this.postPipe); pass.setBindGroup(0, this.postBG); pass.draw(3);
      pass.end();
    }
    if (this.gpu.headless && this.readback) {
      enc.copyTextureToBuffer({ texture: this.out }, { buffer: this.readback, bytesPerRow: this.paddedRow() }, [this.width, this.height]);
    }
  }

  /** Headless only: reads the final image and paints it onto a 2D canvas. */
  async present2D(canvas: HTMLCanvasElement) {
    if (!this.readback || this.readback.mapState !== 'unmapped') return;
    await this.readback.mapAsync(GPUMapMode.READ);
    const src = new Uint8Array(this.readback.getMappedRange());
    const img = new ImageData(this.width, this.height);
    const row = this.paddedRow();
    for (let y = 0; y < this.height; y++) img.data.set(src.subarray(y * row, y * row + this.width * 4), y * this.width * 4);
    this.readback.unmap();
    canvas.width = this.width; canvas.height = this.height;
    canvas.getContext('2d')!.putImageData(img, 0, 0);
  }
}
