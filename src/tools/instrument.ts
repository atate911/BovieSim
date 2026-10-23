import { mat4, vec3 } from 'gl-matrix';

/**
 * Instruments. The dominant hand (pointer) holds any of them; the non-dominant hand holds the
 * retracting set (`HAND_B_ORDER`) and is driven while Space is held, then latched (see `hands.ts`).
 */
export type ToolKind = 'pencil' | 'scalpel' | 'scissors' | 'forceps' | 'suction' | 'hook';
export const TOOL_ORDER: ToolKind[] = ['pencil', 'scalpel', 'scissors', 'forceps', 'suction', 'hook'];
export const TOOL_LABEL: Record<ToolKind, string> = {
  pencil: 'Bovie pencil', scalpel: '#15 scalpel', scissors: 'Metzenbaum scissors', forceps: 'Adson forceps', suction: 'Frazier suction', hook: 'Skin hook',
};
/** what the non-dominant hand can hold ('none' parks it off the field) */
export type HandBKind = 'none' | 'forceps' | 'hook' | 'suction';
export const HAND_B_ORDER: HandBKind[] = ['none', 'forceps', 'hook', 'suction'];

/**
 * Shared hand-held instrument pose: the handle runs toward the surgeon's right hand (the
 * to-camera direction rotated 55 deg clockwise, 38 deg above the surface) unless pen tilt is given.
 * The non-dominant hand mirrors it (rotated the other way), so the two instruments do not cross.
 */
export function handAxis(hit: vec3, cameraPos: vec3, tilt: { x: number; y: number } | null, leftHand = false): vec3 {
  let axis: vec3;
  if (tilt) {
    const tx = tilt.x * Math.PI / 180, ty = tilt.y * Math.PI / 180;
    axis = vec3.fromValues(Math.sin(tx), Math.cos(tx) * Math.cos(ty), -Math.sin(ty));
  } else {
    const toCam = vec3.sub(vec3.create(), cameraPos, hit); toCam[1] = 0; vec3.normalize(toCam, toCam);
    const ang = (leftHand ? 55 : -55) * Math.PI / 180, ca = Math.cos(ang), sa = Math.sin(ang);
    const hx = toCam[0] * ca - toCam[2] * sa, hz = toCam[0] * sa + toCam[2] * ca;
    const el = (leftHand ? 45 : 38) * Math.PI / 180;
    axis = vec3.fromValues(hx * Math.cos(el), Math.sin(el), hz * Math.cos(el));
  }
  return vec3.normalize(axis, axis);
}

/** Orthonormal frame (x, axis, z) with x in the vertical plane of the axis and z horizontal. */
export function handFrame(axis: vec3): { x: vec3; z: vec3 } {
  const z = vec3.cross(vec3.create(), axis, [0, 1, 0]);
  if (vec3.length(z) < 1e-3) vec3.set(z, 0, 0, 1);
  vec3.normalize(z, z);
  const x = vec3.cross(vec3.create(), z, axis);
  return { x, z };
}

export function frameMatrix(out: mat4, x: vec3, y: vec3, z: vec3, origin: vec3): mat4 {
  return mat4.set(out, x[0], x[1], x[2], 0, y[0], y[1], y[2], 0, z[0], z[1], z[2], 0, origin[0], origin[1], origin[2], 1);
}

export const HIDDEN = mat4.fromTranslation(mat4.create(), [0, -1e4, 0]);

/**
 * Distance (mm) from a point to the metal of an instrument modelled as a capsule from `tip` along
 * `axis` for `length`, radius `r`: used for pencil-to-instrument contact (buzzing the forceps, or an
 * accidental touch on a retractor that diverts the current).
 */
export function metalDistance(p: vec3, tip: vec3, axis: vec3, length: number, r: number): number {
  const rel = vec3.sub(vec3.create(), p, tip);
  const t = Math.min(Math.max(vec3.dot(rel, axis), 0), length);
  return vec3.length(vec3.scaleAndAdd(rel, rel, axis, -t)) - r;
}
