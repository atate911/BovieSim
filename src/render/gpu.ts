export interface GpuContext {
  adapter: GPUAdapter;
  device: GPUDevice;
  canvas: HTMLCanvasElement;
  /** null when running headless (no presentable swapchain); the final image is read back instead */
  context: GPUCanvasContext | null;
  format: GPUTextureFormat;
  headless: boolean;
}

export async function initWebGPU(canvas: HTMLCanvasElement, headless: boolean): Promise<GpuContext> {
  if (!('gpu' in navigator)) throw new Error('WebGPU is not available in this browser. Use Chrome 113+, Edge 113+, or Safari 18+.');
  const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
  if (!adapter) throw new Error('No WebGPU adapter found. Check that hardware acceleration is enabled.');
  const limits = adapter.limits;
  const device = await adapter.requestDevice({
    requiredLimits: {
      maxStorageBufferBindingSize: Math.min(limits.maxStorageBufferBindingSize, 1 << 30),
      maxBufferSize: Math.min(limits.maxBufferSize, 1 << 30),
      maxComputeWorkgroupsPerDimension: limits.maxComputeWorkgroupsPerDimension,
    },
  });
  device.lost.then((info) => { console.error('WebGPU device lost:', info.reason, info.message); });
  device.addEventListener('uncapturederror', (e) => console.error('WebGPU error:', (e as GPUUncapturedErrorEvent).error.message));
  const format: GPUTextureFormat = headless ? 'rgba8unorm' : navigator.gpu.getPreferredCanvasFormat();
  let context: GPUCanvasContext | null = null;
  if (!headless) {
    context = canvas.getContext('webgpu');
    if (!context) throw new Error('Could not create a WebGPU canvas context.');
    context.configure({ device, format, alphaMode: 'opaque' });
  }
  return { adapter, device, canvas, context, format, headless };
}

/** Creates a shader module and logs any compiler messages (WGSL errors are otherwise silent until pipeline use). */
export function createShader(device: GPUDevice, label: string, code: string): GPUShaderModule {
  const module = device.createShaderModule({ label, code });
  module.getCompilationInfo().then((info) => {
    for (const m of info.messages) {
      const line = code.split('\n')[m.lineNum - 1] ?? '';
      const text = `[wgsl ${label}] ${m.type} at ${m.lineNum}:${m.linePos}: ${m.message}\n    ${line.trim()}`;
      if (m.type === 'error') console.error(text); else console.warn(text);
    }
  });
  return module;
}
