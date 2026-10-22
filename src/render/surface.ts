import surfaceWgsl from './wgsl/surface.wgsl?raw';
import { Volume, VOLUME_INFO_WGSL } from '@/core/volume';
import { createShader } from '@/render/gpu';

export const VERTEX_STRIDE = 32;

/** GPU surface extraction (marching tetrahedra) producing a vertex-pulled triangle soup. */
export class SurfaceExtractor {
  readonly vertexBuffer: GPUBuffer;
  readonly counter: GPUBuffer;
  readonly indirect: GPUBuffer;
  readonly capacity: number;
  private pipeline: GPUComputePipeline;
  private finalizePipeline: GPUComputePipeline;
  private bindGroup: GPUBindGroup;
  private readback: GPUBuffer;
  lastVertexCount = 0;
  dirty = true;

  constructor(private device: GPUDevice, private volume: Volume, capacityVertices = 2_400_000) {
    const gpu = volume.gpu!;
    this.capacity = Math.floor(capacityVertices / 3) * 3;
    this.vertexBuffer = device.createBuffer({ label: 'surfaceVerts', size: this.capacity * VERTEX_STRIDE, usage: GPUBufferUsage.STORAGE });
    this.counter = device.createBuffer({ label: 'surfaceCounter', size: 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | GPUBufferUsage.COPY_SRC });
    this.indirect = device.createBuffer({ label: 'surfaceIndirect', size: 16, usage: GPUBufferUsage.INDIRECT | GPUBufferUsage.COPY_DST });
    this.readback = device.createBuffer({ label: 'surfaceCountReadback', size: 16, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
    const module = createShader(device, 'surface', VOLUME_INFO_WGSL + surfaceWgsl);
    const layout = device.createBindGroupLayout({
      entries: [
        { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform' } },
        ...[1, 2, 3, 4, 5].map((b) => ({ binding: b, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' as GPUBufferBindingType } })),
        { binding: 6, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
        { binding: 7, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
      ],
    });
    const pl = device.createPipelineLayout({ bindGroupLayouts: [layout] });
    this.pipeline = device.createComputePipeline({ label: 'surface', layout: pl, compute: { module, entryPoint: 'main' } });
    this.finalizePipeline = device.createComputePipeline({ label: 'surfaceFinalize', layout: pl, compute: { module, entryPoint: 'finalize' } });
    this.bindGroup = device.createBindGroup({
      layout,
      entries: [
        { binding: 0, resource: { buffer: gpu.info } },
        { binding: 1, resource: { buffer: gpu.occupancy } },
        { binding: 2, resource: { buffer: gpu.cell } },
        { binding: 3, resource: { buffer: gpu.temp } },
        { binding: 4, resource: { buffer: gpu.water } },
        { binding: 5, resource: { buffer: gpu.omega } },
        { binding: 6, resource: { buffer: this.vertexBuffer } },
        { binding: 7, resource: { buffer: this.counter } },
      ],
    });
  }

  /** Encodes the extraction. Indirect args: [count, 1, 0, 0]; count copied from the counter. */
  encode(enc: GPUCommandEncoder) {
    const { nx, ny, nz } = this.volume.dims;
    this.device.queue.writeBuffer(this.counter, 0, new Uint32Array([0, this.capacity, 0, 0]));
    this.device.queue.writeBuffer(this.indirect, 0, new Uint32Array([0, 1, 0, 0]));
    const pass = enc.beginComputePass({ label: 'surface' });
    pass.setPipeline(this.pipeline);
    pass.setBindGroup(0, this.bindGroup);
    pass.dispatchWorkgroups(Math.ceil((nx - 1) / 4), Math.ceil((ny - 1) / 4), Math.ceil((nz - 1) / 4));
    pass.setPipeline(this.finalizePipeline);
    pass.dispatchWorkgroups(1);
    pass.end();
    // clamped vertex count -> indirect args
    enc.copyBufferToBuffer(this.counter, 0, this.indirect, 0, 4);
    enc.copyBufferToBuffer(this.counter, 0, this.readback, 0, 16);
    this.dirty = false;
  }

  /** Reads the last vertex count (async, for HUD/overflow detection). */
  async readCount(): Promise<number> {
    if (this.readback.mapState !== 'unmapped') return this.lastVertexCount;
    try {
      await this.readback.mapAsync(GPUMapMode.READ);
      const c = new Uint32Array(this.readback.getMappedRange())[0];
      this.readback.unmap();
      this.lastVertexCount = c;
    } catch { /* buffer in use */ }
    return this.lastVertexCount;
  }
}
