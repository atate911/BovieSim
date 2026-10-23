import { SLICE_W, SLICE_H, PhysicsPipeline } from '@/physics/pipeline';

export const SLICE_MODES = ['tissue + state', 'temperature', 'thermal damage Ω', 'water', 'electrical potential', 'Joule heating (log)'];

/** Cross-section inspector: a 2D canvas showing the GPU slice through the electrode tip (drawn at SLICE_W x SLICE_H, shown at 480 x 300 CSS px). */
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
    const k = SLICE_W / 480; // canvas pixels per CSS pixel
    // depth scale (mm) and labels
    const pxPerMm = SLICE_H / p.slice.depthMm;
    c.strokeStyle = 'rgba(255,255,255,0.55)'; c.fillStyle = 'rgba(255,255,255,0.8)'; c.font = `${Math.round(10 * k)}px ui-monospace, monospace`; c.lineWidth = k;
    for (let mm = 0; mm <= p.slice.depthMm; mm += 1) {
      const y = mm * pxPerMm;
      c.beginPath(); c.moveTo(0, y); c.lineTo((mm % 5 === 0 ? 10 : 5) * k, y); c.stroke();
      if (mm % 5 === 0) c.fillText(`${mm}`, 12 * k, y + 4 * k);
    }
    const mmPx = SLICE_W / p.slice.widthMm;
    c.beginPath(); c.moveTo(SLICE_W - 10 * k - mmPx * 5, SLICE_H - 8 * k); c.lineTo(SLICE_W - 10 * k, SLICE_H - 8 * k); c.stroke();
    c.fillText('5 mm', SLICE_W - 10 * k - mmPx * 5, SLICE_H - 12 * k);
    c.fillText(`section: ${SLICE_MODES[p.slice.mode]}  (C to cycle)`, 4 * k, 12 * k);
    // depth-peel plane of the 3D view (the kernel draws the dashed line): label it
    if (p.slice.peelY < 1e8) {
      const y = (p.slice.o[1] - p.slice.peelY) * pxPerMm;
      if (y > 0 && y < SLICE_H) { c.fillStyle = 'rgba(102,255,230,0.95)'; c.fillText(`peel ${(-p.slice.peelY).toFixed(1)} mm`, SLICE_W - 78 * k, y - 3 * k); }
    }
  }
}
