import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/**
 * Folded 4x4 gauze held in the fingers (or a peanut sponge). Pressed on the field it soaks up liquid
 * blood within its footprint and tamponades the surface beneath it; dragged with pressure it wipes,
 * taking clot and stain with it but disturbing the capillaries so the surface oozes again. It holds a
 * limited volume; `U` discards it for a fresh one.
 */
export class GauzeTool {
  readonly centre = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  readonly velocity = vec3.create();
  /** 0..1 how hard the gauze is pressed (left button + pen pressure) */
  pressure = 0;
  visible = true;
  /** footprint radius, mm (a folded 4x4 pressed down covers about a 12 mm patch) */
  radius = 6;
  /** capacity of a folded 4x4, microlitres */
  capacityUl = 8000;
  soakedUl = 0;
  discarded = 0;
  readonly model = mat4.create();
  private lastCentre = vec3.create();
  private hadLast = false;

  get saturation() { return Math.min(1, this.soakedUl / this.capacityUl); }
  get saturated() { return this.soakedUl >= this.capacityUl; }
  /** absorption capability left: 0 when saturated */
  get absorb() { return this.saturated ? 0 : 1; }

  fresh() { this.soakedUl = 0; this.discarded++; }

  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, pressed: boolean, penPressure: number, dt: number) {
    if (!hit) { this.visible = false; mat4.copy(this.model, HIDDEN); this.pressure = 0; this.hadLast = false; vec3.set(this.velocity, 0, 0, 0); return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt);
    vec3.copy(this.axis, axis);
    // the pad sits on the surface, pressed in a little
    this.pressure = pressed ? Math.max(0.3, penPressure) : 0;
    vec3.scaleAndAdd(this.centre, hit, axis, pressed ? -0.4 * this.pressure : 0.8);
    if (this.hadLast && dt > 0) { vec3.sub(this.velocity, this.centre, this.lastCentre); vec3.scale(this.velocity, this.velocity, 1 / dt); }
    else vec3.set(this.velocity, 0, 0, 0);
    vec3.copy(this.lastCentre, this.centre); this.hadLast = true;
    const { x, z } = handFrame(axis);
    frameMatrix(this.model, x, axis, z, this.centre);
  }
}
