import { mat4, vec3 } from 'gl-matrix';

export type ElectrodeType = 'blade' | 'needle' | 'ball' | 'forceps';

/** Bovie pencil pose and electrode geometry in world mm. */
export class ElectrodeTool {
  type: ElectrodeType = 'blade';
  /** tip (distal end of the electrode) */
  readonly tip = vec3.create();
  /** unit axis from tip toward the handle */
  readonly axis = vec3.fromValues(0, 1, 0);
  /** blade width direction (edge spans +-halfWidth along it) */
  readonly wdir = vec3.fromValues(1, 0, 0);
  /** blade face normal */
  readonly ndir = vec3.fromValues(0, 0, 1);
  /** 0..1 from pen pressure; pushes the tip into tissue */
  pressure = 0.5;
  /** pen tilt in degrees, x and y */
  tiltX = 0; tiltY = 0;
  /** rotation of the blade about its axis, radians (R key) */
  roll = 0;
  visible = true;
  readonly model = mat4.create();

  /** forceps jaw span when the pencil is replaced by buzzed forceps */
  private jawSpan = 0.5;
  private jawRadius = 0.6;
  get length() { return this.type === 'blade' ? 12 : this.type === 'needle' ? 10 : this.type === 'forceps' ? this.jawSpan : 3; }
  get halfWidth() { return this.type === 'blade' ? 1.2 : this.type === 'needle' ? 0.2 : this.type === 'forceps' ? this.jawRadius : 1.5; }
  get halfThickness() { return this.type === 'blade' ? 0.25 : this.type === 'needle' ? 0.2 : this.type === 'forceps' ? this.jawRadius : 1.5; }

  /** Use closed forceps jaws (tip a -> tip b, capsule radius r) as the electrode. */
  asForceps(a: vec3, b: vec3, r: number, handleAxis: vec3) {
    this.type = 'forceps'; this.visible = true;
    vec3.copy(this.tip, a);
    const d = vec3.sub(vec3.create(), b, a); this.jawSpan = Math.max(vec3.length(d), 0.1); vec3.normalize(d, d);
    vec3.copy(this.wdir, d); vec3.copy(this.axis, handleAxis);
    vec3.cross(this.ndir, d, handleAxis); vec3.normalize(this.ndir, this.ndir);
    this.jawRadius = r;
  }
  /** penetration depth into tissue from pressure (mm) */
  get penetration() { return 0.15 + this.pressure * 0.9; }

  /**
   * Places the pencil at a surface hit. The handle runs toward the surgeon's right hand:
   * the to-camera direction rotated 55 deg clockwise, 38 deg above the surface. Pen tilt, when
   * present, overrides that orientation.
   */
  place(hit: vec3 | null, cameraPos: vec3, hasTilt: boolean) {
    if (!hit) { this.visible = false; mat4.fromTranslation(this.model, [0, -1e4, 0]); return; }
    this.visible = true;
    let axis: vec3;
    if (hasTilt) {
      // pointer tilt: tiltX/tiltY are the angles of the pen from vertical toward +x / +y(screen)
      const tx = this.tiltX * Math.PI / 180, ty = this.tiltY * Math.PI / 180;
      axis = vec3.fromValues(Math.sin(tx), Math.cos(tx) * Math.cos(ty), -Math.sin(ty));
    } else {
      const toCam = vec3.sub(vec3.create(), cameraPos, hit); toCam[1] = 0; vec3.normalize(toCam, toCam);
      const ang = -55 * Math.PI / 180, ca = Math.cos(ang), sa = Math.sin(ang);
      const hx = toCam[0] * ca - toCam[2] * sa, hz = toCam[0] * sa + toCam[2] * ca;
      const el = 38 * Math.PI / 180;
      axis = vec3.fromValues(hx * Math.cos(el), Math.sin(el), hz * Math.cos(el));
    }
    vec3.normalize(axis, axis);
    vec3.copy(this.axis, axis);
    // tip pushed into tissue along the axis by the pressure-dependent penetration
    vec3.scaleAndAdd(this.tip, hit, axis, -this.penetration);
    // blade frame: width direction perpendicular to the axis, rolled about it
    const z0 = vec3.cross(vec3.create(), axis, [0, 1, 0]);
    if (vec3.length(z0) < 1e-3) vec3.set(z0, 0, 0, 1);
    vec3.normalize(z0, z0);
    const x0 = vec3.cross(vec3.create(), z0, axis);
    const cr = Math.cos(this.roll), sr = Math.sin(this.roll);
    // default: blade face normal = z0 (edge in the plane containing the axis and x0)
    vec3.set(this.ndir, z0[0] * cr + x0[0] * sr, z0[1] * cr + x0[1] * sr, z0[2] * cr + x0[2] * sr);
    vec3.set(this.wdir, x0[0] * cr - z0[0] * sr, x0[1] * cr - z0[1] * sr, x0[2] * cr - z0[2] * sr);
    mat4.set(this.model, x0[0], x0[1], x0[2], 0, axis[0], axis[1], axis[2], 0, z0[0], z0[1], z0[2], 0, this.tip[0], this.tip[1], this.tip[2], 1);
  }
}
