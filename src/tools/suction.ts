import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/** Frazier suction: tip radius 1.5 mm; drains blood within 2.5 mm while the button is held. */
export class SuctionTool {
  readonly tip = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  on = false;
  visible = true;
  readonly model = mat4.create();
  tipRadius = 1.5;
  drainRadius = 2.5;
  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, leftHand = false) {
    if (!hit) { this.visible = false; mat4.copy(this.model, HIDDEN); return; }
    this.visible = true;
    const axis = handAxis(hit, cameraPos, tilt, leftHand);
    vec3.copy(this.axis, axis);
    vec3.scaleAndAdd(this.tip, hit, axis, 0.5);
    const { x, z } = handFrame(axis);
    frameMatrix(this.model, x, axis, z, this.tip);
  }
}
