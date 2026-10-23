import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * Kelly / mosquito hemostat: curved jaws that grasp like forceps and, pushed into a plane and opened,
 * dissect bluntly. Capabilities: grasp (closed jaws coapt, pinch vessels, can be buzzed), blunt dissect
 * (spread: the jaws open inside the tissue and separate low-cohesion planes between the tips; push: the
 * closed tips are advanced along a plane), lift (lattice attachment).
 *
 * Pose: the tips sit `insertMm` below the surface hit along the handle axis (pressure pushes them deeper);
 * `spread` 0..1 opens the jaws from closedMm to spreadMm over about half a second while the spread key is held.
 */
export class HemostatTool {
  readonly tipA = vec3.create();
  readonly tipB = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  /** jaws closed (grasp / push) */
  closed = false;
  /** 0..1 opening while spreading */
  spread = 0;
  /** true while the spread key is held with the jaws inserted */
  spreading = false;
  visible = true;
  openMm = 5.0; closedMm = 0.6; spreadMm = 8.0;
  /** capsule radius of the jaw tips used for coaptation, conduction and dissection, mm */
  jawRadius = 0.7;
  /** how deep the tips are pushed into tissue (mm), from pen pressure */
  insertMm = 1.5;
  readonly modelA = mat4.create();
  readonly modelB = mat4.create();
  armLength = 120;
  /** tip midpoint of the previous frame (for push dissection along the direction of travel) */
  readonly prevMid = vec3.create();
  private hadPrev = false;

  get span() { return this.spreading ? this.closedMm + (this.spreadMm - this.closedMm) * this.spread : this.closed ? this.closedMm : this.openMm; }
  get mid() { return vec3.lerp(vec3.create(), this.tipA, this.tipB, 0.5); }

  /** Advances the spread animation: opens while `hold` and the jaws are inserted, closes again when released. */
  animate(hold: boolean, dt: number) {
    this.spreading = hold && this.closed;
    this.spread = Math.min(1, Math.max(0, this.spread + (this.spreading ? dt / 0.5 : -dt / 0.25)));
  }

  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, pressure = 0.5) {
    if (!hit) { this.visible = false; mat4.copy(this.modelA, HIDDEN); mat4.copy(this.modelB, HIDDEN); this.hadPrev = false; return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    const { x, z } = handFrame(axis);
    // closed tips are pushed into the tissue along the handle axis; open jaws rest on the surface
    this.insertMm = this.closed ? 0.8 + pressure * 2.6 : 0.3;
    const centre = vec3.scaleAndAdd(vec3.create(), hit, axis, -this.insertMm);
    if (this.hadPrev) vec3.copy(this.prevMid, this.mid); else { vec3.copy(this.prevMid, centre); this.hadPrev = true; }
    vec3.scaleAndAdd(this.tipA, centre, z, -this.span / 2);
    vec3.scaleAndAdd(this.tipB, centre, z, this.span / 2);
    // each arm: local y along the arm from its tip to the pivot (box lock), arms converge at the pivot
    const pivot = vec3.scaleAndAdd(vec3.create(), centre, axis, this.armLength * 0.55);
    for (const [tip, model] of [[this.tipA, this.modelA], [this.tipB, this.modelB]] as [vec3, mat4][]) {
      const y = vec3.sub(vec3.create(), pivot, tip); vec3.normalize(y, y);
      const zz = vec3.cross(vec3.create(), x, y); vec3.normalize(zz, zz);
      const xx = vec3.cross(vec3.create(), y, zz);
      frameMatrix(model, xx, y, zz, tip);
    }
  }

  /** Tip travel since the previous frame (mm); push dissection needs the closed tips to be moving through tissue. */
  travel(): number { return this.hadPrev ? vec3.distance(this.prevMid, this.mid) : 0; }
}
