import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * Adson forceps: two arms pivoting at the proximal end, jaw tips at the hit point. Capabilities:
 * grasp (closed jaws coapt the tissue between the tips; pinches vessels), conduct (buzzing: the
 * jaws become the electrode), lift (Sprint 4: attaches to the mechanics lattice).
 */
export class ForcepsTool {
  readonly tipA = vec3.create();
  readonly tipB = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  closed = false;
  toothed = true;
  visible = true;
  /** jaw tip separation when open / closed, mm */
  openMm = 4.0; closedMm = 0.5;
  /** capsule radius used for coaptation and conduction, mm */
  jawRadius = 0.6;
  readonly modelA = mat4.create();
  readonly modelB = mat4.create();
  armLength = 95;

  get span() { return this.closed ? this.closedMm : this.openMm; }
  get mid() { return vec3.lerp(vec3.create(), this.tipA, this.tipB, 0.5); }

  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, penetration = 0.3) {
    if (!hit) { this.visible = false; mat4.copy(this.modelA, HIDDEN); mat4.copy(this.modelB, HIDDEN); return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    const { x, z } = handFrame(axis);
    // jaws straddle the horizontal direction z; tips pushed slightly into tissue
    const centre = vec3.scaleAndAdd(vec3.create(), hit, axis, -penetration);
    vec3.scaleAndAdd(this.tipA, centre, z, -this.span / 2);
    vec3.scaleAndAdd(this.tipB, centre, z, this.span / 2);
    // each arm: local y along the arm from its tip to the pivot; arms converge at the pivot
    const pivot = vec3.scaleAndAdd(vec3.create(), centre, axis, this.armLength);
    for (const [tip, model] of [[this.tipA, this.modelA], [this.tipB, this.modelB]] as [vec3, mat4][]) {
      const y = vec3.sub(vec3.create(), pivot, tip); vec3.normalize(y, y);
      const zz = vec3.cross(vec3.create(), x, y); vec3.normalize(zz, zz);
      const xx = vec3.cross(vec3.create(), y, zz);
      frameMatrix(model, xx, y, zz, tip);
    }
  }
}
