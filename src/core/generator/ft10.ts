/**
 * Valleylab FT10 generator model (monopolar). Output = f(setting, mode, load impedance).
 *
 * Curve shape: constant power between zLo and zHi; current-limited below zLo (P ∝ Z);
 * voltage-limited above zHi (P ∝ 1/Z); RMS voltage additionally capped by vPeak / crest.
 * Numbers marked ~ are approximations pending digitisation of the FT10 user guide output
 * curves (docs/physics.md, open items).
 */

export type CutMode = 'pure' | 'blend' | 'low' | 'valleylab';
export type CoagMode = 'fulgurate' | 'spray';
export type ModeId = CutMode | CoagMode;

export interface ModeSpec {
  id: ModeId;
  label: string;
  kind: 'cut' | 'coag';
  maxWatts: number;
  /** ~ constant-power band, ohms */
  zLo: number;
  zHi: number;
  /** ~ peak open-circuit voltage, V */
  vPeak: number;
  /** waveform crest factor (Vpeak / Vrms) */
  crest: number;
  /** duty cycle of the modulated waveform (1 = continuous sinusoid) */
  duty: number;
  /** fraction of boiling energy that removes tissue (continuous wave vaporises; interrupted desiccates) */
  vapFactor: number;
  /** ~ arc reach scale: mm of non-contact gap bridged per kV of peak voltage */
  arcMmPerKv: number;
  /** ~ thickness of the tissue layer electrically coupled to the electrode surface (mm): thin steam front in cut, desiccating layer in coag */
  contactMm: number;
  /** ~ fraction of delivered power dissipated in the unresolved near-field layer at the electrode surface (deposited into wet contact voxels) */
  nearField: number;
  /** activation tone frequency, Hz (cut ~ 500, coag ~ 700 on Valleylab units) */
  toneHz: number;
}

export const FT10_MODES: Record<ModeId, ModeSpec> = {
  pure:      { id: 'pure',      label: 'Pure',      kind: 'cut',  maxWatts: 300, zLo: 100, zHi: 2000, vPeak: 1600, crest: 1.4, duty: 1.0,  vapFactor: 1.0,  arcMmPerKv: 0.08, contactMm: 0.1, nearField: 0.7, toneHz: 500 },
  blend:     { id: 'blend',     label: 'Blend',     kind: 'cut',  maxWatts: 200, zLo: 100, zHi: 1500, vPeak: 2200, crest: 2.8, duty: 0.5,  vapFactor: 0.6,  arcMmPerKv: 0.15, contactMm: 0.15, nearField: 0.5, toneHz: 500 },
  low:       { id: 'low',       label: 'Low',       kind: 'cut',  maxWatts: 200, zLo: 100, zHi: 600,  vPeak: 1200, crest: 1.4, duty: 1.0,  vapFactor: 1.0,  arcMmPerKv: 0.05, contactMm: 0.1, nearField: 0.7, toneHz: 500 },
  valleylab: { id: 'valleylab', label: 'Valleylab', kind: 'cut',  maxWatts: 300, zLo: 100, zHi: 2000, vPeak: 2000, crest: 2.4, duty: 0.6,  vapFactor: 0.7,  arcMmPerKv: 0.15, contactMm: 0.15, nearField: 0.5, toneHz: 500 },
  fulgurate: { id: 'fulgurate', label: 'Fulgurate', kind: 'coag', maxWatts: 120, zLo: 200, zHi: 3000, vPeak: 4500, crest: 6.0, duty: 0.06, vapFactor: 0.12, arcMmPerKv: 0.45, contactMm: 0.3, nearField: 0.4, toneHz: 700 },
  spray:     { id: 'spray',     label: 'Spray',     kind: 'coag', maxWatts: 120, zLo: 300, zHi: 4000, vPeak: 6000, crest: 8.0, duty: 0.04, vapFactor: 0.08, arcMmPerKv: 0.6,  contactMm: 0.3, nearField: 0.4, toneHz: 700 },
};

export interface GeneratorOutput { power: number; vRms: number; zOhm: number }

/** Delivered power and RMS voltage into load Z for a setting (W) in a mode. Pure TS; mirrored in WGSL. */
export function generatorOutput(mode: ModeSpec, settingW: number, zOhm: number): GeneratorOutput {
  const setting = Math.min(settingW, mode.maxWatts);
  if (!(zOhm > 0) || !isFinite(zOhm)) return { power: 0, vRms: mode.vPeak / mode.crest, zOhm: Infinity };
  let p: number;
  if (zOhm < mode.zLo) p = setting * (zOhm / mode.zLo);
  else if (zOhm <= mode.zHi) p = setting;
  else p = setting * (mode.zHi / zOhm);
  const vMax = mode.vPeak / mode.crest;
  let v2 = p * zOhm;
  if (v2 > vMax * vMax) { v2 = vMax * vMax; p = v2 / zOhm; }
  return { power: p, vRms: Math.sqrt(v2), zOhm };
}

export type Activation = 'none' | 'cut' | 'coag';

export interface GeneratorSettings {
  cutMode: CutMode;
  coagMode: CoagMode;
  cutWatts: number;
  coagWatts: number;
}

export class FT10 {
  settings: GeneratorSettings;
  activation: Activation = 'none';
  /** REM return-electrode fault: generator refuses to activate */
  remFault = false;
  constructor(init: GeneratorSettings) { this.settings = { ...init }; }
  get activeMode(): ModeSpec | null {
    if (this.activation === 'cut') return FT10_MODES[this.settings.cutMode];
    if (this.activation === 'coag') return FT10_MODES[this.settings.coagMode];
    return null;
  }
  get activeWatts(): number {
    return this.activation === 'cut' ? this.settings.cutWatts : this.activation === 'coag' ? this.settings.coagWatts : 0;
  }
  adjust(kind: 'cut' | 'coag', delta: number) {
    const key = kind === 'cut' ? 'cutWatts' : 'coagWatts';
    const max = FT10_MODES[kind === 'cut' ? this.settings.cutMode : this.settings.coagMode].maxWatts;
    this.settings[key] = Math.max(1, Math.min(max, this.settings[key] + delta));
  }
  cycleMode(kind: 'cut' | 'coag') {
    if (kind === 'cut') {
      const order: CutMode[] = ['pure', 'blend', 'low', 'valleylab'];
      this.settings.cutMode = order[(order.indexOf(this.settings.cutMode) + 1) % order.length];
    } else {
      this.settings.coagMode = this.settings.coagMode === 'fulgurate' ? 'spray' : 'fulgurate';
    }
    this.adjust(kind, 0);
  }
}
