import { mat4, vec3 } from 'gl-matrix';

export type ToolKind = 'pencil' | 'forceps' | 'hemostat' | 'suction' | 'gauze' | 'hook';
export const TOOL_ORDER: ToolKind[] = ['pencil', 'forceps', 'hemostat', 'suction', 'gauze', 'hook'];
export const TOOL_LABEL: Record<ToolKind, string> = { pencil: 'Bovie pencil', forceps: 'Adson forceps', hemostat: 'Kelly hemostat', suction: 'Frazier suction', gauze: '4x4 gauze', hook: 'Skin hook' };

/**
 * Shared hand-held instrument pose: the handle runs toward the surgeon's right hand (the
 * to-camera direction rotated 55 deg clockwise, 38 deg above the surface) unless pen tilt is given.
 */
export function handAxis(hit: vec3, cameraPos: vec3, tilt: { x: number; y: number } | null): vec3 {
  let axis: vec3;
  if (tilt) {
    const tx = tilt.x * Math.PI / 180, ty = tilt.y * Math.PI / 180;
    axis = vec3.fromValues(Math.sin(tx), Math.cos(tx) * Math.cos(ty), -Math.sin(ty));
  } else {
    const toCam = vec3.sub(vec3.create(), cameraPos, hit); toCam[1] = 0; vec3.normalize(toCam, toCam);
    const ang = -55 * Math.PI / 180, ca = Math.cos(ang), sa = Math.sin(ang);
    const hx = toCam[0] * ca - toCam[2] * sa, hz = toCam[0] * sa + toCam[2] * ca;
    const el = 38 * Math.PI / 180;
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
