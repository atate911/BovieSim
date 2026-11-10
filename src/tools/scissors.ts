import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * Metzenbaum scissors: the dissecting scissors, with two capabilities.
 *  - cut (mechanical): a snip divides the sheet of tissue between the open tips along the blades, no heat
 *    (`mechCut`): what a septum, a strand or a controlled vessel across a plane needs;
 *  - blunt dissect: held closed in the tissue and opened, the tips spread it apart with the same rules as the
 *    hemostat (`dissect`, spread): loose planes part, vessels and nerves bridge the opening until torn or cut.
 * A click snips; holding the button pushes the closed tips in (pen pressure sets how far) and the spread key opens
 * them. The blades lie in the plane of the handle axis and the horizontal frame direction.
 */
export class ScissorsTool {
  /** point between the two tips (mm) */
  readonly tip = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  /** opening direction (the tips separate along it) */
  readonly wdir = vec3.fromValues(1, 0, 0);
  /** blade plane normal (the pivot axis) */
  readonly ndir = vec3.fromValues(0, 0, 1);
  readonly tipA = vec3.create();
  readonly tipB = vec3.create();
  readonly modelA = mat4.create();
  readonly modelB = mat4.create();
  visible = true;
  /** tip separation (mm): 0 closed, restMm ready to snip, up to spreadMm when spreading */
  open = 0;
  restMm = 3; spreadMm = 8;
  /** blade length from the tips to the pivot (mm) */
  jawLength = 20;
  /** shanks from the pivot to the rings */
  shankLength = 95;
  /** depth of the tips in the tissue (mm) */
  insert = 0.3;
  /** tips held closed in the tissue (button down) */
  closed = false;
  /** opening while the spread key is held with the tips in the tissue */
  spreading = false;
  /** capsule radius of the tips for blunt dissection, mm */
  jawRadius = 0.5;

  get mid() { return vec3.clone(this.tip); }
  /** half length along the axis of the sheet a snip divides: from the tips up to just above the surface */
  get halfLength() { return (this.insert + 0.5) / 2; }
  /** centre of that sheet */
  get centre() { return vec3.scaleAndAdd(vec3.create(), this.tip, this.axis, this.halfLength); }

  /**
   * @param held the button is down: closed tips pushed into the tissue
   * @param spread the spread key is held as well: the tips open inside the tissue
   * @param pressure 0..1 (pen pressure): how far the tips go in, 1 to 6 mm
   */
  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, held: boolean, spread: boolean, pressure: number, dt: number) {
    if (!hit) { this.visible = false; this.open = 0; this.closed = false; this.spreading = false; mat4.copy(this.modelA, HIDDEN); mat4.copy(this.modelB, HIDDEN); return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    const { x, z } = handFrame(axis);
    vec3.copy(this.wdir, z); vec3.copy(this.ndir, x);
    this.closed = held; this.spreading = held && spread;
    // resting, the open tips straddle the tissue at the surface; held, they close and go in; spreading, they open again
    const target = this.spreading ? this.spreadMm : held ? 0 : this.restMm;
    const rate = (this.spreading ? this.spreadMm / 0.5 : this.spreadMm / 0.2) * dt;
    this.open += Math.min(Math.max(target - this.open, -rate), rate);
    this.insert = held ? 1 + pressure * 5 : 1.2;
    vec3.scaleAndAdd(this.tip, hit, axis, -this.insert);
    vec3.scaleAndAdd(this.tipA, this.tip, this.wdir, -this.open / 2);
    vec3.scaleAndAdd(this.tipB, this.tip, this.wdir, this.open / 2);
    // each blade: local +Y from its tip to the pivot; the blades converge at the pivot
    const pivot = vec3.scaleAndAdd(vec3.create(), this.tip, axis, this.jawLength);
    for (const [tp, model] of [[this.tipA, this.modelA], [this.tipB, this.modelB]] as [vec3, mat4][]) {
      const y = vec3.sub(vec3.create(), pivot, tp); vec3.normalize(y, y);
      const zz = vec3.cross(vec3.create(), this.ndir, y); vec3.normalize(zz, zz);
      const xx = vec3.cross(vec3.create(), y, zz);
      frameMatrix(model, xx, y, zz, tp);
    }
  }
}
