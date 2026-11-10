/**
 * Tissue types, states and physical property table.
 *
 * Electrical conductivity is for ~400-500 kHz (electrosurgical fundamental).
 * Starting values are drawn from the IT'IS Foundation tissue property database and the
 * literature summarised in docs/physics.md. They are the calibration surface: tune here,
 * never in shaders.
 *
 * The mechanical block (compliance, strain stiffening, yield, tearing, cohesion, thermal
 * shrink/stiffening, capillary ooze) drives the XPBD lattice, blunt dissection and the raw-surface
 * bleeding rules; see docs/physics.md "Tissue mechanics" for the provenance of each number.
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
  /** loose areolar connective tissue: the wispy, stringy plane between fat lobules, over fasciae and around capsules */
  Areolar = 14,
  /** visceral parenchyma (bowel wall / solid organ): soft, friable, very vascular */
  Viscera = 15,
  /** serosa / organ capsule: thin glistening collagen membrane */
  Serosa = 16,
  COUNT = 17,
}

/**
 * Cell state (3 bits). For tissue: Normal, Coagulated, Desiccated, Carbonized. For air (type 0):
 * Normal = the original air above the skin, Vaporized = air made by cutting, vaporising or tearing
 * (an open wound surface: exposed vessel ends bleed, raw surfaces ooze), Dissected = air made by
 * blunt dissection (a separated plane: vessels crossing it stay intact and do not bleed).
 */
export enum TissueState {
  Normal = 0,
  Coagulated = 1,
  Desiccated = 2,
  Carbonized = 3,
  Vaporized = 4,
  Dissected = 5,
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
  // ---- mechanics (XPBD lattice) ----
  /** XPBD compliance of a lattice link through this tissue (lower = stiffer) */
  compliance: number;
  /** strain stiffening knee: compliance falls by 1 + (strain / kneeStrain)^2 (collagen J-curve; small knee = stiffens at once) */
  kneeStrain: number;
  /** strain beyond which the tissue yields: links lengthen permanently and become strands (stringiness) */
  yieldStrain: number;
  /** strain at which a link through healthy tissue tears */
  tearStrain: number;
  /** 0..1 adhesion of the tissue to its neighbours: planes with cohesion below the instrument threshold separate under blunt dissection */
  cohesion: number;
  /** fractional rest-length contraction once fully coagulated (collagen denaturation) */
  coagShrink: number;
  /** stiffness multiplier once coagulated (> 1 firmer, < 1 softer: rendered fat) */
  coagStiffen: number;
  /** capillary ooze from a fresh raw surface, microlitres per second per mm2 of exposed surface */
  oozeRate: number;
}

/** ln(A) such that the damage rate is 1/s at temperature T1s (Kelvin) for the given Ea. */
function lnAFor(Ea: number, T1sC: number): number {
  return Ea / (R_GAS * (T1sC + 273.15));
}

const HENRIQUES_EA = 6.28e5; // J/mol, skin (Henriques 1947)
const HENRIQUES_LNA = Math.log(3.1e98); // 226.8
const COLLAGEN_EA = 3.06e5, COLLAGEN_LNA = lnAFor(3.06e5, 65);
const FAT_EA = 3.0e5, FAT_LNA = lnAFor(3.0e5, 65);
const MUSCLE_EA = 2.6e5, MUSCLE_LNA = Math.log(2.94e39);
// blood proteins (albumin, fibrinogen) denature steeply: ~1/s at 68 C, negligible below 55 C
const BLOOD_EA = 5.0e5, BLOOD_LNA = lnAFor(5.0e5, 68);

type Mech = Pick<TissueProps, 'compliance' | 'kneeStrain' | 'yieldStrain' | 'tearStrain' | 'cohesion' | 'coagShrink' | 'coagStiffen' | 'oozeRate'>;
const mech = (compliance: number, kneeStrain: number, yieldStrain: number, tearStrain: number, cohesion: number, coagShrink: number, coagStiffen: number, oozeRate: number): Mech =>
  ({ compliance, kneeStrain, yieldStrain, tearStrain, cohesion, coagShrink, coagStiffen, oozeRate });

export const TISSUE: Readonly<Record<number, TissueProps>> = {
  [Tissue.Air]: { name: 'air', sigma: 0, k: 0.026, rho: 1.2, c: 1005, perfusion: 0, water: 0, Ea: 1e9, lnA: 0, meltC: NaN, ...mech(1e-5, 9, 9, 9, 0, 0, 1, 0) },
  // tough, pre-tensioned, no yield to speak of: skin stretches a long way then tears
  [Tissue.Epidermis]: { name: 'epidermis', sigma: 0.05, k: 0.24, rho: 1190, c: 3600, perfusion: 0, water: 0.5, Ea: HENRIQUES_EA, lnA: HENRIQUES_LNA, meltC: NaN, ...mech(1.0e-6, 0.30, 0.5, 0.9, 1.0, 0.12, 2.5, 0) },
  [Tissue.Dermis]: { name: 'dermis', sigma: 0.3, k: 0.37, rho: 1109, c: 3391, perfusion: 106, water: 0.65, Ea: HENRIQUES_EA, lnA: HENRIQUES_LNA, meltC: NaN, ...mech(2.0e-6, 0.35, 0.5, 1.0, 1.0, 0.15, 2.5, 0.08) },
  // soft lobules held by weak septa: parts easily, little ooze, rendered fat softens
  [Tissue.Fat]: { name: 'subcutaneous fat', sigma: 0.04, k: 0.21, rho: 911, c: 2348, perfusion: 33, water: 0.18, Ea: FAT_EA, lnA: FAT_LNA, meltC: 45, ...mech(4.0e-5, 0.50, 0.25, 0.6, 0.3, 0.04, 0.6, 0.01) },
  [Tissue.Septum]: { name: 'fibrous septum', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 20, water: 0.6, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(5.0e-6, 0.15, 0.3, 0.8, 0.5, 0.15, 2, 0.02) },
  // fasciae: stiff collagen sheets that resist stretch and then split at low strain
  [Tissue.SuperficialFascia]: { name: 'superficial fascia', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 15, water: 0.6, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(8.0e-7, 0.05, 0.15, 0.3, 0.9, 0.18, 2, 0.02) },
  [Tissue.DeepFascia]: { name: 'deep fascia', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 10, water: 0.6, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(5.0e-7, 0.05, 0.15, 0.25, 0.9, 0.18, 2, 0.02) },
  // muscle: anisotropic (the lattice stiffens along the fibre direction), bleeds briskly from any raw surface
  [Tissue.Muscle]: { name: 'muscle', sigma: 0.55, k: 0.49, rho: 1090, c: 3421, perfusion: 37, water: 0.75, Ea: MUSCLE_EA, lnA: MUSCLE_LNA, meltC: NaN, ...mech(1.0e-5, 0.25, 0.35, 0.7, 0.7, 0.10, 2, 0.15) },
  // vessels: elastic, stretch far before snapping, recoil when cut, shrink strongly when coagulated
  [Tissue.Artery]: { name: 'artery', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(5.0e-6, 0.30, 0.6, 1.2, 1.0, 0.25, 2, 0) },
  [Tissue.Vein]: { name: 'vein', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(8.0e-6, 0.35, 0.6, 1.2, 1.0, 0.25, 2, 0) },
  [Tissue.Nerve]: { name: 'nerve', sigma: 0.2, k: 0.49, rho: 1075, c: 3613, perfusion: 20, water: 0.7, Ea: MUSCLE_EA, lnA: MUSCLE_LNA, meltC: NaN, ...mech(3.0e-6, 0.20, 0.4, 0.9, 1.0, 0.10, 1.5, 0) },
  [Tissue.Lipoma]: { name: 'lipoma', sigma: 0.045, k: 0.21, rho: 920, c: 2348, perfusion: 15, water: 0.2, Ea: FAT_EA, lnA: FAT_LNA, meltC: 45, ...mech(3.0e-5, 0.50, 0.25, 0.5, 0.6, 0.04, 0.6, 0.01) },
  // the capsule plane is where a lipoma shells out: low adhesion to the surrounding fat
  [Tissue.LipomaCapsule]: { name: 'lipoma capsule', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 10, water: 0.6, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(4.0e-6, 0.15, 0.3, 0.7, 0.2, 0.15, 2, 0.01) },
  [Tissue.Blood]: { name: 'blood', sigma: 0.7, k: 0.52, rho: 1050, c: 3617, perfusion: 0, water: 0.8, Ea: BLOOD_EA, lnA: BLOOD_LNA, meltC: NaN, ...mech(1e-4, 9, 9, 9, 0, 0, 1, 0) },
  // areolar tissue: the stringy plane. Nearly no resistance, yields into long strands, separates under a spreading instrument
  [Tissue.Areolar]: { name: 'areolar tissue', sigma: 0.35, k: 0.45, rho: 1050, c: 3500, perfusion: 25, water: 0.72, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(6.0e-5, 0.80, 0.4, 1.2, 0.1, 0.10, 1.5, 0.02) },
  // viscera: friable, tears at low strain, oozes heavily from any raw surface, low impedance
  [Tissue.Viscera]: { name: 'viscera', sigma: 0.55, k: 0.52, rho: 1079, c: 3540, perfusion: 800, water: 0.76, Ea: MUSCLE_EA, lnA: MUSCLE_LNA, meltC: NaN, ...mech(2.0e-5, 0.30, 0.3, 0.4, 0.8, 0.08, 1.5, 0.15) },
  [Tissue.Serosa]: { name: 'serosa', sigma: 0.3, k: 0.4, rho: 1100, c: 3300, perfusion: 15, water: 0.6, Ea: COLLAGEN_EA, lnA: COLLAGEN_LNA, meltC: NaN, ...mech(1.5e-6, 0.08, 0.2, 0.6, 0.9, 0.15, 2, 0.02) },
};

/** Tissues that the forceps/hemostat/electrode treat as vessels. */
export const isVessel = (t: Tissue) => t === Tissue.Artery || t === Tissue.Vein;
/** Tissues a blunt dissector cannot divide: they bridge a separated plane as strands until torn or cut. */
export const isBridging = (t: Tissue) => t === Tissue.Artery || t === Tissue.Vein || t === Tissue.Nerve;

/** Blunt dissection thresholds: a spreading instrument separates planes with cohesion up to the threshold. */
export const DISSECT_SPREAD_COHESION = 0.55;  // opening jaws: areolar, fat lobules, capsule plane, septa
export const DISSECT_PUSH_COHESION = 0.35;    // pushing closed tips: areolar, fat, capsule plane only

/** Thermal-state mechanics (global): desiccated and charred tissue is stiffer and brittle. */
export const DRY_TEAR_FACTOR = 0.65;    // tear strain falls by this fraction at full desiccation
export const DRY_STIFFEN = 0.5;         // compliance falls by this fraction at full desiccation
export const CHAR_TEAR_FACTOR = 0.25;   // charred links keep this fraction of their tear strain
export const COAG_TEAR_FACTOR = 0.8;    // coagulated tissue is more friable

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
 * Layout per tissue (12 floats, 48 bytes): sigma, k, rho, c, w_b, water, Ea/R, lnA, cohesion, oozeRate, tearStrain, 0
 */
export const PROPS_STRIDE = 12;
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
    out[o + 8] = p.cohesion;
    out[o + 9] = p.oozeRate;
    out[o + 10] = p.tearStrain;
    out[o + 11] = 0;
  }
  return out;
}

export const TISSUE_NAMES: string[] = Array.from({ length: Tissue.COUNT }, (_, i) => TISSUE[i].name);
export const STATE_NAMES = ['normal', 'coagulated', 'desiccated', 'carbonized', 'vaporized', 'dissected'];
