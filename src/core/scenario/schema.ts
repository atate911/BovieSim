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
  /** loose areolar plane on either face of Scarpa's fascia and above the deep fascia (mm, default 0.5; 0 disables) */
  areolarMm?: number;
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
  /** deterministic perforators (bed-centred mm) in addition to the seeded ones */
  explicit?: { xMm: number; yMm: number; rMm: number; arterial: boolean }[];
}

/** Pre-existing wound carved before the case starts (signed-distance shapes). */
export interface PreCarve {
  kind: 'trench' | 'pit';
  centerMm: [number, number];
  /** trench: length along its axis; pit: top radius */
  lengthMm: number;
  /** trench: top half-width; pit: unused */
  widthMm: number;
  depthMm: number;
  /** trench axis angle from +x, degrees */
  angleDeg?: number;
  /** wall slope: extra half-width (trench) or radius shrink (pit) per mm of depth */
  slope?: number;
  /** the floor follows a layer instead of a fixed depth: just onto the deep fascia, the muscle or the organ's serosa */
  floor?: 'deepFascia' | 'muscle' | 'organ';
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
  /** thickness of the loose areolar sheath around the capsule (the plane that shells out), mm; default 0.4 */
  areolarMm?: number;
}

export type LesionSpec = LipomaSpec;

/**
 * A visceral organ beneath the muscle: a serosal membrane over soft, very vascular parenchyma.
 * The muscle layer is `muscleMm` thick below the deep fascia; the organ fills the rest of the bed.
 */
export interface OrganSpec {
  kind: 'bowel' | 'liver';
  muscleMm: number;
  serosaMm: number;
  /** a serosal vessel laid at the surface so that a pre-carved floor onto the organ opens it (bed-centred mm) */
  bleeder?: { xMm: number; yMm: number; rMm: number; arterial: boolean; angleDeg?: number; lengthMm?: number };
}

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
  organ?: OrganSpec;
  preCarve?: PreCarve[];
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
  if (sc.organ && !(sc.organ.muscleMm >= 0 && sc.organ.serosaMm >= 0)) throw new Error('organ.muscleMm and organ.serosaMm must be >= 0');
  return { ...sc, nerves: sc.nerves ?? [], faults: sc.faults ?? [] };
}
