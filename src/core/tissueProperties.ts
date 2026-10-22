/**
 * Tissue types, states and physical property table.
 *
 * Electrical conductivity is for ~400-500 kHz (electrosurgical fundamental).
 * Starting values are drawn from the IT'IS Foundation tissue property database and the
 * literature summarised in docs/physics.md. They are the calibration surface: tune there,
 * never in shaders.
 */

export const R_GAS = 8.314; // J/(mol K)
export const T_BODY_C = 37;
export const LATENT_HEAT_WATER = 2.26e6; // J/kg
export const BLOOD_RHO = 1050; // kg/m3
export const BLOOD_C = 3617; // J/(kg K)

export enum Tissue {
  Air = 0,
  Epidermis = 1,
  Dermis = 2,
  Fat = 3,
  Septum = 4,
  SuperficialFascia = 5,
  DeepFascia = 6,
  Muscle = 7,
  Artery = 8,
  Vein = 9,
  Nerve = 10,
  Lipoma = 11,
  LipomaCapsule = 12,
  Blood = 13,
  COUNT = 14,
}

export enum TissueState {
  Normal = 0,
  Coagulated = 1,
  Desiccated = 2,
  Carbonized = 3,
  Vaporized = 4,
}

export interface TissueProps {
  name: string;
  /** electrical conductivity S/m at ~450 kHz */
  sigma: number;
  /** thermal conductivity W/(m K) */
  k: number;
  /** density kg/m3 */
  rho: number;
  /** specific heat J/(kg K) */
  c: number;
  /** blood perfusion ml/(min kg) */
  perfusion: number;
  /** water mass fraction */
  water: number;
  /** Arrhenius activation energy J/mol */
  Ea: number;
  /** Arrhenius ln(A), A in 1/s */
  lnA: number;
  /** lipid melting point C (NaN if not applicable) */
  meltC: number;
}

/** ln(A) such that the damage rate is 1/s at temperature T1s (Kelvin) for the given Ea. */
function lnAFor(Ea: number, T1sC: number): number {
  return Ea / (R_GAS * (T1sC + 273.15));
}

const HENRIQUES_EA = 6.28e5; // J/mol, skin (Henriques 1947)
const HENRIQUES_LNA = Math.log(3.1e98); // 226.8

export const TISSUE: Readonly<Record<number, TissueProps>> = {
  [Tissue.Air]: { name: 'air', sigma: 0, k: 0.026, rho: 1.2, c: 1005, perfusion: 0, water: 0, Ea: 1e9, lnA: 0, meltC: NaN },
  [Tissue.Epidermis]: { name: 'epidermis', sigma: 0.05, k: 0.24, rho: 1190, c: 3600, perfusion: 0, water: 0.5, Ea: HENRIQUES_EA, lnA: HENRIQUES_LNA, meltC: NaN },
  [Tissue.Dermis]: { name: 'dermis', sigma: 0.3, k: 0.37, rho: 1109, c: 3391, perfusion: 106, water: 0.65, Ea: HENRIQUES_EA, lnA: HENRIQUES_LNA, meltC: NaN },
  [Tissue.Fat]: { name: 'subcutaneous fat', sigma: 0.04, k: 0.21, rho: 911, c: 2348, perfusion: 33, water: 0.18, Ea: 3.0e5, lnA: lnAFor(3.0e5, 65), meltC: 45 },
  [Tissue.Septum]: { name: 'fibrous septum', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 20, water: 0.6, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.SuperficialFascia]: { name: 'superficial fascia', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 15, water: 0.6, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.DeepFascia]: { name: 'deep fascia', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 10, water: 0.6, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.Muscle]: { name: 'muscle', sigma: 0.55, k: 0.49, rho: 1090, c: 3421, perfusion: 37, water: 0.75, Ea: 2.6e5, lnA: Math.log(2.94e39), meltC: NaN },
  [Tissue.Artery]: { name: 'artery', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.Vein]: { name: 'vein', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.Nerve]: { name: 'nerve', sigma: 0.2, k: 0.49, rho: 1075, c: 3613, perfusion: 20, water: 0.7, Ea: 2.6e5, lnA: Math.log(2.94e39), meltC: NaN },
  [Tissue.Lipoma]: { name: 'lipoma', sigma: 0.045, k: 0.21, rho: 920, c: 2348, perfusion: 15, water: 0.2, Ea: 3.0e5, lnA: lnAFor(3.0e5, 65), meltC: 45 },
  [Tissue.LipomaCapsule]: { name: 'lipoma capsule', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 10, water: 0.6, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
  [Tissue.Blood]: { name: 'blood', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: 3.06e5, lnA: lnAFor(3.06e5, 65), meltC: NaN },
};

/** Perfusion rate w_b in (m3 blood / m3 tissue / s) from ml/(min kg tissue). */
export function perfusionRate(p: TissueProps): number {
  return (p.perfusion * 1e-6 / 60) * p.rho;
}

/** Thermal diffusivity m2/s */
export function diffusivity(p: TissueProps): number {
  return p.k / (p.rho * p.c);
}

/**
 * Packs the property table into a float array for a GPU uniform/storage buffer.
 * Layout per tissue (8 floats, 32 bytes): sigma, k, rho, c, w_b, water, Ea/R, lnA
 */
export const PROPS_STRIDE = 8;
export function packTissueProps(): Float32Array<ArrayBuffer> {
  const out = new Float32Array(new ArrayBuffer(Tissue.COUNT * PROPS_STRIDE * 4));
  for (let t = 0; t < Tissue.COUNT; t++) {
    const p = TISSUE[t];
    const o = t * PROPS_STRIDE;
    out[o + 0] = p.sigma;
    out[o + 1] = p.k;
    out[o + 2] = p.rho;
    out[o + 3] = p.c;
    out[o + 4] = perfusionRate(p);
    out[o + 5] = p.water;
    out[o + 6] = p.Ea / R_GAS;
    out[o + 7] = p.lnA;
  }
  return out;
}

export const TISSUE_NAMES: string[] = Array.from({ length: Tissue.COUNT }, (_, i) => TISSUE[i].name);
export const STATE_NAMES = ['normal', 'coagulated', 'desiccated', 'carbonized', 'vaporized'];
