import { mat4, vec3 } from 'gl-matrix';
import { handAxis, handFrame, frameMatrix, HIDDEN } from './instrument';

/** Skin hook: attaches to the nearest lattice node on press and drags it with the cursor. */
export class HookTool {
  readonly tip = vec3.create();
  readonly axis = vec3.fromValues(0, 1, 0);
  readonly model = mat4.create();
  visible = true;
  /** attached lattice node, -1 when free */
  node = -1;
  place(hit: vec3 | null, cameraPos: vec3, tilt: { x: number; y: number } | null, dragTarget: vec3 | null, leftHand = false) {
    const anchor = dragTarget ?? hit;
    if (!anchor) { this.visible = false; mat4.copy(this.model, HIDDEN); return; }
    this.visible = true;
    const axis = handAxis(anchor, cameraPos, tilt, leftHand);
    vec3.copy(this.axis, axis);
    vec3.copy(this.tip, anchor);
    const { x, z } = handFrame(axis);
    frameMatrix(this.model, x, axis, z, this.tip);
  }
}
