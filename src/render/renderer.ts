import { mat4, vec3 } from 'gl-matrix';
import commonWgsl from './wgsl/common.wgsl?raw';
import lightingWgsl from './wgsl/lighting.wgsl?raw';
import tissueWgsl from './wgsl/tissue.wgsl?raw';
import simpleWgsl from './wgsl/simple.wgsl?raw';
import postWgsl from './wgsl/post.wgsl?raw';
import strandWgsl from './wgsl/strand.wgsl?raw';
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

/** Texture maps for a drawn mesh (albedo in sRGB, normal and roughness linear), sampled triplanar in local mm. */
export interface TextureSet {
  albedo?: GPUTexture;
  normal?: GPUTexture;
  roughness?: GPUTexture;
  /** physical size of one texture repeat (mm) */
  tileMm: number;
  normalStrength?: number;
  roughnessScale?: number;
  /** mean albedo colour (linear), for deciding whether to tint a grey map */
  meanColor?: [number, number, number];
}

export interface SimpleDraw {
  buffer: GPUBuffer;
  count: number;
  material: GPUBuffer;
  bindGroup: GPUBindGroup;
  castsShadow: boolean;
  color: [number, number, number];
  opts: { metallic?: number; rough?: number; kind?: number; emissive?: number };
  textures: TextureSet | null;
}

/** One head of the overhead surgical light: a dome fixed in the room, ~0.85 m above the field. */
export interface LightHead {
  /** position in field millimetres */
  position: vec3;
  intensity: number;
  shadowSize: number;
}

/** A texture slot to fill from public/textures/<slot>/ (see docs/textures.md). */
export interface TextureRequest {
  slot: string;
  draws: SimpleDraw[];
  tileMm: number;
  /** tint applied when the map is grey/neutral (a CC0 scan to colour as surgical blue, for example) */
  tintIfGrey?: [number, number, number];
  /** use this slot's files when the slot itself has none */
  fallbackSlot?: string;
  normalStrength?: number;
}

const FRAME_BYTES = 64 * 4 + 16 * 9;
const MATERIAL_BYTES = 16 * 3 + 64 * 2;
/** half-angle of the light dome seen from the field (radius ~0.2 m at ~0.85 m): sets penumbra width and highlight size */
const DOME_TAN = 0.22;
const SHADOW_NEAR = 100, SHADOW_FAR = 500;

const MIP_WGSL = `
struct VSOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f };
@vertex fn vs(@builtin(vertex_index) vi: u32) -> VSOut {
  var o: VSOut; let x = f32((vi << 1u) & 2u); let y = f32(vi & 2u);
  o.pos = vec4f(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0); o.uv = vec2f(x, y); return o;
}
@group(0) @binding(0) var src: texture_2d<f32>; @group(0) @binding(1) var samp: sampler;
@fragment fn fs(in: VSOut) -> @location(0) vec4f { return textureSample(src, samp, in.uv); }`;

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
  private strandPipe: GPURenderPipeline; private strandBG: GPUBindGroup; private strandBuf: GPUBuffer; private strandCount = 0;
  /** strand vertex capacity (2 per strand) */
  static readonly MAX_STRANDS = 4096;
  private tissueBG!: GPUBindGroup; private simpleFrameBG: GPUBindGroup;
  private tissueShadowBG: GPUBindGroup[] = []; private simpleShadowBG: GPUBindGroup[] = [];
  private shadowFrameLayout: GPUBindGroupLayout;
  private postBG!: GPUBindGroup;
  private simpleMatLayout: GPUBindGroupLayout;
  private frameLayout: GPUBindGroupLayout;
  private linearSampler: GPUSampler;
  private texSampler: GPUSampler;
  private whiteTex: GPUTexture; private flatNormalTex: GPUTexture;
  private mipPipes = new Map<GPUTextureFormat, GPURenderPipeline>();
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
  /** names of texture slots that loaded (for the HUD / headless checks) */
  readonly loadedTextures: string[] = [];

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
    this.texSampler = d.createSampler({ magFilter: 'linear', minFilter: 'linear', mipmapFilter: 'linear', addressModeU: 'repeat', addressModeV: 'repeat', maxAnisotropy: 8 });
    this.whiteTex = this.solidTexture([255, 255, 255, 255], 'rgba8unorm-srgb');
    this.flatNormalTex = this.solidTexture([128, 128, 255, 255], 'rgba8unorm');

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
    this.simpleMatLayout = d.createBindGroupLayout({ label: 'simpleMat', entries: [
      { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
      { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
      { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
      { binding: 3, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
      { binding: 4, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } }] });

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
    const strandMod = createShader(d, 'strand', commonWgsl + strandWgsl);
    const strandFrameLayout = d.createBindGroupLayout({ label: 'strandFrame', entries: [{ binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } }] });
    this.strandPipe = d.createRenderPipeline({
      label: 'strands', layout: d.createPipelineLayout({ bindGroupLayouts: [strandFrameLayout] }),
      vertex: { module: strandMod, entryPoint: 'vs_main', buffers: [{ arrayStride: 16, attributes: [{ shaderLocation: 0, offset: 0, format: 'float32x3' }, { shaderLocation: 1, offset: 12, format: 'float32' }] }] },
      fragment: { module: strandMod, entryPoint: 'fs_main', targets: [{ format: 'rgba16float', blend: { color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' }, alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' } } }] },
      primitive: { topology: 'line-list' },
      depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'less' },
    });
    this.strandBG = d.createBindGroup({ layout: strandFrameLayout, entries: [{ binding: 0, resource: { buffer: this.frameBuf } }] });
    this.strandBuf = d.createBuffer({ label: 'strands', size: Renderer.MAX_STRANDS * 2 * 16, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
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

  /** Strand segments from Lattice.strandData (8 floats per strand: a.xyz, w, b.xyz, w). */
  uploadStrands(data: Float32Array<ArrayBuffer>, count: number) {
    this.strandCount = Math.min(count, Renderer.MAX_STRANDS);
    if (this.strandCount > 0) this.device.queue.writeBuffer(this.strandBuf, 0, data, 0, this.strandCount * 8);
  }

  // ---- meshes and materials ---------------------------------------------------------------------------------
  addMesh(mb: MeshBuilder, label: string, color: [number, number, number], opts: { metallic?: number; rough?: number; kind?: number; emissive?: number; castsShadow?: boolean; model?: mat4 } = {}): SimpleDraw {
    const { buffer, count } = mb.toBuffer(this.device, label);
    const material = this.device.createBuffer({ label: label + 'Mat', size: MATERIAL_BYTES, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const draw: SimpleDraw = { buffer, count, material, castsShadow: opts.castsShadow ?? true, color, opts, textures: null, bindGroup: null as unknown as GPUBindGroup };
    draw.bindGroup = this.materialBindGroup(draw);
    this.setMaterial(draw, color, opts);
    this.setModel(draw, opts.model ?? mat4.create());
    this.draws.push(draw);
    return draw;
  }

  private materialBindGroup(draw: SimpleDraw): GPUBindGroup {
    const t = draw.textures;
    return this.device.createBindGroup({ layout: this.simpleMatLayout, entries: [
      { binding: 0, resource: { buffer: draw.material } },
      { binding: 1, resource: (t?.albedo ?? this.whiteTex).createView() },
      { binding: 2, resource: (t?.normal ?? this.flatNormalTex).createView() },
      { binding: 3, resource: (t?.roughness ?? this.whiteTex).createView() },
      { binding: 4, resource: this.texSampler }] });
  }

  setMaterial(draw: SimpleDraw, color: [number, number, number], opts: { metallic?: number; rough?: number; kind?: number; emissive?: number }) {
    draw.color = color; draw.opts = opts;
    const t = draw.textures;
    const flags = t ? (t.albedo ? 1 : 0) | (t.normal ? 2 : 0) | (t.roughness ? 4 : 0) : 0;
    const f = new Float32Array(12);
    f.set([color[0], color[1], color[2], opts.metallic ?? 0, opts.rough ?? 0.5, opts.kind ?? 0, opts.emissive ?? 0, flags,
      t?.tileMm ?? 1, t?.normalStrength ?? 1, t?.roughnessScale ?? 1, 0]);
    this.device.queue.writeBuffer(draw.material, 0, f);
  }

  setModel(draw: SimpleDraw, model: mat4) {
    const inv = mat4.invert(mat4.create(), model) ?? mat4.create();
    const f = new Float32Array(32); f.set(model, 0); f.set(inv, 16);
    this.device.queue.writeBuffer(draw.material, 48, f);
  }

  /** Gives a mesh texture maps (or removes them with null); its colour becomes a tint over the albedo map. */
  setTextures(draw: SimpleDraw, set: TextureSet | null, tint?: [number, number, number]) {
    draw.textures = set;
    draw.bindGroup = this.materialBindGroup(draw);
    this.setMaterial(draw, tint ?? draw.color, draw.opts);
  }

  // ---- texture maps from public/textures ---------------------------------------------------------------------
  /**
   * Loads texture sets for the requested slots from `/textures/<slot>/albedo.jpg` (+ `normal.jpg`, `roughness.jpg`),
   * or the paths given in `/textures/manifest.json` (written by scripts/fetch-textures.mjs), and applies them to the
   * draws as they arrive. Missing files are not an error: the procedural materials stay. A missing normal map is
   * derived from the albedo's luminance.
   */
  async loadTextures(requests: TextureRequest[]) {
    let manifest: Record<string, Record<string, unknown>> = {};
    try {
      const r = await fetch('/textures/manifest.json', { cache: 'no-store' });
      if (r.ok && (r.headers.get('content-type') ?? '').includes('json')) manifest = await r.json();
    } catch { /* no manifest: use the default paths */ }
    const cache = new Map<string, Promise<TextureSet | null>>();
    const load = (slot: string, tileMm: number, normalStrength?: number) => {
      if (!cache.has(slot)) cache.set(slot, this.loadTextureSet(slot, manifest[slot] ?? {}, tileMm, normalStrength));
      return cache.get(slot)!;
    };
    await Promise.all(requests.map(async (req) => {
      let set = await load(req.slot, req.tileMm, req.normalStrength);
      if (!set && req.fallbackSlot) set = await load(req.fallbackSlot, req.tileMm, req.normalStrength);
      if (!set) return;
      this.loadedTextures.push(req.slot);
      // a grey map (a scan to colour, a brushed-steel sheet) is tinted so its mean becomes the slot's colour; a
      // coloured map (a photo of the blue drape itself) is used as it is
      const m = set.meanColor ?? [0.5, 0.5, 0.5];
      const sat = Math.max(...m) - Math.min(...m);
      const lum = Math.max(0.02, 0.2126 * m[0] + 0.7152 * m[1] + 0.0722 * m[2]);
      const base = req.tintIfGrey ?? (req.slot === 'steel' ? [0.62, 0.62, 0.63] as [number, number, number] : null);
      const tint: [number, number, number] = base && sat < 0.08 ? [base[0] / lum, base[1] / lum, base[2] / lum] : [1, 1, 1];
      for (const dr of req.draws) this.setTextures(dr, set, tint);
    }));
  }

  private async fetchImage(url: string): Promise<ImageBitmap | null> {
    try {
      const r = await fetch(url, { cache: 'no-store' });
      if (!r.ok || !(r.headers.get('content-type') ?? '').startsWith('image/')) return null;
      return await createImageBitmap(await r.blob(), { colorSpaceConversion: 'none', premultiplyAlpha: 'none' });
    } catch { return null; }
  }

  private async loadTextureSet(slot: string, entry: Record<string, unknown>, tileMm: number, normalStrength?: number): Promise<TextureSet | null> {
    const path = (key: string) => typeof entry[key] === 'string' ? '/textures/' + (entry[key] as string) : `/textures/${slot}/${key}.jpg`;
    const albedoImg = await this.fetchImage(path('albedo'));
    if (!albedoImg) return null;
    const [normalImg, roughImg] = await Promise.all([this.fetchImage(path('normal')), this.fetchImage(path('roughness'))]);
    const set: TextureSet = { tileMm: typeof entry.tileMm === 'number' ? entry.tileMm : tileMm, normalStrength: normalStrength ?? 1 };
    const pixels = this.readPixels(albedoImg);
    set.meanColor = pixels.mean;
    set.albedo = this.textureFromImage(albedoImg, 'rgba8unorm-srgb', slot + ' albedo');
    set.normal = normalImg ? this.textureFromImage(normalImg, 'rgba8unorm', slot + ' normal') : this.textureFromPixels(this.deriveNormal(pixels.data, pixels.width, pixels.height), pixels.width, pixels.height, 'rgba8unorm', slot + ' normal (derived)');
    if (roughImg) set.roughness = this.textureFromImage(roughImg, 'rgba8unorm', slot + ' roughness');
    return set;
  }

  private readPixels(img: ImageBitmap): { data: Uint8ClampedArray; width: number; height: number; mean: [number, number, number] } {
    const c = new OffscreenCanvas(img.width, img.height);
    const ctx = c.getContext('2d', { willReadFrequently: true })!;
    ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, img.width, img.height).data;
    const sum = [0, 0, 0]; const n = img.width * img.height; const step = Math.max(1, Math.floor(n / 20000)) * 4;
    let cnt = 0;
    for (let i = 0; i < data.length; i += step) { sum[0] += data[i]; sum[1] += data[i + 1]; sum[2] += data[i + 2]; cnt++; }
    const toLin = (v: number) => { const c = v / 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    return { data, width: img.width, height: img.height, mean: [toLin(sum[0] / cnt), toLin(sum[1] / cnt), toLin(sum[2] / cnt)] };
  }

  /** Tangent-space normal map from an albedo: luminance as height, Sobel slopes (wrapping, so tiles stay seamless). */
  private deriveNormal(px: Uint8ClampedArray, w: number, h: number): Uint8ClampedArray {
    const lum = new Float32Array(w * h);
    for (let i = 0; i < w * h; i++) lum[i] = (0.299 * px[i * 4] + 0.587 * px[i * 4 + 1] + 0.114 * px[i * 4 + 2]) / 255;
    const out = new Uint8ClampedArray(w * h * 4);
    const strength = 2.5;
    for (let y = 0; y < h; y++) {
      const ym = ((y - 1 + h) % h) * w, yp = ((y + 1) % h) * w, y0 = y * w;
      for (let x = 0; x < w; x++) {
        const xm = (x - 1 + w) % w, xp = (x + 1) % w;
        const dx = (lum[ym + xp] + 2 * lum[y0 + xp] + lum[yp + xp]) - (lum[ym + xm] + 2 * lum[y0 + xm] + lum[yp + xm]);
        const dy = (lum[yp + xm] + 2 * lum[yp + x] + lum[yp + xp]) - (lum[ym + xm] + 2 * lum[ym + x] + lum[ym + xp]);
        const nx = -dx * strength, ny = -dy * strength; const nz = 1;
        const l = Math.hypot(nx, ny, nz);
        const o = (y0 + x) * 4;
        out[o] = (nx / l * 0.5 + 0.5) * 255; out[o + 1] = (ny / l * 0.5 + 0.5) * 255; out[o + 2] = (nz / l * 0.5 + 0.5) * 255; out[o + 3] = 255;
      }
    }
    return out;
  }

  private solidTexture(rgba: number[], format: GPUTextureFormat): GPUTexture {
    const t = this.device.createTexture({ size: [1, 1], format, usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
    this.device.queue.writeTexture({ texture: t }, new Uint8Array(rgba), { bytesPerRow: 4 }, [1, 1]);
    return t;
  }

  private mipLevels(w: number, h: number) { return Math.floor(Math.log2(Math.max(w, h))) + 1; }

  private textureFromImage(img: ImageBitmap, format: GPUTextureFormat, label: string): GPUTexture {
    const t = this.device.createTexture({ label, size: [img.width, img.height], format, mipLevelCount: this.mipLevels(img.width, img.height),
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT });
    this.device.queue.copyExternalImageToTexture({ source: img }, { texture: t }, [img.width, img.height]);
    this.generateMips(t, format);
    return t;
  }

  private textureFromPixels(px: Uint8ClampedArray, w: number, h: number, format: GPUTextureFormat, label: string): GPUTexture {
    const t = this.device.createTexture({ label, size: [w, h], format, mipLevelCount: this.mipLevels(w, h),
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT });
    this.device.queue.writeTexture({ texture: t }, new Uint8Array(px.buffer as ArrayBuffer, px.byteOffset, px.byteLength), { bytesPerRow: w * 4 }, [w, h]);
    this.generateMips(t, format);
    return t;
  }

  /** Box-filtered mip chain through a tiny blit pipeline (one per format). */
  private generateMips(t: GPUTexture, format: GPUTextureFormat) {
    let pipe = this.mipPipes.get(format);
    if (!pipe) {
      const mod = this.device.createShaderModule({ code: MIP_WGSL });
      pipe = this.device.createRenderPipeline({ layout: 'auto', vertex: { module: mod, entryPoint: 'vs' }, fragment: { module: mod, entryPoint: 'fs', targets: [{ format }] }, primitive: { topology: 'triangle-list' } });
      this.mipPipes.set(format, pipe);
    }
    const enc = this.device.createCommandEncoder({ label: 'mips' });
    for (let level = 1; level < t.mipLevelCount; level++) {
      const bg = this.device.createBindGroup({ layout: pipe.getBindGroupLayout(0), entries: [
        { binding: 0, resource: t.createView({ baseMipLevel: level - 1, mipLevelCount: 1 }) }, { binding: 1, resource: this.linearSampler }] });
      const pass = enc.beginRenderPass({ colorAttachments: [{ view: t.createView({ baseMipLevel: level, mipLevelCount: 1 }), loadOp: 'clear', clearValue: { r: 0, g: 0, b: 0, a: 1 }, storeOp: 'store' }] });
      pass.setPipeline(pipe); pass.setBindGroup(0, bg); pass.draw(3); pass.end();
    }
    this.device.queue.submit([enc.finish()]);
  }

  // ---- frame ---------------------------------------------------------------------------------------------------
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
      if (this.strandCount > 0) { pass.setPipeline(this.strandPipe); pass.setBindGroup(0, this.strandBG); pass.setVertexBuffer(0, this.strandBuf); pass.draw(this.strandCount * 2); }
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
