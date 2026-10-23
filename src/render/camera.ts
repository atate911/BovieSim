import { mat4, vec3 } from 'gl-matrix';

/** Orbit camera in mm. Right-drag orbit, wheel zoom, middle/shift-drag pan. */
export class OrbitCamera {
  target = vec3.fromValues(0, 0, 0);
  azimuth = -0.35;      // rad, around Y
  elevation = 0.95;     // rad above horizon
  distance = 120;       // mm
  fovY = 35 * Math.PI / 180;
  near = 5; far = 2000;
  readonly view = mat4.create();
  readonly proj = mat4.create();
  readonly viewProj = mat4.create();
  readonly position = vec3.create();
  private dragging: 'orbit' | 'pan' | null = null;
  /** false while an instrument is holding tissue: the wheel then lifts it instead of zooming */
  zoomEnabled = true;
  private last = [0, 0];

  attach(el: HTMLElement) {
    el.addEventListener('pointerdown', (e) => {
      if (e.button === 2 || (e.button === 0 && e.altKey)) { this.dragging = 'orbit'; }
      else if (e.button === 1 || (e.button === 0 && e.shiftKey)) { this.dragging = 'pan'; }
      else return;
      this.last = [e.clientX, e.clientY];
      el.setPointerCapture(e.pointerId);
      e.preventDefault();
    });
    el.addEventListener('pointermove', (e) => {
      if (!this.dragging) return;
      const dx = e.clientX - this.last[0], dy = e.clientY - this.last[1];
      this.last = [e.clientX, e.clientY];
      if (this.dragging === 'orbit') {
        this.azimuth -= dx * 0.005;
        this.elevation = Math.min(1.5, Math.max(0.15, this.elevation + dy * 0.005));
      } else {
        const right = vec3.fromValues(Math.cos(this.azimuth), 0, -Math.sin(this.azimuth));
        const fwd = vec3.fromValues(Math.sin(this.azimuth), 0, Math.cos(this.azimuth));
        const s = this.distance * 0.0012;
        vec3.scaleAndAdd(this.target, this.target, right, -dx * s);
        vec3.scaleAndAdd(this.target, this.target, fwd, -dy * s);
      }
    });
    const end = () => { this.dragging = null; };
    el.addEventListener('pointerup', end); el.addEventListener('pointercancel', end);
    // Ctrl + wheel is the depth peel (main.ts); plain wheel zooms
    el.addEventListener('wheel', (e) => { e.preventDefault(); if (e.ctrlKey || !this.zoomEnabled) return; this.distance = Math.min(600, Math.max(25, this.distance * Math.exp(e.deltaY * 0.001))); }, { passive: false });
    el.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  update(aspect: number) {
    const ce = Math.cos(this.elevation);
    vec3.set(this.position,
      this.target[0] + this.distance * ce * Math.sin(this.azimuth),
      this.target[1] + this.distance * Math.sin(this.elevation),
      this.target[2] + this.distance * ce * Math.cos(this.azimuth));
    mat4.lookAt(this.view, this.position, this.target, [0, 1, 0]);
    mat4.perspectiveZO(this.proj, this.fovY, aspect, this.near, this.far);
    mat4.multiply(this.viewProj, this.proj, this.view);
  }

  /** World-space ray through a pixel (normalised device coords in [-1,1]). */
  ray(ndcX: number, ndcY: number): { origin: vec3; dir: vec3 } {
    const inv = mat4.invert(mat4.create(), this.viewProj)!;
    const p0 = vec3.transformMat4(vec3.create(), [ndcX, ndcY, 0], inv);
    const p1 = vec3.transformMat4(vec3.create(), [ndcX, ndcY, 1], inv);
    const dir = vec3.normalize(vec3.create(), vec3.sub(vec3.create(), p1, p0));
    return { origin: vec3.clone(this.position), dir };
  }
}
