import { SLICE_W, SLICE_H, PhysicsPipeline } from '@/physics/pipeline';

export const SLICE_MODES = ['tissue + state', 'temperature', 'thermal damage Ω', 'water', 'electrical potential', 'Joule heating (log)'];

/** Cross-section inspector: a 2D canvas showing the GPU slice through the electrode tip. */
export class SliceView {
  readonly canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private img: ImageData;
  constructor(parent: HTMLElement, private physics: PhysicsPipeline) {
    this.canvas = document.createElement('canvas');
    this.canvas.width = SLICE_W; this.canvas.height = SLICE_H;
    this.canvas.className = 'slice';
    parent.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d')!;
    this.img = new ImageData(SLICE_W, SLICE_H);
  }
  draw() {
    const p = this.physics;
    this.img.data.set(p.slicePixels);
    this.ctx.putImageData(this.img, 0, 0);
    const c = this.ctx;
    // depth scale (mm) and labels
    const pxPerMm = SLICE_H / p.slice.depthMm;
    c.strokeStyle = 'rgba(255,255,255,0.55)'; c.fillStyle = 'rgba(255,255,255,0.8)'; c.font = '10px ui-monospace, monospace'; c.lineWidth = 1;
    for (let mm = 0; mm <= p.slice.depthMm; mm += 1) {
      const y = mm * pxPerMm;
      c.beginPath(); c.moveTo(0, y); c.lineTo(mm % 5 === 0 ? 10 : 5, y); c.stroke();
      if (mm % 5 === 0) c.fillText(`${mm}`, 12, y + 4);
    }
    const mmPx = SLICE_W / p.slice.widthMm;
    c.beginPath(); c.moveTo(SLICE_W - 10 - mmPx * 5, SLICE_H - 8); c.lineTo(SLICE_W - 10, SLICE_H - 8); c.stroke();
    c.fillText('5 mm', SLICE_W - 10 - mmPx * 5, SLICE_H - 12);
    c.fillText(`section: ${SLICE_MODES[p.slice.mode]}  (C to cycle)`, 4, 12);
  }
}
