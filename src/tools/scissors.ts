import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * Metzenbaum scissors: two capabilities.
 *  - cut (mechanical): a snip divides the tissue between the open tips along the blades, no heat;
 *  - blunt dissect: inserted closed and opened, the tips spread the tissue apart. Fat and the plane around a
 *    capsule yield; septa, fasciae, skin and the capsule itself resist (they must be cut); small vessels tear,
 *    larger ones and nerves are stretched (`mechCut`, blunt mode).
 * The jaws lie in the plane of the handle axis and the horizontal frame direction; `open` is the tip separation.
 */
export class ScissorsTool {
  /** point between the two tips (mm) */
  readonly tip = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  /** opening direction (tips separate along it) */
  readonly wdir = vec3.fromValues(1, 0, 0);
  /** blade plane normal (pivot axis) */
  readonly ndir = vec3.fromValues(0, 0, 1);
  readonly tipA = vec3.create();
  readonly tipB = vec3.create();
  readonly modelA = mat4.create();
  readonly modelB = mat4.create();
  visible = true;
  /** tip separation (mm): 0 closed, up to maxOpen */
  open = 0;
  maxOpen = 7;
  /** blade length from the tips to the pivot (mm); the inserted part is `insert` */
  jawLength = 20;
  /** handles from the pivot to the rings */
  shankLength = 95;
  /** depth of the tips in the tissue while working (mm) */
  insert = 2;
  pressure = 0.5;
  /** snip requested this frame (button pressed): cut the sheet between the open tips */
  snip = false;
  /** spreading this frame */
  spreading = false;
  /** blunt wedge rounding radius (mm) */
  spreadRadius = 0.7;

  get mid() { return vec3.clone(this.tip); }
  /** inserted jaw half length along the axis (the cut sheet / spread wedge extends from the tips up to the surface) */
  get halfLength() { return (this.insert + 0.5) / 2; }
  get centre() { return vec3.scaleAndAdd(vec3.create(), this.tip, this.axis, this.halfLength); }

  /**
   * @param action 'none' hover with closed tips, 'snip' cut once (tips shown open then closing), 'spread' open the tips
   * @param dt frame time (s) for the spread ramp
   */
  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, action: 'none' | 'snip' | 'spread', dt: number) {
    this.snip = false; this.spreading = false;
    if (!hit) { this.visible = false; this.open = 0; mat4.copy(this.modelA, HIDDEN); mat4.copy(this.modelB, HIDDEN); return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    const { x, z } = handFrame(axis);
    vec3.copy(this.wdir, z); vec3.copy(this.ndir, x);
    // tips in the tissue by the insertion depth (pressure: 1 to 8 mm) while working, resting on it otherwise
    this.insert = 1 + this.pressure * 7;
    const working = action !== 'none';
    vec3.scaleAndAdd(this.tip, hit, axis, working ? -this.insert : 0.3);
    if (action === 'spread') { this.open = Math.min(this.maxOpen, this.open + this.maxOpen * dt / 0.4); this.spreading = true; }
    else if (action === 'snip') { this.open = Math.max(this.open, 3); this.snip = true; }
    else this.open = Math.max(0, this.open - this.maxOpen * dt / 0.25);
    vec3.scaleAndAdd(this.tipA, this.tip, this.wdir, -this.open / 2);
    vec3.scaleAndAdd(this.tipB, this.tip, this.wdir, this.open / 2);
    // each blade: local +Y from its tip to the pivot; blades converge at the pivot
    const pivot = vec3.scaleAndAdd(vec3.create(), this.tip, axis, this.jawLength);
    for (const [tp, model] of [[this.tipA, this.modelA], [this.tipB, this.modelB]] as [vec3, mat4][]) {
      const y = vec3.sub(vec3.create(), pivot, tp); vec3.normalize(y, y);
      const zz = vec3.cross(vec3.create(), this.ndir, y); vec3.normalize(zz, zz);
      const xx = vec3.cross(vec3.create(), y, zz);
      frameMatrix(model, xx, y, zz, tp);
    }
  }
}
