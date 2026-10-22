/**
 * Scenario schema. Every scenario carries both the anatomy and the educational structure
 * (goal, success, failure points, decision points, modeled ambiguity). Phase 1 consumes the
 * anatomy and emits the events the educational fields reference.
 */

export interface BedSpec {
  /** x, y (surface) and z (depth) extents in mm */
  sizeMm: [number, number, number];
  /** voxel edge in mm */
  voxelMm: number;
}

export interface LayerSpec {
  epidermisMm: number;
  dermisMm: number;
  fatMm: number;
  /** superficial (Scarpa's) fascia: thickness and fractional depth within the fat layer (0..1) */
  superficialFasciaMm: number;
  superficialFasciaAt: number;
  deepFasciaMm: number;
  /** +- random variation of layer thickness, fraction (modeled ambiguity) */
  variation?: number;
}

export interface SeptaSpec {
  /** mean fat lobule diameter in mm (septa form the lobule boundaries) */
  lobuleMm: number;
  thicknessMm: number;
}

export interface VesselSpec {
  seed: number;
  /** perforating vessels per cm2 of fascia */
  perforatorsPerCm2: number;
  arterialFraction: number;
  radiusMm: [number, number];
  dermalPlexus: boolean;
}

export interface NerveSpec {
  fromMm: [number, number];
  toMm: [number, number];
  depthMm: number;
  radiusMm: number;
}

export interface LipomaSpec {
  type: 'lipoma';
  centerMm: [number, number];
  /** depth of the top of the lesion below the skin surface */
  topDepthMm: number;
  /** semi-axes in mm */
  sizeMm: [number, number, number];
  capsuleMm: number;
  /** 'subcutaneous' | 'subfascial' | 'intramuscular' */
  plane: 'subcutaneous' | 'subfascial' | 'intramuscular';
}

export type LesionSpec = LipomaSpec;

export interface SkinSpec {
  prep: 'chlorhexidine' | 'betadine' | 'none';
  /** 0 = light, 1 = dark */
  tone: number;
  /** Langer's line direction, degrees from +x */
  langerDeg: number;
}

export interface GeneratorPreset {
  cutMode: 'pure' | 'blend' | 'low' | 'valleylab';
  coagMode: 'fulgurate' | 'spray';
  cutWatts: number;
  coagWatts: number;
  /** locked: learner cannot change; suggested: shown; free: nothing shown */
  policy: 'locked' | 'suggested' | 'free';
}

export interface Criterion {
  metric: string;
  op: '<' | '<=' | '>' | '>=' | '==';
  value: number;
  label: string;
}

export interface FailurePoint {
  id: string;
  /** event name from education/events.ts, with optional filter */
  trigger: string;
  consequence: string;
  teaching: string;
}

export interface DecisionPoint {
  id: string;
  trigger: string;
  prompt: string;
  options: { label: string; outcome: string }[];
}

export interface Uncertainty {
  id: string;
  description: string;
  /** which anatomy/settings field is randomised or hidden */
  affects: string;
}

export interface EducationSpec {
  goal: string;
  success: Criterion[];
  failurePoints: FailurePoint[];
  decisionPoints: DecisionPoint[];
  ambiguity: Uncertainty[];
}

export interface Scenario {
  id: string;
  name: string;
  description: string;
  bed: BedSpec;
  layers: LayerSpec;
  septa: SeptaSpec;
  vessels: VesselSpec;
  nerves: NerveSpec[];
  lesion?: LesionSpec;
  skin: SkinSpec;
  generator: GeneratorPreset;
  faults: string[];
  education: EducationSpec;
}

export function validateScenario(s: unknown): Scenario {
  const o = s as Record<string, unknown>;
  const need = (k: string) => { if (!(k in o)) throw new Error(`scenario missing field: ${k}`); };
  ['id', 'name', 'bed', 'layers', 'septa', 'vessels', 'skin', 'generator', 'education'].forEach(need);
  const bed = o.bed as BedSpec;
  if (!(bed.voxelMm > 0.05 && bed.voxelMm <= 2)) throw new Error('bed.voxelMm out of range');
  const edu = o.education as EducationSpec;
  if (typeof edu.goal !== 'string') throw new Error('education.goal required');
  const sc = o as unknown as Scenario;
  return { ...sc, nerves: sc.nerves ?? [], faults: sc.faults ?? [] };
}
