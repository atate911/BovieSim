import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * #15 scalpel on a #3 handle: a mechanical cut with no thermal effect. The blade is a thin plate in the
 * plane of the handle axis and the direction of travel; pen pressure (or the button) sets how deep the
 * belly sits in the tissue. Each frame the blade sweeps from its previous pose to the current one and the
 * physics removes the tissue it passed through (`mechCut`, sharp mode).
 */
export class ScalpelTool {
  /** deepest point of the blade (mm) */
  readonly tip = vec3.create();
  /** unit axis from the tip toward the handle */
  readonly axis = vec3.fromValues(0, 1, 0);
  /** direction along the cutting edge (fore and aft in the plane of the blade) */
  readonly wdir = vec3.fromValues(1, 0, 0);
  /** blade face normal */
  readonly ndir = vec3.fromValues(0, 0, 1);
  /** previous frame's blade centre, for the swept cut; valid when `sweeping` */
  readonly prevCentre = vec3.create();
  sweeping = false;
  /** blade is in the tissue (button held / pen on the tablet) */
  cutting = false;
  pressure = 0.5;
  visible = true;
  readonly model = mat4.create();
  /** belly half length along the edge, half thickness (mm) */
  halfWidth = 1.6; halfThickness = 0.15;
  private lastHit: vec3 | null = null;

  /** depth of the blade in the tissue from pressure: 0.5 mm (feather) to 5 mm (through dermis into fat) */
  get depth() { return 0.5 + this.pressure * 4.5; }
  /** blade half length along the axis, for the cut box: the part in the tissue plus a little above the surface */
  get halfLength() { return (this.depth + 1.0) / 2; }
  /** centre of the cut box (mm) */
  get centre() { return vec3.scaleAndAdd(vec3.create(), this.tip, this.axis, this.halfLength); }

  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, cutting: boolean) {
    if (!hit) { this.visible = false; this.cutting = false; this.sweeping = false; this.lastHit = null; mat4.copy(this.model, HIDDEN); return; }
    this.visible = true;
    const wasCutting = this.cutting && this.lastHit !== null;
    const prev = wasCutting ? this.centre : null;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    const { x, z } = handFrame(axis);
    // blade plane follows the direction of travel so the edge leads; stationary, keep the last orientation
    if (this.lastHit) {
      const travel = vec3.sub(vec3.create(), hit, this.lastHit);
      vec3.scaleAndAdd(travel, travel, axis, -vec3.dot(travel, axis));
      if (vec3.length(travel) > 0.4) {
        vec3.normalize(travel, travel);
        const n = vec3.cross(vec3.create(), axis, travel);
        if (vec3.length(n) > 1e-3) { vec3.normalize(n, n); vec3.copy(this.ndir, n); }
      }
    } else { vec3.copy(this.ndir, z); }
    // keep the frame orthonormal with the (possibly new) axis
    vec3.scaleAndAdd(this.ndir, this.ndir, axis, -vec3.dot(this.ndir, axis));
    if (vec3.length(this.ndir) < 1e-3) vec3.copy(this.ndir, z);
    vec3.normalize(this.ndir, this.ndir);
    vec3.cross(this.wdir, this.ndir, axis); vec3.normalize(this.wdir, this.wdir);
    this.cutting = cutting;
    // tip below the surface by the depth while cutting; resting on it otherwise
    vec3.scaleAndAdd(this.tip, hit, axis, cutting ? -this.depth : 0.2);
    this.sweeping = cutting && prev !== null;
    if (prev) vec3.copy(this.prevCentre, prev); else vec3.copy(this.prevCentre, this.centre);
    this.lastHit = vec3.clone(hit);
    // mesh frame: +Y along the handle, +Z the blade face normal, origin at the blade tip
    void x;
    frameMatrix(this.model, this.wdir, axis, this.ndir, this.tip);
  }
}
