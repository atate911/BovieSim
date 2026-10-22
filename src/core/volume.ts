import { Tissue, TissueState } from './tissueProperties';

/**
 * Voxel volume. Axes: i -> world X (mm), j -> world Z (mm, toward viewer), k -> world Y (up).
 * World origin is the centre of the nominal skin surface; tissue lies at Y <= 0.
 *
 * `cell` packs: bits 0-7 tissue type, 8-15 state, 16-23 aux (vessel radius in 0.05 mm units,
 * or fat lobule id), 24-31 flags.
 */
export interface VolumeDims {
  nx: number; ny: number; nz: number;
  /** voxel edge, mm */
  h: number;
  /** number of air voxel layers above the nominal skin surface */
  airLayers: number;
}

export const FLAG_DERMAL_PLEXUS = 1 << 24;
export const FLAG_FIBRE_X = 1 << 25; // fibre direction hint for shading

export function packCell(type: Tissue, state: TissueState = TissueState.Normal, aux = 0, flags = 0): number {
  return ((type & 0xff) | ((state & 0xff) << 8) | ((aux & 0xff) << 16) | flags) >>> 0;
}
export function cellType(c: number): Tissue { return (c & 0xff) as Tissue; }
export function cellState(c: number): TissueState { return ((c >>> 8) & 0xff) as TissueState; }
export function cellAux(c: number): number { return (c >>> 16) & 0xff; }

export interface VolumeBuffers {
  cell: GPUBuffer;
  occupancy: GPUBuffer;
  temp: GPUBuffer;
  tempNext: GPUBuffer;
  water: GPUBuffer;
  omega: GPUBuffer;
  potential: GPUBuffer;
  potentialNext: GPUBuffer;
  /** per-voxel Joule heating W/m3 from the last field solve */
  heat: GPUBuffer;
  /** VolumeInfo uniform */
  info: GPUBuffer;
}

export class Volume {
  readonly dims: VolumeDims;
  readonly count: number;
  cell: Uint32Array<ArrayBuffer>;
  occupancy: Float32Array<ArrayBuffer>;
  temp: Float32Array<ArrayBuffer>;
  water: Float32Array<ArrayBuffer>;
  omega: Float32Array<ArrayBuffer>;
  gpu: VolumeBuffers | null = null;

  constructor(dims: VolumeDims) {
    this.dims = dims;
    this.count = dims.nx * dims.ny * dims.nz;
    this.cell = new Uint32Array(this.count);
    this.occupancy = new Float32Array(this.count);
    this.temp = new Float32Array(this.count).fill(37);
    this.water = new Float32Array(this.count);
    this.omega = new Float32Array(this.count);
  }

  index(i: number, j: number, k: number): number {
    return i + this.dims.nx * (j + this.dims.ny * k);
  }

  /** World (mm) position of the voxel centre. */
  voxelToWorld(i: number, j: number, k: number): [number, number, number] {
    const { nx, ny, nz, h, airLayers } = this.dims;
    return [(i + 0.5 - nx / 2) * h, (k + 0.5 - (nz - airLayers)) * h, (j + 0.5 - ny / 2) * h];
  }

  /** Fractional voxel coordinates of a world (mm) position. */
  worldToVoxel(x: number, y: number, z: number): [number, number, number] {
    const { nx, ny, nz, h, airLayers } = this.dims;
    return [x / h + nx / 2 - 0.5, z / h + ny / 2 - 0.5, y / h + (nz - airLayers) - 0.5];
  }

  /** Skin surface nominal height (mm) in voxel k units. */
  get surfaceK(): number { return this.dims.nz - this.dims.airLayers; }

  /** Physical size of the bed in mm (x, y-depth, z). */
  get sizeMm(): [number, number, number] {
    const { nx, ny, nz, h, airLayers } = this.dims;
    return [nx * h, (nz - airLayers) * h, ny * h];
  }

  /** VolumeInfo uniform: nx, ny, nz, airLayers, h(mm), h(m), 0, 0 */
  infoData(): ArrayBuffer {
    const buf = new ArrayBuffer(32);
    const u = new Uint32Array(buf, 0, 4);
    const f = new Float32Array(buf, 16, 4);
    u[0] = this.dims.nx; u[1] = this.dims.ny; u[2] = this.dims.nz; u[3] = this.dims.airLayers;
    f[0] = this.dims.h; f[1] = this.dims.h * 1e-3; f[2] = 0; f[3] = 0;
    return buf;
  }

  upload(device: GPUDevice): VolumeBuffers {
    const bytes = this.count * 4;
    const usage = GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | GPUBufferUsage.COPY_SRC;
    const mk = (label: string) => device.createBuffer({ label, size: bytes, usage });
    const cell = mk('cell'); device.queue.writeBuffer(cell, 0, this.cell);
    const occupancy = mk('occupancy'); device.queue.writeBuffer(occupancy, 0, this.occupancy);
    const temp = mk('temp'); device.queue.writeBuffer(temp, 0, this.temp);
    const tempNext = mk('tempNext'); device.queue.writeBuffer(tempNext, 0, this.temp);
    const water = mk('water'); device.queue.writeBuffer(water, 0, this.water);
    const omega = mk('omega'); device.queue.writeBuffer(omega, 0, this.omega);
    const potential = mk('potential');
    const potentialNext = mk('potentialNext');
    const heat = mk('heat');
    const info = device.createBuffer({ label: 'volumeInfo', size: 32, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(info, 0, this.infoData());
    this.gpu = { cell, occupancy, temp, tempNext, water, omega, potential, potentialNext, heat, info };
    return this.gpu;
  }
}

export const VOLUME_INFO_WGSL = /* wgsl */ `
struct VolumeInfo {
  nx: u32, ny: u32, nz: u32, airLayers: u32,
  hMm: f32, hM: f32, pad0: f32, pad1: f32,
};
fn vidx(info: VolumeInfo, i: u32, j: u32, k: u32) -> u32 { return i + info.nx * (j + info.ny * k); }
fn cellType(c: u32) -> u32 { return c & 0xffu; }
fn cellState(c: u32) -> u32 { return (c >> 8u) & 0xffu; }
fn cellAux(c: u32) -> u32 { return (c >> 16u) & 0xffu; }
fn packCellState(c: u32, s: u32) -> u32 { return (c & 0xffff00ffu) | ((s & 0xffu) << 8u); }
fn voxelToWorld(info: VolumeInfo, p: vec3f) -> vec3f {
  // p is fractional voxel coords (i, j, k) at voxel centre offsets; returns mm
  return vec3f((p.x + 0.5 - f32(info.nx) * 0.5) * info.hMm,
               (p.z + 0.5 - f32(info.nz - info.airLayers)) * info.hMm,
               (p.y + 0.5 - f32(info.ny) * 0.5) * info.hMm);
}
`;
